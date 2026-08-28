import os, sys, re
from difflib import SequenceMatcher
from collections import defaultdict
from typing import Dict, List
from config import config
from itertools import combinations

ENGLISH_COMMON = {'the','and','for','from','with','that','this','have','been','book','text','section','chapter','part','volume','page','origin','see','also','note','edition','press','published','though','place','family','darkness','cal','encou','dar','ceremonia','madam','origin','ест','этот','книгу'}

class EntityDiscovery:
    def __init__(self):
        self.cooccurrence = defaultdict(lambda: defaultdict(int))

    def discover(self, text: str, known: Dict[str, List[str]]) -> Dict[str, List[str]]:
        candidates = set(re.findall(r'\b[А-ЯA-Z][а-яa-z]{2,}(?:[ -][А-ЯA-Z][а-яa-z]{2,})*\b', text))
        discovered = defaultdict(list)
        for c in candidates:
            if len(c) < config.discovery_min_entity_length: continue
            if c.lower() in ENGLISH_COMMON: continue
            for ename, variants in known.items():
                for v in variants:
                    if config.discovery_similarity_threshold <= SequenceMatcher(None, c.lower(), v.lower()).ratio() < 1.0:
                        discovered[ename].append(c); break
        return {k: list(set(v)) for k, v in discovered.items()}

    def discover_from_files(self, results: list, known: Dict[str, List[str]]) -> Dict[str, List[str]]:
        all_d = defaultdict(list)
        # Строим матрицу совместной встречаемости
        self.cooccurrence.clear()
        for r in results:
            entities_in_file = {m['entity'] for m in r.get('mentions', [])}
            for e1, e2 in combinations(entities_in_file, 2):
                self.cooccurrence[e1][e2] += 1
                self.cooccurrence[e2][e1] += 1
            # Поиск новых имён
            for m in r.get('mentions', [])[:5]:
                for ename, variants in self.discover(m.get('context', ''), known).items():
                    all_d[ename].extend(variants)
        return {k: list(set(v)) for k, v in all_d.items()}

    def get_cooccurrence(self, entity: str, top_n: int = 10) -> list:
        if entity not in self.cooccurrence:
            return []
        return sorted(self.cooccurrence[entity].items(), key=lambda x: x[1], reverse=True)[:top_n]

discovery = EntityDiscovery()
