#!/usr/bin/env python3
"""Generate the pinned E5 ONNX golden vectors and the fp32 reference.

The int8 golden file is produced from the *same* `onnx/model_int8.onnx` and
`tokenizer.json` that the C# embedder uses. The Python pipeline is written out
explicitly instead of going through `optimum` or sentence-transformers:

    role prefix -> tokenizer.json -> BOS/EOS -> truncation with EOS reserve
    -> int64 tensors -> last_hidden_state -> masked mean (all tokens, including
    BOS/EOS) -> L2 normalization

The fp32 reference is intentionally separate. It is produced by
sentence-transformers from the pinned base model and is only a loose
quantization-quality reference; it is not used to validate the C# pipeline.

Run from this directory after fetching the verified int8 profile:

    python generate.py --model-dir ../../models/e5-small-int8

Requires the versions in requirements-golden.txt. The base fp32 model is
downloaded at the pinned revision by sentence-transformers; model weights are
never committed.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import math
import sys
from importlib.metadata import version
from pathlib import Path
from typing import Any

import numpy as np
import onnxruntime as ort
from tokenizers import Tokenizer

SCHEMA_VERSION = 1
MAX_TOKENS = 512
BOS_ID = 0
PAD_ID = 1
EOS_ID = 2
UNK_ID = 3
PREFIXES = {"query": "query: ", "document": "passage: "}
REFERENCE_MODEL = "intfloat/multilingual-e5-small"
REFERENCE_REVISION = "614241f622f53c4eeff9890bdc4f31cfecc418b3"
REFERENCE_COSINE_MIN = 0.975

# These are part of the numerical contract, not just convenience pins. A
# different ONNX Runtime can select a different int8 kernel and silently move
# the vectors. Regenerating with another version requires an explicit review of
# both fixtures and the platform-specific thresholds.
INT8_RUNTIME = {
    "onnxruntime": "1.31.0",
    "numpy": "2.5.2",
    "tokenizers": "0.22.2",
}
REFERENCE_RUNTIME = {
    "sentence-transformers": "3.0.1",
    "transformers": "4.57.6",
    "torch": "2.13.0",
    "numpy": "2.5.2",
}


def assert_runtime(expected: dict[str, str]) -> None:
    mismatches = [
        f"{name}: installed {version(name)}, expected {wanted}"
        for name, wanted in expected.items()
        if version(name) != wanted
    ]
    if mismatches:
        raise SystemExit(
            "golden generator runtime does not match its pinned contract:\n  "
            + "\n  ".join(mismatches)
            + "\nUpdate requirements-golden.txt and this guard, regenerate both fixtures, "
            "and re-baseline the documented thresholds. Do not only raise a test tolerance."
        )

LONG_TEXT = (
    "Древний жрец Наг и его спутник Йеб шли через пустыню. "
    "The priest Nag and his companion Yeb walked through the desert. "
) * 30

# Keep the IDs stable: they are part of the test names and failure messages.
CASES: list[tuple[str, str, str]] = [
    ("ru-query", "Наг — древний жрец.", "query"),
    ("ru-document", "Наг — древний жрец.", "document"),
    ("en-query", "The priest Nag served Yeb in the temple.", "query"),
    ("en-document", "The priest Nag served Yeb in the temple.", "document"),
    ("mixed-query", "Наг and Yeb spoke in the dark.", "query"),
    ("mixed-document", "Наг and Yeb spoke in the dark.", "document"),
    ("long-query", LONG_TEXT, "query"),
    ("long-document", LONG_TEXT, "document"),
    ("punctuation-query", "?!.", "query"),
    ("punctuation-document", "?!.", "document"),
    ("unicode-query", "ёж и café — Yēb", "query"),
    ("unicode-document", "ёж и café — Yēb", "document"),
]


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def write_json(path: Path, value: Any) -> None:
    text = json.dumps(value, ensure_ascii=False, indent=2) + "\n"
    path.write_text(text, encoding="utf-8", newline="\n")


def load_verified_profile(model_dir: Path, repo_root: Path) -> tuple[dict[str, Any], dict[str, Any]]:
    manifest_path = repo_root / "models" / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    profile = manifest["models"]["e5-small"]
    for entry in profile["files"]:
        path = model_dir / entry["path"]
        if not path.is_file():
            raise SystemExit(f"missing model file: {path}")
        actual = sha256_file(path)
        if actual != entry["sha256"]:
            raise SystemExit(
                f"hash mismatch for {entry['path']}: expected {entry['sha256']}, got {actual}"
            )
    return profile, manifest


def prepare_ids(tokenizer: Tokenizer, text: str) -> tuple[list[int], bool, int]:
    encoded = tokenizer.encode(text, add_special_tokens=True)
    ids = list(encoded.ids)
    if not ids or ids[0] != BOS_ID or ids[-1] != EOS_ID:
        raise SystemExit(f"tokenizer did not emit BOS/EOS for {text!r}: {ids}")
    token_count = len(ids)
    truncated = token_count > MAX_TOKENS
    if truncated:
        ids = ids[: MAX_TOKENS - 1] + [EOS_ID]
    if len(ids) != min(token_count, MAX_TOKENS):
        raise SystemExit("unexpected truncation length")
    return ids, truncated, token_count


def embed_int8(
    session: ort.InferenceSession,
    tokenizer: Tokenizer,
    text: str,
) -> tuple[np.ndarray, list[int], bool, int]:
    ids, truncated, token_count = prepare_ids(tokenizer, text)
    input_ids = np.asarray([ids], dtype=np.int64)
    attention_mask = np.ones_like(input_ids, dtype=np.int64)
    token_type_ids = np.zeros_like(input_ids, dtype=np.int64)
    feed = {
        "input_ids": input_ids,
        "attention_mask": attention_mask,
        "token_type_ids": token_type_ids,
    }
    hidden = session.run(None, feed)[0][0]
    if hidden.shape != (len(ids), 384):
        raise SystemExit(f"unexpected last_hidden_state shape: {hidden.shape}")

    # Explicit masked mean. The mask is all ones because this pipeline embeds
    # one sequence without padding; BOS and EOS are intentionally included.
    pooled = hidden[attention_mask[0] == 1].mean(axis=0)
    norm = float(np.linalg.norm(pooled))
    if not math.isfinite(norm) or norm <= 0.0:
        raise SystemExit("non-finite or zero pooled vector")
    return (pooled / norm).astype(np.float64), ids, truncated, token_count


def cosine(left: np.ndarray, right: np.ndarray) -> float:
    denominator = float(np.linalg.norm(left) * np.linalg.norm(right))
    if denominator == 0.0:
        return 0.0
    return float(np.dot(left, right) / denominator)


def main() -> int:
    script_dir = Path(__file__).resolve().parent
    repo_root = script_dir.parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--model-dir",
        type=Path,
        default=repo_root / "models" / "e5-small-int8",
        help="verified int8 E5 profile (default: ../../models/e5-small-int8)",
    )
    parser.add_argument("--output-dir", type=Path, default=script_dir)
    parser.add_argument(
        "--skip-reference",
        action="store_true",
        help="only regenerate golden_int8.json; keep the existing fp32 reference",
    )
    args = parser.parse_args()

    assert_runtime(INT8_RUNTIME)
    model_dir = args.model_dir.resolve()
    output_dir = args.output_dir.resolve()
    profile, _manifest = load_verified_profile(model_dir, repo_root)

    tokenizer_path = model_dir / "tokenizer.json"
    tokenizer = Tokenizer.from_file(str(tokenizer_path))
    model_path = model_dir / "onnx" / "model_int8.onnx"

    session_options = ort.SessionOptions()
    session_options.intra_op_num_threads = 1
    session = ort.InferenceSession(
        str(model_path),
        sess_options=session_options,
        providers=["CPUExecutionProvider"],
    )

    int8_cases: list[dict[str, Any]] = []
    int8_vectors: dict[str, np.ndarray] = {}
    for case_id, text, role in CASES:
        full_text = PREFIXES[role] + text
        vector, ids, truncated, token_count = embed_int8(session, tokenizer, full_text)
        int8_vectors[case_id] = vector
        int8_cases.append(
            {
                "id": case_id,
                "text": text,
                "role": role,
                "input_ids": ids,
                "token_count": token_count,
                "used_tokens": len(ids),
                "truncated": truncated,
                "vector": [float(value) for value in vector],
            }
        )

    long_case = next(case for case in int8_cases if case["id"] == "long-query")
    if not long_case["truncated"] or long_case["used_tokens"] != MAX_TOKENS:
        raise SystemExit("long case did not exceed the 512-token limit")

    golden = {
        "schema_version": SCHEMA_VERSION,
        "kind": "onnx-int8-golden",
        "generated": dt.date.today().isoformat(),
        "model": {
            "source": profile["source"],
            "base_model": profile["baseModel"],
            "revision": profile["revision"],
            "file": "onnx/model_int8.onnx",
            "sha256": next(
                entry["sha256"]
                for entry in profile["files"]
                if entry["path"] == "onnx/model_int8.onnx"
            ),
            "precision": "int8",
        },
        "tokenizer": {
            "file": "tokenizer.json",
            "sha256": sha256_file(tokenizer_path),
            "special_tokens": {"bos": BOS_ID, "pad": PAD_ID, "eos": EOS_ID, "unk": UNK_ID},
        },
        "runtime": {
            "onnxruntime": version("onnxruntime"),
            "numpy": version("numpy"),
            "tokenizers": version("tokenizers"),
            "python": sys.version.split()[0],
        },
        "pipeline": {
            "prefix": PREFIXES,
            "max_tokens": MAX_TOKENS,
            "truncation": "keep first 511 ids and append EOS when token_count > 512",
            "attention_mask": "all ones; no padding",
            "pooling": "masked mean over all tokens, including BOS and EOS",
            "normalization": "L2",
            "tensor_layout": "int64 input_ids/attention_mask/token_type_ids [1, sequence]",
        },
        "cases": int8_cases,
    }
    write_json(output_dir / "golden_int8.json", golden)
    print(
        f"wrote golden_int8.json: {len(int8_cases)} cases; "
        f"long token_count={long_case['token_count']} truncated={long_case['truncated']}"
    )

    if args.skip_reference:
        return 0

    assert_runtime(REFERENCE_RUNTIME)
    from sentence_transformers import SentenceTransformer

    reference = SentenceTransformer(
        REFERENCE_MODEL,
        revision=REFERENCE_REVISION,
        device="cpu",
    )
    reference.max_seq_length = MAX_TOKENS
    reference_cases: list[dict[str, Any]] = []
    reference_vectors: dict[str, np.ndarray] = {}
    for case_id, text, role in CASES:
        full_text = PREFIXES[role] + text
        vector = reference.encode(
            [full_text],
            batch_size=1,
            show_progress_bar=False,
            convert_to_numpy=True,
            normalize_embeddings=True,
        )[0].astype(np.float64)
        if vector.shape != (384,):
            raise SystemExit(f"unexpected sentence-transformers shape: {vector.shape}")
        reference_vectors[case_id] = vector
        reference_cases.append(
            {
                "id": case_id,
                "text": text,
                "role": role,
                "vector": [float(value) for value in vector],
            }
        )

    per_case = {
        case_id: cosine(int8_vectors[case_id], reference_vectors[case_id])
        for case_id in int8_vectors
    }
    minimum = min(per_case.values())
    mean = sum(per_case.values()) / len(per_case)
    if minimum < REFERENCE_COSINE_MIN:
        print(
            f"warning: measured int8-vs-fp32 cosine minimum {minimum:.6f} "
            f"is below the configured {REFERENCE_COSINE_MIN:.2f}",
            file=sys.stderr,
        )

    reference_file = {
        "schema_version": SCHEMA_VERSION,
        "kind": "sentence-transformers-fp32-reference",
        "generated": dt.date.today().isoformat(),
        "model": {
            "id": REFERENCE_MODEL,
            "revision": REFERENCE_REVISION,
            "precision": "fp32",
            "library": "sentence-transformers",
        },
        "runtime": {
            "sentence_transformers": version("sentence-transformers"),
            "transformers": version("transformers"),
            "torch": version("torch"),
            "numpy": version("numpy"),
            "python": sys.version.split()[0],
        },
        "pipeline": {
            "prefix": PREFIXES,
            "max_seq_length": MAX_TOKENS,
            "pooling": "sentence-transformers default mean pooling over attention mask; verified to include BOS/EOS",
            "normalization": "L2 (normalize_embeddings=True)",
        },
        "comparison": {
            "cosine_int8_vs_fp32": {
                "min": minimum,
                "mean": mean,
                "per_case": per_case,
            },
            "tolerance": {
                "cosine_min": REFERENCE_COSINE_MIN,
                "note": (
                    "Loose quantization reference, not a C# correctness check. "
                    "The pinned int8 profile is compared against the fp32 base model; "
                    "the threshold is deliberately below 0.99 because the measured "
                    "int8 degradation is larger than that. Current fixture minimum is "
                    f"{minimum:.6f}; the {REFERENCE_COSINE_MIN:.3f} floor leaves a small margin."
                ),
            },
        },
        "cases": reference_cases,
    }
    write_json(output_dir / "reference_fp32.json", reference_file)
    print(
        f"wrote reference_fp32.json: {len(reference_cases)} cases; "
        f"int8-vs-fp32 cosine min={minimum:.6f} mean={mean:.6f}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
