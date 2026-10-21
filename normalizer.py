import os, sys, re, unicodedata
class TextNormalizer:
    def __init__(self):
        self.replacements = {
            r'\(cid:\d+\)': '',
            r'[–—−]': '—',
            r'[«»"„"]': '"',
            r'\s+': ' ',
        }

    def normalize(self, text: str) -> str:
        text = unicodedata.normalize('NFKC', text)
        for p, r in self.replacements.items():
            text = re.sub(p, r, text)
        return text.strip()

normalizer = TextNormalizer()
