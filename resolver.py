import os, sys, re
from typing import List
from noise_detector import noise_detector
from entities import get_entity_name

class MatchResolver:
    def resolve_overlapping(self, matches: List[re.Match], text: str) -> List[re.Match]:
        if not matches: return []
        matches.sort(key=lambda m: (m.start(), -(m.end()-m.start())))
        resolved = []
        for match in matches:
            if not resolved:
                resolved.append(match); continue
            last = resolved[-1]
            if match.start() >= last.start() and match.end() <= last.end():
                if noise_detector.get_priority(get_entity_name(match.group())) > noise_detector.get_priority(get_entity_name(last.group())):
                    resolved[-1] = match
                continue
            if last.start() >= match.start() and last.end() <= match.end():
                if noise_detector.get_priority(get_entity_name(match.group())) >= noise_detector.get_priority(get_entity_name(last.group())):
                    resolved[-1] = match
                continue
            resolved.append(match)
        return resolved

    def filter_subword_matches(self, matches: List[re.Match], text: str) -> List[re.Match]:
        return [m for m in matches if not ((m.start()>0 and text[m.start()-1].isalpha()) or (m.end()<len(text) and text[m.end()].isalpha()))]

resolver = MatchResolver()
