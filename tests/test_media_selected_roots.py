"""Selected indexing resolves only authorized aliases, without broad source scans."""
from pathlib import Path
import tempfile
import json
import unittest
from unittest.mock import patch
import sys
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from media_worker.core import Engine, JobPaused
from test_media_worker import Tools, Encoder


class SelectedRootTests(unittest.TestCase):
    def test_completed_legacy_checkpoint_is_refused_before_manifest_publication(self):
        with tempfile.TemporaryDirectory() as folder:
            base=Path(folder);footage=base/'footage';archive=footage/'archive'
            source=archive/'Incoming'/'legacy'/'assembled'/'video.mp4'
            source.parent.mkdir(parents=True);source.write_bytes(b'video')
            tools=Tools();tools.durations[str(source)]=1
            engine=Engine(base/'state',{'expected_host':'','source_allowlist':[str(footage)],'model_revision':'test'},runner=tools,encoder=Encoder())
            root=engine.register_root({'path':str(footage)})['root_id'];asset=engine.register_asset(source,root)
            job=engine.submit({'request_id':'legacy-complete','kind':'index','scope':'library','project_id':None,'params':{'root_ids':[root],'asset_ids':[asset]}})
            engine.run_next();engine.control({'job_id':job['job_id']},'pause')
            plan=engine.job_dir(job['job_id'])/'index-plan.json';before=plan.read_bytes();saved=json.loads(before)
            self.assertEqual(saved['cursor'],1);self.assertEqual(len(saved['results']),1)
            prior_images=engine.encoder.calls;self.assertGreater(prior_images,0)
            engine.config['archive_root']=str(archive);tools.calls.clear()
            engine.control({'job_id':job['job_id']},'resume');engine.run_next()
            result=engine.get_results({'job_id':job['job_id']})
            self.assertEqual(result['job']['status'],'failed');self.assertEqual(result['job']['error']['code'],'unsafe_path')
            self.assertIsNone(result['manifest']);self.assertEqual(plan.read_bytes(),before)
            self.assertEqual(tools.calls,[]);self.assertEqual(engine.encoder.calls,prior_images)

    def test_legacy_incoming_alias_and_paused_checkpoint_never_probe_or_decode(self):
        for checkpointed in (False, True):
            with self.subTest(checkpointed=checkpointed), tempfile.TemporaryDirectory() as folder:
                base=Path(folder); footage=base/'footage'; archive=footage/'archive'
                source=archive/'Incoming'/'legacy'/'assembled'/'video.mp4'
                source.parent.mkdir(parents=True);source.write_bytes(b'video')
                tools=Tools();tools.durations[str(source)]=60
                engine=Engine(base/'state',{'expected_host':'','source_allowlist':[str(footage)],'model_revision':'test'},runner=tools,encoder=Encoder())
                root=engine.register_root({'path':str(footage)})['root_id'];asset=engine.register_asset(source,root)
                job=engine.submit({'request_id':'legacy','kind':'index','scope':'library','project_id':None,'params':{'root_ids':[root],'asset_ids':[asset]}})
                if checkpointed:
                    def pause_before_decode(e,j,name,args,**kwargs):
                        if name=='ffmpeg':raise JobPaused()
                        return tools(e,j,name,args,**kwargs)
                    engine.runner=pause_before_decode;engine.run_next()
                    self.assertEqual(engine.get_job({'job_id':job['job_id']})['status'],'paused')
                    self.assertTrue((engine.job_dir(job['job_id'])/'index-plan.json').exists())
                    engine.runner=tools
                engine.config['archive_root']=str(archive);tools.calls.clear()
                if checkpointed:engine.control({'job_id':job['job_id']},'resume')
                for _ in range(4):
                    if engine.get_job({'job_id':job['job_id']})['status'] in ('completed','failed','needs_review'):break
                    engine.run_next()
                result=engine.get_results({'job_id':job['job_id']})
                self.assertIn(result['job']['status'],('failed','needs_review'))
                self.assertEqual((result['manifest'] or {}).get('results',[]),[])
                self.assertEqual(tools.calls,[]);self.assertEqual(engine.encoder.calls,0)

    def test_two_roots_different_assets_and_shared_aliases(self):
        with tempfile.TemporaryDirectory() as folder:
            base=Path(folder);roots=[];assets=[];tools=Tools()
            engine=Engine(base/'state',{'expected_host':'','source_allowlist':[str(base/'footage')],'model_revision':'test'},runner=tools,encoder=Encoder())
            for i in range(2):
                root=base/'footage'/str(i);root.mkdir(parents=True)
                rid=engine.register_root({'path':str(root)})['root_id'];roots.append(rid)
                p=root/'clip.mp4';p.write_bytes(b'video'+bytes([i]));tools.durations[str(p)]=1
                assets.append(engine.register_asset(p,rid))
            shared=base/'footage/1/shared.mp4';shared.write_bytes((base/'footage/0/clip.mp4').read_bytes());tools.durations[str(shared)]=1
            self.assertEqual(engine.register_asset(shared,roots[1]),assets[0])
            job=engine.submit({'request_id':'selected','kind':'index','scope':'library','project_id':None,'params':{'root_ids':roots,'asset_ids':assets}})
            with patch.object(engine,'media_files',side_effect=AssertionError('Selected indexing must not scan roots')):
                for _ in range(20):
                    if engine.get_job({'job_id':job['job_id']})['status'] in ('completed','failed','needs_review'):break
                    engine.run_next()
            result=engine.get_results({'job_id':job['job_id']});self.assertEqual(result['job']['status'],'completed',result['job']['error'])
            self.assertEqual({r['asset_id'] for r in result['manifest']['results']},set(assets))
            self.assertEqual(len(result['manifest']['results']),3)

    def test_selected_asset_outside_requested_root_refuses(self):
        with tempfile.TemporaryDirectory() as folder:
            base=Path(folder);a=base/'a';b=base/'b';a.mkdir();b.mkdir();tools=Tools()
            engine=Engine(base/'state',{'expected_host':'','source_allowlist':[str(a),str(b)],'model_revision':'test'},runner=tools,encoder=Encoder())
            ra=engine.register_root({'path':str(a)})['root_id'];rb=engine.register_root({'path':str(b)})['root_id']
            p=a/'clip.mp4';p.write_bytes(b'video');asset=engine.register_asset(p,ra)
            job=engine.submit({'request_id':'outside','kind':'index','scope':'library','project_id':None,'params':{'root_ids':[rb],'asset_ids':[asset]}})
            engine.run_next();result=engine.get_job({'job_id':job['job_id']})
            self.assertEqual(result['status'],'failed');self.assertEqual(result['error']['code'],'source_changed')
            self.assertEqual(tools.calls,[])


if __name__=='__main__':unittest.main()
