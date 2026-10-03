"""Small isolated check of retrieval, stale-data handling, and bridge boundaries.

Dependencies: Python 3.12 + numpy. Outputs: temporary SQLite index and PASS text.
Command from project root: python tests/check_core.py
Uses synthetic vectors, not the real model. Does not measure semantic quality.
"""
import json
import socket
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import numpy as np

from xiaomi_search.config import PROJECT, validate
from xiaomi_search.extract import chunks
from xiaomi_search.embedding import Embedder
from xiaomi_search.service import Service
from xiaomi_search.store import highlighted


def main():
    original_socket = socket.socket

    def no_network(*args, **kwargs):
        raise AssertionError("Core attempted a network connection")

    socket.socket = no_network
    try:
        with tempfile.TemporaryDirectory(prefix="xiaomi-search-check-") as temporary:
            directory = Path(temporary)
            corpus = directory / "corpus"
            corpus.mkdir()
            config = json.loads((PROJECT / "config.example.json").read_text(encoding="utf-8-sig"))
            config.update(roots=[str(corpus)], semantic_enabled=False, index_protection='none')
            config = validate(config, directory / "config.json")
            service = Service(config, directory / "data", directory / "config.json")
            first = corpus / "cohesion.md"
            second = corpus / "budget.md"
            first.write_text("# Shared particles\nA single coefficient vector is reused for every particle.\n<script>alert(1)</script>", encoding="utf-8")
            second.write_text("Monthly household expenses and accounts.", encoding="utf-8")
            for path in (first, second):
                row = service.store.discover(path, path.stat())
                service.store.save(row["id"], path.stat(), "fixture", chunks(path, config), service.indexer.extraction_key)
            results = service.search({"text": "cohesion.md", "semantic": False})["results"]
            assert results[0]["name"] == "cohesion.md", "Exact filename must rank first"
            assert service.search({"text": "coefficient ext:md", "semantic": False})["results"][0]["name"] == "cohesion.md"
            assert service.search({"text": 'coefficient ext:pdf', "semantic": False})["results"] == []
            assert service.search({"text": "cohesion.md", "semantic": False})["timing"]["cache_hit"]
            folder = corpus / "folder-fixture"
            folder.mkdir()
            service.store.discover(folder, folder.stat())
            assert service.search({"text": "folder-fixture", "semantic": False})["results"][0]["file_type"] == 2
            service.dispatch("local_save_config", {"excluded_extensions": ["md"]})
            assert not service.indexer.allowed(first)
            assert not service.search({"text": "cohesion", "semantic": False})["results"], "Excluded types must hide names and content immediately"
            service.dispatch("local_save_config", {"excluded_extensions": [], "excluded_folders": [str(folder)]})
            assert not service.indexer.allowed(folder)
            assert not service.search({"text": "folder-fixture", "semantic": False})["results"]
            service.dispatch("local_save_config", {"excluded_folders": []})
            for path in (first, second):
                service.store.discover(path, path.stat())
            assert "<script>" not in highlighted(first.read_text(encoding="utf-8"), ["particle"])
            assert "&lt;script&gt;" in highlighted(first.read_text(encoding="utf-8"), ["particle"])
            assert not service.indexer.allowed(directory / "outside.md"), "Outside-root file must not be readable through the bridge"
            assert not service.indexer.allowed(corpus / "node_modules" / "package.js")

            class SyntheticEmbedder:
                config = {"semantic_threshold": .3}

                def identity(self):
                    return "synthetic-v1"

                def encode(self, texts, query=False):
                    return np.asarray([[1, 0]], dtype=np.float32)  # shape: [1,2]

            fid = service.store.get_file(path=str(first))["id"]
            for row in service.store.pending_vectors(fid, "synthetic-v1"):
                service.store.put_vector(row["id"], np.asarray([1, 0], dtype=np.float32), "synthetic-v1")  # shape: [2]
            hybrid = service.store.search("common interpolation weights", SyntheticEmbedder())
            assert hybrid["results"][0]["name"] == "cohesion.md"
            assert "Semantic" in hybrid["results"][0]["matches"], "Vector channel must contribute without literal overlap"
            second.write_text("Utilizing a library for unrelated accounting.", encoding="utf-8")
            row = service.store.get_file(path=str(second))
            service.store.save(row["id"], second.stat(), "coverage-fixture", chunks(second, config), service.indexer.extraction_key)
            for passage in service.store.pending_vectors(row["id"], "synthetic-v1"):
                # The decoy is weaker semantically, but has one incidental lexical word.
                service.store.put_vector(passage["id"], np.asarray([.8, .6], dtype=np.float32), "synthetic-v1")  # shape: [2]
            ranking = service.store.search("learningless synthesis utilizing collective repulsions", SyntheticEmbedder())["results"]
            assert ranking[0]["name"] == "cohesion.md", "One incidental word must not outweigh the strongest meaning-only match"
            assert service.store.search("budget.md", SyntheticEmbedder())["results"][0]["name"] == "budget.md", "Exact filename priority must survive semantic fusion"

            preview = service.dispatch("get_filecontent_matchpoints", {"file_id": fid, "text": "coefficient"})
            assert preview["txt_match_points"] and "position" in preview["txt_match_points"][0]
            denied = service.rpc({"mode": 0, "data": {"id": 1, "request": {"method": "open_file", "params": {"file_path": str(directory / "outside.md")}}}})
            assert denied["response"]["code"] != 0
            response = service.rpc({"mode": 0, "data": {"id": 2, "request": {"method": "search", "params": {"text": "cohesion", "semantic": False}}}})
            assert response["id"] == 2 and response["response"]["data"]["best_match"]
            assert service.search({"text": "coefficient", "semantic": False})["results"]
            service.store.record_error(fid, "Changed file cannot be extracted")
            assert not service.search({"text": "coefficient", "semantic": False})["results"], "Cached stale content from failed extraction must be withheld"
            assert service.store.search("cohesion")["results"], "Filename search must survive extraction failure"
            assert service.search({"text": "cohesion", "semantic": False})["results"]
            service.store.mark_missing(first)
            assert not service.search({"text": "cohesion", "semantic": False})["results"], "Missing files must disappear from cached retrieval"
            service.close()
            standby = Embedder(dict(config, model_standby="keep_loaded", idle_unload_seconds=3600), directory)
            standby.pipeline = object()  # Sentinel verifies policy without loading a model.
            standby.schedule_release()
            assert standby.timer is None and standby.pipeline is not None
            standby.config["model_standby"] = "idle_unload"
            standby.schedule_release()
            assert standby.timer is not None and standby.timer.is_alive()
            previous_timer = standby.timer
            standby.config["model_standby"] = "keep_loaded"
            standby.schedule_release()
            assert previous_timer.finished.is_set() and standby.timer is None
            assert standby.pipeline is not None
            standby.unload()
            assert standby.pipeline is None
        print("PASS: exact/lexical/vector retrieval, filters, escaped snippets, stale files, and bridge boundaries")
    finally:
        socket.socket = original_socket


if __name__ == "__main__":
    main()
