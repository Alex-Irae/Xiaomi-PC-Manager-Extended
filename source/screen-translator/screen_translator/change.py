"""Exact CPU tile hashes and conservative dirty-region expansion, no inference."""
import hashlib


def intersects(a, b):
    return a[0] < b[2] and b[0] < a[2] and a[1] < b[3] and b[1] < a[3]


def union(a, b):
    return min(a[0], b[0]), min(a[1], b[1]), max(a[2], b[2]), max(a[3], b[3])


def tile_hashes(image, tile_size=256):
    """Return {physical-pixel rectangle: digest}; exact changes, including a caret."""
    return {(x, y, min(x+tile_size, image.width), min(y+tile_size, image.height)):
            hashlib.blake2b(image.crop((x, y, min(x+tile_size, image.width),
                                       min(y+tile_size, image.height))).tobytes(), digest_size=16).digest()
            for y in range(0, image.height, tile_size)
            for x in range(0, image.width, tile_size)}


def dirty_rectangles(current, previous, old_regions, size, padding=32):
    """Expand changed tiles around old text and merge overlapping rectangles.

    Re-reading the whole old unit prevents half a changed word remaining cached.
    Missing/new text at crop edges still needs a corpus check; padding is tunable.
    """
    w, h = size
    rectangles = [(max(0, r[0]-padding), max(0, r[1]-padding),
                   min(w, r[2]+padding), min(h, r[3]+padding))
                  for r, value in current.items() if previous.get(r) != value]
    # ponytail: quadratic merging is bounded by screen tiles; replace with a grid if measured slow.
    changed = True
    while changed:
        changed = False
        for i, rect in enumerate(rectangles):
            for region in old_regions:
                bounds = region.bounds
                if intersects(rect, bounds):
                    enlarged = union(rect, (max(0, int(bounds[0])-padding), max(0, int(bounds[1])-padding),
                                           min(w, int(bounds[2])+padding+1), min(h, int(bounds[3])+padding+1)))
                    if enlarged != rect:
                        rect = enlarged
                        changed = True
            rectangles[i] = rect
        for i in range(len(rectangles)):
            match = next((j for j in range(i+1, len(rectangles))
                          if intersects(rectangles[i], rectangles[j])), None)
            if match is not None:
                rectangles[i] = union(rectangles[i], rectangles.pop(match))
                changed = True
                break
    return rectangles
