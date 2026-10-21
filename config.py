import os, sys
try:
    import yaml
except ImportError:
    print("ERROR: pyyaml not installed. Run: pip install pyyaml")
    sys.exit(1)

import multiprocessing
from typing import Dict, List, Set

PROJECT_DIR = os.path.dirname(os.path.abspath(__file__))

class Config:
    def __init__(self, config_path: str = None, preset: str = None):
        if config_path is None:
            config_path = os.path.join(PROJECT_DIR, "config.yaml")
        if not os.path.exists(config_path):
            raise FileNotFoundError(f"config.yaml not found at {config_path}")
        self._config_path = config_path
        self._load_from_file()
        self._apply_preset(preset)

    def _load_from_file(self):
        with open(self._config_path, 'r', encoding='utf-8') as f:
            self._data = yaml.safe_load(f)
        # Преобразуем списки стоп-слов и значимых терминов в множества для быстрого поиска
        self.stop_words = set()
        for line in self._data.get('filters', {}).get('stop_words', []):
            for word in line.split(','):
                w = word.strip()
                if w:
                    self.stop_words.add(w.lower())
        self.significant_terms = set()
        for line in self._data.get('filters', {}).get('significant_terms', []):
            for word in line.split(','):
                w = word.strip()
                if w:
                    self.significant_terms.add(w.lower())
        # Добавим английские стоп-слова
        for word in self._data.get('filters', {}).get('english_stop_words', []):
            self.stop_words.add(word.lower())

    def _apply_preset(self, preset_name: str = None):
        if preset_name is None:
            preset_name = self._data.get('active_preset', 'extended')
        preset = self._data.get('presets', {}).get(preset_name, {})
        # Применяем параметры пресета к основным атрибутам
        self.context_max = preset.get('max_context_words', self._data.get('context', {}).get('max_words', 75))
        self.context_min = preset.get('min_context_words', self._data.get('context', {}).get('min_words', 25))
        self.context_threshold = self._data.get('context', {}).get('threshold', 50)
        self.max_mentions_per_file = preset.get('max_mentions_per_file', self._data.get('processing', {}).get('max_mentions_per_file', 12))
        self.fuzzy_enabled = preset.get('fuzzy', False)
        self.sbert_enabled = preset.get('sbert', False)
        self.deep_corrupt = preset.get('deep_corrupt', True)
        self.iterations = preset.get('iterations', 1)
        # Другие параметры из корня
        self.cache_enabled = self._data.get('cache', {}).get('enabled', True)
        self.cache_dir = self._data.get('cache', {}).get('dir', 'cache')
        self.invalidate_on_config_change = self._data.get('cache', {}).get('invalidate_on_config_change', True)
        self.output_dir = self._data.get('output', {}).get('dir', 'output')
        self.output_formats = self._data.get('output', {}).get('formats', ['txt'])
        self.per_entity_folders = self._data.get('output', {}).get('per_entity_folders', False)
        self.max_workers = self._data.get('processing', {}).get('max_workers', 0)
        # Увеличиваем таймаут по умолчанию до 120 секунд
        self.file_read_timeout = self._data.get('processing', {}).get('file_read_timeout', 120)
        self.entities = self._data.get('entities', {})
        self.discovery_enabled = self._data.get('discovery', {}).get('enabled', False)
        self.discovery_similarity_threshold = self._data.get('discovery', {}).get('similarity_threshold', 0.7)
        self.discovery_min_entity_length = self._data.get('discovery', {}).get('min_entity_length', 3)
        self._preset_name = preset_name

    def __getattr__(self, name):
        if name in self._data:
            return self._data[name]
        raise AttributeError(f"Config has no attribute '{name}'")

    def __getitem__(self, key):
        return self._data[key]

    def reload(self, preset=None):
        """Перезагружает конфиг из файла и применяет новый пресет."""
        with open(self._config_path, 'r', encoding='utf-8') as f:
            self._data = yaml.safe_load(f)
        self._load_from_file()
        self._apply_preset(preset)

# Создаём глобальный экземпляр конфига для использования во всём проекте
config = Config()
