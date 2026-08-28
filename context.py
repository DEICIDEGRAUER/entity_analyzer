import os, sys, re
from typing import List, Tuple
from config import config

# QA BUG-P05: порт SentenceSplitter из .NET — десятичные числа и реплики не рвутся.
_SENTENCE_END = re.compile(r'[.!?…]+["»\)]*(?=\s+[«"(\[]?[A-ZА-ЯЁ])')

def split_sentences(text: str) -> List[str]:
    text = text.replace('\r\n', '\n')
    sentences, start = [], 0
    for m in _SENTENCE_END.finditer(text):
        end_ = m.end()
        chunk = text[start:end_].strip()
        if chunk:
            sentences.append(chunk)
        start = end_
    tail = text[start:].strip()
    if tail:
        sentences.append(tail)
    return sentences or ([text.strip()] if text.strip() else [])

def score_sentence(s: str) -> float:
    wl, wo = s.lower().split(), s.split()
    if not wl: return 0.0
    sc = sum(1 for w in wl if w in config.significant_terms) * 3.0
    cw = [w for w in wl if w not in config.stop_words and len(w) > 1]
    sc += (len(cw)/len(wl))*1.5 if wl else 0
    sc += sum(1 for w in wo if w[0].isupper() and w.isalpha() and len(w)>1) * 2.0
    if 8 <= len(wl) <= 30: sc += 1.0
    elif len(wl) < 4: sc -= 1.0
    return sc

def optimize_sentence(s: str) -> str:
    return re.sub(r'\s+', ' ', re.sub(r'^(Однако|Впрочем|Кстати|Значит|Следовательно|Таким образом|Итак|Пожалуй|Вероятно|Возможно|Кажется|Наверное|По-видимому|Действительно|Правда|Безусловно|Разумеется|Например|В частности|Кроме того|Более того|Вместе с тем|С другой стороны|В то же время|Тем не менее|В любом случае|Во всяком случае|В целом|В общем|В основном|В принципе|Как правило|Как известно|Между прочим|Собственно говоря|Короче говоря|Иными словами|Другими словами|Стало быть|В свою очередь|По крайней мере|По существу|По сути|Согласно)\s*,?\s*', '', s)).strip()

def select_best(sentences: List[str], max_words: int) -> Tuple[List[str], dict]:
    if not sentences: return [], {'available': 0, 'selected': 0}
    tw = sum(len(s.split()) for s in sentences)
    if tw <= max_words: return sentences, {'available': len(sentences), 'selected': len(sentences)}
    scored = [(score_sentence(s), i, s) for i, s in enumerate(sentences)]
    if len(sentences) > 3:
        for idx, (s, i, _) in enumerate(scored):
            scored[idx] = (s + (len(sentences)-abs(i-len(sentences)//2))/len(sentences)*2.0, i, scored[idx][2])
    scored.sort(key=lambda x: x[0], reverse=True)
    idxs, wc = [], 0
    for _, i, s in scored:
        ow = len(optimize_sentence(s).split())
        if wc + ow <= max_words * 1.2:
            idxs.append(i); wc += ow
    idxs.sort()
    return [optimize_sentence(sentences[i]) for i in idxs], {'available': len(sentences), 'selected': len(idxs)}

def find_boundary(sentences: List[str], pos: int) -> Tuple[int, int]:
    cp = 0
    for i, s in enumerate(sentences):
        if cp <= pos < cp + len(s): return i, pos - cp
        cp += len(s) + 1
    return -1, -1

def calculate_context_size(total: int) -> int:
    if total <= 5: return config.context_max
    elif total <= 15: return int(config.context_max - (total-5)*2.5)
    # QA BUG-P10: ветка была захардкожена «50 − …», игнорируя пресет
    # (titanic с context_max=200 получал ~50 слов). Линейная интерполяция
    # от context_max к context_min на диапазоне [15, threshold].
    elif total <= config.context_threshold:
        span = config.context_max - config.context_min
        frac = (total - 15) / max(1, config.context_threshold - 15)
        return int(config.context_max - span * frac)
    return config.context_min

def extract_optimized_context(text: str, start: int, end: int, target: int) -> str:
    sentences = split_sentences(text)
    si, pi = find_boundary(sentences, start)
    if si == -1:
        words = text.split()
        mi = len(text[:start].split())
        return ' '.join(words[max(0,mi-target):mi] + [text[start:end]] + words[mi:min(len(words),mi+target)]).strip()
    cs = sentences[si]
    bc, ac = cs[:pi].strip(), cs[pi+(end-start):].strip()
    bp = [sentences[i] for i in range(si-1, -1, -1)][::-1]
    ap = [sentences[i] for i in range(si+1, len(sentences))]
    bs, _ = select_best(bp, int(target*0.45))
    as_, _ = select_best(ap, int(target*0.45))
    parts = []
    if bs: parts.append(' '.join(bs))
    if bc: parts.append(bc)
    parts.append(text[start:end])
    if ac: parts.append(ac)
    if as_: parts.append(' '.join(as_))
    return ' '.join(parts).strip()
