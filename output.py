import os, sys, json
from pathlib import Path
from collections import defaultdict
from datetime import datetime
from typing import List, Dict
from functools import lru_cache
from config import config

try:
    from sentence_transformers import SentenceTransformer, util
    HAS_SBERT = True
except ImportError:
    HAS_SBERT = False

THEMES = {
    'Мифологическое описание': [
        'бог', 'паук', 'живёт', 'обитает', 'является', 'выглядит', 'описание',
        'spider', 'god', 'living', 'appears', 'dwells', 'form', 'body', 'head',
        'создание', 'существо', 'облик', 'обитель', 'легенда', 'миф'
    ],
    'Ритуал и призывание': [
        'призыв', 'ритуал', 'заклинание', 'жертва', 'алтарь', 'summon', 'ritual',
        'sacrifice', 'offering', 'invoke', 'conjure', 'заклинаю', 'вызов',
        'круг', 'пентаграмма', 'печать', 'sigil'
    ],
    'Художественный сюжет': [
        'сказал', 'пошёл', 'увидел', 'ответил', 'said', 'went', 'saw', 'replied',
        'heard', 'думал', 'чувствовал', 'stood', 'looked', 'walked', 'cried',
        'рассказ', 'повесть', 'история'
    ],
    'Энциклопедическая статья': [
        'НАЗВАНИЕ:', 'ИСТОЧНИК:', 'http', 'Copyright', 'Contents', 'See also',
        'References', 'статья', 'энциклопедия', 'определение', 'термин',
        'происхождение', 'origin', 'history'
    ],
    'Гримуар': [
        'Sigil', 'Gate', 'Врата', 'Печать', 'заклинаю', 'invoke', 'conjure',
        'книга', 'book', 'necronomicon', 'манускрипт', 'свиток', 'parchment',
        'гримуар', 'grimoire'
    ]
}

def classify_context(context: str) -> str:
    """QA BUG-P01: тематическая классификация контекста по ключевым
    словам THEMES (аналог ThemeClassifier из .NET-версии). Раньше функция
    вызывалась, но не была определена — NameError ронял отчёты."""
    ctx = (context or '').lower()
    best_theme, best_score = None, 0
    for theme, keywords in THEMES.items():
        score = sum(1 for kw in keywords if kw and kw.lower() in ctx)
        if score > best_score:
            best_theme, best_score = theme, score
    return best_theme or 'Художественный сюжет'

def generate_reports(results: List[Dict], stats: Dict, density_top10: List, quality_report: dict, discovered: dict):
    if 'txt' in config.output_formats:
        _generate_main_report(results, stats, density_top10, quality_report)
    if 'json' in config.output_formats:
        _generate_main_json(results, stats, density_top10)
    if config.per_entity_folders:
        _generate_per_entity_folders(results)

def _generate_main_report(results, stats, density_top10, quality_report):
    p = Path(config.output_dir) / "entities_report.txt"
    results.sort(key=lambda x: x['file'])
    with open(p, 'w', encoding='utf-8') as f:
        f.write(f"ENTITIES MENTIONS ANALYSIS\nDate: {datetime.now().strftime('%Y-%m-%d %H:%M')}\nTotal: {stats['total']} | Files: {stats['files']} | Entities: {stats['unique']}\n\n")
        if quality_report:
            q = quality_report['summary']
            f.write(f"QUALITY: efficiency={q['filter_efficiency']} S/N={q['signal_noise']} cache={q['cache_hits']}h/{q['cache_misses']}m\n\n")
        f.write("ENTITIES:\n")
        for e, c in stats['entities'].most_common(): f.write(f"  {e}: {c}\n")
        f.write("\nDENSITY TOP 10:\n")
        for i, (n, d, m, ch) in enumerate(density_top10, 1): f.write(f"  {i}. {n}: {d:.2f} ({m}m/{ch}c)\n")
        f.write("\n" + "="*60 + "\n\n")
        for fd in results:
            if not fd.get('mentions'): continue
            f.write(f"FILE: {fd['file']}\nMentions: {len(fd['mentions'])}/{fd.get('total_found',0)} | d:{fd.get('density',0):.1f}/10k\n\n")
            for m in fd['mentions']:
                f.write(f"[{m['num']}] {m['entity']} ({m['var']}) L{m['line']}\n{m['context']}\n\n")
            f.write("---\n\n")
    print(f"  Main report: {p}")

def _generate_main_json(results, stats, density_top10):
    p = Path(config.output_dir) / "entities_data.json"
    results.sort(key=lambda x: x['file'])
    data = {
        'stats': {
            'total': stats['total'], 'files': stats['files'], 'entities': stats['unique'],
            'entities_dist': dict(stats['entities'].most_common()),
            'density_top10': [{'name':n,'density':d,'mentions':m,'chars':c} for n,d,m,c in density_top10]
        },
        'files': [{
            'name': r['file'], 'shown': len(r.get('mentions', [])),
            'total': r.get('total_found',0), 'density': r.get('density',0),
            'mentions': r.get('mentions', [])
        } for r in results if r.get('mentions')]
    }
    json.dump(data, open(p,'w',encoding='utf-8'), ensure_ascii=False, indent=2)
    print(f"  JSON: {p}")

def _generate_per_entity_folders(results):
    base = Path(config.output_dir) / "by_entity"
    base.mkdir(exist_ok=True)
    by_entity = defaultdict(list)
    for r in results:
        for m in r.get('mentions', []):
            by_entity[m['entity']].append({'file': r['file'], 'mention': m})

    for entity_name, mentions in by_entity.items():
        safe = entity_name.replace('/','_').replace(' ','_').replace('(','').replace(')','')
        edir = base / safe
        edir.mkdir(exist_ok=True)

        with open(edir/f"{safe}_mentions.txt",'w',encoding='utf-8') as f:
            f.write(f"ENTITY: {entity_name}\nTotal: {len(mentions)}\nFiles: {len(set(m['file'] for m in mentions))}\n\n")
            cur = None
            for item in mentions:
                if item['file'] != cur:
                    cur = item['file']
                    f.write(f"\n--- {cur} ---\n\n")
                m = item['mention']
                f.write(f"[{m['num']}] {m['var']} L{m['line']}\n{m['context']}\n\n")

        theme_mentions = defaultdict(list)
        for item in mentions:
            theme = classify_context(item['mention']['context'])
            theme_mentions[theme].append(item)

        for theme, items in theme_mentions.items():
            theme_safe = theme.replace(' ', '_')
            themedir = edir / theme_safe
            themedir.mkdir(exist_ok=True)
            with open(themedir / f"{safe}_{theme_safe}.txt", 'w', encoding='utf-8') as f:
                f.write(f"Тема: {theme}\nУпоминаний: {len(items)}\n\n")
                cur = None
                for item in items:
                    if item['file'] != cur:
                        cur = item['file']
                        f.write(f"\n--- {cur} ---\n\n")
                    m = item['mention']
                    f.write(f"[{m['num']}] {m['var']} L{m['line']}\n{m['context']}\n\n")

        print(f"  {entity_name}: {len(mentions)} mentions -> {edir}")
