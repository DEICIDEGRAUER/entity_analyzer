import os, sys
from collections import defaultdict
from typing import List, Dict
from config import config
from entities import ALL_ENTITY_NAMES

def apply_entity_limits(all_results: List[Dict]) -> List[Dict]:
    # QA BUG-P06: config.get_max_mentions не существовал — функция падала бы
    # при первом же вызове. Лимиты читаются из config.yaml: entity_limits.
    entity_limits_cfg = config._data.get('entity_limits', {}) or {}
    limits = {}
    for ename in ALL_ENTITY_NAMES:
        max_t = entity_limits_cfg.get(ename, 0)
        if isinstance(max_t, int) and max_t > 0:
            limits[ename] = max_t

    if not limits:
        print("  No entity limits configured")
        return all_results

    for entity_name, max_total in limits.items():
        mentions = []
        for fr in all_results:
            for m in fr.get('mentions', []):
                if m.get('entity') == entity_name:
                    mentions.append((fr, m))

        total_found = len(mentions)
        if total_found <= max_total:
            print(f"  {entity_name}: {total_found} mentions (limit {max_total}, no cuts)")
            continue

        by_file = defaultdict(list)
        for fr, m in mentions:
            by_file[fr['file']].append((fr, m))

        guaranteed = [items[0] for items in by_file.values()]
        remaining = [item for items in by_file.values() for item in items[1:]]

        if len(guaranteed) >= max_total:
            selected = guaranteed[:max_total]
        else:
            selected = guaranteed + remaining[:max_total - len(guaranteed)]

        keep_ids = {id(m) for _, m in selected}

        removed = 0
        for fr in all_results:
            new_mentions = []
            for m in fr.get('mentions', []):
                if m.get('entity') != entity_name or id(m) in keep_ids:
                    new_mentions.append(m)
                else:
                    removed += 1
            if len(new_mentions) != len(fr.get('mentions', [])):
                fr['mentions'] = new_mentions
                for i, m in enumerate(fr['mentions'], 1):
                    m['num'] = i

        print(f"  {entity_name}: {total_found} -> {len(selected)} (removed {removed}, limit {max_total})")

    return all_results
