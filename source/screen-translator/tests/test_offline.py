"""Check cache transactionality, geometric grouping and dirty-region replacement.

Dependencies: Pillow and project modules, no models, network or accelerator.
Outputs: unittest result only.
Command: python -m unittest discover -s tests -p test_offline.py
Execution is user initiated; this file is not imported by the app.
"""
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest.mock import Mock, patch

from PIL import Image
from screen_translator.core import Pipeline, TextRegion, group_regions
from screen_translator.hardware import choose_profile
from screen_translator.provision import asset_matches
from screen_translator.engine import serve
from screen_translator.runtime import execution_info, compile_model, Translator as ModelTranslator


def region(x, y, text, width=20, height=15):
    return TextRegion([[x, y], [x+width, y], [x+width, y+height], [x, y+height]], text, .99)


class Translator:
    def __init__(self):
        self.calls = []
        self.fail = False

    def batch(self, texts):
        self.calls.append(list(texts))
        if self.fail:
            return []
        return ["English "+text for text in texts]


class OfflinePipelineCheck(unittest.TestCase):
    def test_detection_tiles_cover_edges_and_merge_only_shared_seams(self):
        from screen_translator.ocr import detection_windows,merge_tile_boxes
        windows=detection_windows((3120,2080),1280)
        self.assertEqual(len(windows),6)
        for y in range(0,2080,80):
            for x in range(0,3120,80):
                self.assertTrue(any(a<=x<c and b<=y<d for a,b,c,d in windows))
        self.assertEqual(detection_windows((200,100),1280),[(0,0,200,100)])
        def box(left,right):return [[left,20],[right,20],[right,40],[left,40]]
        complete=box(900,1300)
        self.assertEqual(merge_tile_boxes([(box(900,1279),0,True),(complete,1,False)]),[complete])
        joined=merge_tile_boxes([(box(100,1279),0,True),(box(1088,1800),1,True)])
        self.assertEqual(joined,[box(100,1800)])
        self.assertEqual(len(merge_tile_boxes([(box(100,200),0,False),(box(120,220),0,False)])),2)

    def test_ctc_reduction_preserves_winning_ids_and_confidence(self):
        import numpy as np
        import openvino as ov
        from openvino import opset13 as op
        parameter=op.parameter([1,3,4],np.float32)
        core=ov.Core()
        wrapped=Mock(wraps=core)
        wrapped.available_devices=["CPU"]
        wrapped.read_model.return_value=ov.Model([parameter],[parameter])
        compiled,info=compile_model(wrapped,"generated-ctc","CPU",ctc_top1=True)
        values=np.array([[[.1,.7,.2,0],[.5,.2,.1,.2],[.1,.2,.6,.1]]],np.float32)  # [1,3,4]
        output=compiled([values])
        np.testing.assert_array_equal(output[0],values.argmax(axis=2))
        np.testing.assert_allclose(output[1],values.max(axis=2))
        self.assertEqual(info["ctc_classes"],4)

    def test_mixed_language_geometry_and_exact_ui_labels(self):
        mixed = group_regions([region(5,5,"设置"),region(27,5,"English")])
        self.assertEqual([r.source for r in mixed],["设置","English"])
        translator = ModelTranslator.__new__(ModelTranslator)
        translator._generate = Mock(return_value=["Set the language."])
        self.assertEqual(translator.batch(["设置","设置语言。","取消"]),["Settings","Set the language.","Cancel"])
        translator._generate.assert_called_once_with(["设置语言。"])

    def test_execution_devices_preserves_npu_device_string(self):
        compiled = Mock()
        properties = {"EXECUTION_DEVICES": "NPU", "SUPPORTED_PROPERTIES": []}
        compiled.get_property.side_effect = properties.__getitem__
        self.assertEqual(execution_info(compiled)["execution_devices"], ["NPU"])
        properties["EXECUTION_DEVICES"] = ["GPU.0"]
        self.assertEqual(execution_info(compiled)["execution_devices"], ["GPU.0"])

    def test_engine_retries_failed_load_and_keeps_models_resident(self):
        config = {"models": "local-models", "mode": "Balanced", "devices": ["NPU", "GPU", "GPU"],
                  "batch_size": 8, "incremental": True, "generation": 1}
        image = Image.new("RGB", (32, 16), "white")
        context = {"key": ("monitor", 0, 0, 32, 16), "cache": True}
        connection = Mock()
        connection.recv.side_effect = [
            ("load", config), ("load", {**config, "generation": 2}),
            ("frame", (image, context)), ("frame", (image, context)), ("exit", None)]
        pipeline = Mock()
        pipeline.run.return_value = ([], {"total_ms": 0})
        with patch("screen_translator.engine.prefer_system_runtime"), patch(
                "screen_translator.cli.make_pipeline", side_effect=[RuntimeError("unsupported profile"), (pipeline, {})]) as factory:
            serve(connection)
        events = [call.args[0] for call in connection.send.call_args_list]
        self.assertEqual([event for event, _ in events], ["failed", "ready", "result", "result"])
        self.assertEqual(events[1][1]["generation"], 2)
        self.assertEqual(factory.call_count, 2)
        self.assertEqual(pipeline.run.call_count, 2)
        pipeline.reset_frame.assert_called_once()
        connection.close.assert_called_once()

    def test_model_integrity_detects_same_size_corruption(self):
        with TemporaryDirectory() as directory:
            path = Path(directory)/"asset.bin"
            path.write_bytes(b"abc")
            asset = {"size": 3, "sha256": "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"}
            self.assertTrue(asset_matches(path, asset))
            path.write_bytes(b"abd")
            self.assertFalse(asset_matches(path, asset))

    def test_automatic_selection_requires_successful_accelerator(self):
        rows = [
            {"devices": ["CPU"]*3, "ok": True, "p95_ms": 1},
            {"devices": ["NPU"]*3, "ok": False},
            {"devices": ["GPU.0"]*3, "ok": True, "p95_ms": 20},
            {"devices": ["NPU", "GPU.0", "GPU.0"], "ok": True, "p95_ms": 10},
        ]
        self.assertEqual(choose_profile(rows)["devices"], ["NPU", "GPU.0", "GPU.0"])
        self.assertIsNone(choose_profile(rows[:2]))

    def test_cache_geometry_and_failure(self):
        calls = []
        def ocr(image):
            calls.append(image.size)
            return [region(5, 5, "设置"), region(50, 5, "设置"), region(95, 5, "USB 2026")]

        image = Image.new("RGB", (128, 64), "white")
        translator = Translator()
        pipeline = Pipeline(ocr, translator, batch_size=2)
        regions, timing = pipeline.run(image)
        self.assertEqual(translator.calls, [["设置"]])
        self.assertEqual(regions[-1].translated, "USB 2026")
        self.assertEqual(regions[1].bounds, (50, 5, 70, 20))
        _, timing = pipeline.run(image)
        self.assertTrue(timing["frame_cache_hit"])
        self.assertEqual(len(calls), 1)

        joined = group_regions([region(5, 5, "清华"), region(27, 5, "大学")])
        self.assertEqual(joined[0].source, "清华大学")
        self.assertEqual(len(joined[0].constituent_polygons), 2)
        separated = group_regions([region(5, 5, "取消"), region(60, 5, "确认")])
        self.assertEqual(len(separated), 2)

        before = dict(pipeline.cache)
        translator.fail = True
        pipeline.ocr = lambda img: [region(5, 5, "新的内容")]
        image.putpixel((0, 0), (0, 0, 0))
        with self.assertRaises(RuntimeError):
            pipeline.run(image)
        self.assertEqual(dict(pipeline.cache), before)
        self.assertEqual(pipeline.last_regions[0].source, "设置")

    def test_changed_text_and_disappearance(self):
        sizes = []
        def ocr(image):
            sizes.append(image.size)
            found = []
            for index, color in enumerate(image.getdata()):
                if color in ((255, 0, 0), (0, 0, 255)):
                    x, y = index % image.width, index // image.width
                    found.append(region(x-2, y-2, "设置" if color == (255, 0, 0) else "确认", 4, 4))
            return found

        image = Image.new("RGB", (256, 96), "white")
        image.putpixel((20, 20), (255, 0, 0))
        image.putpixel((200, 20), (0, 0, 255))
        pipeline = Pipeline(ocr, Translator(), tile_size=64, padding=4)
        regions, _ = pipeline.run(image)
        image.putpixel((20, 20), (255, 255, 255))
        regions, timing = pipeline.run(image)
        self.assertEqual([item.source for item in regions], ["确认"])
        self.assertEqual(regions[0].bounds, (198, 18, 202, 22))
        self.assertEqual(timing["spatial_cache_hits"], 1)
        self.assertLess(sizes[-1][0], image.width)
        pipeline.reset_frame()
        self.assertTrue(pipeline.cache)
        self.assertIsNone(pipeline.last_frame)


if __name__ == "__main__":
    unittest.main()
