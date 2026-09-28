# ComfyUI text settings and model tests

Text models → ComfyUI → expand a model contains its maximum reply tokens and
temperature. Save/cancel is independent of aliases, provider credentials, and
the collapsed ComfyUI defaults. Identity is the exact model ID plus normalized
ComfyUI server. Asset extraction always uses temperature 0.2. Queue snapshots
capture resolved values; recovery never substitutes newer defaults.

`TimeoutSeconds` now means text inactivity, starting at dispatch. A monotonic
watchdog observes answer/reasoning deltas, increasing counters, and execution
stage changes. Heartbeats, repeated labels/counters, and elapsed-time displays
do not count. Progress can continue indefinitely; cancellation remains immediate.
Provider completion stops monitoring before result persistence/parsing. The
OpenRouter SDK's network timeout bounds individual reads, not total stream time;
HTTP clients have no overall text deadline. Image/video deadlines are unchanged.

Advanced ComfyUI tests accept 1–32,768 reply tokens, independently of the model's
saved output limit. The quick benchmark still uses 256 tokens. Test results
report observed speed and memory; they do not recommend a maximum context or
reply allowance. Capacity calibration is no longer available.

CI uses fake clocks, transports, and catalogs. Live GPU tests remain explicit
user actions and are never part of CI.
