import os, sys, re
from typing import Set
from normalizer import normalizer
from noise_detector import noise_detector
from config import config

try:
    from sentence_transformers import SentenceTransformer, util
    HAS_SBERT_FILTERS = True
except ImportError:
    HAS_SBERT_FILTERS = False

MYTH_SIGNAL_WORDS = {
    'бог', 'древний', 'великий', 'внешний', 'ужас', 'культ', 'лавкрафт',
    'ктулху', 'некрономикон', 'миф', 'хаос', 'бездна', 'врата', 'демон',
    'god', 'ancient', 'great', 'old', 'elder', 'cthulhu', 'mythos', 'demon'
}

# Векторный индекс ложных контекстов
_false_context_embeddings = {}
_false_context_model = None

def init_false_context_filter():
    global _false_context_model
    if HAS_SBERT_FILTERS and _false_context_model is None:
        _false_context_model = SentenceTransformer('all-MiniLM-L6-v2')

def add_false_context(entity_name: str, context: str):
    if not HAS_SBERT_FILTERS or _false_context_model is None:
        return
    emb = _false_context_model.encode(context, convert_to_tensor=True)
    if entity_name not in _false_context_embeddings:
        _false_context_embeddings[entity_name] = []
    _false_context_embeddings[entity_name].append(emb)
    if len(_false_context_embeddings[entity_name]) > 100:
        _false_context_embeddings[entity_name].pop(0)

from functools import lru_cache

@lru_cache(maxsize=10000)
def _get_context_embedding(context: str):
    """Возвращает эмбеддинг контекста с кэшированием."""
    if not HAS_SBERT_FILTERS or _false_context_model is None:
        return None
    return _false_context_model.encode(context, convert_to_tensor=True)

def is_similar_to_false_context(entity_name: str, context: str, threshold: float = 0.75) -> bool:
    if entity_name not in _false_context_embeddings or not _false_context_embeddings[entity_name]:
        return False
    if not HAS_SBERT_FILTERS or _false_context_model is None:
        return False
    ctx_emb = _get_context_embedding(context)
    if ctx_emb is None:
        return False
    for false_emb in _false_context_embeddings[entity_name]:
        score = util.cos_sim(ctx_emb, false_emb).item()
        if score > threshold:
            return True
    return False

def clean_pdf_artifacts(text: str) -> str:
    return normalizer.normalize(text)

def is_false_positive(variant: str, match_start: int, text: str) -> bool:
    if match_start > 0 and text[match_start - 1].isalpha(): return True
    if match_start + len(variant) < len(text) and text[match_start + len(variant)].isalpha(): return True
    return False

def is_broken_word(variant: str, match_start: int, text: str) -> bool:
    start = match_start
    end = match_start + len(variant)
    if start > 0 and (text[start-1].isalpha() or text[start-1] in "-'"):
        if text[start-1] == '-' or text[start-1] == "'":
            return False
        return True
    if end < len(text) and (text[end].isalpha() or text[end] in "-'"):
        if text[end] == '-' or text[end] == "'":
            return False
        return True
    return False

def evaluate_listing_density(context: str, mention_entity: str, all_entities: Set[str], current_variant: str = '') -> bool:
    ctx_lower = context.lower()
    other_entities = [e for e in all_entities if e.lower() != mention_entity.lower() and e.lower() in ctx_lower]
    other_count = len(other_entities)
    if other_count >= 4:
        return True
    if other_count >= 2:
        words = set(ctx_lower.split())
        if words.intersection(MYTH_SIGNAL_WORDS):
            return False
        return True
    return False

def is_likely_common_word_context(context: str, entity: str) -> bool:
    if not noise_detector.is_common_word_entity(entity):
        return True
    ctx_lower = context.lower()
    return any(word in ctx_lower for word in MYTH_SIGNAL_WORDS)

def validate_title_context(context: str, entity: str, title: str) -> bool:
    entity_data = config.entities.get(entity, {})
    entity_variants = entity_data.get('irregular', [])
    entity_titles = entity_data.get('titles', [])
    canon = entity_data.get('canon', '')
    all_markers = list(entity_variants) + list(entity_titles)
    if canon:
        all_markers.append(canon)
    all_markers = [m for m in all_markers if m.lower() != title.lower()]
    if not all_markers:
        return True
    ctx_lower = context.lower()
    for marker in all_markers:
        if marker.lower() in ctx_lower:
            return True
    return False
