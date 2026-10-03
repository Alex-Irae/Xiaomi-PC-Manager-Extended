"""Lazy, offline OpenVINO embeddings using an existing unencrypted local model."""
import hashlib
import json
import logging
import threading
from pathlib import Path

LOG = logging.getLogger(__name__)


class Embedder:
    def __init__(self, config, data):
        self.config = config
        self.data = Path(data)
        self.lock = threading.RLock()
        self.pipeline = None
        self.device = None
        self.error = None
        self.timer = None
        self._identity = None
        self._file_state = None
        self.failed_devices = set()
        self.on_change = lambda: None

    def identity(self):
        """Fingerprint weights, tokenizer, pooling, and truncation to avoid mixing vectors."""
        with self.lock:
            return self._fingerprint()

    def _fingerprint(self):
        root = Path(self.config["model_path"])
        names = ("openvino_model.xml", "openvino_model.bin", "openvino_tokenizer.xml", "openvino_tokenizer.bin", "config.json")
        profile_state = (str(root.resolve()), self.config.get('embedding_pooling', 'LAST_TOKEN'), self.config.get('embedding_padding_side', 'left'), self.config['max_tokens'], self.config['query_instruction'])
        state = (profile_state, tuple((name, (root / name).stat().st_size, (root / name).stat().st_mtime_ns) for name in names))
        if self._identity is None or state != self._file_state:
            if self._file_state is not None and state != self._file_state:
                self.unload()
                self.failed_devices.clear()
            digest = hashlib.sha256()
            for name in names:
                digest.update(name.encode())
                with (root / name).open("rb") as stream:
                    digest.update(hashlib.file_digest(stream, "sha256").digest())
            profile = {"pooling": self.config.get('embedding_pooling','LAST_TOKEN'), "normalization": "L2", "max_tokens": self.config["max_tokens"], "query_instruction": self.config["query_instruction"]}
            # Preserve existing left-padded Qwen identities while separating other exports.
            if self.config.get('embedding_padding_side', 'left') != 'left':
                profile['padding_side'] = self.config['embedding_padding_side']
            digest.update(json.dumps(profile, sort_keys=True).encode())
            self._identity = digest.hexdigest()
            self._file_state = state
        return self._identity

    def _load(self):
        if self.pipeline is not None:
            return
        import openvino_genai as genai
        failures = []
        preferred = self.config["preferred_device"]
        devices = self.config["devices"] if preferred == "auto" else list(dict.fromkeys([preferred.upper(), "CPU"]))
        for device in devices:
            if device in self.failed_devices:
                continue
            try:
                settings = genai.TextEmbeddingPipeline.Config()
                settings.max_length = self.config["max_tokens"]
                settings.batch_size = 1
                settings.pad_to_max_length = True
                settings.padding_side = self.config.get('embedding_padding_side','left')
                settings.pooling_type = getattr(genai.TextEmbeddingPipeline.PoolingType,self.config.get('embedding_pooling','LAST_TOKEN'))
                settings.normalize = True
                settings.query_instruction = self.config["query_instruction"]
                cache = self.data / "model_cache" / device
                cache.mkdir(parents=True, exist_ok=True)
                LOG.info("Loading local embedding model on %s", device)
                self.pipeline = genai.TextEmbeddingPipeline(self.config["model_path"], device, settings, CACHE_DIR=str(cache))
                self.device, self.error = device, None
                self.on_change()
                return
            except Exception as exc:
                failures.append(f"{device}: {exc}")
                self.failed_devices.add(device)
                LOG.warning("Embedding device unavailable: %s", failures[-1])
        self.error = "; ".join(failures) or "No configured embedding device is available; restart after fixing the model or driver"
        raise RuntimeError(self.error)

    def encode(self, texts, query=False):
        """Return float32 normalized vectors [N,D]; serialize native inference.

        Model input is capped by max_tokens. Queries and passages use distinct
        instructions. No network lookup, model download, or broker IPC occurs.
        """
        import numpy as np
        with self.lock:
            if self.timer:
                self.timer.cancel()
            try:
                self.identity()
                self._load()
                # Batch one keeps the exported model shape compatible with NPU.
                rows = []
                for text in texts:
                    try:
                        value = self.pipeline.embed_query(text) if query else self.pipeline.embed_documents([text])[0]
                    except Exception:
                        # Some devices compile successfully but fail on first inference.
                        self.failed_devices.add(self.device)
                        self.pipeline, self.device = None, None
                        self._load()
                        value = self.pipeline.embed_query(text) if query else self.pipeline.embed_documents([text])[0]
                    rows.append(value)
                matrix = np.asarray(rows, dtype=np.float32)  # shape: [N,D]
                if matrix.ndim != 2 or not np.isfinite(matrix).all():
                    raise ValueError("Embedding model returned malformed vectors")
                # Unit length makes dot products equal cosine similarity.
                norms = np.linalg.norm(matrix, axis=1, keepdims=True)  # shape: [N,1]
                if np.any(norms <= 1e-12):
                    raise ValueError("Embedding model returned a zero vector")
                return matrix / norms  # shape: [N,D], broadcasts norms across D
            except Exception as exc:
                self.error = str(exc)
                raise
            finally:
                self.schedule_release()

    def schedule_release(self):
        """Apply standby without running inference; keep-loaded retains memory only."""
        with self.lock:
            if self.timer:
                self.timer.cancel()
            self.timer = None
            if self.pipeline is not None and self.config["model_standby"] == "idle_unload":
                self.timer = threading.Timer(self.config["idle_unload_seconds"], self.unload)
                self.timer.daemon = True
                self.timer.start()

    def unload(self):
        with self.lock:
            self.pipeline, self.device = None, None
            if self.timer:
                self.timer.cancel()
                self.timer = None
            LOG.info("Embedding model released; no idle inference")
            self.on_change()
