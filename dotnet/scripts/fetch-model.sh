#!/usr/bin/env bash
# Explicitly download and verify ONNX model files for Entity Analyzer.
# The model is never committed and is never downloaded automatically at runtime.

set -euo pipefail

usage() {
    cat <<'EOF'
Usage: fetch-model.sh [options]

  -Model <name>          Model profile from models/manifest.json (default: e5-small)
  -Precision <value>     Precision (default: int8)
  -Destination <path>    Target directory (default: models/e5-small-int8)
  -RecordHashes          Record missing sizes/SHA-256 in manifest.json and README.md
  -Verify                Verify downloaded files offline; no network
  -Force                 Re-download every file

The script downloads to a temporary .part file, verifies size and SHA-256,
then atomically renames it into place. A hash mismatch removes the .part file.
EOF
}

die() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

info() {
    printf '%s\n' "$*"
}

MODEL="e5-small"
PRECISION="int8"
DESTINATION=""
RECORD=0
VERIFY=0
FORCE=0

while [ "$#" -gt 0 ]; do
    case "$1" in
        -Model|--model)
            [ "$#" -ge 2 ] || die "Missing value for $1"
            MODEL="$2"; shift 2 ;;
        -Precision|--precision)
            [ "$#" -ge 2 ] || die "Missing value for $1"
            PRECISION="$2"; shift 2 ;;
        -Destination|--destination)
            [ "$#" -ge 2 ] || die "Missing value for $1"
            DESTINATION="$2"; shift 2 ;;
        -RecordHashes|--record-hashes)
            RECORD=1; shift ;;
        -Verify|--verify)
            VERIFY=1; shift ;;
        -Force|--force)
            FORCE=1; shift ;;
        -h|--help)
            usage; exit 0 ;;
        *)
            die "Unknown argument: $1 (use --help)" ;;
    esac
done

if [ "$VERIFY" -eq 1 ] && { [ "$RECORD" -eq 1 ] || [ "$FORCE" -eq 1 ]; }; then
    die "-Verify cannot be combined with -RecordHashes or -Force."
fi

SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
REPO_ROOT="$(dirname -- "$SCRIPT_DIR")"
MANIFEST="$REPO_ROOT/models/manifest.json"
README="$REPO_ROOT/models/README.md"

[ -f "$MANIFEST" ] || die "Manifest not found: $MANIFEST"
[ -f "$README" ] || die "README not found: $README"

json_string() {
    # First scalar string value for a key in the single-profile manifest.
    sed -n "s/^[[:space:]]*\"$1\"[[:space:]]*:[[:space:]]*\"\\([^\"]*\\)\".*/\\1/p" "$MANIFEST" | head -n 1
}

grep -Eq "\"$MODEL\"[[:space:]]*:[[:space:]]*\\{" "$MANIFEST" \
    || die "Unknown model profile '$MODEL'. See models/manifest.json."

MODEL_PRECISION="$(json_string precision)"
REVISION="$(json_string revision)"
SOURCE="$(json_string source)"
LICENSE="$(json_string license)"
DEFAULT_DESTINATION="$(json_string destination)"

[ "$MODEL_PRECISION" = "$PRECISION" ] \
    || die "Profile '$MODEL' has precision '$MODEL_PRECISION', not '$PRECISION'."
[ -n "$REVISION" ] && [ -n "$SOURCE" ] && [ -n "$LICENSE" ] \
    || die "Profile '$MODEL' is missing source, revision, or license."

if [ -z "$DESTINATION" ]; then
    DESTINATION="${DEFAULT_DESTINATION:-models/$MODEL-$PRECISION}"
fi
case "$DESTINATION" in
    /*) ;;
    *) DESTINATION="$REPO_ROOT/$DESTINATION" ;;
esac

FILES_LIST=""
RECORDS=""
PART=""
cleanup() {
    [ -n "${FILES_LIST:-}" ] && rm -f "$FILES_LIST"
    [ -n "${RECORDS:-}" ] && rm -f "$RECORDS"
    [ -n "${PART:-}" ] && rm -f "$PART"
    return 0
}
trap cleanup EXIT

FILES_LIST="$(mktemp)"
RECORDS="$(mktemp)"

# Parse the pretty-printed manifest into: relative-path<TAB>size<TAB>sha256.
# The manifest is intentionally kept in a stable, human-readable JSON layout.
awk '
    /"path"[[:space:]]*:/ {
        line=$0
        sub(/^.*"path"[[:space:]]*:[[:space:]]*"/, "", line)
        sub(/".*$/, "", line)
        path=line; size=""; hash=""
    }
    /"size"[[:space:]]*:/ {
        line=$0
        sub(/^.*"size"[[:space:]]*:[[:space:]]*/, "", line)
        sub(/[,].*$/, "", line)
        gsub(/[^0-9]/, "", line)
        size=line
    }
    /"sha256"[[:space:]]*:/ {
        line=$0
        sub(/^.*"sha256"[[:space:]]*:[[:space:]]*/, "", line)
        sub(/[,].*$/, "", line)
        gsub(/"/, "", line)
        if (line == "null") line=""
        if (path != "") {
            print path "\t" size "\t" line
            path=""
        }
    }
' "$MANIFEST" > "$FILES_LIST"

[ -s "$FILES_LIST" ] || die "No files found in $MANIFEST"

sha256_file() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | awk '{ print tolower($1) }'
    elif command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$1" | awk '{ print tolower($1) }'
    else
        die "sha256sum or shasum is required."
    fi
}

file_size() {
    wc -c < "$1" | tr -d '[:space:]'
}

download_file() {
    local url="$1"
    local part="$2"
    command -v curl >/dev/null 2>&1 || die "curl is required for downloads."
    rm -f "$part"
    if ! curl --fail --location --retry 3 --retry-delay 2 --silent --show-error \
        --output "$part" "$url"; then
        rm -f "$part"
        die "Download failed: $url"
    fi
}

validate_relative_path() {
    local path="$1"
    case "$path" in
        /*|*..*|*\\*) die "Unsafe file path in manifest: '$path'" ;;
    esac
}

info "Model:       $MODEL ($PRECISION)"
info "Revision:    $REVISION"
info "Source:      $SOURCE"
info "Destination: $DESTINATION"
if [ "$VERIFY" -eq 1 ]; then
    info "Mode:        verify"
elif [ "$RECORD" -eq 1 ]; then
    info "Mode:        record-hashes"
else
    info "Mode:        fetch"
fi
info ""

verified=0
while IFS=$'\t' read -r relative expected_size expected_hash; do
    [ -n "$relative" ] || die "Empty path in manifest."
    validate_relative_path "$relative"

    expected_hash="$(printf '%s' "$expected_hash" | tr 'A-F' 'a-f')"
    if [ -n "$expected_hash" ]; then
        [ "${#expected_hash}" -eq 64 ] || die "Invalid SHA-256 for $relative: $expected_hash"
        case "$expected_hash" in
            *[!0-9a-f]*) die "Invalid SHA-256 for $relative: $expected_hash" ;;
        esac
    fi
    [ "$expected_size" -ge 0 ] 2>/dev/null || die "Invalid size for $relative: $expected_size"

    target="$DESTINATION/$relative"
    mkdir -p "$(dirname -- "$target")"
    exists=0
    [ -f "$target" ] && exists=1

    if [ "$VERIFY" -eq 1 ]; then
        [ "$exists" -eq 1 ] || die "Missing file: $target"
        [ -n "$expected_hash" ] || die "SHA-256 is not pinned for $relative. Run -RecordHashes in a controlled environment."
        actual_size="$(file_size "$target")"
        actual_hash="$(sha256_file "$target")"
        [ "$expected_size" -eq 0 ] || [ "$actual_size" -eq "$expected_size" ] \
            || die "Size mismatch for $relative: expected $expected_size bytes, got $actual_size bytes."
        [ "$actual_hash" = "$expected_hash" ] \
            || die "SHA-256 mismatch for $relative: expected $expected_hash, got $actual_hash."
        info "OK          $relative"
        verified=$((verified + 1))
        printf '%s\t%s\t%s\n' "$relative" "$actual_size" "$actual_hash" >> "$RECORDS"
        continue
    fi

    if [ -z "$expected_hash" ] && [ "$RECORD" -eq 0 ]; then
        die "SHA-256 is not pinned for $relative. Run -RecordHashes in a controlled environment."
    fi

    if [ "$exists" -eq 1 ] && [ "$FORCE" -eq 0 ]; then
        actual_size="$(file_size "$target")"
        actual_hash="$(sha256_file "$target")"
        if [ -n "$expected_hash" ]; then
            [ "$expected_size" -eq 0 ] || [ "$actual_size" -eq "$expected_size" ] \
                || die "Size mismatch for $relative: expected $expected_size bytes, got $actual_size bytes. Run -Force to re-download."
            [ "$actual_hash" = "$expected_hash" ] \
                || die "SHA-256 mismatch for $relative: expected $expected_hash, got $actual_hash. Run -Force to re-download."
        else
            [ "$expected_size" -eq 0 ] || [ "$actual_size" -eq "$expected_size" ] \
                || die "Size mismatch for $relative: expected $expected_size bytes, got $actual_size bytes. Run -Force to re-download."
        fi
        if [ "$RECORD" -eq 1 ]; then
            printf '%s\t%s\t%s\n' "$relative" "$actual_size" "$actual_hash" >> "$RECORDS"
        fi
        info "OK          $relative"
        verified=$((verified + 1))
        continue
    fi

    url="https://huggingface.co/$SOURCE/resolve/$REVISION/$relative"
    PART="$target.part"
    info "Downloading $relative"
    download_file "$url" "$PART"

    actual_size="$(file_size "$PART")"
    actual_hash="$(sha256_file "$PART")"
    if [ "$expected_size" -gt 0 ] && [ "$actual_size" -ne "$expected_size" ]; then
        rm -f "$PART"
        PART=""
        die "Size mismatch for $relative: expected $expected_size bytes, got $actual_size bytes."
    fi
    if [ -n "$expected_hash" ] && [ "$actual_hash" != "$expected_hash" ]; then
        rm -f "$PART"
        PART=""
        die "SHA-256 mismatch for $relative: expected $expected_hash, got $actual_hash."
    fi
    if [ -z "$expected_hash" ] && [ "$RECORD" -eq 0 ]; then
        rm -f "$PART"
        PART=""
        die "SHA-256 is not pinned for $relative. Run -RecordHashes in a controlled environment."
    fi

    mv -f "$PART" "$target"
    PART=""
    if [ "$RECORD" -eq 1 ]; then
        printf '%s\t%s\t%s\n' "$relative" "$actual_size" "$actual_hash" >> "$RECORDS"
    fi
    info "OK          $relative"
    verified=$((verified + 1))
done < "$FILES_LIST"

update_manifest() {
    local lookup="$1"
    local tmp
    tmp="$(mktemp)"
    awk -F '\t' -v OFS='\t' '
        NR==FNR {
            sizes[$1]=$2
            hashes[$1]=$3
            next
        }
        /"path"[[:space:]]*:/ {
            line=$0
            sub(/^.*"path"[[:space:]]*:[[:space:]]*"/, "", line)
            sub(/".*$/, "", line)
            current=line
            inblock=1
        }
        inblock && /"size"[[:space:]]*:/ {
            line=$0
            sub(/"size"[[:space:]]*:[[:space:]]*[0-9]+/, "\"size\": " sizes[current], line)
            print line
            next
        }
        inblock && /"sha256"[[:space:]]*:/ {
            value=hashes[current]
            if (value == "") value="null"
            else value="\"" value "\""
            line=$0
            sub(/"sha256"[[:space:]]*:[[:space:]]*("([^"]*)"|null)/, "\"sha256\": " value, line)
            print line
            next
        }
        inblock && /^[[:space:]]*}/ { inblock=0 }
        { print }
    ' "$lookup" "$MANIFEST" > "$tmp"
    mv "$tmp" "$MANIFEST"
}

write_readme_table() {
    local rows="$1"
    local block tmp mib
    block="$(mktemp)"
    {
        printf '%s\n' '<!-- BEGIN MODEL HASHES -->'
        printf '%s\n' '| Файл | Размер | SHA-256 | Лицензия | Источник |'
        printf '%s\n' '|------|-------:|---------|----------|----------|'
        while IFS=$'\t' read -r path size hash; do
            [ -n "$hash" ] || hash='pending'
            if [ "$size" -ge 1048576 ]; then
                mib="$(awk -v b="$size" 'BEGIN { printf "%.1f", b/1048576 }')"
                printf '| `%s` | %s B (%s MiB) | `%s` | %s | `%s @ %s` |\n' \
                    "$path" "$size" "$mib" "$hash" "$LICENSE" "$SOURCE" "$REVISION"
            else
                printf '| `%s` | %s B | `%s` | %s | `%s @ %s` |\n' \
                    "$path" "$size" "$hash" "$LICENSE" "$SOURCE" "$REVISION"
            fi
        done < "$rows"
        printf '%s\n' '<!-- END MODEL HASHES -->'
    } > "$block"

    grep -q '<!-- BEGIN MODEL HASHES -->' "$README" \
        || die "BEGIN marker missing in $README"
    grep -q '<!-- END MODEL HASHES -->' "$README" \
        || die "END marker missing in $README"

    tmp="$(mktemp)"
    awk -v blockfile="$block" '
        /<!-- BEGIN MODEL HASHES -->/ {
            while ((getline line < blockfile) > 0) print line
            close(blockfile)
            skip=1
            next
        }
        /<!-- END MODEL HASHES -->/ { skip=0; next }
        !skip { print }
    ' "$README" > "$tmp"
    mv "$tmp" "$README"
    rm -f "$block"
}

if [ "$RECORD" -eq 1 ]; then
    update_manifest "$RECORDS"
    write_readme_table "$RECORDS"
    info ""
    info "Recorded hashes and updated $MANIFEST and $README"
elif [ "$VERIFY" -eq 1 ]; then
    info ""
    info "Verified $verified file(s) in $DESTINATION"
else
    info ""
    info "Fetched $verified file(s) into $DESTINATION"
fi
