"""Editable folder exclusions. Dependencies: stdlib. Output: excluded-folders.txt.

Used by python -m xiaomi_search.backend --config <profile>/config.json.
"""
import ntpath
import os
from pathlib import Path


def canonical_path(path):
    """Normalize a path for boundary comparisons, including Windows case and separators."""
    raw = str(path)
    windows = os.name == 'nt' or bool(ntpath.splitdrive(raw)[0]) or '\\' in raw
    normalized = ntpath.normpath(raw) if windows else os.path.abspath(raw)
    normalized = normalized.replace('\\', '/').rstrip('/')
    return normalized.casefold() if windows else normalized


def is_within(path, folder):
    """Include the folder itself and every descendant, without excluding sibling prefixes."""
    candidate, ancestor = canonical_path(path), canonical_path(folder)
    return candidate == ancestor or candidate.startswith(ancestor + '/')


def read_paths(path):
    """Read absolute paths, ignoring empty lines and whole-line # comments."""
    path = Path(path)
    text = path.read_text(encoding="utf-8-sig")
    # Repair the old deployment's literal CRLF delimiters, not Windows path escapes.
    if text.startswith('#') and '\\r\\n' in text:
        text = text.replace('\\r\\n', '\n')
        path.write_text(text, encoding='utf-8')
    paths = []
    for number, line in enumerate(text.splitlines(), 1):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        if not Path(line).is_absolute():
            raise ValueError(f"Exclusion line {number} must be an absolute folder path")
        paths.append(str(Path(line).resolve()))
    return list(dict.fromkeys(paths))


def refresh(config, config_path):
    """Make the adjacent text file authoritative, migrating existing settings once."""
    for key, filename in [('excluded_folders', 'excluded-folders.txt'), ('roots', 'included-folders.txt')]:
        path = Path(config_path).parent / filename
        try:
            with path.open("x", encoding="utf-8") as stream:
                stream.write('# One absolute folder path per line.\n')
                stream.write("\n".join(config[key]) + "\n")
        except FileExistsError:
            pass
        config[key] = read_paths(path)
    return Path(config_path).parent / 'excluded-folders.txt'
