"""Geometry-preserving pipeline. No network or UI dependencies."""
from collections import OrderedDict
from dataclasses import dataclass, asdict, field, replace
import hashlib
import math
import re
import unicodedata
from time import perf_counter

from .reading_order import ocr_lines
from .change import tile_hashes, dirty_rectangles, intersects


def is_chinese_character(character):
    """Recognize Han ideographs, including compatibility and extension blocks."""
    value = ord(character)
    return (0x3400 <= value <= 0x4DBF or 0x4E00 <= value <= 0x9FFF
            or 0xF900 <= value <= 0xFAFF or 0x20000 <= value <= 0x323AF)


def translation_eligible(region):
    """Translate confident Chinese-only OCR units; preserve other scripts intact.

    A mixed OCR box cannot safely mask only its Chinese glyphs because the
    recognizer returns one polygon. Leaving that box untouched protects Latin
    app names and other languages, including OCR's stray Chinese guesses.
    Han-only Japanese names cannot be distinguished by script alone.
    """
    return (region.confidence >= 0.85
            and any(is_chinese_character(c) for c in region.source)
            and not any(unicodedata.category(c).startswith("L")
                        and not is_chinese_character(c) for c in region.source))


@dataclass
class TextRegion:
    polygon: list
    source: str
    confidence: float
    translated: str = ""
    constituent_polygons: list = field(default_factory=list)
    style: dict = field(default_factory=dict)
    cache_identity: str = ""

    def __post_init__(self):
        if len(self.polygon) < 4 or any(len(p) != 2 or not all(math.isfinite(x) for x in p) for p in self.polygon):
            raise ValueError("Text region requires finite polygon coordinates")
        x1, y1, x2, y2 = self.bounds
        if x2 <= x1 or y2 <= y1 or not isinstance(self.source, str) or not 0 <= self.confidence <= 1:
            raise ValueError("Invalid text region geometry, source or confidence")

    @property
    def bounds(self):
        xs, ys = zip(*self.polygon)
        return min(xs), min(ys), max(xs), max(ys)


def group_regions(regions):
    """Join close fragments on a common baseline, retain constituent polygons.

    Conservative 0.35-height gap avoids merging most adjacent UI controls.
    It cannot distinguish every borderless control. No cross-line prose merging.
    """
    grouped = []
    for line in ocr_lines([(r.polygon, r.source, r) for r in regions]):
        for _, _, region in line:
            if grouped:
                previous = grouped[-1]
                a, b = previous.bounds, region.bounds
                height = min(a[3]-a[1], b[3]-b[1])
                same_line = abs((a[1]+a[3]-b[1]-b[3])/2) < height*.35
                both_chinese = re.search(r"[\u3400-\u9fff]", previous.source) and re.search(r"[\u3400-\u9fff]", region.source)
                if same_line and both_chinese and 0 <= b[0]-a[2] <= height*.35:
                    bounds = (min(a[0], b[0]), min(a[1], b[1]), max(a[2], b[2]), max(a[3], b[3]))
                    polygon = [[bounds[0], bounds[1]], [bounds[2], bounds[1]],
                               [bounds[2], bounds[3]], [bounds[0], bounds[3]]]
                    separator = "" if re.search(r"[\u3400-\u9fff]$", previous.source) and re.match(r"[\u3400-\u9fff]", region.source) else " "
                    grouped[-1] = TextRegion(polygon, previous.source+separator+region.source,
                                            min(previous.confidence, region.confidence),
                                            constituent_polygons=previous.constituent_polygons+[region.polygon])
                    continue
            grouped.append(replace(region, constituent_polygons=[region.polygon]))
    return grouped


class Pipeline:
    """One instance per resident worker; bounded caches, deduplicated batches."""
    def __init__(self, ocr, translator, capacity=4096, batch_size=8, tile_size=256,
                 padding=32, identity="zh-en", incremental=True):
        if capacity < 1 or batch_size < 1:
            raise ValueError("Cache capacity and batch size must be positive")
        self.ocr, self.translator = ocr, translator
        self.capacity, self.batch_size = capacity, batch_size
        if tile_size < 32 or padding < 0:
            raise ValueError("Invalid tile size or padding")
        self.tile_size, self.padding = tile_size, padding
        self.identity, self.incremental = identity, incremental
        self.cache = OrderedDict()
        self.last_frame = None
        self.last_regions = []
        self.last_size = None

    def clear(self):
        self.cache.clear()
        self.last_frame = None
        self.last_regions = []
        self.last_size = None

    def reset_frame(self):
        """Keep text translations when switching monitors/selected rectangles."""
        self.last_frame = None
        self.last_size = None
        self.last_regions = []

    def run(self, image, use_cache=True):
        start = perf_counter()
        image = image.convert("RGB")
        key = tile_hashes(image, self.tile_size)
        timing = {"change_detection_ms": (perf_counter() - start) * 1000}
        if use_cache and key == self.last_frame:
            return self.last_regions, {**timing, "frame_cache_hit": True, "ocr_ms": 0,
                                       "detection_ms": 0, "recognition_ms": 0,
                                       "translation_ms": 0, "grouping_ms": 0,
                                       "cache_lookup_ms": 0, "regions": len(self.last_regions),
                                       "translation_units": len(self.last_regions), "translated_strings": 0,
                                       "translation_cache_hits": 0, "spatial_cache_hits": len(self.last_regions),
                                       "changed_rectangles": [], "tokens_generated": 0,
                                       "total_ms": (perf_counter()-start)*1000}
        t = perf_counter()
        partial = use_cache and self.incremental and self.last_frame is not None and self.last_size == image.size
        rectangles = dirty_rectangles(key, self.last_frame, self.last_regions, image.size, self.padding) if partial else [(0, 0, image.width, image.height)]
        kept = [replace(r) for r in self.last_regions if not any(intersects(r.bounds, rect) for rect in rectangles)] if partial else []
        detected, stage = [], {"detection_ms": 0, "recognition_ms": 0,"ocr_tiles":0,"detected_boxes":0}
        for left, top, right, bottom in rectangles:
            for region in self.ocr(image.crop((left, top, right, bottom))):
                polygon = [[x+left, y+top] for x, y in region.polygon]
                detected.append(replace(region, polygon=polygon))
            for name in stage:
                stage[name] += getattr(self.ocr, "timings", {}).get(name, 0)
        timing["ocr_ms"] = (perf_counter()-t)*1000
        timing.update(stage)
        t = perf_counter()
        # Reject each raw OCR box before grouping so a confident neighbor cannot
        # make an uncertain or mixed-script box eligible for replacement.
        eligible = [r for r in detected if translation_eligible(r)]
        untouched = [r for r in detected if not translation_eligible(r)]
        regions = kept + group_regions(eligible) + untouched
        # Re-sort retained and newly recognized regions together for stable reading order.
        regions = [item[2] for line in ocr_lines([(r.polygon, r.source, r) for r in regions]) for item in line]
        timing["grouping_ms"] = (perf_counter()-t)*1000
        t = perf_counter()
        misses = list(dict.fromkeys(r.source for r in regions
                                   if translation_eligible(r)
                                   and (not use_cache or (self.identity, r.source) not in self.cache)))
        timing["cache_lookup_ms"] = (perf_counter()-t)*1000
        t = perf_counter()
        resolved = {}
        tokens_generated = 0
        for offset in range(0, len(misses), self.batch_size):
            batch = misses[offset:offset+self.batch_size]
            translated = self.translator.batch(batch)
            if len(translated) != len(batch) or any(not x.strip() for x in translated):
                raise RuntimeError("Translator returned an incomplete batch")
            resolved.update(zip(batch, translated))
            tokens_generated += getattr(self.translator, "last_tokens", 0)
        timing["translation_ms"] = (perf_counter()-t)*1000
        hits = 0
        for region in regions:
            region.cache_identity = hashlib.sha256((self.identity+"\0"+region.source).encode("utf-8")).hexdigest()
            if not translation_eligible(region):
                region.translated = region.source
            elif region.source in resolved:
                region.translated = resolved[region.source]
            elif use_cache and (self.identity, region.source) in self.cache:
                region.translated = self.cache[self.identity, region.source]
                self.cache.move_to_end((self.identity, region.source))
                hits += 1
            else:
                region.translated = region.source
        # Commit only after every batch succeeds. Model/language identity is fixed
        # for this pipeline's lifetime; changing either requires a new pipeline.
        if use_cache:
            self.cache.update(((self.identity, source), text) for source, text in resolved.items())
            while len(self.cache) > self.capacity:
                self.cache.popitem(last=False)
            self.last_frame, self.last_regions = key, regions
            self.last_size = image.size
        else:
            self.reset_frame()
        timing.update(regions=len(regions), translation_cache_hits=hits,
                      ocr_boxes=sum(len(r.constituent_polygons) for r in regions),
                      translated_strings=len(misses), frame_cache_hit=False,
                      spatial_cache_hits=len(kept), translation_units=len(regions),
                      changed_rectangles=rectangles, tokens_generated=tokens_generated,
                      total_ms=(perf_counter()-start)*1000)
        return regions, timing


def serialize(regions):
    return [asdict(region) for region in regions]
