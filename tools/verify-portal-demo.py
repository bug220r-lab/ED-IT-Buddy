"""Verify the synthetic Buddy export, without modifying input media.

This checks media mechanics only. The exact title text, title visibility and saved
MAGIX project still require recorded human/agent visual review. FFmpeg fixtures
used to test this verifier are not evidence that MAGIX edited a video.
"""
from __future__ import annotations

import argparse
import datetime as dt
from fractions import Fraction
import hashlib
import json
from pathlib import Path
import subprocess
import sys

from PIL import Image, ImageChops, ImageDraw, ImageStat

ROOT = Path(__file__).resolve().parents[1]
WIDTH, HEIGHT, FPS, FRAME_COUNT = 640, 360, 30, 150
CAPTION_ROI = (40, 96, 350, 136)
BACKGROUND_ROI = (5, 5, 30, 30)
MOTION_ROI = (0, 220, 640, 265)
TITLE_MASK = (0, 140, 640, 220)
MAX_CAPTION_MAE = 8.0
MAX_BACKGROUND_MAE = 8.0
MAX_MOTION_SHIFT = 1.25


def digest(path: Path) -> str:
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def run(command: list[str], timeout: int = 90) -> subprocess.CompletedProcess:
    result = subprocess.run(command, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, timeout=timeout,
                            creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    if result.returncode:
        raise RuntimeError(f'{Path(command[0]).name} failed: {result.stderr.decode("utf-8", "replace")[:4000]}')
    return result


def probe(path: Path, ffprobe: Path) -> dict:
    result = run([str(ffprobe), '-v', 'error', '-select_streams', 'v:0', '-count_frames',
                  '-show_entries', 'stream=width,height,avg_frame_rate,r_frame_rate,nb_read_frames,duration,codec_name:format=duration',
                  '-of', 'json', str(path)])
    value = json.loads(result.stdout)
    if len(value.get('streams', [])) != 1:
        raise RuntimeError('Expected one selected video stream.')
    return value


def decode(path: Path, ffmpeg: Path, max_frames: int = 301):
    # The metadata is checked first. The frame limit also bounds malformed inputs.
    result = run([str(ffmpeg), '-v', 'error', '-xerror', '-nostdin', '-i', str(path),
                  '-map', '0:v:0', '-an', '-sn', '-dn', '-fps_mode', 'passthrough',
                  '-frames:v', str(max_frames), '-f', 'rawvideo', '-pix_fmt', 'rgb24', 'pipe:1'])
    if result.stderr.strip():
        raise RuntimeError('Decoder reported errors: ' + result.stderr.decode('utf-8', 'replace')[:4000])
    stride = WIDTH * HEIGHT * 3
    if len(result.stdout) % stride:
        raise RuntimeError('Decoder returned an incomplete RGB frame.')
    for start in range(0, len(result.stdout), stride):
        yield Image.frombytes('RGB', (WIDTH, HEIGHT), result.stdout[start:start + stride])


def mae(first: Image.Image, second: Image.Image) -> float:
    return sum(ImageStat.Stat(ImageChops.difference(first, second)).mean) / 3


def yellow_center(image: Image.Image) -> tuple[float, int] | None:
    cropped = image.crop(MOTION_ROI)
    total, count = 0, 0
    pixels = cropped.get_flattened_data() if hasattr(cropped, 'get_flattened_data') else cropped.getdata()
    for index, (red, green, blue) in enumerate(pixels):
        if red > 180 and green > 145 and blue < 145 and red - blue > 70:
            total += index % cropped.width
            count += 1
    if count < 500:
        return None
    return total / count, count


def features(image: Image.Image) -> dict:
    return {'caption': image.crop(CAPTION_ROI), 'background': image.crop(BACKGROUND_ROI),
            'motion': yellow_center(image)}


def reference(path: Path, ffmpeg: Path, ffprobe: Path) -> list[dict]:
    stream = probe(path, ffprobe)['streams'][0]
    if (stream.get('width'), stream.get('height')) != (WIDTH, HEIGHT) or Fraction(stream['avg_frame_rate']) != FPS:
        raise RuntimeError('Reference clip has unexpected geometry or frame rate: ' + str(path))
    frames = [features(image) for image in decode(path, ffmpeg, 121)]
    if len(frames) != 120 or any(frame['motion'] is None for frame in frames):
        raise RuntimeError('Reference clip must contain the 120 known synthetic motion frames: ' + str(path))
    return frames


def write_contact_sheet(samples: dict[int, Image.Image], output: Path) -> str | None:
    if not samples:
        return None
    sheet = Image.new('RGB', (WIDTH * 2, (HEIGHT + 36) * 2), '#15171c')
    draw = ImageDraw.Draw(sheet)
    for index, frame in enumerate((0, 59, 60, 149)):
        left, top = index % 2 * WIDTH, index // 2 * (HEIGHT + 36)
        expected = f'A frame {frame}' if frame < 60 else f'B frame {frame - 30}'
        draw.text((left + 10, top + 8), f'Output {frame} | expected {expected} | title {"ON" if frame < 60 else "OFF"}', fill='white')
        if frame in samples:
            sheet.paste(samples[frame], (left, top + 36))
    path = output / 'contact-sheet.png'
    sheet.save(path)
    return str(path)


def verify(mp4: Path, output: Path, ffmpeg: Path, ffprobe: Path,
           clip_a: Path, clip_b: Path, provenance: str = 'unspecified') -> dict:
    mp4, output, clip_a, clip_b = (path.resolve() for path in (mp4, output, clip_a, clip_b))
    if not all(path.is_file() for path in (mp4, clip_a, clip_b, ffmpeg, ffprobe)):
        raise FileNotFoundError('Input video, both reference clips, ffmpeg and ffprobe must exist.')
    before = {str(path): digest(path) for path in (mp4, clip_a, clip_b)}
    output.mkdir(parents=True, exist_ok=False)
    report = {
        'schema_version': 1, 'created_utc': dt.datetime.now(dt.timezone.utc).isoformat(),
        'input': str(mp4), 'provenance_claim': provenance,
        'note': 'This validates the file content, not which application created it.',
        'expected': {'width': WIDTH, 'height': HEIGHT, 'fps': FPS, 'frames': FRAME_COUNT,
                     'sequence': ['A source frames [0,60)', 'B source frames [30,120)']},
        'sha256_before': before, 'automated_media_pass': False, 'overall_demo_verified': False,
        'title_review': {'status': 'required', 'exact_text': 'ED-IT Buddy',
                         'expected_frame_range': [0, 60], 'excluded_title_band_xyxy': list(TITLE_MASK),
                         'instructions': 'Visually verify exact title text on frames 0 and 59, absent on 60 and 149. This tool does not perform OCR or approve the title.'},
        'project_review': {'status': 'required', 'instructions': 'Reopen and verify the saved MAGIX project separately.'},
        'failures': []
    }
    samples = {}
    try:
        report['ffmpeg_version'] = run([str(ffmpeg), '-version']).stdout.decode('utf-8', 'replace').splitlines()[0]
        metadata = probe(mp4, ffprobe)
        report['metadata'] = metadata
        stream = metadata['streams'][0]
        checks = {
            'dimensions': (stream.get('width'), stream.get('height')) == (WIDTH, HEIGHT),
            'frame_rate': Fraction(stream.get('avg_frame_rate', '0/1')) == FPS,
            'frame_count': int(stream.get('nb_read_frames', -1)) == FRAME_COUNT,
            'duration': abs(float(stream.get('duration', metadata.get('format', {}).get('duration', -1))) - 5.0) <= 0.001
        }
        report['metadata_checks'] = checks
        for name, passed in checks.items():
            if not passed:
                report['failures'].append('metadata_' + name)
        # A wrong duration/frame count still needs boundary pictures and frame
        # diagnostics. Decode only when the fixed-size RGB layout is valid;
        # retain every metadata failure so this cannot turn a failed export green.
        if not checks['dimensions'] or not checks['frame_rate']:
            report['status'] = 'failed_media_checks'
        else:
            refs_a, refs_b = reference(clip_a, ffmpeg, ffprobe), reference(clip_b, ffmpeg, ffprobe)
            report['frame_checks'] = []
            failures = 0
            decoded_count = 0
            for index, image in enumerate(decode(mp4, ffmpeg)):
                decoded_count += 1
                if index in (0, 59, 60, 149):
                    samples[index] = image.copy()
                if index >= FRAME_COUNT:
                    failures += 1
                    continue
                source = 'A' if index < 60 else 'B'
                source_index = index if index < 60 else index - 30
                expected = (refs_a if source == 'A' else refs_b)[source_index]
                actual = features(image)
                caption_error = mae(actual['caption'], expected['caption'])
                background_error = mae(actual['background'], expected['background'])
                motion_shift = abs(actual['motion'][0] - expected['motion'][0]) if actual['motion'] else None
                passed = caption_error <= MAX_CAPTION_MAE and background_error <= MAX_BACKGROUND_MAE and motion_shift is not None and motion_shift <= MAX_MOTION_SHIFT
                if not passed:
                    failures += 1
                report['frame_checks'].append({'output_frame': index, 'expected_clip': source, 'expected_source_frame': source_index,
                    'caption_mae': round(caption_error, 4), 'background_mae': round(background_error, 4),
                    'motion_centroid_shift_px': round(motion_shift, 4) if motion_shift is not None else None, 'passed': passed})
            report['decoded_frames'] = decoded_count
            report['decode_error_free'] = True
            report['content_failed_frames'] = failures
            report['tolerances'] = {'caption_mae': MAX_CAPTION_MAE, 'background_mae': MAX_BACKGROUND_MAE, 'motion_centroid_shift_px': MAX_MOTION_SHIFT}
            if decoded_count != FRAME_COUNT:
                report['failures'].append('decoded_frame_count')
            if failures:
                report['failures'].append('clip_order_or_source_frames')
            report['automated_media_pass'] = not report['failures']
            report['status'] = 'media_checks_passed_reviews_pending' if report['automated_media_pass'] else 'failed_media_checks'
    except Exception as error:
        report['status'] = 'verification_error'
        report['failures'].append(str(error))
    report['contact_sheet'] = write_contact_sheet(samples, output)
    after = {str(path): digest(path) for path in (mp4, clip_a, clip_b)}
    report['sha256_after'] = after
    report['inputs_unchanged'] = before == after
    if before != after:
        report['automated_media_pass'] = False
        report['status'] = 'input_changed_during_verification'
        report['failures'].append('input_changed_during_verification')
    (output / 'verification.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mp4', type=Path)
    parser.add_argument('output_directory', type=Path, help='New directory; existing directories are refused.')
    parser.add_argument('--ffmpeg', type=Path, default=ROOT / '.build/ffmpeg/ffmpeg.exe')
    parser.add_argument('--ffprobe', type=Path, default=ROOT / '.build/ffmpeg/ffprobe.exe')
    parser.add_argument('--clip-a', type=Path, default=ROOT / 'artifacts/direct-edit-test/Clip-A.avi')
    parser.add_argument('--clip-b', type=Path, default=ROOT / 'artifacts/direct-edit-test/Clip-B.avi')
    parser.add_argument('--provenance', default='unspecified', help='Descriptive claim only; never used as verification evidence.')
    args = parser.parse_args()
    report = verify(args.mp4, args.output_directory, args.ffmpeg, args.ffprobe, args.clip_a, args.clip_b, args.provenance)
    print(json.dumps({key: report[key] for key in ('status', 'automated_media_pass', 'overall_demo_verified', 'contact_sheet', 'failures')}, indent=2))
    return 0 if report['automated_media_pass'] else 1


if __name__ == '__main__':
    sys.exit(main())
