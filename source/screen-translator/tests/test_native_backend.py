"""Native bridge regression checks, without loading models or capturing a screen.

Dependencies: stdlib and Pillow from root requirements.txt.
Outputs: terminal unittest report; command: python -m unittest discover -s tests -p test_native_backend.py
"""
import base64
import io
import json
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from screen_translator.backend import Backend, decode_frame, validate_config


class NativeBackendChecks(unittest.TestCase):
    def test_settings_boundary_rejects_invalid_devices_and_batches(self):
        config = {"models": "models/zh-en", "mode": "Balanced", "devices": ["NPU", "GPU", "GPU"],
                  "batch_size": 8, "incremental": True}
        self.assertTrue(Path(validate_config(config)["models"]).is_absolute())
        for changed in ({"devices": ["AUTO", "GPU"]}, {"batch_size": True}, {"batch_size": 0},
                        {"incremental": "yes"}, {"mode": "unknown"}):
            with self.assertRaises(ValueError):
                validate_config({**config, **changed})

    def test_png_transport_preserves_physical_dimensions_and_negative_origin(self):
        from PIL import Image
        with Image.new("RGB", (8, 6), (20, 40, 60)) as image:
            stream = io.BytesIO()
            image.save(stream, format="PNG")
        payload = {"png": base64.b64encode(stream.getvalue()).decode(), "key": ["monitor", -100, 12, 8, 6], "cache": True}
        decoded, context = decode_frame(payload)
        with decoded:
            self.assertEqual(decoded.getpixel((0, 0)), (20, 40, 60))
            self.assertEqual(context["key"], ("monitor", -100, 12, 8, 6))
        with self.assertRaises(ValueError):
            decode_frame({**payload, "key": ["monitor", -100, 12, 9, 6]})

    def test_stop_suppresses_stale_work_and_accepts_immediate_reload(self):
        output = io.StringIO()
        backend = Backend(output)
        backend.submit({"id": "old", "command": "load", "payload": {}})
        backend.submit({"id": "stop", "command": "stop"})
        backend.submit({"id": "new", "command": "load", "payload": {}})
        backend.thread.start()
        # Queue completion is observed through the protocol, not an inference timing assertion.
        from time import monotonic, sleep
        deadline = monotonic() + 3
        while '"id": "new"' not in output.getvalue() and monotonic() < deadline:
            sleep(.01)
        backend.closed.set()
        backend.thread.join(timeout=2)
        messages = [json.loads(line) for line in output.getvalue().splitlines()]
        self.assertNotIn("old", [message["id"] for message in messages])
        self.assertEqual([message["event"] for message in messages], ["stopped", "failed"])
        self.assertEqual(messages[-1]["id"], "new")
        self.assertFalse(backend.thread.is_alive())


if __name__ == "__main__":
    unittest.main(verbosity=2)
