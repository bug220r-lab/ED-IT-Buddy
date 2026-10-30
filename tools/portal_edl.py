"""Experimental Samplitude EDL 1.5 compiler for Buddy's synthetic edit demo.

This writes an edit list referencing original media. It does not render media,
modify an MVP, invoke MAGIX, or prove that MAGIX accepts the generated file.

Format evidence:
- MAGIX Movie Edit Pro 2021 Steam manual, pp.264-265: Samplitude EDL import.
- https://raw.githubusercontent.com/tomtjes/DAW-converters/master/hindenburg_to_samplitude-edl.sh
  maps Play-In/Out to timeline positions and Record-In/Out to source positions.
No official field-level schema has been located. Import remains experimental.
"""
from __future__ import annotations

import argparse
from dataclasses import asdict, dataclass
from fractions import Fraction
import hashlib
import json
import ntpath
import os
from pathlib import Path, PureWindowsPath
import re

SAMPLE_RATE = 48000
FPS = 30
FORMAT_VERSION = '1.5'
ID_PATTERN = re.compile(r'^[A-Za-z0-9_.-]{1,80}$')


def identifier(value: str) -> str:
    if not isinstance(value, str) or not ID_PATTERN.fullmatch(value):
        raise ValueError('Clip IDs must contain at most 80 letters, digits, underscores, hyphens or dots.')
    return value


def windows_path(value: str, workspace: str | None = None) -> str:
    if not isinstance(value, str) or any(char in value for char in ('"', '\r', '\n', '\0')):
        raise ValueError('EDL paths must not contain quotes, newlines or NUL.')
    # Prototype uses ASCII intentionally: this avoids assuming an unverified
    # ANSI/UTF-8/UTF-16 convention in the 2021 importer.
    try:
        value.encode('ascii')
    except UnicodeEncodeError as error:
        raise ValueError('Non-ASCII paths require an encoding compatibility test first.') from error
    path = PureWindowsPath(value)
    if not path.is_absolute() or not re.fullmatch('[A-Za-z]:', path.drive) or '..' in path.parts:
        raise ValueError('Use a normal absolute Windows drive path without parent traversal.')
    normalized = ntpath.normpath(str(path))
    if workspace is not None:
        root = windows_path(workspace)
        if ntpath.normcase(ntpath.commonpath((root, normalized))) != ntpath.normcase(root) or ntpath.normcase(normalized) == ntpath.normcase(root):
            raise ValueError('Every source must be a file inside the selected MSI workspace.')
    return normalized


def frames_to_samples(frame: int, fps: int = FPS, sample_rate: int = SAMPLE_RATE) -> int:
    if isinstance(frame, bool) or not isinstance(frame, int) or frame < 0:
        raise ValueError('Frame positions must be nonnegative integers.')
    if fps != FPS or sample_rate != SAMPLE_RATE:
        raise ValueError('This prototype supports only 30 fps and 48000 samples/second.')
    value = Fraction(frame * sample_rate, fps)
    if value.denominator != 1:
        raise ValueError('Frame position is not exactly representable in sample ticks.')
    return value.numerator


@dataclass(frozen=True)
class Clip:
    clip_id: str
    media_path: str
    timeline_in_frame: int
    source_in_frame: int
    source_out_frame: int
    track: int = 1


def render_edl(workspace: str, clips: list[Clip]) -> tuple[str, dict]:
    root = windows_path(workspace)
    if not clips or len(clips) > 64:
        raise ValueError('Expected between one and 64 video objects.')
    source_ids, sources, rows, normalized = {}, [], [], []
    used_ids = set()
    track_ends = {}
    for clip in sorted(clips, key=lambda item: (item.track, item.timeline_in_frame)):
        identifier(clip.clip_id)
        if clip.clip_id in used_ids:
            raise ValueError('A clip ID may be appended only once in this prototype.')
        used_ids.add(clip.clip_id)
        path = windows_path(clip.media_path, root)
        if PureWindowsPath(path).suffix.lower() not in ('.avi', '.mp4', '.mov', '.mxf', '.mts', '.m2ts', '.wmv', '.mxv'):
            raise ValueError('This compiler accepts original video paths only.')
        if type(clip.track) is not int or not 1 <= clip.track <= 99:
            raise ValueError('Track numbers are one-based, between 1 and 99.')
        if clip.source_out_frame <= clip.source_in_frame:
            raise ValueError('Source range must be nonempty and use an exclusive out frame.')
        duration = clip.source_out_frame - clip.source_in_frame
        if clip.timeline_in_frame < track_ends.get(clip.track, 0):
            raise ValueError('Overlapping objects on the same track are outside this prototype.')
        track_ends[clip.track] = clip.timeline_in_frame + duration
        play_in = frames_to_samples(clip.timeline_in_frame)
        play_out = frames_to_samples(clip.timeline_in_frame + duration)
        record_in = frames_to_samples(clip.source_in_frame)
        record_out = frames_to_samples(clip.source_out_frame)
        key = ntpath.normcase(path)
        if key not in source_ids:
            source_ids[key] = len(sources) + 1
            sources.append(path)
        source_id = source_ids[key]
        rows.append(f'{source_id} {clip.track} {play_in} {play_out} {record_in} {record_out} 0.0 0 0 0 0 "*default" 0 0 "*default" "{clip.clip_id}"')
        normalized.append(asdict(Clip(clip.clip_id, path, clip.timeline_in_frame, clip.source_in_frame, clip.source_out_frame, clip.track)))

    lines = [f'Samplitude EDL File Format Version {FORMAT_VERSION}',
             'Title: "ED-IT Buddy synthetic demo"', f'Sample Rate: {SAMPLE_RATE}',
             'Output Channels: 2', '', f'Source Table Entries: {len(sources)}']
    lines.extend(f'{index} "{path}"' for index, path in enumerate(sources, 1))
    for track in sorted(track_ends):
        lines += ['', f'Track {track}: "Buddy video {track}" Solo: 0 Mute: 0']
    lines += ['', '#Source Track Play-In Play-Out Record-In Record-Out Vol(dB) MT LK FadeIn % CurveType FadeOut % CurveType Name']
    lines.extend(rows)
    content = '\r\n'.join(lines) + '\r\n'
    manifest = {
        'status': 'prototype_generated', 'import_verified': False,
        'schema_evidence': 'Observed open-source Samplitude EDL 1.5 writer; no official field-level schema found',
        'workspace': root, 'format_version': FORMAT_VERSION, 'encoding': 'ASCII, CRLF',
        'sample_rate': SAMPLE_RATE, 'fps': FPS,
        'time_fields': {'Play-In/Play-Out': 'timeline start/end in sample ticks', 'Record-In/Record-Out': 'source start/end in sample ticks'},
        'source_media': sources, 'clips': normalized, 'timeline_frames': max(track_ends.values()),
        'edl_sha256': hashlib.sha256(content.encode('ascii')).hexdigest(),
        'limits': ['Video-reference import in MAGIX 2021 is untested.', 'Out-boundary behavior must be checked against rendered frame counts.',
                   'Titles, effects and project/export operations are not encoded.', 'File paths are lexical references; remote file existence is not checked.']
    }
    return content, manifest


def demo_clips(workspace: str) -> list[Clip]:
    root = PureWindowsPath(windows_path(workspace))
    return [Clip('A', str(root / 'Clip-A.avi'), 0, 0, 60),
            Clip('B', str(root / 'Clip-B.avi'), 60, 30, 120)]


def compile_sequence(sequence: dict) -> tuple[str, dict]:
    """Compile typed plan clip steps; retain titles/save/export as explicit follow-ups.

    The returned EDL is a batch description. No individual editing step has been
    performed or verified just because it can be represented in this description.
    """
    root = windows_path(sequence['workspace'])
    imports, trims, clips, cursor, deferred = {}, {}, [], {}, []
    appended = set()
    project_seen, finishing = False, False
    for step in sequence['steps']:
        action, args = step['action'], step['args']
        if finishing and action in ('create_project', 'import_media', 'trim_clip', 'append_clip'):
            raise ValueError('Clip editing after title/save/export would change sequence order; cannot batch it safely.')
        if action == 'create_project':
            if project_seen or clips or imports or args.get('fps') != FPS:
                raise ValueError('Project creation must precede clips and specify 30 fps.')
            project_seen = True
            windows_path(args['project_path'], root)
            deferred.append(step)
        elif action == 'import_media':
            clip_id = identifier(args['clip_id'])
            if clip_id in imports:
                raise ValueError('Duplicate imported clip ID.')
            imports[clip_id] = windows_path(args['media_path'], root)
        elif action == 'trim_clip':
            clip_id = identifier(args['clip_id'])
            if clip_id not in imports or clip_id in appended:
                raise ValueError('Trim requires an imported, not-yet-appended clip.')
            start, end = args['source_in_frame'], args['source_out_frame']
            frames_to_samples(start); frames_to_samples(end)
            if end <= start:
                raise ValueError('Empty or reversed trim range.')
            trims[clip_id] = (start, end)
        elif action == 'append_clip':
            clip_id = identifier(args['clip_id'])
            if clip_id not in trims or clip_id in appended:
                raise ValueError('Append requires one explicitly trimmed imported clip.')
            track = args['track']
            start, end = trims[clip_id]
            position = cursor.get(track, 0)
            clips.append(Clip(clip_id, imports[clip_id], position, start, end, track))
            cursor[track] = position + end - start
            appended.add(clip_id)
        elif action in ('add_title', 'save_project', 'export_video'):
            finishing = True
            deferred.append(step)
        else:
            raise ValueError('Unsupported action in EDL prototype: ' + str(action))
    if set(imports) != appended:
        raise ValueError('Every imported clip must be explicitly trimmed and appended; none may be silently dropped.')
    content, manifest = render_edl(root, clips)
    manifest['unexecuted_steps'] = deferred
    manifest['note'] = 'All original steps remain unexecuted. EDL compiles import/trim/append intent only; a future worker must import and verify the batch before marking those steps complete.'
    return content, manifest


def write_prototype(path: Path, content: str, manifest: dict) -> None:
    if path.suffix.lower() != '.edl':
        raise ValueError('Output filename must end in .edl.')
    receipt = path.with_suffix('.edl.json')
    if path.exists() or receipt.exists():
        raise FileExistsError('Refusing to overwrite an EDL or its receipt.')
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open('xb') as stream:
        stream.write(content.encode('ascii'))
    with receipt.open('x', encoding='utf-8') as stream:
        json.dump(manifest, stream, indent=2)


def parse_edl(content: str) -> dict:
    """Parse only the known source/track/sample-position subset, rejecting ambiguity."""
    source_table, rows = {}, []
    source_remaining = None
    sample_rate = None
    version = None
    for raw in content.lstrip('\ufeff').splitlines():
        line = raw.strip()
        if not line or line.startswith('#'):
            continue
        if version is None:
            match = re.fullmatch(r'Samplitude EDL File Format Version (1\.[567])', line)
            if not match:
                raise ValueError('Expected a known Samplitude EDL 1.5, 1.6 or 1.7 header.')
            version = match.group(1)
            continue
        if source_remaining:
            match = re.fullmatch(r'(\d+)\s+"([^"]+)"', line)
            if not match:
                raise ValueError('Malformed source table row.')
            index = int(match.group(1))
            if index < 1 or index in source_table:
                raise ValueError('Duplicate or invalid source table ID.')
            source_table[index] = windows_path(match.group(2))
            source_remaining -= 1
            continue
        if line.startswith('Sample Rate:'):
            if sample_rate is not None:
                raise ValueError('Duplicate sample rate header.')
            sample_rate = int(line.split(':', 1)[1].strip())
            if sample_rate <= 0:
                raise ValueError('Invalid sample rate.')
            continue
        if line.startswith('Source Table Entries:'):
            if source_remaining is not None:
                raise ValueError('Duplicate source table.')
            source_remaining = int(line.split(':', 1)[1].strip())
            if not 1 <= source_remaining <= 64:
                raise ValueError('Source count outside prototype bounds.')
            continue
        if re.fullmatch(r'Title:\s*"[^"]*"', line) or re.fullmatch(r'Output Channels:\s*\d+', line) or re.fullmatch(r'Track \d+:\s*"[^"]*"(?:\s+Solo:\s*[01]\s+Mute:\s*[01])?', line):
            continue
        match = re.match(r'^(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)(?:\s+.*)?$', line)
        if not match:
            raise ValueError('Unrecognized EDL record; cannot safely compare: ' + line[:120])
        source, track, play_in, play_out, record_in, record_out = map(int, match.groups())
        if source not in source_table or track < 1 or play_out <= play_in or record_out <= record_in or play_out - play_in != record_out - record_in:
            raise ValueError('Invalid source, track or object sample range.')
        rows.append({'source': ntpath.normcase(source_table[source]), 'track': track,
                     'play_in': play_in, 'play_out': play_out, 'record_in': record_in, 'record_out': record_out})
    if version is None or sample_rate is None or source_remaining != 0 or not rows:
        raise ValueError('Incomplete EDL document.')
    if len(rows) > 64:
        raise ValueError('Too many EDL objects.')
    return {'version': version, 'sample_rate': sample_rate,
            'sources': sorted(ntpath.normcase(path) for path in source_table.values()),
            'objects': sorted(rows, key=lambda row: (row['track'], row['play_in'], row['source'], row['record_in']))}


def compare_edl(expected: str, observed: str) -> dict:
    """A text comparison is not proof that MAGIX applied an edit."""
    left, right = parse_edl(expected), parse_edl(observed)
    mismatches = [key for key in ('sample_rate', 'sources', 'objects') if left[key] != right[key]]
    return {'matches_expected': not mismatches, 'mismatches': mismatches,
            'expected': left, 'observed': right, 'native_application_verified': False}


def validate_roundtrip(expected_path: Path, exported_path: Path, native_provenance: dict | None = None) -> dict:
    """Bind comparison to an explicitly identified distinct native export artifact.

    Provenance is supplied by the trusted UI adapter, not inferred from a name or
    from matching generated text. This function cannot authenticate the caller's
    native-export claim; the coordinator must retain its actual operation receipt.
    """
    expected_path, exported_path = expected_path.resolve(), exported_path.resolve()
    if expected_path == exported_path or os.path.samefile(expected_path, exported_path):
        raise ValueError('Roundtrip evidence must be a distinct MAGIX-exported file, not the generated input.')
    before = {str(path): hashlib.sha256(path.read_bytes()).hexdigest() for path in (expected_path, exported_path)}
    def read_text(path):
        data = path.read_bytes()
        return data.decode('utf-16') if data.startswith((b'\xff\xfe', b'\xfe\xff')) else data.decode('utf-8-sig')
    result = compare_edl(read_text(expected_path), read_text(exported_path))
    provenance_valid = bool(isinstance(native_provenance, dict)
        and native_provenance.get('origin') == 'magix_native_export'
        and native_provenance.get('operation_receipt_id')
        and native_provenance.get('export_sha256') == before[str(exported_path)]
        and native_provenance.get('export_path')
        and Path(native_provenance['export_path']).resolve() == exported_path
        and type(native_provenance.get('magix_process_id')) is int
        and native_provenance['magix_process_id'] > 0)
    after = {str(path): hashlib.sha256(path.read_bytes()).hexdigest() for path in (expected_path, exported_path)}
    result.update({'input_edl': str(expected_path), 'exported_edl': str(exported_path),
                   'sha256': before, 'files_unchanged': before == after,
                   'native_export_claim_supplied': provenance_valid,
                   'eligible_native_evidence': result['matches_expected'] and before == after and provenance_valid,
                   'native_export_provenance': native_provenance,
                   'limitation': 'The caller must substantiate this claim with an actual MAGIX export operation receipt; generated-file equality alone is never native verification.'})
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--workspace', required=True, help='Absolute MSI test workspace containing Clip-A.avi and Clip-B.avi.')
    parser.add_argument('--output', required=True, type=Path, help='New local .edl output path.')
    parser.add_argument('--sequence', type=Path, help='Optional typed sequence JSON; otherwise compile the fixed five-second demo.')
    args = parser.parse_args()
    if args.sequence:
        sequence = json.loads(args.sequence.read_text(encoding='utf-8-sig'))
        if windows_path(sequence['workspace']) != windows_path(args.workspace):
            raise ValueError('Sequence workspace differs from the requested workspace.')
        content, manifest = compile_sequence(sequence)
    else:
        content, manifest = render_edl(args.workspace, demo_clips(args.workspace))
    write_prototype(args.output.resolve(), content, manifest)
    print(json.dumps({'edl': str(args.output.resolve()), 'import_verified': False, 'timeline_frames': manifest['timeline_frames']}, indent=2))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
