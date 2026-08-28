import os, sys, json
from pathlib import Path
from datetime import datetime
from collections import defaultdict

class QualityControl:
    def __init__(self):
        self.m = {
            'start_time': datetime.now().isoformat(),
            'total_files_processed': 0,
            'total_matches_found': 0,
            'false_positives_removed': 0,
            'listing_contexts_removed': 0,
            'valid_mentions_final': 0,
            'cache_hits': 0,
            'cache_misses': 0,
            'errors': [],
            'per_entity': defaultdict(lambda: {'found':0,'false_positives':0,'listings_removed':0,'final':0})
        }

    def log_match(self, e: str):
        self.m['total_matches_found'] += 1
        self.m['per_entity'][e]['found'] += 1

    def log_false_positive(self, e: str):
        self.m['false_positives_removed'] += 1
        self.m['per_entity'][e]['false_positives'] += 1

    def log_listing_removed(self, e: str):
        self.m['listing_contexts_removed'] += 1
        self.m['per_entity'][e]['listings_removed'] += 1

    def log_valid_mention(self, e: str):
        self.m['valid_mentions_final'] += 1
        self.m['per_entity'][e]['final'] += 1

    def log_error(self, fn: str, err: str):
        self.m['errors'].append({'file':fn,'error':err,'timestamp':datetime.now().isoformat()})

    def log_cache_hit(self): self.m['cache_hits'] += 1
    def log_cache_miss(self): self.m['cache_misses'] += 1

    def get_report(self) -> dict:
        tr = self.m['false_positives_removed'] + self.m['listing_contexts_removed']
        tf = max(self.m['total_matches_found'], 1)
        vf = self.m['valid_mentions_final']
        return {
            'summary': {
                'duration': f"{(datetime.now()-datetime.fromisoformat(self.m['start_time'])).total_seconds():.1f}s",
                'total_found': self.m['total_matches_found'],
                'fp_removed': self.m['false_positives_removed'],
                'listings_removed': self.m['listing_contexts_removed'],
                'valid_final': vf,
                'filter_efficiency': f"{(tr/tf*100):.1f}%",
                'signal_noise': f"{(vf/max(tr,1)):.1f}:1",
                'cache_hits': self.m['cache_hits'],
                'cache_misses': self.m['cache_misses'],
                'errors': len(self.m['errors'])
            },
            'per_entity': dict(self.m['per_entity']),
            'errors': self.m['errors'][-20:]
        }

    def save(self, out_dir: str):
        json.dump(self.get_report(), open(Path(out_dir)/"quality_report.json",'w',encoding='utf-8'), ensure_ascii=False, indent=2)

quality = QualityControl()
