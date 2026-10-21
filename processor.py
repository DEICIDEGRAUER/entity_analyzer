# ==================== processor.py ====================
import os, sys, re, time, pickle, hashlib
from pathlib import Path
from config import config
from entities import ALL_ENTITIES, ALL_ENTITY_NAMES, get_entity_name
from filters import (
    clean_pdf_artifacts, is_false_positive, evaluate_listing_density,
    is_broken_word, is_likely_common_word_context, validate_title_context,
    add_false_context, is_similar_to_false_context, init_false_context_filter
)
from context import extract_optimized_context, calculate_context_size, split_sentences
from noise_detector import noise_detector
from quality import quality
from cache import IncrementalCache

try:
    import ahocorasick
except ImportError:
    print("ERROR: 'pyahocorasick' is required. Install with: pip install pyahocorasick")
    sys.exit(1)

# Импортируем автомат из entities (он уже построен при загрузке модуля)
from entities import AUTOMATON, build_automaton, rebuild_automaton

try:
    from rapidfuzz import fuzz as rapidfuzz_fuzz
    HAS_RAPIDFUZZ = True
except ImportError:
    HAS_RAPIDFUZZ = False

_titles_set = {}
for ename, data in config.entities.items():
    _titles_set[ename] = set(data.get('titles', []))

# --- Кэширование сырых совпадений ---
_raw_cache_dir = Path(config.cache_dir) / "raw_matches"
_raw_cache_dir.mkdir(parents=True, exist_ok=True)

def _get_raw_matches_cache_key(file_path: str, entities_hash: str) -> str:
    """Генерирует ключ для кэша на основе содержимого файла и хеша сущностей."""
    with open(file_path, 'rb') as f:
        content_hash = hashlib.md5(f.read()).hexdigest()
    return f"{content_hash}_{entities_hash}"

def get_raw_matches(file_path: str, entities_hash: str) -> list:
    """Возвращает сырые совпадения из кэша или вычисляет заново."""
    cache_key = _get_raw_matches_cache_key(file_path, entities_hash)
    cache_file = _raw_cache_dir / cache_key
    if cache_file.exists():
        try:
            with open(cache_file, 'rb') as f:
                return pickle.load(f)
        except:
            pass
    # Вычисляем заново
    text, _ = read_file_fast(file_path)
    raw = _find_matches_aho(text)
    with open(cache_file, 'wb') as f:
        pickle.dump(raw, f)
    return raw

# --- Кэширование предложений ---
_sent_cache_dir = Path(config.cache_dir) / "sentence_cache"
_sent_cache_dir.mkdir(parents=True, exist_ok=True)

def get_cached_sentences(text: str, file_path: str) -> tuple:
    """Возвращает разбивку на предложения из кэша или вычисляет."""
    text_hash = hashlib.md5(text.encode('utf-8')).hexdigest()
    cache_file = _sent_cache_dir / f"{text_hash}.pkl"
    if cache_file.exists():
        try:
            with open(cache_file, 'rb') as f:
                return pickle.load(f)
        except:
            pass
    sentences = split_sentences(text)
    positions = []
    current_pos = 0
    for sent in sentences:
        start = text.find(sent, current_pos)
        if start == -1:
            start = current_pos
        end = start + len(sent)
        positions.append((start, end))
        current_pos = end
    result = (sentences, positions)
    with open(cache_file, 'wb') as f:
        pickle.dump(result, f)
    return result

def read_file_fast(file_path: str) -> tuple:
    """Быстрое чтение файла с обработкой кодировок и таймаутом через threading."""
    import threading
    import time
    result = [None, None]
    def read_file():
        try:
            with open(file_path, 'rb') as f:
                raw = f.read()
            # QA BUG-P03: UTF-16 LE/BE по BOM — раньше декодировались как CP1251
            # и превращались в кракозябры (0 находок, файл молча терялся).
            if len(raw) >= 2 and raw[0] == 0xFF and raw[1] == 0xFE:
                text = clean_pdf_artifacts(raw.decode('utf-16-le', errors='replace'))
                result[0], result[1] = text, len(raw)
                return
            if len(raw) >= 2 and raw[0] == 0xFE and raw[1] == 0xFF:
                text = clean_pdf_artifacts(raw.decode('utf-16-be', errors='replace'))
                result[0], result[1] = text, len(raw)
                return
            for enc in ['utf-8', 'cp1251']:
                try:
                    text = clean_pdf_artifacts(raw.decode(enc))
                    result[0] = text
                    result[1] = len(raw)
                    return
                except:
                    pass
            text = clean_pdf_artifacts(raw.decode('utf-8', errors='ignore'))
            result[0] = text
            result[1] = len(raw)
        except Exception as e:
            result[0] = None
            result[1] = str(e)
    # Используем таймаут из конфига (по умолчанию 120 сек)
    timeout = config.file_read_timeout if hasattr(config, 'file_read_timeout') else 120
    timer = threading.Timer(timeout, lambda: None)
    timer.start()
    thread = threading.Thread(target=read_file)
    thread.start()
    thread.join(timeout)
    if thread.is_alive():
        timer.cancel()
        raise TimeoutError(f"File read timeout after {timeout}s")
    timer.cancel()
    if result[0] is None:
        raise Exception(result[1] if result[1] else "Unknown read error")
    return result[0], result[1]

def _find_matches_aho(text: str) -> list:
    """Возвращает сырые совпадения из автомата."""
    if AUTOMATON is None:
        build_automaton()
    matches = []
    text_lower = text.lower()
    for end_idx, (entity_name, variant) in AUTOMATON.iter(text_lower):
        start_idx = end_idx - len(variant) + 1
        matches.append((start_idx, end_idx+1, variant, entity_name))
    return matches

def process_file(file_path: str, entities_hash: str = None) -> dict:
    fname = Path(file_path).name
    t_start = time.time()
    # QA BUG-P07 (исправлено): воркер подхватывает шумовой профиль по config_hash —
    # прежний вызов load_cache(dir, [], None) никогда не срабатывал.
    try:
        if not getattr(noise_detector, '_profile_loaded', False):
            noise_detector.load_profile_fresh(config.cache_dir,
                os.environ.get('EA_CONFIG_HASH'))
            noise_detector._profile_loaded = True
    except Exception:
        pass

    try:
        text, char_count = read_file_fast(file_path)
        raw_matches = get_raw_matches(file_path, entities_hash if entities_hash else '')

        # QA BUG-P04: локальные счётчики качества (агрегируются в родителе —
        # модуль quality в воркерах отдельный из-за ProcessPoolExecutor).
        qc = {'match': {}, 'fp': {}, 'listing': {}, 'valid': {}}
        def _bump(bucket, entity):
            qc[bucket][entity] = qc[bucket].get(entity, 0) + 1

        matches = []
        for start, end, variant, entity in raw_matches:
            _bump('match', entity)
            if is_false_positive(variant, start, text) or is_broken_word(variant, start, text):
                _bump('fp', entity)
                continue
            matches.append((start, end, variant, entity))

        matches.sort(key=lambda x: (x[0], -(x[1]-x[0])))
        resolved = []
        for m in matches:
            if not resolved:
                resolved.append(m)
                continue
            last = resolved[-1]
            if m[0] >= last[0] and m[1] <= last[1]:
                cur_priority = noise_detector.get_priority(m[3])
                last_priority = noise_detector.get_priority(last[3])
                if cur_priority > last_priority:
                    resolved[-1] = m
                continue
            elif last[0] >= m[0] and last[1] <= m[1]:
                cur_priority = noise_detector.get_priority(m[3])
                last_priority = noise_detector.get_priority(last[3])
                if cur_priority >= last_priority:
                    resolved[-1] = m
                continue
            else:
                resolved.append(m)

        total_found = len(resolved)
        if not resolved:
            return {'file': fname, 'mentions': [], 'total_found': 0, 'context_size': 0, 'char_count': char_count, 'density': 0.0}

        ctx_size = calculate_context_size(total_found)
        selected = select_mentions(resolved, config.max_mentions_per_file) if config.max_mentions_per_file > 0 and len(resolved) > config.max_mentions_per_file else resolved

        sentences, sent_positions = get_cached_sentences(text, file_path)
        mentions = []
        found_entities = set()
        
        for start, end, variant, entity in selected:
            nearby_sents, _, _ = _select_sentences_by_proximity(sentences, sent_positions, start, end, ctx_size)
            before, after = [], []
            mention_text = text[start:end]
            for sent in nearby_sents:
                if mention_text in sent:
                    parts = sent.split(mention_text)
                    if len(parts) >= 2:
                        before.append(parts[0].strip())
                        after.append(parts[1].strip())
                    else:
                        before.append(sent)
                else:
                    sent_start = text.find(sent)
                    if sent_start < start:
                        before.append(sent)
                    else:
                        after.append(sent)
            context_block = ' '.join(before + [mention_text] + after).strip()

            # QA BUG-P08: файл из одной строки (нет границ предложений) —
            # контекст схлопывается в одно слово или раздувается на мегабайт.
            # Fallback: оконный контекст по словам ± ctx_size/2.
            block_words = context_block.split()
            if len(block_words) < 3 or len(block_words) > ctx_size * 4:
                words = text.split()
                # Позиция слова, содержащего начало совпадения.
                mi = len(text[:start].split())
                lo = max(0, mi - ctx_size // 2)
                hi = min(len(words), mi + ctx_size // 2 + (len(mention_text.split()) or 1))
                context_block = ' '.join(words[lo:hi]).strip()

            # Проверка титулов
            if variant in _titles_set.get(entity, set()):
                if not validate_title_context(context_block, entity, variant):
                    add_false_context(entity, context_block)
                    continue

            # Проверка на ложный контекст (векторный индекс)
            if is_similar_to_false_context(entity, context_block):
                continue

            if evaluate_listing_density(context_block, entity, ALL_ENTITY_NAMES, variant):
                _bump('listing', entity)
                continue
            if not is_likely_common_word_context(context_block, entity):
                add_false_context(entity, context_block)
                _bump('fp', entity)
                continue

            mentions.append({
                'num': len(mentions)+1,
                'entity': entity,
                'var': variant,
                'line': text[:start].count('\n') + 1,
                'context': context_block,
                'ctx_size': ctx_size
            })
            _bump('valid', entity)
            found_entities.add(entity)

        mentions = deduplicate_contexts(mentions)
        for i, m in enumerate(mentions, 1):
            m['num'] = i

        # Fuzzy-поиск пропущенных сущностей
        if HAS_RAPIDFUZZ and found_entities:
            fuzzy_matches = _fuzzy_search_missed_entities(text, found_entities)
            for fm in fuzzy_matches:
                # QA BUG-P11: fuzzy-кандидаты раньше обходили фильтры и получали
                # line=0. Прогоняем через те же проверки перечней/общих слов
                # и вычисляем реальный номер строки.
                f_context = fm['sentence']
                f_start = text.find(f_context)
                f_line = text[:f_start].count('\n') + 1 if f_start >= 0 else 0
                if evaluate_listing_density(f_context, fm['entity'], ALL_ENTITY_NAMES, fm['variant']):
                    _bump('listing', fm['entity'])
                    continue
                if not is_likely_common_word_context(f_context, fm['entity']):
                    add_false_context(fm['entity'], f_context)
                    _bump('fp', fm['entity'])
                    continue
                mentions.append({
                    'num': len(mentions)+1,
                    'entity': fm['entity'],
                    'var': f"{fm['variant']} [fuzzy:{fm['score']}%]",
                    'line': f_line,
                    'context': f_context,
                    'ctx_size': ctx_size
                })

        density = (total_found / char_count) * 10000 if char_count > 0 else 0
        result = {
            'file': fname,
            'mentions': mentions,
            'total_found': total_found,
            'context_size': ctx_size,
            'char_count': char_count,
            'density': density,
            'cleaned_text': text,
            # QA BUG-P04: счётчики едут в родительский процесс для агрегации.
            '_qc': qc,
        }
        return result
    except Exception as e:
        import traceback
        traceback.print_exc()
        quality.log_error(fname, str(e))
        return {'file': fname, 'mentions': [], 'total_found': 0, 'context_size': 0, 'char_count': 0, 'density': 0.0, 'error': str(e)}

def select_mentions(mentions: list, max_m: int) -> list:
    """Выбирает равномерно распределённые упоминания."""
    if max_m == 0 or len(mentions) <= max_m:
        return mentions
    step = len(mentions) / max_m
    return [mentions[int(i*step)] for i in range(max_m) if int(i*step) < len(mentions)]

def _select_sentences_by_proximity(sentences, positions, match_start, match_end, max_words):
    """Выбирает предложения рядом с совпадением."""
    target = match_start
    closest_idx = -1
    min_dist = float('inf')
    for i, (s, e) in enumerate(positions):
        if s <= target < e:
            closest_idx = i
            break
        dist = min(abs(target - s), abs(target - e))
        if dist < min_dist:
            min_dist = dist
            closest_idx = i
    if closest_idx == -1:
        return [], sentences, positions
    before, after = [], []
    word_count = 0
    for i in range(closest_idx - 1, -1, -1):
        wc = len(sentences[i].split())
        if word_count + wc <= max_words * 0.45:
            before.insert(0, sentences[i])
            word_count += wc
        else:
            break
    word_count = 0
    for i in range(closest_idx + 1, len(sentences)):
        wc = len(sentences[i].split())
        if word_count + wc <= max_words * 0.45:
            after.append(sentences[i])
            word_count += wc
        else:
            break
    return before + [sentences[closest_idx]] + after, sentences, positions

def deduplicate_contexts(mentions: list) -> list:
    """Удаляет упоминания с одинаковым контекстом."""
    seen = set()
    unique = []
    for m in mentions:
        norm = re.sub(r'\s+', '', m.get('context', ''))
        if norm not in seen:
            seen.add(norm)
            unique.append(m)
    return unique

def _fuzzy_search_missed_entities(text: str, found_entities: set, threshold=85):
    """Fuzzy-поиск пропущенных сущностей."""
    if not HAS_RAPIDFUZZ:
        return []
    missed = set(ALL_ENTITY_NAMES) - found_entities
    if not missed:
        return []
    fuzzy_matches = []
    sentences = split_sentences(text)
    for entity_name in missed:
        variants = config.entities.get(entity_name, {}).get('irregular', [])
        canon = config.entities.get(entity_name, {}).get('canon', '')
        search_variants = list(variants) + [canon]
        for sentence in sentences:
            words = sentence.split()
            for window_size in range(1, 5):
                for i in range(len(words) - window_size + 1):
                    window = ' '.join(words[i:i+window_size])
                    for variant in search_variants:
                        if len(window) >= len(variant) * 0.7 and len(window) <= len(variant) * 1.3:
                            score = rapidfuzz_fuzz.ratio(window.lower(), variant.lower())
                            if score >= threshold:
                                fuzzy_matches.append({
                                    'entity': entity_name,
                                    'variant': window,
                                    'matched_variant': variant,
                                    'score': score,
                                    'sentence': sentence
                                })
    return fuzzy_matches