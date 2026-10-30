import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('portal_edl', ROOT / 'tools/portal_edl.py')
edl = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = edl
spec.loader.exec_module(edl)
WORKSPACE = r'C:\Users\jlewi\AppData\Local\ED-IT Buddy\portal\workspaces\edl-prototype'


class PortalEdlTests(unittest.TestCase):
    def test_demo_has_exact_timeline_and_source_offsets_in_samples(self):
        text, report = edl.render_edl(WORKSPACE, edl.demo_clips(WORKSPACE))
        self.assertTrue(text.startswith('Samplitude EDL File Format Version 1.5\r\n'))
        self.assertIn('Sample Rate: 48000\r\n', text)
        self.assertIn('1 1 0 96000 0 96000 0.0', text)
        self.assertIn('2 1 96000 240000 48000 192000 0.0', text)
        self.assertIn('Clip-A.avi"', text)
        self.assertIn('Clip-B.avi"', text)
        self.assertEqual(report['timeline_frames'], 150)
        self.assertFalse(report['import_verified'])
        self.assertNotIn('\n', text.replace('\r\n', ''))

    def test_typed_sequence_compiles_clip_intent_and_retains_other_steps(self):
        sequence = {'workspace': WORKSPACE, 'steps': [
            {'action': 'create_project', 'args': {'project_path': WORKSPACE + r'\demo.mvp', 'width': 640, 'height': 360, 'fps': 30}},
            {'action': 'import_media', 'args': {'media_path': WORKSPACE + r'\Clip-A.avi', 'clip_id': 'A'}},
            {'action': 'trim_clip', 'args': {'clip_id': 'A', 'source_in_frame': 0, 'source_out_frame': 60}},
            {'action': 'append_clip', 'args': {'clip_id': 'A', 'track': 1}},
            {'action': 'import_media', 'args': {'media_path': WORKSPACE + r'\Clip-B.avi', 'clip_id': 'B'}},
            {'action': 'trim_clip', 'args': {'clip_id': 'B', 'source_in_frame': 30, 'source_out_frame': 120}},
            {'action': 'append_clip', 'args': {'clip_id': 'B', 'track': 1}},
            {'action': 'add_title', 'args': {'text': 'ED-IT Buddy', 'timeline_frame': 0, 'duration_frames': 60, 'track': 2}},
            {'action': 'save_project', 'args': {'project_path': WORKSPACE + r'\demo.mvp'}},
            {'action': 'export_video', 'args': {'output_path': WORKSPACE + r'\demo.mp4'}}
        ]}
        text, report = edl.compile_sequence(sequence)
        expected, _ = edl.render_edl(WORKSPACE, edl.demo_clips(WORKSPACE))
        self.assertEqual(text, expected)
        self.assertEqual([step['action'] for step in report['unexecuted_steps']], ['create_project', 'add_title', 'save_project', 'export_video'])
        self.assertFalse(report['import_verified'])

    def test_escaping_and_workspace_boundaries(self):
        for path in (r'C:\elsewhere\Clip-A.avi', WORKSPACE + r'\..\Clip-A.avi', WORKSPACE + '\\evil"\nfile.avi', WORKSPACE + '\\café.avi'):
            with self.subTest(path=path), self.assertRaises(ValueError):
                edl.render_edl(WORKSPACE, [edl.Clip('A', path, 0, 0, 60)])

    def test_no_overlaps_empty_ranges_duplicate_ids_or_fractional_frames(self):
        source = WORKSPACE + r'\Clip-A.avi'
        invalid = [
            [edl.Clip('A', source, 0, 0, 60), edl.Clip('B', source, 59, 0, 30)],
            [edl.Clip('A', source, 0, 60, 60)],
            [edl.Clip('A', source, 0, 0, 60), edl.Clip('A', source, 60, 0, 30)],
            [edl.Clip('A', source, 0, 0.5, 60)],
        ]
        for clips in invalid:
            with self.subTest(clips=clips), self.assertRaises(ValueError):
                edl.render_edl(WORKSPACE, clips)

    def test_unappended_media_and_reordered_finishing_steps_are_refused(self):
        imported = {'action': 'import_media', 'args': {'media_path': WORKSPACE + r'\Clip-A.avi', 'clip_id': 'A'}}
        with self.assertRaises(ValueError):
            edl.compile_sequence({'workspace': WORKSPACE, 'steps': [imported]})
        with self.assertRaises(ValueError):
            edl.compile_sequence({'workspace': WORKSPACE, 'steps': [{'action': 'save_project', 'args': {'project_path': WORKSPACE + r'\x.mvp'}}, imported]})

    def test_no_overwrite_of_existing_output(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'demo.edl'
            text, report = edl.render_edl(WORKSPACE, edl.demo_clips(WORKSPACE))
            edl.write_prototype(path, text, report)
            self.assertEqual(path.read_bytes(), text.encode('ascii'))
            with self.assertRaises(FileExistsError):
                edl.write_prototype(path, text, report)
            self.assertTrue(path.with_suffix('.edl.json').exists())

    def test_roundtrip_compares_resolved_sources_and_every_position(self):
        text, _ = edl.render_edl(WORKSPACE, edl.demo_clips(WORKSPACE))
        # Native exporters may use another EDL version and source index numbering.
        native = text.replace('Version 1.5', 'Version 1.7').replace('1 "C:', '7 "C:').replace('2 "C:', '8 "C:').replace('\r\n1 1 ', '\r\n7 1 ').replace('\r\n2 1 ', '\r\n8 1 ')
        same = edl.compare_edl(text, native)
        self.assertTrue(same['matches_expected'])
        self.assertFalse(same['native_application_verified'])
        for changed in (native.replace('48000 192000', '0 144000'), native.replace('8 1 96000', '8 2 96000'), native.replace('Clip-B.avi', 'Other-B.avi')):
            self.assertFalse(edl.compare_edl(text, changed)['matches_expected'])

    def test_unknown_or_truncated_records_are_not_silently_ignored(self):
        text, _ = edl.render_edl(WORKSPACE, edl.demo_clips(WORKSPACE))
        for broken in (text + 'Unrecognized state data\r\n', text.replace('Source Table Entries: 2', 'Source Table Entries: 3'), text.replace('2 1 96000 240000 48000 192000', '2 1 96000 240000 48000 192001')):
            with self.subTest(broken=broken[-70:]), self.assertRaises(ValueError):
                edl.parse_edl(broken)

    def test_generated_input_alone_cannot_be_native_proof(self):
        with tempfile.TemporaryDirectory() as folder:
            text, _ = edl.render_edl(WORKSPACE, edl.demo_clips(WORKSPACE))
            source, exported = Path(folder) / 'generated.edl', Path(folder) / 'claimed-native.edl'
            source.write_bytes(text.encode('ascii'))
            exported.write_bytes(text.encode('ascii'))
            with self.assertRaises(ValueError):
                edl.validate_roundtrip(source, source)
            comparison = edl.validate_roundtrip(source, exported)
            self.assertTrue(comparison['matches_expected'])
            self.assertFalse(comparison['eligible_native_evidence'])
            self.assertFalse(comparison['native_application_verified'])
            claim = {'origin': 'magix_native_export', 'operation_receipt_id': 'test-only-not-a-real-native-operation',
                     'export_path': str(exported), 'export_sha256': comparison['sha256'][str(exported)], 'magix_process_id': 1}
            compared = edl.validate_roundtrip(source, exported, claim)
            self.assertTrue(compared['eligible_native_evidence'])
            self.assertFalse(compared['native_application_verified'], 'caller provenance is only an attestation, not independently proven here')
            claim['export_sha256'] = 'wrong'
            self.assertFalse(edl.validate_roundtrip(source, exported, claim)['eligible_native_evidence'])


if __name__ == '__main__':
    unittest.main()
