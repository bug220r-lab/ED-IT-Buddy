"""Narrow report overlay against distinct current source and installed snapshots."""
import importlib.util
import tempfile
import unittest
from pathlib import Path

spec=importlib.util.spec_from_file_location('inbox_candidate',Path(__file__).resolve().parents[1]/'integrations/interlinked/prepare_candidate.py')
candidate=importlib.util.module_from_spec(spec);spec.loader.exec_module(candidate)

class NarrowCandidateTests(unittest.TestCase):
    def test_each_side_retains_its_unrelated_bytes_and_v7(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);source=root/'source';kit=root/'kit';output=root/'candidate'
            files=[('repository/app/interlinked_buddy.py','INTERLINKED-App/interlinked_buddy.py',b'old companion'),
                   ('repository/work/magix-buddy/candidate/magix_projects.py','magix_projects.py',("BUDDY_SHA='"+'a'*64+"'\n").encode()),
                   ('repository/app/interlinked_gui.py','INTERLINKED-App/interlinked_gui.py',("MAGIX_SHA='"+'b'*64+"'\nseal.get('format')=='interlinked-gui-checksums-v7'\n").encode()),
                   ('repository/app/web/app.js','INTERLINKED-App/web/app.js',b'// BEGIN BUDDY PROJECT REPORT\nold();\n// END BUDDY PROJECT REPORT\n')]
            for rel,dest,raw in files:
                for location,prefix in [(source/rel,b'// source-only newer work\n' if rel.endswith('.js') else b'# source-only newer work\n'),(kit/dest,b'// installed baseline\n' if rel.endswith('.js') else b'# installed baseline\n')]:
                    location.parent.mkdir(parents=True,exist_ok=True);location.write_bytes(prefix+raw)
            report='// BEGIN BUDDY PROJECT REPORT\nnewReport();\n// END BUDDY PROJECT REPORT\n'
            overlay=candidate.prepare_v7(source,output,kit,b'new companion',report)
            self.assertEqual(overlay['format'],'interlinked-buddy-source-overlay-v2');self.assertEqual(len(overlay['files']),4)
            source_js=(output/'repository/app/web/app.js').read_text();installed_js=(output/'installed/INTERLINKED-App/web/app.js').read_text()
            self.assertIn('source-only newer work',source_js);self.assertNotIn('source-only newer work',installed_js)
            self.assertIn('installed baseline',installed_js);self.assertIn('newReport()',source_js);self.assertIn('newReport()',installed_js)
            self.assertIn('interlinked-gui-checksums-v7',(output/'installed/INTERLINKED-App/interlinked_gui.py').read_text())
            self.assertEqual((source/'repository/app/interlinked_buddy.py').read_bytes(),b'# source-only newer work\nold companion')

if __name__=='__main__':unittest.main()
