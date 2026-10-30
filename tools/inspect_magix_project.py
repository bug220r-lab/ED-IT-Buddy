"""Bounded, read-only RIFF/MAGIX snapshot inspection. Standard library only."""
from __future__ import annotations

import argparse
from bisect import bisect_right
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
import ntpath
import os
from pathlib import Path
import re
import struct

MAX_BYTES = 64 * 1024 * 1024
MAX_CHUNKS = 50000
MAX_DEPTH = 32
MAX_REFERENCES = 2000
MAX_STRING_BYTES = 65536
MAX_UNICODE_RECORDS = 5000
MEDIA_EXTENSIONS = {".mp4", ".mov", ".avi", ".wmv", ".mkv", ".m2ts", ".mts", ".mpg", ".mpeg", ".mxv", ".wav", ".mp3", ".ogg", ".flac", ".m4a", ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".gif"}
PROJECT_EXTENSIONS = {".mvp", ".mvd"}


class InspectionError(ValueError):
    pass


def _tag(raw: bytes) -> str:
    return "".join(chr(value) if 32 <= value <= 126 else "\\x%02x" % value for value in raw)


def decode_unicode_tables(data: bytes, chunks: list[dict]) -> list[dict]:
    """Decode only the observed ustr/ustl/LIST(0) length-delimited record layout.

    Values are exact decoded bytes. No project-editing semantics are assigned to IDs.
    An unfamiliar or malformed table is reported as undecoded, never partially trusted.
    """
    tables = []
    total_records = 0
    for node in chunks:
        parent = chunks[node["parent"]] if node["parent"] is not None else None
        grandparent = chunks[parent["parent"]] if parent and parent["parent"] is not None else None
        if not (node["tag"] == "LIST" and node["form"] == "\\x00\\x00\\x00\\x00" and parent and parent["tag"] == "LIST" and parent["form"] == "ustl" and grandparent and grandparent["tag"] == "RIFF" and grandparent["form"] == "ustr"):
            continue
        result = {"containerOffset": node["offset"], "status": "undecoded", "records": []}
        position, end = node["payloadOffset"] + 4, node["payloadEnd"]
        records, ids = [], set()
        try:
            while position < end:
                if end - position < 8:
                    raise InspectionError("Incomplete Unicode record header.")
                if total_records >= MAX_UNICODE_RECORDS:
                    raise InspectionError("Unicode record count exceeds the inspection limit.")
                record_id, byte_length = struct.unpack_from("<II", data, position)
                text_start = position + 8
                if byte_length & 1 or byte_length > MAX_STRING_BYTES or byte_length > end - text_start:
                    raise InspectionError("Invalid or out-of-bounds Unicode byte length.")
                if record_id in ids:
                    raise InspectionError("Duplicate Unicode record IDs in one table.")
                text = data[text_start:text_start + byte_length].decode("utf-16le", errors="strict")
                ids.add(record_id)
                records.append({"id": record_id, "recordOffset": position, "textOffset": text_start, "textBytes": byte_length, "text": text})
                total_records += 1
                position = text_start + byte_length
            result.update(status="decoded", recordCount=len(records), records=records)
        except (InspectionError, UnicodeDecodeError) as error:
            result["reason"] = str(error)
        tables.append(result)
    return tables


def inspect_string_links(data: bytes, chunks: list[dict], tables: list[dict]) -> dict:
    """Report exact duplicate-text matches and experimental sfnm ID resolutions.

    Existing whiteboard candidates remain based on directly observed strings.
    """
    records = [record for table in tables if table["status"] == "decoded" for record in table["records"]]
    by_id = {}
    for record in records:
        by_id.setdefault(record["id"], []).append(record)
    sfnm_links, duplicate_matches = [], []
    for node in chunks:
        if node["tag"] not in ("sfnm", "SVIP", "file"):
            continue
        payload = data[node["payloadOffset"]:node["payloadEnd"]]
        if node["tag"] == "sfnm":
            terminator = payload.find(b"\0")
            if terminator >= 0 and len(payload) - terminator - 1 == 8:
                record_id = struct.unpack_from("<Q", payload, terminator + 1)[0]
                matches = by_id.get(record_id, [])
                if len(matches) == 1:
                    record = matches[0]
                    legacy = payload[:terminator].decode("ascii") if payload[:terminator].isascii() else None
                    sfnm_links.append({"chunkOffset": node["offset"], "legacyTextOffset": node["payloadOffset"], "idOffset": node["payloadOffset"] + terminator + 1, "id": record_id, "unicodeRecordOffset": record["recordOffset"], "unicodeText": record["text"], "legacyAsciiText": legacy, "legacyAsciiEqualsUnicode": legacy == record["text"] if legacy is not None else None, "interpretation": "Experimental: trailing uint64 matches a unique Unicode table ID; no edit has been tested."})
        for match in re.finditer(rb"[ -~]+", payload):
            start, stop = match.span()
            if stop - start > MAX_STRING_BYTES or stop + 9 > len(payload) or payload[stop] != 0 or (start > 0 and payload[start - 1] != 0):
                continue
            record_id = struct.unpack_from("<Q", payload, stop + 1)[0]
            matches = by_id.get(record_id, [])
            if len(matches) != 1 or match.group().decode("ascii") != matches[0]["text"]:
                continue
            record = matches[0]
            duplicate_matches.append({"chunkTag": node["tag"], "chunkOffset": node["offset"], "legacyTextOffset": node["payloadOffset"] + start, "id": record_id, "unicodeRecordOffset": record["recordOffset"], "text": record["text"], "basis": "Exact ASCII text + NUL + uint64 ID duplicates the length-delimited Unicode record."})
            if len(duplicate_matches) > MAX_REFERENCES:
                raise InspectionError("String-link evidence count exceeds the inspection limit.")
    return {"sfnmIdLinks": sfnm_links, "duplicateTextAndIdMatches": duplicate_matches}


def inspect_bytes(data: bytes) -> dict:
    if len(data) > MAX_BYTES:
        raise InspectionError("Project exceeds the 64 MiB inspection limit.")
    if len(data) < 12 or data[:4] != b"RIFF" or data[8:12] != b"SEKD":
        raise InspectionError("Expected a RIFF/SEKD project snapshot; this format is unsupported.")
    chunks: list[dict] = []
    warnings: list[str] = []

    def walk(start: int, end: int, parent: int | None, depth: int) -> None:
        if depth > MAX_DEPTH:
            raise InspectionError("Chunk nesting exceeds the inspection limit.")
        position = start
        ordinal = 0
        while position < end:
            if end - position < 8:
                raise InspectionError("Truncated chunk header at byte %d." % position)
            if len(chunks) >= MAX_CHUNKS:
                raise InspectionError("Chunk count exceeds the inspection limit.")
            tag_bytes = data[position:position + 4]
            size = struct.unpack_from("<I", data, position + 4)[0]
            stop = position + 8 + size
            padded_stop = stop + (size & 1)
            if stop > end or padded_stop > end:
                raise InspectionError("Chunk at byte %d extends beyond its parent." % position)
            if parent is None and tag_bytes != b"RIFF":
                raise InspectionError("Unsupported trailing data at byte %d." % position)
            container = tag_bytes in (b"RIFF", b"LIST")
            if container and size < 4:
                raise InspectionError("Container at byte %d has no form type." % position)
            form_bytes = data[position + 8:position + 12] if container else b""
            node = {
                "tag": _tag(tag_bytes), "form": _tag(form_bytes) if container else None,
                "offset": position, "payloadOffset": position + 8,
                "payloadEnd": stop, "payloadBytes": size, "parent": parent, "ordinal": ordinal,
            }
            index = len(chunks)
            chunks.append(node)
            if container:
                if all(32 <= value <= 126 for value in form_bytes):
                    walk(position + 12, stop, index, depth + 1)
                else:
                    # Observed ustr tables use LIST with a binary form identifier and opaque records.
                    warnings.append("Binary-form LIST payload at byte %d; see experimental Unicode tables for recognized layouts." % position)
            position = padded_stop
            ordinal += 1

    walk(0, len(data), None, 0)
    unicode_tables = decode_unicode_tables(data, chunks)
    string_links = inspect_string_links(data, chunks, unicode_tables)
    starts = [chunk["offset"] for chunk in chunks]

    def containing_chunk(offset: int) -> int | None:
        index = bisect_right(starts, offset) - 1
        while index >= 0:
            node = chunks[index]
            if node["payloadOffset"] <= offset < node["payloadEnd"]:
                return index
            if node["parent"] is None:
                return None
            index = node["parent"]
        return None

    def ancestry(index: int | None) -> list[dict]:
        route = []
        while index is not None:
            node = chunks[index]
            route.append({"tag": node["tag"], "form": node["form"], "offset": node["offset"]})
            index = node["parent"]
        return list(reversed(route))

    references: dict[str, dict] = {}
    evidence_count = 0
    skipped_long = 0
    for pattern, encoding, width in ((rb"[ -~]{5,}", "ascii", 1), (rb"(?:[ -~]\x00){5,}", "utf-16le", 2)):
        for match in re.finditer(pattern, data):
            if match.end() - match.start() > MAX_STRING_BYTES:
                skipped_long += 1
                continue
            text = match.group().decode(encoding)
            # Keep the exact stored path when a printable run has a binary prefix.
            drive = re.search(r"[A-Za-z]:[\\/]", text)
            shift = drive.start() if drive else 0
            candidate = text[shift:]
            extension = ntpath.splitext(candidate)[1].lower()
            if extension not in MEDIA_EXTENSIONS | PROJECT_EXTENSIONS:
                continue
            if evidence_count >= MAX_REFERENCES:
                raise InspectionError("Reference count exceeds the inspection limit.")
            offset = match.start() + shift * width
            index = containing_chunk(offset)
            # A global printable run can cross a chunk boundary. It is not evidence of a
            # reference stored in that field unless the complete string fits its payload.
            if index is None or offset + len(candidate) * width > chunks[index]["payloadEnd"]:
                continue
            payload_start = chunks[index]["payloadOffset"]
            payload_end = chunks[index]["payloadEnd"]
            string_end = offset + len(candidate) * width
            null_unit = b"\0" * width
            start_boundary = offset == payload_start or (offset - width >= payload_start and data[offset - width:offset] == null_unit)
            end_boundary = string_end == payload_end or (string_end + width <= payload_end and data[string_end:string_end + width] == null_unit)
            route = ancestry(index)
            record = references.setdefault(candidate, {
                "storedReference": candidate, "fileName": ntpath.basename(candidate),
                "kindCandidate": "project_or_movie" if extension in PROJECT_EXTENSIONS else "media",
                "classificationBasis": "filename extension only", "evidence": [],
            })
            record["evidence"].append({"byteOffset": offset, "encoding": encoding, "chunkPath": route, "completeStringBoundaryObserved": start_boundary and end_boundary})
            evidence_count += 1
    if skipped_long:
        warnings.append("Skipped %d printable runs exceeding the string-length limit." % skipped_long)
    counts = Counter(chunk["tag"] + (":" + chunk["form"] if chunk["form"] else "") for chunk in chunks)
    candidates = []
    for reference in references.values():
        evidence = [entry for entry in reference["evidence"] if entry["completeStringBoundaryObserved"] and entry["chunkPath"] and entry["chunkPath"][-1]["tag"] == "sfnm" and any(node["tag"] == "LIST" and node["form"] == "objs" for node in entry["chunkPath"])]
        if reference["kindCandidate"] == "media" and evidence:
            candidates.append({
                "id": hashlib.sha256(reference["storedReference"].encode("utf-8")).hexdigest()[:16],
                "label": reference["fileName"], "storedReference": reference["storedReference"],
                "evidenceOffsets": [entry["byteOffset"] for entry in evidence],
                "basis": "Exact stored string in an sfnm chunk inside a LIST:objs container.",
                "currentTimelineUseVerified": False, "track": None, "start": None, "duration": None,
            })
    roots = [{"form": chunk["form"], "offset": chunk["offset"], "payloadBytes": chunk["payloadBytes"]} for chunk in chunks if chunk["parent"] is None]
    return {
        "reportVersion": 1,
        "inspectionLimits": {"maxBytes": MAX_BYTES, "maxChunks": MAX_CHUNKS, "maxDepth": MAX_DEPTH, "maxReferences": MAX_REFERENCES, "maxStringBytes": MAX_STRING_BYTES, "maxUnicodeRecords": MAX_UNICODE_RECORDS},
        "formatObservation": "RIFF/SEKD with bounded chunk parsing",
        "rootContainers": roots, "chunkCount": len(chunks), "chunkCounts": dict(sorted(counts.items())),
        "storedReferences": list(references.values()), "whiteboardCandidates": candidates,
        "experimentalUnicodeTables": unicode_tables,
        "experimentalStringLinks": string_links,
        "warnings": warnings,
        "limitations": [
            "This is a saved byte snapshot, not a live MAGIX connection. An autosave may be incomplete or differ from the intended project.",
            "Reference strings are verified in these bytes. Their existence, current timeline use, and scene meaning are not verified.",
            "No referenced media files or network shares were opened or checked.",
            "Track numbers, clip timing, trims, transitions, and effects were not decoded.",
            "String discovery covers printable ASCII and ASCII-compatible UTF-16LE. Other filenames may be missed.",
            "General storedReferences may contain printable fragments of non-ASCII filenames. Whiteboard candidates require observed full string boundaries within the same chunk.",
            "Opaque binary-form LIST payloads are retained as structural observations; proprietary record meanings remain unknown.",
            "Recognized Unicode table records are decoded strictly by declared byte lengths. ID links remain experimental; duplicate text-and-ID bytes do not validate a project modification.",
        ],
    }


def _identity(stat: os.stat_result) -> tuple:
    return stat.st_dev, stat.st_ino, stat.st_size, stat.st_mtime_ns


def inspect_project(path: Path) -> dict:
    path = path.resolve(strict=True)
    with path.open("rb") as source:
        before = os.fstat(source.fileno())
        if before.st_size > MAX_BYTES:
            raise InspectionError("Project exceeds the 64 MiB inspection limit.")
        data = source.read(MAX_BYTES + 1)
        after = os.fstat(source.fileno())
    try:
        named_after = path.stat()
    except OSError as error:
        raise InspectionError("Project disappeared or changed while being read; retry a stable saved snapshot.") from error
    if _identity(before) != _identity(after) or _identity(before) != _identity(named_after) or len(data) != before.st_size:
        raise InspectionError("Project changed while being read; retry a stable saved snapshot.")
    report = inspect_bytes(data)
    report["source"] = {
        "path": str(path), "fileName": path.name, "sizeBytes": len(data),
        "sha256": hashlib.sha256(data).hexdigest(),
        "lastWriteUtc": datetime.fromtimestamp(before.st_mtime, timezone.utc).isoformat(),
        "snapshotKind": "autosave_candidate" if path.suffix.lower() == ".mv_" else "saved_project_candidate",
        "readConsistency": "Size, identity, and modification time were unchanged across the bounded read.",
    }
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("project", type=Path, help="MVP/MV_ snapshot to read; the source is never modified")
    parser.add_argument("--output", type=Path, help="Optional new JSON report destination; existing files are never replaced")
    args = parser.parse_args()
    try:
        if args.output:
            if args.output.suffix.lower() != ".json":
                raise InspectionError("Output must use a .json extension; project files are never output targets.")
            if args.output.resolve() == args.project.resolve() or (args.output.exists() and os.path.samefile(args.output, args.project)):
                raise InspectionError("Output must not be the source project.")
            if args.output.exists():
                raise InspectionError("Output already exists; choose a new report path or use stdout.")
        report = inspect_project(args.project)
        text = json.dumps(report, ensure_ascii=False, indent=2) + "\n"
        if args.output:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            with args.output.open("x", encoding="utf-8", newline="\n") as output:
                output.write(text)
            print("Wrote read-only snapshot report:", args.output)
            print("Observed chunks:", report["chunkCount"], "| Stored references:", len(report["storedReferences"]), "| Candidate assets:", len(report["whiteboardCandidates"]))
        else:
            print(text, end="")
        return 0
    except (OSError, InspectionError, struct.error) as error:
        parser.exit(2, "Inspection failed: %s\n" % error)


if __name__ == "__main__":
    raise SystemExit(main())
