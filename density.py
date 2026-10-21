import os, sys
from typing import List, Tuple

def get_density_ranking(results: List[dict]) -> List[Tuple[str, float, int, int]]:
    data = [(r['file'], r.get('density',0), r.get('total_found',0), r.get('char_count',0)) for r in results if r['mentions'] and r.get('char_count',0)>0]
    data.sort(key=lambda x: x[1], reverse=True)
    return data[:10]
