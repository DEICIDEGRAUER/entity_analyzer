import os, sys, re, json
from typing import Dict, List
from pathlib import Path

COMMON_WORDS = {
    'грот','кот','тит','йод','муп','сон','мир','дом','рот','ток',
    'grot','cat','tit','iod','mup','son','mir','dom','rot','tok'
}

class NoiseDetector:
    def __init__(self):
        self.entity_profiles: Dict[str, dict] = {}

    def analyze_all(self, entities: Dict[str, List[str]], sample_texts: List[str]) -> None:
        import ahocorasick
        A = ahocorasick.Automaton()
        for entity_name, variants in entities.items():
            for v in variants:
                if v:
                    A.add_word(v.lower(), (entity_name, v))
        A.make_automaton()

        total_matches = {e: 0 for e in entities}
        false_positives = {e: 0 for e in entities}

        for text in sample_texts:
            text_lower = text.lower()
            for end_idx, (entity_name, variant_text) in A.iter(text_lower):
                start_idx = end_idx - len(variant_text) + 1
                total_matches[entity_name] += 1
                if (start_idx > 0 and text_lower[start_idx-1].isalpha()) or                    (end_idx + 1 < len(text_lower) and text_lower[end_idx+1].isalpha()):
                    false_positives[entity_name] += 1

        for entity_name, variants in entities.items():
            total = total_matches[entity_name]
            fp = false_positives[entity_name]
            noise = fp / max(total, 1)
            avg_length = sum(len(v) for v in variants) / max(len(variants), 1)
            is_common = any(v.lower() in COMMON_WORDS for v in variants)
            if is_common:
                noise = max(noise, 0.7)
            self.entity_profiles[entity_name] = {
                'entity_name': entity_name,
                'noise_score': noise,
                # QA BUG-P06: get_priority читает avg_variant_length —
                # раньше ключ не сохранялся и слагаемое всегда было 0.
                'avg_variant_length': round(avg_length, 2),
                'is_noisy': noise > 0.3 or avg_length < 5 or is_common,
                'needs_strict_boundary': avg_length < 5 or noise > 0.3,
                'total_test_matches': total,
                'false_positives': fp,
                'is_common_word': is_common
            }

    def should_skip_entity(self, name: str) -> bool:
        return self.entity_profiles.get(name, {}).get('noise_score', 0) > 0.8

    def get_priority(self, name: str) -> int:
        p = self.entity_profiles.get(name, {})
        return int((1 - p.get('noise_score', 0)) * 100 + p.get('avg_variant_length', 0) * 10)

    def is_common_word_entity(self, name: str) -> bool:
        return self.entity_profiles.get(name, {}).get('is_common_word', False)

    def save_cache(self, cache_dir: str, sample_files: list, config_hash: str):
        path = Path(cache_dir) / "noise_cache.json"
        data = {
            "config_hash": config_hash,
            "sample_files": sample_files,
            "profiles": self.entity_profiles
        }
        with open(path, 'w', encoding='utf-8') as f:
            json.dump(data, f, ensure_ascii=False, indent=2)

    def load_cache(self, cache_dir: str, sample_files: list, config_hash: str) -> bool:
        path = Path(cache_dir) / "noise_cache.json"
        if not path.exists():
            return False
        with open(path, 'r', encoding='utf-8') as f:
            data = json.load(f)
        if data.get("config_hash") == config_hash and data.get("sample_files") == sample_files:
            self.entity_profiles = data.get("profiles", {})
            return True
        return False

    def load_profile_fresh(self, cache_dir: str, config_hash: str) -> bool:
        """QA BUG-P07: загрузка профиля воркером — совпадение только config_hash.

        Прежний путь load_cache(dir, [], None) требовал равенства sample_files
        ([] != files[:30]) и всегда возвращал False: воркеры работали с пустыми
        профилями, фильтры общеупотребительных слов были отключены.
        """
        path = Path(cache_dir) / "noise_cache.json"
        if not config_hash or not path.exists():
            return False
        try:
            with open(path, 'r', encoding='utf-8') as f:
                data = json.load(f)
            if data.get("config_hash") == config_hash:
                self.entity_profiles = data.get("profiles", {})
                return bool(self.entity_profiles)
        except (OSError, ValueError):
            pass
        return False

noise_detector = NoiseDetector()
