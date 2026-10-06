"""Read explicit local configuration; never install, download, or scan by default."""
import json
import re
import time
from pathlib import Path

PROJECT = Path(__file__).resolve().parents[1]


def write_atomic(path, text):
    """Replace a UTF-8 settings file, tolerating brief Windows reader locks."""
    path = Path(path)
    pending = path.with_name(path.name + '.tmp')
    pending.write_text(text, encoding='utf-8')
    for attempt in range(8):
        try:
            pending.replace(path)
            return
        except PermissionError:
            if attempt == 7:
                raise
            time.sleep(.01 * 2**attempt)


def load(path):
    config = json.loads((PROJECT / "config.example.json").read_text(encoding="utf-8-sig"))
    path = Path(path).resolve()
    if path.is_file():
        custom = json.loads(path.read_text(encoding="utf-8-sig"))
        if not isinstance(custom, dict) or set(custom) - set(config):
            raise ValueError("Configuration contains unknown settings or is not an object")
        config.update(custom)
    from .exclusions import refresh
    refresh(config, path)
    return validate(config, path)


def validate(config, path):
    path = Path(path).resolve()
    if not isinstance(config["roots"], list) or any(not isinstance(p, str) for p in config["roots"]):
        raise ValueError("roots must be a list of directory paths")
    if any(not Path(p).is_absolute() for p in config['roots']):
        raise ValueError('Included folders must be absolute paths')
    config["roots"] = list(dict.fromkeys(str(Path(p).resolve()) for p in config["roots"]))
    if any(not Path(p).is_dir() for p in config["roots"]):
        raise ValueError("Each configured root must be an existing directory")
    if not isinstance(config["excluded_names"], list) or any(not isinstance(n, str) for n in config["excluded_names"]):
        raise ValueError("excluded_names must be a list of directory names")
    if config["indexing_mode"] not in ("normal", "battery_saver", "paused"):
        raise ValueError("indexing_mode must be normal, battery_saver, or paused")
    if config["indexing_load"] not in ("100", "75", "50", "25"):
        raise ValueError("indexing_load must be 100, 75, 50 or 25")
    if config['indexing_frequency'] not in ('realtime', '5_minutes', '15_minutes', 'hourly', 'daily', 'manual'):
        raise ValueError('Unsupported indexing frequency')
    if config['theme'] not in ('system', 'light', 'dark') or config['index_protection'] not in ('windows', 'none', 'efs'):
        raise ValueError('Unsupported theme or index protection')
    if any(type(config[key]) is not bool for key in ('name_enabled', 'content_enabled', 'semantic_enabled', 'windows_semantic_enabled')) or not any(config[key] for key in ('name_enabled', 'content_enabled', 'semantic_enabled', 'windows_semantic_enabled')):
        raise ValueError('Enable at least one search channel: names, contents, meaning, or Windows')
    if not isinstance(config["shortcut"], str) or not config["shortcut"].strip() or len(config["shortcut"]) > 80:
        raise ValueError("shortcut must be a nonempty key combination of at most 80 characters")
    if type(config["follow_suite_appearance"]) is not bool:
        raise ValueError("follow_suite_appearance must be true or false")
    if type(config['programs_enabled']) is not bool:
        raise ValueError('programs_enabled must be a boolean')
    if type(config['run_at_startup']) is not bool:
        raise ValueError('run_at_startup must be a boolean')
    # Older settings files have no entry: AI Center's tray icon then stays off at sign-in.
    if type(config.setdefault('center_at_startup', False)) is not bool:
        raise ValueError('center_at_startup must be a boolean')
    for key in ("excluded_folders", "excluded_extensions"):
        if not isinstance(config[key], list) or any(not isinstance(v, str) or not v.strip() for v in config[key]):
            raise ValueError(f"{key} must be a list of nonempty strings")
    if any(not Path(p).is_absolute() for p in config['excluded_folders']):
        raise ValueError('Excluded folders must be absolute paths')
    config["excluded_folders"] = list(dict.fromkeys(str(Path(p).resolve()) for p in config["excluded_folders"]))
    if any(not re.fullmatch(r"\.?[\w+-]+", v) for v in config["excluded_extensions"]):
        raise ValueError("Excluded file types must be extensions such as .pdf or .py")
    config["excluded_extensions"] = list(dict.fromkeys("." + v.lower().lstrip(".") for v in config["excluded_extensions"]))
    if config["preferred_device"] not in ("auto", "cpu", "gpu", "npu"):
        raise ValueError("preferred_device must be auto, cpu, gpu, or npu")
    if config["model_standby"] not in ("idle_unload", "keep_loaded"):
        raise ValueError("model_standby must be idle_unload or keep_loaded")
    if not re.fullmatch(r"#[0-9a-fA-F]{6}", config["accent_color"]):
        raise ValueError("accent_color must be a six-digit hex color")
    if config["font_family"] not in ("Segoe UI", "MiSans", "Arial") or config["bar_size"] not in ("compact", "comfortable") or config["arrow_style"] not in ("arrow", "chevron"):
        raise ValueError("Unsupported appearance setting")
    if not isinstance(config["devices"], list) or not config["devices"] or any(d not in ("NPU", "GPU", "CPU") for d in config["devices"]):
        raise ValueError("devices must be an ordered list of NPU, GPU, CPU")
    for name in ("max_tokens", "chunk_characters", "max_file_mb", "max_extracted_characters", "max_chunks_per_file", "idle_unload_seconds", "double_ctrl_ms"):
        if type(config[name]) is not int or config[name] <= 0:
            raise ValueError(f"{name} must be a positive integer")
    if not 128 <= config["max_tokens"] <= 4096:
        raise ValueError("max_tokens must be between 128 and 4096")
    if type(config["overlap_characters"]) is not int or not 0 <= config["overlap_characters"] < config["chunk_characters"]:
        raise ValueError("overlap_characters must be smaller than chunk_characters")
    if type(config["semantic_enabled"]) is not bool or not isinstance(config["semantic_threshold"], (float, int)) or not 0 <= config["semantic_threshold"] < 1:
        raise ValueError("Invalid semantic settings")
    if not isinstance(config["model_path"], str) or not isinstance(config["query_instruction"], str):
        raise ValueError("model_path and query_instruction must be strings")
    if config['embedding_pooling'] not in ('LAST_TOKEN','CLS','MEAN') or config['embedding_padding_side'] not in ('left','right'):
        raise ValueError('Unsupported embedding pooling or padding side')
    if config["model_path"]:
        model = Path(config["model_path"])
        config["model_path"] = str((path.parent / model).resolve() if not model.is_absolute() else model.resolve())
    return config
