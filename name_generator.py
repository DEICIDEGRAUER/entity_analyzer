import os, sys, re
class NameGenerator:
    def __init__(self):
        self.generated = set()
    
    def generate(self, english_name: str) -> list:
        self.generated = set()
        variants = set()
        base = self._transliterate(english_name)
        variants.add(base)
        variants.update(self._apply_translit_rules(english_name))
        variants.update(self._hyphen_variants(english_name))
        declined = set()
        for v in variants:
            declined.update(self._decline(v))
        variants.update(declined)
        result = [v for v in variants if v and len(v) >= 2]
        result = list(set(result))
        result.sort(key=len)
        return result[:200]  # Ограничиваем чтобы не взорваться
    
    def _transliterate(self, name: str) -> str:
        mapping = {
            'a': 'а', 'b': 'б', 'c': 'к', 'd': 'д', 'e': 'е', 'f': 'ф',
            'g': 'г', 'h': 'х', 'i': 'и', 'j': 'дж', 'k': 'к', 'l': 'л',
            'm': 'м', 'n': 'н', 'o': 'о', 'p': 'п', 'q': 'к', 'r': 'р',
            's': 'с', 't': 'т', 'u': 'у', 'v': 'в', 'w': 'в', 'x': 'кс',
            'y': 'и', 'z': 'з',
            'A': 'А', 'B': 'Б', 'C': 'К', 'D': 'Д', 'E': 'Е', 'F': 'Ф',
            'G': 'Г', 'H': 'Х', 'I': 'И', 'J': 'Дж', 'K': 'К', 'L': 'Л',
            'M': 'М', 'N': 'Н', 'O': 'О', 'P': 'П', 'Q': 'К', 'R': 'Р',
            'S': 'С', 'T': 'Т', 'U': 'У', 'V': 'В', 'W': 'В', 'X': 'Кс',
            'Y': 'И', 'Z': 'З',
            'th': 'т', 'Th': 'Т', 'TH': 'Т',
            'ch': 'ч', 'Ch': 'Ч', 'CH': 'Ч',
            'sh': 'ш', 'Sh': 'Ш', 'SH': 'Ш',
            'kh': 'кх', 'Kh': 'Кх', 'KH': 'КХ',
            'gh': 'г', 'Gh': 'Г', 'GH': 'Г',
            'ph': 'ф', 'Ph': 'Ф', 'PH': 'Ф',
            'ts': 'ц', 'Ts': 'Ц', 'TS': 'Ц',
        }
        result = name
        for eng, rus in sorted(mapping.items(), key=lambda x: -len(x[0])):
            result = result.replace(eng, rus)
        return result
    
    def _apply_translit_rules(self, name: str) -> set:
        results = set()
        endings = {'oth': ['от', 'отх', 'ос', 'оф'], 'eth': ['ет', 'етх', 'ес', 'еф'], 'ath': ['ат', 'атх', 'ас', 'аф']}
        for eng_end, rus_ends in endings.items():
            if name.lower().endswith(eng_end):
                for rus in rus_ends:
                    results.add(name[:-len(eng_end)] + rus)
        return results
    
    def _hyphen_variants(self, name: str) -> set:
        results = set()
        if '-' in name:
            results.add(name.replace('-', ' '))
            results.add(name.replace('-', ''))
        if ' ' in name:
            results.add(name.replace(' ', '-'))
            results.add(name.replace(' ', ''))
        return results
    
    def _decline(self, name: str) -> set:
        results = {name}
        if not name or not any(c in 'аеёиоуыэюя' for c in name[-2:]):
            return results
        last = name[-1]
        if last == 'а':
            results.update([name[:-1] + e for e in ['ы', 'е', 'у', 'ой']])
        elif last == 'я':
            results.update([name[:-1] + e for e in ['и', 'е', 'ю', 'ей']])
        elif last not in 'еёиоуыэюь':
            results.update([name + e for e in ['а', 'у', 'ом', 'е']])
        elif last == 'ь':
            results.update([name[:-1] + e for e in ['я', 'ю', 'ем', 'е']])
        return results

name_generator = NameGenerator()
