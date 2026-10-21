import os, sys, re, random
from functools import lru_cache

class Transliterator:
    def __init__(self, max_variants=600):
        self.max_variants = max_variants
        random.seed(42)

    @lru_cache(maxsize=512)
    def _basic_translit(self, name: str) -> str:
        mapping = {
            'a':'а','b':'б','c':'к','d':'д','e':'е','f':'ф','g':'г','h':'х','i':'и','j':'дж',
            'k':'к','l':'л','m':'м','n':'н','o':'о','p':'п','q':'к','r':'р','s':'с','t':'т',
            'u':'у','v':'в','w':'в','x':'кс','y':'и','z':'з',
            'A':'А','B':'Б','C':'К','D':'Д','E':'Е','F':'Ф','G':'Г','H':'Х','I':'И','J':'Дж',
            'K':'К','L':'Л','M':'М','N':'Н','O':'О','P':'П','Q':'К','R':'Р','S':'С','T':'Т',
            'U':'У','V':'В','W':'В','X':'Кс','Y':'И','Z':'З',
            'th':'т','Th':'Т','TH':'Т','ch':'ч','Ch':'Ч','CH':'Ч',
            'sh':'ш','Sh':'Ш','SH':'Ш','kh':'кх','Kh':'Кх','KH':'КХ',
            'gh':'г','Gh':'Г','GH':'Г','ph':'ф','Ph':'Ф','PH':'Ф','ts':'ц','Ts':'Ц','TS':'Ц'
        }
        result = name
        for eng, rus in sorted(mapping.items(), key=lambda x: -len(x[0])):
            result = result.replace(eng, rus)
        return result

    def generate(self, english: str, deep_corrupt: bool = None, is_common: bool = False) -> list:
        if deep_corrupt is None:
            deep_corrupt = len(english) > 6 and not is_common
        variants = set()
        base = self._basic_translit(english)
        variants.add(base)
        if not is_common:
            variants.update(self._apply_rules(english, deep=deep_corrupt))
            variants.update(self._hyphen_variants(english))
        declined = set()
        for v in list(variants)[:50]:
            if not self._is_exotic_long(v) and not self._is_compound(v):
                declined.update(self._decline_russian(v))
        variants.update(declined)
        if deep_corrupt and not is_common:
            corrupted = set()
            for v in list(variants)[:30]:
                corrupted.update(self._deep_corrupt(v))
            variants.update(corrupted)
        result = [v for v in variants if v and len(v) >= 3]
        result = list(dict.fromkeys(result))
        return result[:self.max_variants]

    def generate_iterative(self, english: str, iterations: int = 2, is_common: bool = False) -> list:
        all_variants = set()
        current_batch = {english}
        for i in range(iterations):
            next_batch = set()
            for name in current_batch:
                generated = self.generate(name, deep_corrupt=True, is_common=is_common)
                next_batch.update(generated)
                all_variants.update(generated)
            current_batch = next_batch
        all_variants.update(self.generate(english, deep_corrupt=True, is_common=is_common))
        result = [v for v in all_variants if v and len(v) >= 3]
        result = list(dict.fromkeys(result))
        return result[:self.max_variants]

    def generate_two_pass(self, canon: str, irregular: list, is_common: bool) -> tuple:
        narrow = set(irregular)
        narrow.add(self._basic_translit(canon))
        narrow = [v for v in narrow if v and len(v) >= 3]
        if len(canon) > 6 and not is_common:
            wide = self.generate_iterative(canon, iterations=2, is_common=is_common)
        else:
            wide = self.generate(canon, deep_corrupt=True, is_common=is_common)
        return narrow, wide

    def _is_exotic_long(self, name: str) -> bool:
        return len(name) > 7 and name.endswith('а') and not any(c in 'еёиоуыэюя' for c in name[:-1])

    def _is_compound(self, name: str) -> bool:
        return ' ' in name

    def _apply_rules(self, name: str, deep: bool = False) -> set:
        variants = set()
        for pattern, repl in [('th','т'),('th','тх'),('th','с'),('th','ф'),
                              ('Th','Т'),('Th','Тх'),('Th','С'),('Th','Ф'),
                              ('TH','Т'),('TH','ТХ'),('TH','С'),('TH','Ф')]:
            if pattern in name:
                variants.add(name.replace(pattern, repl))
        for pattern, repl in [('gh','г'),('gh','гх'),('gh','х'),
                              ('Gh','Г'),('Gh','Гх'),('Gh','Х'),
                              ('GH','Г'),('GH','ГХ'),('GH','Х')]:
            if pattern in name:
                variants.add(name.replace(pattern, repl))
        for pattern, repl in [('kh','кх'),('kh','х'),('Kh','Кх'),('Kh','Х'),('KH','КХ'),('KH','Х')]:
            if pattern in name:
                variants.add(name.replace(pattern, repl))
        if name.endswith('oa'):
            variants.add(name[:-2] + 'о')
            variants.add(name[:-2] + 'ос')
            variants.add(name[:-2] + 'от')
        if name.endswith('ia'):
            variants.add(name[:-2] + 'ия')
            variants.add(name[:-2] + 'иа')
        if name.endswith('ea'):
            variants.add(name[:-2] + 'еа')
            variants.add(name[:-2] + 'ея')
        for gpat, grepl in [('ge','же'),('gi','жи'),('gy','жи'),('Ge','Же'),('Gi','Жи'),('Gy','Жи')]:
            if gpat in name:
                variants.add(name.replace(gpat, grepl))
        return variants

    def _hyphen_variants(self, name: str) -> set:
        variants = set()
        if '-' in name:
            variants.add(name.replace('-', ' '))
            variants.add(name.replace('-', ''))
        if ' ' in name:
            variants.add(name.replace(' ', '-'))
            variants.add(name.replace(' ', ''))
        return variants

    def _decline_russian(self, name: str) -> set:
        if not name or not any(c in 'аеёиоуыэюя' for c in name[-2:]):
            return {name}
        results = {name}
        last = name[-1]
        if last == 'а':
            results.update([name[:-1]+e for e in ['ы','е','у','ой']])
        elif last == 'я':
            results.update([name[:-1]+e for e in ['и','е','ю','ей']])
        elif last not in 'еёиоуыэюь':
            results.update([name+e for e in ['а','у','ом','е']])
        elif last == 'ь':
            results.update([name[:-1]+e for e in ['я','ю','ем','е']])
        return results

    def _deep_corrupt(self, name: str) -> set:
        corrupted = set()
        mutations = [
            ('т','ф'), ('ф','т'), ('с','з'), ('з','с'), ('к','г'), ('г','к'),
            ('х','г'), ('б','п'), ('п','б'), ('д','т'), ('т','д')
        ]
        for i, c in enumerate(name):
            for a, b in mutations:
                if c == a:
                    corrupted.add(name[:i] + b + name[i+1:])
                elif c.lower() == a:
                    corrupted.add(name[:i] + b.upper() + name[i+1:])
        for i in range(len(name)):
            corrupted.add(name[:i] + name[i+1:])
            if name[i].lower() in 'аеёиоуыэюя':
                corrupted.add(name[:i+1] + 'х' + name[i+1:])
        for i, c in enumerate(name):
            if c.isalpha() and c.lower() not in 'аеёиоуыэюя':
                corrupted.add(name[:i+1] + c + name[i+1:])
        return corrupted

translator = Transliterator()
