import os, sys
from context import split_sentences  # Единый подход с основным пайплайном

class SemanticSearcher:
    def __init__(self):
        self.model = None
        self.util = None
        self.available = False
        try:
            from sentence_transformers import SentenceTransformer, util
            self.model = SentenceTransformer('all-MiniLM-L6-v2')
            self.util = util
            self.available = True
        except ImportError:
            pass

    def find_similar_contexts(self, text: str, descriptions: list, top_k=3, window_size=3, threshold=0.5):
        """Ищет окна в тексте, похожие на описания сущности."""
        if not self.available:
            return []
        
        # Используем тот же split_sentences, что и processor.py
        sentences = split_sentences(text)
        if not sentences:
            return []
        
        # Объединяем в окна
        windows = []
        for i in range(len(sentences) - window_size + 1):
            window = ' '.join(sentences[i:i+window_size])
            windows.append(window)
        if not windows:
            return []

        # Эмбеддинги описаний
        desc_embs = self.model.encode(descriptions, convert_to_tensor=True)
        # Эмбеддинги окон
        window_embs = self.model.encode(windows, convert_to_tensor=True)

        # Косинусное сходство
        scores = self.util.cos_sim(window_embs, desc_embs).max(dim=1).values
        top_indices = scores.argsort(descending=True)[:top_k]
        
        results = []
        for i in top_indices:
            score = scores[i].item()
            if score > threshold:
                results.append((windows[i], score))
        return results

semantic_searcher = SemanticSearcher()
