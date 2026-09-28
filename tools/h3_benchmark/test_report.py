import json
from pathlib import Path
import tempfile
import unittest

from app_preview_worker import manifest_for, output_record
from report import candidate_rows, clip_card, generate


class ReportTests(unittest.TestCase):
    def test_application_workflow_receipt_generates_clip_page_and_stage_totals(self):
        actual = dict(width=1344, height=768, frames=73, fps=24)
        manifest = manifest_for(dict(name='app-20-upscaled', seed=123, expected=actual), 'request-hash')
        manifest['outputs'] = [output_record('lumibelle/run/video.mp4', 'video-hash', actual)]
        measurements = dict(status='complete', stages=[dict(name=name, seconds=seconds) for name, seconds in [
            ('1/UNETLoader', 2), ('10/SamplerCustomAdvanced', 100), ('40/LTXVSeparateAVLatent', 1),
            ('41/MinimaxH3LatentUpscaler3D', 5), ('42/LTXVConcatAVLatent', 1), ('11/VAEDecode', 3),
            ('12/VAEDecodeAudio', 1), ('13/CreateVideo', 2), ('14/SaveVideo', 4),
            ('17/RebatchImages', 1), ('15/SaveAnimatedWEBP', 6)]])
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); result = root/'results'/'app-20-upscaled'; result.mkdir(parents=True)
            (root/'budget.json').write_text(json.dumps(dict(ConsumedSeconds=130, Runs=[
                dict(Name='app-20-upscaled', Case='preview-app', Status='complete', Seconds=130)])))
            (result/'manifest.json').write_text(json.dumps(manifest))
            (result/'measurements.json').write_text(json.dumps(measurements))
            generate(root)
            row = json.loads((root/'comparison.json').read_text())['candidates'][0]
            self.assertEqual(row['seconds'], dict(preparation=2, sampling=100, upscaling=7, decoding=6, saving=11))
            self.assertEqual(row['stage_total_seconds'], 126)
            page = (root/'clips.html').read_text(encoding='utf-8')
            self.assertIn('app-20-upscaled', page)
            self.assertIn('1344 × 768', page)
            self.assertIn('results/app-20-upscaled/lumibelle/run/video.mp4', page)
            self.assertNotIn('Not finalized', page)

    def test_failed_or_interrupted_attempt_is_never_a_completed_candidate(self):
        self.assertEqual(candidate_rows("r",{}, {"status":"complete"}, "production_work_arrived"),[])
        self.assertEqual(candidate_rows("r",{}, {"status":"failed"}, "complete"),[])

    def test_branch_timing_excludes_other_branch_and_diagnostic_encodes(self):
        def stage(name,seconds):
            return dict(name=name,seconds=seconds,sampled_minimum_device_free=3*1024**3,
                        sampled_peak_device_used=17*1024**3,torch_counters_available=True)
        manifest = dict(frames=73,outputs=[dict(label="lbh",file="lbh.mp4",validation={}),
                                          dict(label="lbh-upscaled",file="raw.mp4",validation={})])
        metrics = dict(status="complete",stages=[stage("load-h3",10),stage("base-sampling",100),
            stage("preview/decode-video",40),stage("lbh/upscale",5),stage("lbh/refine-sampling",50),
            stage("lbh/decode-video",20),stage("lbh/encode-mp4",2),stage("lbh/archive-webp",8),
            stage("lbh-upscaled/decode-video",40),stage("plus/upscale",500)])
        rows = candidate_rows("r",manifest,metrics,"complete")
        self.assertEqual(len(rows),1)
        self.assertEqual(rows[0]["stage_total_seconds"],195)
        self.assertEqual(rows[0]["seconds"]["sampling"],150)
        self.assertEqual(rows[0]["seconds"]["saving"],10)
        self.assertEqual(rows[0]["classification"],"comfortable")

        manifest["outputs"].append(dict(label="preview",file="preview.mp4",validation={}))
        diagnostics = {r["label"]:r for r in candidate_rows("r",manifest,metrics,"complete",include_diagnostics=True)}
        self.assertEqual(diagnostics["lbh-upscaled"]["stage_total_seconds"],155)
        self.assertEqual(diagnostics["lbh-upscaled"]["seconds"]["sampling"],100)
        self.assertEqual(diagnostics["lbh-upscaled"]["seconds"]["upscaling"],5)
        self.assertEqual(diagnostics["preview"]["stage_total_seconds"],150)
        self.assertEqual(diagnostics["preview"]["seconds"]["upscaling"],0)

    def test_clip_page_shows_validated_size_but_no_final_timing_for_interrupted_run(self):
        output = dict(label="preview",file="preview.mp4",validation=dict(width=832,height=480,frames=73,fps="24"))
        page = clip_card(dict(Name="r",Status="production_work_arrived",Seconds=200),
                         dict(case="comparison",seed=123,frames=73),output,None)
        self.assertIn("832 × 480",page)
        self.assertIn("3.04s",page)
        self.assertIn("73 / 24 fps",page)
        self.assertIn("Not finalized",page)
        self.assertNotIn("3m 20s",page)
        self.assertNotIn("comfortable",page)

    def test_progressive_transfer_is_not_double_counted(self):
        manifest = dict(case="progressive", frames=73,
            outputs=[dict(label="progressive", file="p.mp4", validation={})])
        metrics = dict(status="complete", stages=[dict(name="base-sampling", seconds=100)])
        flow = dict(events=[dict(kind=kind, fields=dict(elapsed_ms=value)) for kind, value in
            [("low_stage_wall", 60000), ("handoff_transfer_wall", 15000),
             ("handoff_learned_upscale_wall", 10000), ("high_stage_wall", 25000)]])
        row = candidate_rows("r", manifest, metrics, "complete", flow)[0]
        self.assertEqual(row["seconds"]["sampling"], 90)
        self.assertEqual(row["seconds"]["upscaling"], 10)
        self.assertEqual(row["stage_total_seconds"], 100)
        self.assertEqual(row["progressive_wall_seconds"]["handoff_transfer_wall"], 15)
