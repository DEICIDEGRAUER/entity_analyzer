# ==================== main.py (исправленная версия) ====================
# QA BUG-P02: путь больше не захардкожен под Android — вычисляется из __file__,
# поэтому скрипт запускается на любой ОС из любого каталога.
import sys, os

# QA BUG-P01 (Blocker): при перенаправлении вывода (pipe/файл/CI) stdout
# получает системную кодировку (cp1252/cp1251), и эмодзи роняют print()
# с UnicodeEncodeError. Принудительный UTF-8 с заменой непечатаемых
# символов делает приложение пригодным для автоматизации.
if hasattr(sys.stdout, "reconfigure"):
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

PROJECT_DIR = os.path.dirname(os.path.abspath(__file__))
if PROJECT_DIR not in sys.path:
    sys.path.insert(0, PROJECT_DIR)
os.chdir(PROJECT_DIR)

import logging, hashlib
from concurrent.futures import ProcessPoolExecutor, as_completed
from pathlib import Path
from collections import Counter

from config import config
from entities import ALL_ENTITIES, ALL_ENTITY_NAMES, reload_entities
from processor import process_file, read_file_fast
from limits import apply_entity_limits
from density import get_density_ranking
from output import generate_reports
from noise_detector import noise_detector
from cache import IncrementalCache
from quality import quality
from filters import init_false_context_filter

PRESETS = {
    "1": {
        "name": "minimal",
        "description": "Только канонические имена, минимальный контекст (25 слов, 3 цитаты)",
        "emoji": "⚡"
    },
    "2": {
        "name": "light",
        "description": "Быстрый поиск с базовой генерацией (40 слов, 5 цитат)",
        "emoji": "💨"
    },
    "3": {
        "name": "medium",
        "description": "Стандартный анализ + fuzzy-поиск (60 слов, 8 цитат)",
        "emoji": "🔍"
    },
    "4": {
        "name": "extended",
        "description": "Расширенный поиск с семантикой (75 слов, 12 цитат) [ПО УМОЛЧАНИЮ]",
        "emoji": "🚀"
    },
    "5": {
        "name": "heavy",
        "description": "Глубокий анализ (100 слов, 20 цитат)",
        "emoji": "💪"
    },
    "6": {
        "name": "maximum",
        "description": "Максимальный охват (150 слов, 50 цитат)",
        "emoji": "🔥"
    },
    "7": {
        "name": "titanic",
        "description": "Полный анализ без ограничений (200 слов, безлимит цитат)",
        "emoji": "🏔️"
    },
    "8": {
        "name": "custom1",
        "description": "Пользовательский режим 1 (настраивается в config.yaml)",
        "emoji": "🛠️"
    },
    "9": {
        "name": "custom2",
        "description": "Пользовательский режим 2 (настраивается в config.yaml)",
        "emoji": "⚙️"
    },
    "10": {
        "name": "custom3",
        "description": "Пользовательский режим 3 (настраивается в config.yaml)",
        "emoji": "🔧"
    },
}

def show_menu():
    """Показывает интерактивное меню выбора режима."""
    print("\n" + "=" * 70)
    print("                    🔮 ENTITY ANALYZER v18 🔮")
    print("=" * 70)
    print("\n  ВЫБЕРИТЕ РЕЖИМ АНАЛИЗА:\n")
    
    for key, data in PRESETS.items():
        emoji = data["emoji"]
        name = data["name"].upper()
        desc = data["description"]
        print(f"  {key:>2}. {emoji} {name:<12} — {desc}")
    
    print("\n  0. ❌ Выход")
    print("\n" + "-" * 70)
    
    while True:
        choice = input("  Ваш выбор (0-10) [по умолчанию 4]: ").strip()
        if choice == "":
            choice = "4"
        if choice == "0":
            print("\n  👋 До встречи!\n")
            sys.exit(0)
        if choice in PRESETS:
            return PRESETS[choice]["name"]
        print(f"  ⚠️  Неверный выбор. Введите число от 0 до 10.")

def show_preset_info(preset_name: str):
    """Показывает информацию о выбранном пресете из config.yaml."""
    import yaml
    with open(os.path.join(PROJECT_DIR, "config.yaml"), 'r', encoding='utf-8') as f:
        data = yaml.safe_load(f)
    
    presets = data.get("presets", {})
    if preset_name in presets:
        p = presets[preset_name]
        print(f"\n  ┌─────────────────────────────────────────┐")
        print(f"  │ Режим: {preset_name.upper()}")
        print(f"  │ {p.get('description', '')}")
        print(f"  │ Итераций генерации: {p.get('iterations', 0)}")
        print(f"  │ Fuzzy-поиск: {'Да' if p.get('fuzzy') else 'Нет'}")
        print(f"  │ Семантическая фильтрация: {'Да' if p.get('sbert') else 'Нет'}")
        print(f"  │ Контекст: до {p.get('max_context_words', 75)} слов")
        print(f"  │ Цитат на файл: {p.get('max_mentions_per_file', 12) if p.get('max_mentions_per_file', 0) > 0 else 'без ограничений'}")
        print(f"  └─────────────────────────────────────────┘")

def process_one_file(file_path, cache_dir, entities_hash, config_hash=None):
    """Обрабатывает один файл, создавая локальный экземпляр кэша.

    QA BUG-P02: сбои кэша больше НЕ всплывают в результат файла —
    воркер работает без кэша, родитель агрегирует предупреждение.
    Индекс (index.json) из воркеров не пишется вовсе: записи регистрируются
    в памяти, сохранение — один раз в родителе после сбора результатов.
    """
    cache = None
    if cache_dir and config.cache_enabled:
        try:
            cache = IncrementalCache(cache_dir, config_hash=config_hash)
        except Exception:
            cache = None

    if cache:
        try:
            if cache.is_cached(file_path):
                r = cache.get(file_path)
                if r is not None:
                    r['_qc']['cache'] = 'hit'   # QA BUG-P05
                    return r
            r_qc_marker = 'miss'
        except Exception:
            cache = None
            r_qc_marker = None
    else:
        r_qc_marker = None

    r = process_file(file_path, entities_hash)

    if cache:
        try:
            # persist=False: blob пишется (он контентно-адресный и безопасен),
            # index.json из воркеров не трогаем — устраняет гонку os.replace.
            cache.set(file_path, r, persist=False)
            r_qc_marker = r_qc_marker or 'miss'
        except Exception:
            pass

    if r_qc_marker:
        r.setdefault('_qc', {})['cache'] = r_qc_marker
    return r

def parallel_process_files(files, total_files, entities_hash, config_hash=None):
    """Параллельная обработка файлов с использованием ProcessPoolExecutor."""
    results = []
    files_with_mentions = 0
    # QA BUG-P04: дедупликация однотипных ошибок — шторм «Permission denied»
    # заменяется первым сообщением + финальным счётчиком.
    error_counter = Counter()
    # Если max_workers <= 0, используем значение по умолчанию (None)
    max_workers = config.max_workers if config.max_workers > 0 else None
    with ProcessPoolExecutor(max_workers=max_workers) as executor:
        future_to_file = {executor.submit(process_one_file, f, config.cache_dir, entities_hash, config_hash): f for f in files}
        for i, future in enumerate(as_completed(future_to_file), 1):
            f = future_to_file[future]
            try:
                r = future.result()
            except Exception as e:
                error_counter[str(e).split(':')[0]] += 1
                r = {'file': Path(f).name, 'mentions': [], 'total_found': 0, 'density': 0.0}
            results.append(r)
            # QA BUG-P04: агрегация счётчиков качества из воркеров.
            qc = r.get('_qc') or {}
            if qc.get('cache') == 'hit':
                quality.log_cache_hit()
            elif qc.get('cache') == 'miss':
                quality.log_cache_miss()
            for entity, n in qc.get('match', {}).items():
                quality.m['per_entity'][entity]['found'] += n
                quality.m['total_matches_found'] += n
            for entity, n in qc.get('fp', {}).items():
                quality.m['per_entity'][entity]['false_positives'] += n
                quality.m['false_positives_removed'] += n
            for entity, n in qc.get('listing', {}).items():
                quality.m['per_entity'][entity]['listings_removed'] += n
                quality.m['listing_contexts_removed'] += n
            for entity, n in qc.get('valid', {}).items():
                quality.m['per_entity'][entity]['final'] += n
                quality.m['valid_mentions_final'] += n
            if r.get('mentions'):
                files_with_mentions += 1
                es = set(m['entity'] for m in r['mentions'])
                print(f"  [{i}/{total_files}] {r['file']}: {len(r['mentions'])}/{r['total_found']} d:{r.get('density',0):.1f}/10k [{', '.join(sorted(es))}]")

    # QA BUG-P04: агрегированное предупреждение вместо сотен строк.
    if error_counter:
        total_err = sum(error_counter.values())
        top = ', '.join(f"{msg} ×{n}" for msg, n in error_counter.most_common(3))
        print(f"⚠️ Ошибок обработки: {total_err} ({top})")
        print("   Кэш переведён в режим отказа: результаты получены без кэширования.")
    return results, files_with_mentions

def resolve_preset(arg: str):
    """QA-фикс: пресет можно передать аргументом CLI (python main.py light)."""
    if arg in PRESETS:
        return PRESETS[arg]['name']
    for v in PRESETS.values():
        if v['name'] == arg:
            return v['name']
    return None

def main():
    init_false_context_filter()
    preset_name = resolve_preset(sys.argv[1]) if len(sys.argv) > 1 else show_menu()
    if preset_name is None:
        # QA BUG-P09: неизвестный пресет — ненулевой код возврата для CI.
        print(f"❌ Неизвестный пресет: {sys.argv[1]}")
        sys.exit(2)
    show_preset_info(preset_name)
    # Применяем выбранный пресет к глобальному конфигу
    config.reload(preset_name)

    # Сбор файлов
        # QA-фикс: корпус можно переопределить переменной EA_CORPUS_DIR (автотесты).
    env_dir = os.environ.get('EA_CORPUS_DIR')
    corpus_dir = Path(env_dir or (config.corpus_dir if hasattr(config, 'corpus_dir') else config._data.get('corpus', {}).get('dir', '')))
    if not corpus_dir.exists():
        print(f"❌ Директория корпуса не найдена: {corpus_dir}")
        return
    extensions = config._data.get('corpus', {}).get('extensions', ['.txt'])
    files = []
    for ext in extensions:
        files.extend(corpus_dir.rglob(f'*{ext}') if config._data.get('corpus', {}).get('recursive', True) else corpus_dir.glob(f'*{ext}'))
    files = [str(f) for f in files if f.is_file()]

    # QA BUG-P08: опции max_file_size_mb и skip_duplicates объявлены в
    # config.yaml, но раньше не читались кодом.
    max_mb = config._data.get('corpus', {}).get('max_file_size_mb', 100) or 100
    skip_dups = config._data.get('corpus', {}).get('skip_duplicates', True)
    seen_hashes = set()
    unique_files = []
    skipped_size = skipped_dupes = 0
    for fp in files:
        try:
            if os.path.getsize(fp) > max_mb * 1024 * 1024:
                skipped_size += 1
                continue
            if skip_dups:
                with open(fp, 'rb') as fh:
                    h = hashlib.md5(fh.read()).hexdigest()
                if h in seen_hashes:
                    skipped_dupes += 1
                    continue
                seen_hashes.add(h)
        except OSError:
            continue
        unique_files.append(fp)
    files = unique_files
    if skipped_size or skipped_dupes:
        print(f"🧹 Отфильтровано при сборе: слишком большие {skipped_size}, дубликаты {skipped_dupes}")

    print(f"📂 Найдено файлов: {len(files)}")
    if not files:
        print("Нет файлов для обработки.")
        return

    # Хэш конфигурации с учётом пресета
    config_hash = None
    if config.invalidate_on_config_change:
        try:
            with open(os.path.join(PROJECT_DIR, 'config.yaml'), 'rb') as f:
                content = f.read()
            # Добавляем имя пресета в хэш, чтобы кэш инвалидировался при смене пресета
            config_hash = hashlib.md5(content + preset_name.encode()).hexdigest()
        except:
            pass
    # QA BUG-P07: хэш конфига в окружении — воркеры подхватывают шумовой профиль.
    if config_hash:
        os.environ['EA_CONFIG_HASH'] = config_hash

    # Обработка
    # QA BUG-P07: антишумовой профиль реально строится до пула процессов
    # и кэшируется на диск — воркеры подхватывают его через load_cache.
    sample_files = files[:30]
    if not (config_hash and noise_detector.load_cache(config.cache_dir, sample_files, config_hash)):
        sample_texts = []
        for sf in sample_files:
            try:
                with open(sf, 'r', encoding='utf-8', errors='ignore') as fh:
                    sample_texts.append(fh.read(20000))
            except OSError:
                pass
        noise_detector.analyze_all(ALL_ENTITIES, sample_texts)
        try:
            noise_detector.save_cache(config.cache_dir, sample_files, config_hash)
        except Exception:
            pass

    # QA-фикс: entities_hash раньше не вычислялся вовсе (в кэш-ключ уходил
    # config_hash). Считаем отпечаток словаря вариантов сущностей.
    entities_hash = hashlib.md5(
        ''.join(n + '|' + ','.join(sorted(ALL_ENTITIES[n]))
                for n in sorted(ALL_ENTITIES)).encode('utf-8')
    ).hexdigest()

    results, files_with_mentions = parallel_process_files(files, len(files), entities_hash, config_hash)

    # QA BUG-P02: индекс кэша сохраняет ТОЛЬКО родитель — один писатель,
    # ноль гонок os.replace. Воркеры писали только контентные blob-файлы.
    if config.cache_enabled and config.cache_dir:
        try:
            parent_cache = IncrementalCache(config.cache_dir, config_hash=config_hash)
            registered = 0
            for fp, r in zip(files, results):
                if not r.get('error'):
                    parent_cache.register(fp)
                    registered += 1
            parent_cache.save_index()
        except Exception as e:
            print(f"⚠️ Индекс кэша не сохранён (результаты не затронуты): {e}")

    # QA BUG-P06: лимиты на сущность применяются к результатам.
    results = apply_entity_limits(results)
    
    # Статистика
    stats = {
        'total': sum(len(r.get('mentions', [])) for r in results),
        'files': files_with_mentions,
        'unique': len(set(m['entity'] for r in results for m in r.get('mentions', []))),
        'entities': Counter(m['entity'] for r in results for m in r.get('mentions', []))
    }
    density_top10 = get_density_ranking(results)
    
    # Отчёты
    quality_report = quality.get_report() if config._data.get('quality', {}).get('save_quality_report', True) else None

    # QA BUG-P09: discovery больше не мёртвая ветка — вызывается при включении.
    discovered = {}
    if config.discovery_enabled and results:
        try:
            from discovery import EntityDiscovery
            discovered = EntityDiscovery().discover_from_files(results, ALL_ENTITIES)
        except Exception as e:
            print(f"⚠️ Discovery пропущен: {e}")

    generate_reports(results, stats, density_top10, quality_report, discovered)
    if quality_report:
        quality.save(config.output_dir)
    
    print(f"\n✅ Готово! Найдено упоминаний: {stats['total']}, файлов с упоминаниями: {stats['files']}")

if __name__ == "__main__":
    main()
