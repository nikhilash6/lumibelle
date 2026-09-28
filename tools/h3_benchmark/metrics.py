"""Benchmark-only telemetry; does not register or modify ComfyUI nodes."""
import ctypes
import json
import os
from pathlib import Path
import threading
import time
from contextlib import contextmanager

GIB = 1024 ** 3


def memory_classification(minimum_free, failed=False):
    if failed:
        return "failed"
    if minimum_free is None:
        return "unknown"
    return "comfortable" if minimum_free >= 2 * GIB else "tight"


def atomic_json(path, value):
    path = Path(path)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, allow_nan=False), encoding="utf-8")
    temporary.replace(path)


class DeviceMemory:
    class Memory(ctypes.Structure):
        _fields_ = [("total", ctypes.c_ulonglong), ("free", ctypes.c_ulonglong), ("used", ctypes.c_ulonglong)]

    def __init__(self, uuid):
        self.lib = ctypes.CDLL("libnvidia-ml.so.1")
        if self.lib.nvmlInit_v2() != 0:
            raise RuntimeError("NVML initialization failed")
        self.handle = ctypes.c_void_p()
        if self.lib.nvmlDeviceGetHandleByUUID(uuid.encode(), ctypes.byref(self.handle)) != 0:
            raise RuntimeError("Cannot resolve the benchmark GPU UUID")

    def read(self):
        memory = self.Memory()
        if self.lib.nvmlDeviceGetMemoryInfo(self.handle, ctypes.byref(memory)) != 0:
            raise RuntimeError("NVML memory read failed")
        return dict(total=memory.total, used=memory.used, free=memory.free)


class Measurements:
    def __init__(self, directory, torch, uuid, remaining_seconds):
        import psutil
        self.directory = Path(directory)
        self.directory.mkdir(parents=True, exist_ok=True)
        self.torch, self.psutil = torch, psutil
        self.device = DeviceMemory(uuid)
        self.process = psutil.Process()
        self.started = time.monotonic()
        self.deadline = self.started + remaining_seconds
        self.lock = threading.RLock()
        self.active = None
        self.rows = []
        self.running = True
        self.peak_used = 0
        self.minimum_free = None
        self.peak_rss = 0
        self.minimum_host_available = None
        self.peak_host_swap_used = 0
        self.sampling_errors = []
        self.baseline = self.device.read()
        self.thread = threading.Thread(target=self._monitor, daemon=True)
        self.thread.start()

    def observe(self):
        memory = self.device.read()
        rss = self.process.memory_info().rss
        available = self.psutil.virtual_memory().available
        swap_used = self.psutil.swap_memory().used
        with self.lock:
            self.peak_used = max(self.peak_used, memory["used"])
            self.minimum_free = min(memory["free"], self.minimum_free if self.minimum_free is not None else memory["free"])
            self.peak_rss = max(self.peak_rss, rss)
            self.minimum_host_available = min(available, self.minimum_host_available if self.minimum_host_available is not None else available)
            self.peak_host_swap_used = max(swap_used, self.peak_host_swap_used)
            if self.active is not None:
                self.active["sampled_peak_device_used"] = max(self.active["sampled_peak_device_used"], memory["used"])
                self.active["sampled_minimum_device_free"] = min(self.active["sampled_minimum_device_free"], memory["free"])
                self.active["peak_process_rss"] = max(self.active["peak_process_rss"], rss)
            return dict(elapsed=time.monotonic()-self.started, stage=self.active["name"] if self.active else None,
                        device=memory, process_rss=rss, host_available=available, host_swap_used=swap_used)

    def _monitor(self):
        next_log = 0
        with (self.directory / "memory.jsonl").open("w", encoding="utf-8") as log:
            while self.running:
                elapsed = time.monotonic() - self.started
                if time.monotonic() >= self.deadline:
                    self.flush("case_timeout")
                    os._exit(124)  # This isolated worker only; never interrupt the production server.
                try:
                    sample = self.observe()
                    if elapsed >= next_log:
                        log.write(json.dumps(sample) + "\n")
                        log.flush()
                        next_log = elapsed + 1
                except Exception as error:
                    with self.lock:
                        self.sampling_errors.append(str(error))
                time.sleep(.1)

    @contextmanager
    def stage(self, name):
        self.torch.cuda.synchronize()
        self.torch.cuda.reset_peak_memory_stats()
        before = self.observe()["device"]
        started = time.monotonic()
        row = dict(name=name, started_seconds=started-self.started, baseline_device_used=before["used"],
                   sampled_peak_device_used=before["used"], sampled_minimum_device_free=before["free"], peak_process_rss=0)
        with self.lock:
            if self.active is not None:
                raise RuntimeError("Stage scopes must not overlap")
            self.active = row
        print("STAGE " + name, flush=True)
        try:
            yield row
        except BaseException as error:
            row["error"] = type(error).__name__ + ": " + str(error)
            raise
        finally:
            try:
                self.torch.cuda.synchronize()
            except Exception:
                pass
            row["seconds"] = time.monotonic() - started
            row["torch_peak_allocated"] = self.torch.cuda.max_memory_allocated()
            row["torch_peak_reserved"] = self.torch.cuda.max_memory_reserved()
            row["allocator_backend"] = self.torch.cuda.get_allocator_backend()
            # cudaMallocAsync can report zero/unsupported counters; never treat this as zero VRAM usage.
            row["torch_counters_available"] = row["torch_peak_allocated"] > 0
            # Capture endpoints too, including stages shorter than the sampling interval.
            try:
                self.observe()
            except Exception as error:
                self.sampling_errors.append(str(error))
            with self.lock:
                self.rows.append(row)
                self.active = None
            self.flush("running")
            print("STAGE_DONE " + json.dumps(row), flush=True)

    def flush(self, status):
        with self.lock:
            atomic_json(self.directory / "measurements.json", dict(status=status,
                elapsed_seconds=time.monotonic()-self.started, baseline=self.baseline,
                sampled_peak_device_used=self.peak_used, sampled_minimum_device_free=self.minimum_free,
                peak_process_rss=self.peak_rss, minimum_host_available=self.minimum_host_available,
                peak_host_swap_used=self.peak_host_swap_used,
                classification=memory_classification(self.minimum_free, status in ("oom", "failed")),
                sample_interval_seconds=.1, sampled_peaks_are_lower_bounds=True,
                sampling_errors=self.sampling_errors, stages=self.rows, active_stage=self.active))

    def close(self, status):
        self.running = False
        self.thread.join(timeout=2)
        self.flush(status)
