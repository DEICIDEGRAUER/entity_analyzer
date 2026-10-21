import os, sys
from pathlib import Path
from typing import Dict, List, Set
from config import config
from translator import translator
from name_generator import name_generator
from noise_detector import noise_detector
import ahocorasick





# Глобальные переменные для сущностей

def build_entity_variants() -> Dict[str, List[str]]:
    """Строит словарь вариантов для всех сущностей на основе текущего конфига."""
    entities = {}
    for entity_name, data in config.entities.items():
        vs: Set[str] = set()
        canon = data.get('canon', entity_name)
        irregular = data.get('irregular', [])
        titles = data.get('titles', [])
        is_common = noise_detector.is_common_word_entity(entity_name)

        narrow, wide = translator.generate_two_pass(canon, irregular, is_common)
        vs.update(narrow)
        vs.update(wide)
        vs.update(titles)

        # Дополнительные варианты от name_generator
        name_gen_variants = name_generator.generate(canon)
        vs.update(name_gen_variants)

        lowercase = {v.lower() for v in vs if v and v[0].isupper()}
        vs.update(lowercase)
        vs.update({v[0].lower() + v[1:] if v else v for v in vs if v})
        entities[entity_name] = [v for v in vs if v and len(v) >= 3]

    custom_dir = Path(config.output_dir) / "custom_variants"
    if custom_dir.exists():
        for entity_name in entities:
            safe = entity_name.replace('/', '_').replace(' ', '_')
            custom_file = custom_dir / f"{safe}.txt"
            if custom_file.exists():
                with open(custom_file, encoding='utf-8') as f:
                    custom_vars = [line.strip() for line in f if line.strip()]
                entities[entity_name].extend(custom_vars)
                seen = set()
                uniq = []
                for v in entities[entity_name]:
                    if v not in seen:
                        seen.add(v)
                        uniq.append(v)
                entities[entity_name] = uniq

    out_dir = Path(config.output_dir) / "variants"
    out_dir.mkdir(parents=True, exist_ok=True)
    for ename, vlist in entities.items():
        safe = ename.replace('/', '_').replace(' ', '_')
        with open(out_dir / f"{safe}.txt", 'w', encoding='utf-8') as f:
            f.write('\n'.join(sorted(vlist)))

    return entities


ALL_ENTITIES = build_entity_variants()
ALL_ENTITY_NAMES = set(ALL_ENTITIES.keys())
VARIANT_TO_ENTITY = {}

# Автомат Ахо-Корасик для быстрого поиска
AUTOMATON = None

def build_automaton():
    """Строит автомат Ахо-Корасик на основе текущих сущностей."""
    global AUTOMATON
    automaton = ahocorasick.Automaton()
    for entity_name, variants in ALL_ENTITIES.items():
        if noise_detector.should_skip_entity(entity_name):
            continue
        for variant in variants:
            if variant:
                automaton.add_word(variant.lower(), (entity_name, variant))
    automaton.make_automaton()
    AUTOMATON = automaton
    return AUTOMATON

def rebuild_automaton():
    """Перестраивает автомат (вызывать после изменения сущностей)."""
    build_automaton()

# Инициализируем автомат при первом импорте
build_automaton()
for en, vs in ALL_ENTITIES.items():
    for v in vs:
        if v:
            VARIANT_TO_ENTITY[v.lower()] = en

def get_entity_name(variant: str) -> str:
    return VARIANT_TO_ENTITY.get(variant.lower(), variant)

def reload_entities():
    """Обновляет глобальные переменные сущностей после смены конфига."""
    global ALL_ENTITIES, ALL_ENTITY_NAMES, VARIANT_TO_ENTITY
    ALL_ENTITIES = build_entity_variants()
    ALL_ENTITY_NAMES = set(ALL_ENTITIES.keys())
    VARIANT_TO_ENTITY = {}
    for en, vs in ALL_ENTITIES.items():
        for v in vs:
            if v:
                VARIANT_TO_ENTITY[v.lower()] = en
