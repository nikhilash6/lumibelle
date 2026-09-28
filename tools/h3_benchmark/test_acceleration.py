import copy
import json
from pathlib import Path
import tempfile
import unittest
from acceleration_graph import accelerator_evidence, logical_archive_frames, modify


def control():
    graph = {str(i):dict(class_type='Control', inputs={}) for i in range(1, 16)}
    graph['6'] = dict(class_type='BasicGuider', inputs=dict(model=['1',0], conditioning=['5',0]))
    graph['8'] = dict(class_type='KSamplerSelect', inputs=dict(sampler_name='res_multistep'))
    graph['9'] = dict(class_type='BasicScheduler', inputs=dict(model=['1',0], scheduler='simple', steps=20))
    graph['10'] = dict(class_type='SamplerCustomAdvanced', inputs=dict(sigmas=['9',0], latent_image=['5',1]))
    graph['40'] = dict(class_type='LTXVSeparateAVLatent', inputs=dict(av_latent=['10',1]))
    graph['41'] = dict(class_type='MinimaxH3LatentUpscaler3D', inputs=dict(latent=['40',0]))
    graph['42'] = dict(class_type='LTXVConcatAVLatent', inputs=dict(video_latent=['41',0], audio_latent=['40',1]))
    return dict(configuration='standard', workflow=dict(prompt=graph))


SCHEMAS = {'SpectrumApplyMiniMaxH3': {'required': {'enabled': ['BOOLEAN', {'default':True}]}, 'optional':{'audio_blend_weight':['FLOAT',{'default':0.0}]}},
           'MiniMaxH3PDDAccApply': {}, 'MiniMaxH3TurboLoRA': {},
           'ApplyMiniMaxH3FirstBlockCache': {'required':{'mode':[['H3 Safe example','H3 Fast example']]}}}


class AccelerationTests(unittest.TestCase):
    def test_upscaler_and_audio_are_untouched_for_every_patch(self):
        original = control(); frozen = copy.deepcopy(original)
        for configuration in ('spectrum','cache','pdd','larry'):
            graph = modify(original, configuration, SCHEMAS)['workflow']['prompt']
            for id in ('40','41','42','5','7','11','12','13','14','15'):
                self.assertEqual(graph[id], frozen['workflow']['prompt'][id])
            self.assertEqual(sum(n['class_type']=='SamplerCustomAdvanced' for n in graph.values()), 1)
        self.assertEqual(original, frozen)

    def test_pdd_uses_head_bank_sigmas_and_exact_shifts(self):
        graph = modify(control(), 'pdd', SCHEMAS)['workflow']['prompt']
        self.assertNotIn('9', graph)
        self.assertEqual(graph['10']['inputs']['sigmas'], ['901',1])
        self.assertEqual(graph['900']['inputs']['shift_audio'], 3)
        self.assertEqual(graph['900']['inputs']['shift_video'], 12)
        self.assertEqual(graph['8']['inputs']['sampler_name'], 'euler')
        self.assertEqual(graph['901']['inputs']['on_off_grid'], 'error')

    def test_larry_preserves_pruned_loader_and_full_quality(self):
        graph = modify(control(), 'larry', SCHEMAS)['workflow']['prompt']
        self.assertFalse(graph['901']['inputs']['low_vram'])
        self.assertEqual(graph['9']['inputs']['steps'], 6)
        self.assertEqual(graph['8']['class_type'], 'MiniMaxH3TurboSampler')

    def test_spectrum_keeps_default_audio_and_ram_replay(self):
        inputs = modify(control(), 'spectrum', SCHEMAS)['workflow']['prompt']['901']['inputs']
        self.assertEqual(inputs['audio_blend_weight'], 0)
        self.assertTrue(inputs['offline_smoothing_replay'])
        self.assertEqual(inputs['history_storage'], 'system_ram')

    def test_rejects_collisions_wrong_control_or_extra_sampling(self):
        for mutation in ('collision','control','sampling'):
            request=control()
            if mutation=='collision': request['workflow']['prompt']['901']=dict(class_type='Control')
            if mutation=='control': request['configuration']='turbo8'
            if mutation=='sampling': request['workflow']['prompt']['99']=dict(class_type='SamplerCustomAdvanced')
            with self.assertRaises(ValueError): modify(request,'spectrum',SCHEMAS)

    def test_accounting_does_not_claim_acceleration_without_hits(self):
        self.assertIsNone(accelerator_evidence('cache','enabled')['effective'])
        self.assertFalse(accelerator_evidence('cache','FBCache: cached 0/20 steps')['effective'])
        self.assertTrue(accelerator_evidence('cache','FBCache: cached 9/20 steps')['effective'])
        log='Spectrum H3 run summary phase=offline_first_pass actual_transformer_calls=11 forecast_calls=9 fallbacks=0\nSpectrum H3 run summary phase=offline_replay actual_transformer_calls=0 forecast_calls=0 offline_replay_calls=20 fallbacks=0'
        evidence=accelerator_evidence('spectrum',log)
        self.assertEqual(evidence['actual'],11);self.assertEqual(evidence['forecast'],9)
        self.assertTrue(evidence['effective'])

    def test_coalesced_archive_timeline(self):
        self.assertEqual(logical_archive_frames([42,83,42]),4)
        with self.assertRaises(ValueError): logical_archive_frames([60])

    def test_report_excludes_contended_completed_outputs(self):
        from acceleration_report import collect, build
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary); directory=root/'results'/'sample'; (directory/'cold').mkdir(parents=True)
            (root/'budget.json').write_text(json.dumps({'Runs':[{'Name':'sample','Status':'production_work_arrived'}]}))
            manifest=dict(name='sample', configuration='standard',scene='motion',seed=20260911,frames=141,native=False,status='complete',
                          runs=[dict(temperature='cold',status='complete',file='video.mp4',execution_seconds=100)])
            (directory/'manifest.json').write_text(json.dumps(manifest))
            (directory/'cold'/'measurements.json').write_text(json.dumps(dict(status='complete',stages=[])))
            row=collect(root)[0]
            self.assertFalse(row['valid']);self.assertIsNone(row['video'])
            self.assertIsNone(row['free_gib'])
            self.assertIsNone(row['stages']['sampling'])
            build(root)
            self.assertIn('production_work_arrived',(root/'clips.html').read_text())

    def test_archive_keeps_measurements_without_copying_private_media(self):
        from archive_acceleration import archive
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary)/'source';root.mkdir()
            (root/'results').mkdir()
            (root/'references.json').write_text(json.dumps({'face.png':{'source':'private/project/face.png','sha256':'abc'}}))
            destination=Path(temporary)/'archive'
            archive(root,destination)
            self.assertEqual(json.loads((destination/'reference-hashes.json').read_text()),{'face.png':{'sha256':'abc'}})
            self.assertEqual(json.loads((destination/'results.json').read_text())['attempts'],[])
            self.assertFalse((destination/'face.png').exists())
            with self.assertRaises(FileExistsError): archive(root,destination)


if __name__=='__main__': unittest.main()
