"""Offline synthetic format tests; no MAGIX, UI, or media access."""
import importlib.util
from pathlib import Path
import struct
import tempfile
import sys
import unittest
from unittest.mock import patch

MODULE = Path(__file__).resolve().parents[1] / "tools" / "inspect_magix_project.py"
spec = importlib.util.spec_from_file_location("project_inspector", MODULE)
reader = importlib.util.module_from_spec(spec)
spec.loader.exec_module(reader)


def chunk(tag, payload):
    return tag + struct.pack("<I", len(payload)) + payload + (b"\0" if len(payload) & 1 else b"")


def riff(payload, form=b"SEKD"):
    return chunk(b"RIFF", form + payload)


def unicode_table(records):
    payload = b"".join(struct.pack("<II", record_id, len(text.encode("utf-16le"))) + text.encode("utf-16le") for record_id, text in records)
    return riff(chunk(b"LIST", b"ustl" + chunk(b"LIST", b"\0\0\0\0" + payload)), b"ustr")


class ProjectInspectorTests(unittest.TestCase):
    def test_stored_source_reference_has_byte_evidence_not_timing(self):
        path = b"C:\\source\\example.mp4\0"
        data = riff(chunk(b"LIST", b"objs" + chunk(b"sfnm", path)))
        report = reader.inspect_bytes(data)
        candidate = report["whiteboardCandidates"][0]
        self.assertEqual(candidate["label"], "example.mp4")
        self.assertFalse(candidate["currentTimelineUseVerified"])
        self.assertIsNone(candidate["duration"])
        self.assertEqual(data[candidate["evidenceOffsets"][0]:].split(b"\0")[0], path[:-1])

    def test_concatenated_unicode_table_is_opaque_and_scanned(self):
        table = chunk(b"LIST", b"\0\0\0\0" + "example.mp4\0".encode("utf-16le"))
        report = reader.inspect_bytes(riff(b"") + riff(table, b"ustr"))
        self.assertEqual(len(report["rootContainers"]), 2)
        self.assertEqual(report["storedReferences"][0]["evidence"][0]["encoding"], "utf-16le")
        self.assertEqual(report["whiteboardCandidates"], [])
        self.assertTrue(report["warnings"])

    def test_printable_run_crossing_chunk_boundary_is_not_a_reference(self):
        # The final 'mp4' bytes are a following chunk tag, not part of sfnm's payload.
        pieces = chunk(b"sfnm", b"C:\\asset\\ab.") + chunk(b"mp4\0", b"")
        report = reader.inspect_bytes(riff(chunk(b"LIST", b"objs" + pieces)))
        self.assertEqual(report["storedReferences"], [])
        self.assertEqual(report["whiteboardCandidates"], [])

    def test_non_ascii_filename_fragments_are_not_asset_candidates(self):
        for name, encoding in (("C:\\source\\café clip.mp4\0", "utf-8"), ("C:\\source\\café clip.mp4\0", "utf-16le"), ("C:\\source\\clip.mp4é\0", "utf-8")):
            with self.subTest(name=name, encoding=encoding):
                report = reader.inspect_bytes(riff(chunk(b"LIST", b"objs" + chunk(b"sfnm", name.encode(encoding)))))
                self.assertEqual(report["whiteboardCandidates"], [])
                for reference in report["storedReferences"]:
                    self.assertFalse(any(item["completeStringBoundaryObserved"] for item in reference["evidence"]))

    def test_bad_headers_and_truncation_rejected(self):
        for data in (b"", b"not a project", riff(b"", b"WAVE"), riff(chunk(b"sfnm", b"a.mp4"))[:-1]):
            with self.subTest(data=data), self.assertRaises(reader.InspectionError):
                reader.inspect_bytes(data)

    def test_child_size_cannot_escape_parent(self):
        with self.assertRaises(reader.InspectionError):
            reader.inspect_bytes(riff(b"sfnm" + struct.pack("<I", 0xFFFFFFFF)))

    def test_depth_chunk_and_reference_limits(self):
        data = chunk(b"sfnm", b"a.mp4\0")
        for unused in range(5):
            data = chunk(b"LIST", b"test" + data)
        with patch.object(reader, "MAX_DEPTH", 3), self.assertRaises(reader.InspectionError):
            reader.inspect_bytes(riff(data))
        with patch.object(reader, "MAX_CHUNKS", 2), self.assertRaises(reader.InspectionError):
            reader.inspect_bytes(riff(chunk(b"name", b"a.mp4\0") * 3))
        with patch.object(reader, "MAX_REFERENCES", 1), self.assertRaises(reader.InspectionError):
            reader.inspect_bytes(riff(chunk(b"name", b"a.mp4\0b.mp4\0")))

    def test_read_is_bounded_hashed_and_does_not_modify_source(self):
        with tempfile.TemporaryDirectory() as folder:
            project = Path(folder) / "example.MVP"
            data = riff(chunk(b"name", b"a.mp4\0"))
            project.write_bytes(data)
            report = reader.inspect_project(project)
            self.assertEqual(len(report["source"]["sha256"]), 64)
            self.assertEqual(project.read_bytes(), data)
            with patch.object(reader, "MAX_BYTES", 4), self.assertRaises(reader.InspectionError):
                reader.inspect_project(project)

    def test_changed_named_snapshot_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            project = Path(folder) / "example.MVP"
            project.write_bytes(riff(b""))
            original_identity = reader._identity
            calls = [0]
            def changed(stat):
                calls[0] += 1
                result = original_identity(stat)
                return result if calls[0] < 4 else result[:-1] + (result[-1] + 1,)
            with patch.object(reader, "_identity", changed), self.assertRaises(reader.InspectionError):
                reader.inspect_project(project)

    def test_cli_refuses_existing_output_without_changing_either_file(self):
        with tempfile.TemporaryDirectory() as folder:
            project = Path(folder) / "example.MVP"
            output = Path(folder) / "existing.json"
            data = riff(b"")
            project.write_bytes(data)
            output.write_text("keep this", encoding="utf-8")
            with patch.object(sys, "argv", ["inspect_magix_project.py", str(project), "--output", str(output)]), self.assertRaises(SystemExit) as result:
                reader.main()
            self.assertEqual(result.exception.code, 2)
            self.assertEqual(output.read_text(encoding="utf-8"), "keep this")
            self.assertEqual(project.read_bytes(), data)

    def test_unicode_table_decodes_counted_text_and_noncontiguous_ids(self):
        records = [(1, "Movie.MVD"), (19, "C:\\source\\café 🎬.mp4"), (27, "")]
        report = reader.inspect_bytes(riff(b"") + unicode_table(records))
        table = report["experimentalUnicodeTables"][0]
        self.assertEqual(table["status"], "decoded")
        self.assertEqual([(item["id"], item["text"]) for item in table["records"]], records)
        self.assertEqual(table["records"][1]["textBytes"], len(records[1][1].encode("utf-16le")))

    def test_sfnm_id_link_does_not_replace_legacy_candidate(self):
        legacy = b"C:\\source\\caf? clip.mp4"
        unicode_name = "C:\\source\\café clip.mp4"
        object_data = chunk(b"LIST", b"objs" + chunk(b"sfnm", legacy + b"\0" + struct.pack("<Q", 19)))
        report = reader.inspect_bytes(riff(object_data) + unicode_table([(19, unicode_name)]))
        link = report["experimentalStringLinks"]["sfnmIdLinks"][0]
        self.assertEqual(link["id"], 19)
        self.assertEqual(link["unicodeText"], unicode_name)
        self.assertFalse(link["legacyAsciiEqualsUnicode"])
        self.assertEqual(report["whiteboardCandidates"][0]["storedReference"], legacy.decode("ascii"))

    def test_duplicate_text_and_id_match_is_exact(self):
        text = "Example.MVD"
        payload = b"\0" * 12 + text.encode("ascii") + b"\0" + struct.pack("<Q", 7)
        report = reader.inspect_bytes(riff(chunk(b"SVIP", payload)) + unicode_table([(7, text)]))
        matches = report["experimentalStringLinks"]["duplicateTextAndIdMatches"]
        self.assertEqual(len(matches), 1)
        self.assertEqual(matches[0]["chunkTag"], "SVIP")
        self.assertEqual(matches[0]["id"], 7)

    def test_malformed_unicode_tables_are_never_partially_trusted(self):
        valid = struct.pack("<II", 1, 2) + b"a\0"
        for tail in (b"\0", struct.pack("<II", 2, 3) + b"abc", struct.pack("<II", 2, 100), valid, struct.pack("<II", 2, 2) + b"\0\xd8"):
            data = riff(b"") + riff(chunk(b"LIST", b"ustl" + chunk(b"LIST", b"\0\0\0\0" + valid + tail)), b"ustr")
            with self.subTest(tail=tail):
                table = reader.inspect_bytes(data)["experimentalUnicodeTables"][0]
                self.assertEqual(table["status"], "undecoded")
                self.assertEqual(table["records"], [])
        with patch.object(reader, "MAX_UNICODE_RECORDS", 1):
            table = reader.inspect_bytes(riff(b"") + unicode_table([(1, "a"), (2, "b")]))["experimentalUnicodeTables"][0]
            self.assertEqual(table["status"], "undecoded")

    def test_ambiguous_ids_across_tables_are_not_resolved(self):
        payload = chunk(b"sfnm", b"example.mp4\0" + struct.pack("<Q", 4))
        data = riff(payload) + unicode_table([(4, "example.mp4")]) + unicode_table([(4, "other.mp4")])
        report = reader.inspect_bytes(data)
        self.assertEqual(report["experimentalStringLinks"]["sfnmIdLinks"], [])


if __name__ == "__main__":
    unittest.main()
