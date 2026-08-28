import os, sys, hashlib, pickle, json, shutil
from pathlib import Path
from datetime import datetime, timedelta
from typing import Optional

class IncrementalCache:
    def __init__(self, cache_dir: str, max_age_days: int = 30, config_hash: str = None):
        self.cache_dir = Path(cache_dir)
        self.cache_dir.mkdir(parents=True, exist_ok=True)
        self.max_age = timedelta(days=max_age_days)
        self.config_hash = config_hash
        self.index = self._load()
        self._cleanup()

    def _load(self) -> dict:
        p = self.cache_dir / "index.json"
        if p.exists():
            try:
                with open(p, 'r', encoding='utf-8') as f:
                    return json.load(f)
            except (json.JSONDecodeError, OSError, ValueError):
                backup = p.with_suffix('.json.bak')
                try:
                    shutil.copy(p, backup)
                    p.unlink()
                except:
                    pass
                return {}
        return {}

    def _save(self):
        # Гарантируем существование директории
        self.cache_dir.mkdir(parents=True, exist_ok=True)
        temp_file = self.cache_dir / "index.json.tmp"
        try:
            with open(temp_file, 'w', encoding='utf-8') as f:
                json.dump(self.index, f, indent=2)
            # Атомарное переименование; QA BUG-P02: сбой сохранения индекса
            # не должен всплывать в результат обработки файла.
            os.replace(temp_file, self.cache_dir / "index.json")
        except OSError:
            try:
                if temp_file.exists():
                    temp_file.unlink()
            except OSError:
                pass

    def _cleanup(self):
        now = datetime.now()
        expired = []
        for path, data in self.index.items():
            if now - datetime.fromisoformat(data['timestamp']) > self.max_age:
                expired.append(path)
            elif self.config_hash and data.get('config_hash') != self.config_hash:
                expired.append(path)
        for path in expired:
            cf = self.cache_dir / self.index[path]['hash']
            if cf.exists(): cf.unlink()
            del self.index[path]
        if expired: self._save()

    def get_file_hash(self, path: str) -> str:
        s = os.stat(path)
        return hashlib.sha256(f"{s.st_size}:{s.st_mtime}:{path}".encode()).hexdigest()[:16]

    def is_cached(self, path: str) -> bool:
        if path not in self.index or self.index[path]['hash'] != self.get_file_hash(path):
            return False
        # QA BUG-P03: запись другого пресета/конфига не считается валидной.
        if self.config_hash and self.index[path].get('config_hash') != self.config_hash:
            return False
        return True

    def get(self, path: str) -> Optional[dict]:
        if not self.is_cached(path): return None
        cf = self.cache_dir / self.index[path]['hash']
        return pickle.load(open(cf, 'rb')) if cf.exists() else None

    def set(self, path: str, result: dict, persist: bool = True):
        """Сохраняет результат обработки файла.

        QA BUG-P02: persist=False — воркеры пишут только контентно-адресный
        blob (безопасно параллельно); index.json из воркеров не пишется,
        его сохраняет родитель одним вызовом save_index().
        """
        self.cache_dir.mkdir(parents=True, exist_ok=True)
        h = self.get_file_hash(path)
        self.index[path] = {'hash': h, 'timestamp': datetime.now().isoformat(), 'file_name': Path(path).name}
        if self.config_hash:
            self.index[path]['config_hash'] = self.config_hash
        temp_file = self.cache_dir / (h + '.tmp')
        try:
            with open(temp_file, 'wb') as f:
                pickle.dump(result, f)
            os.replace(temp_file, self.cache_dir / h)
        except Exception:
            if temp_file.exists():
                temp_file.unlink()
        if persist:
            self._save()

    def register(self, path: str):
        """QA BUG-P02: регистрация обработанного файла в индексе без записи blob
        (воркер уже сохранил его) — для единственного сохранения индекса родителем."""
        h = self.get_file_hash(path)
        self.index[path] = {'hash': h, 'timestamp': datetime.now().isoformat(), 'file_name': Path(path).name}
        if self.config_hash:
            self.index[path]['config_hash'] = self.config_hash

    def save_index(self):
        """Публичная точка сохранения индекса (один писатель — родитель)."""
        self._save()
