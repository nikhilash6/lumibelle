import unittest

from runner import verify_flow


class FlowExecutionTests(unittest.TestCase):
    def test_guidance_requires_observed_nonzero_direction_correction(self):
        for fields in ({}, {"mode":"direction", "correction_rms":0},
                       {"mode":"direction", "correction_rms":float("nan")},
                       {"mode":"full", "correction_rms":1}):
            with self.assertRaisesRegex(RuntimeError, "No nonzero"):
                verify_flow({"events":[{"kind":"guidance", "fields":fields}]})

    def test_progressive_requires_both_stages_and_learned_transfer_without_fallback(self):
        evidence = dict(events=[dict(kind="guidance", fields=dict(mode="direction", correction_rms=.001))])
        self.assertTrue(verify_flow(evidence)["nonzero_direction_guidance"])
        with self.assertRaisesRegex(RuntimeError, "requested stages"):
            verify_flow(evidence, progressive=True)
        evidence["events"].extend(dict(kind=kind) for kind in
            ("low_stage_wall", "high_stage_wall", "handoff_learned_upscale_wall"))
        self.assertTrue(verify_flow(evidence, progressive=True)["progressive_learned_handoff"])
        evidence["counters"] = {"progressive_target_fallbacks":1}
        with self.assertRaisesRegex(RuntimeError, "requested stages"):
            verify_flow(evidence, progressive=True)
