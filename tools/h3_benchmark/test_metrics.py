import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

from metrics import GIB, Measurements, atomic_json, memory_classification


class MetricsTests(unittest.TestCase):
    def test_headroom_boundary_and_missing_observation(self):
        self.assertEqual(memory_classification(2*GIB), "comfortable")
        self.assertEqual(memory_classification(2*GIB-1), "tight")
        self.assertEqual(memory_classification(None), "unknown")
        self.assertEqual(memory_classification(10*GIB, failed=True), "failed")

    def test_json_replaces_complete_document_and_rejects_nonfinite(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder)/"result.json"
            atomic_json(path, {"status":"running"})
            atomic_json(path, {"status":"complete", "minimum_free":None})
            self.assertEqual(json.loads(path.read_text()), {"status":"complete", "minimum_free":None})
            with self.assertRaises(ValueError):
                atomic_json(path, {"value":float("nan")})
            self.assertEqual(json.loads(path.read_text())["status"], "complete")

    def test_short_stage_endpoints_capture_peak_and_preserve_failure(self):
        gpu = {"total":20*GIB, "used":GIB, "free":19*GIB}
        device = Mock()
        device.read.side_effect = lambda:gpu.copy()
        process = SimpleNamespace(memory_info=lambda:SimpleNamespace(rss=3*GIB))
        psutil = SimpleNamespace(Process=lambda:process,
            virtual_memory=lambda:SimpleNamespace(available=10*GIB),
            swap_memory=lambda:SimpleNamespace(used=GIB))
        cuda = Mock()
        cuda.max_memory_allocated.return_value = 0
        cuda.max_memory_reserved.return_value = 0
        cuda.get_allocator_backend.return_value = "cudaMallocAsync"
        with tempfile.TemporaryDirectory() as folder, patch.dict("sys.modules", {"psutil":psutil}), \
             patch("metrics.DeviceMemory",return_value=device), patch("metrics.threading.Thread"):
            measurement = Measurements(folder,SimpleNamespace(cuda=cuda),"test-gpu",60)
            with self.assertRaisesRegex(ValueError,"test stage failed"):
                with measurement.stage("decode"):
                    gpu.update(used=19*GIB,free=GIB)
                    raise ValueError("test stage failed")
            measurement.close("failed")
            saved = json.loads((Path(folder)/"measurements.json").read_text())
            self.assertEqual(saved["sampled_peak_device_used"],19*GIB)
            self.assertEqual(saved["sampled_minimum_device_free"],GIB)
            self.assertEqual(saved["peak_host_swap_used"],GIB)
            self.assertEqual(saved["classification"],"failed")
            self.assertFalse(saved["stages"][0]["torch_counters_available"])
            self.assertEqual(saved["stages"][0]["error"],"ValueError: test stage failed")


if __name__ == "__main__":
    unittest.main()
