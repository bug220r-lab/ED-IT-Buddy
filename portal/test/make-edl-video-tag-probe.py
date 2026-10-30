"""Create the explicitly requested source-type-only EDL probe; never invoke MAGIX."""
from pathlib import Path
import hashlib
import json
import re

root = Path(__file__).resolve().parents[2]
original = root / '.build' / 'buddy-demo-20261030-original-clips.edl'
output = root / '.build' / 'buddy-demo-video-tagged.edl'
before = original.read_bytes()
after, changes = re.subn(rb'(?m)^(\d+ "[^"\r\n]+\.avi")(?=\r?$)', rb'\1 VIDEO', before)
if changes != 2 or after.replace(b'" VIDEO', b'"') != before:
    raise RuntimeError('Expected exactly two source-type additions and no other changes.')
with output.open('xb') as target:
    target.write(after)
if original.read_bytes() != before:
    raise RuntimeError('Original EDL changed while creating the probe.')
source = 'https://www.magix.info/us/forum/magix-video-deluxe-2026-premium-time-line-list--1352691/'
report = {
    'experiment': 'Add VIDEO type to each AVI source-table entry only',
    'original': str(original), 'output': str(output),
    'original_sha256': hashlib.sha256(before).hexdigest(),
    'output_sha256': hashlib.sha256(after).hexdigest(),
    'source_rows_changed': changes,
    'timing_header_and_all_other_bytes_unchanged': True,
    'native_import_verified': False,
    'evidence': {'url': source, 'author': 'johnebaker', 'date': '2026-03-20',
                 'description': 'Firsthand Video Pro X native export source-table entries include VIDEO or AUDIO after the quoted path. This later-build example does not prove necessity on Movie Edit Pro 2021.'}
}
with output.with_suffix('.provenance.json').open('x', encoding='utf-8') as target:
    json.dump(report, target, indent=2)
    target.write('\n')
manifest = json.loads(original.with_suffix('.edl.json').read_text(encoding='utf-8-sig'))
manifest.update({'edl_sha256': report['output_sha256'], 'source_type': 'VIDEO',
                 'experiment': 'Only source-table VIDEO tags added. Original failed native duration verification; this variant remains unverified.',
                 'provenance_file': output.with_suffix('.provenance.json').name})
with output.with_suffix('.edl.json').open('x', encoding='utf-8') as target:
    json.dump(manifest, target, indent=2)
    target.write('\n')
note = f'''# Controlled EDL source-type probe

The only differences from `{original.name}` are ` VIDEO` after each of the two quoted AVI source paths. Version 1.5, 48000 Hz, tracks, all timing fields, names, fades, line endings and every other byte are unchanged.

The source-type token is demonstrated in a [firsthand native Video Pro X export]({source}) posted by johnebaker on 20 March 2026. That later-build example establishes the token's existence, not that it fixes this import.

The original is unchanged (SHA256 `{report['original_sha256']}`). The probe SHA256 is `{report['output_sha256']}`.

Import only into a distinct disposable comparison project and export a distinct native checkpoint. Success requires 150 frames: A0–59 followed by B30–119, no unintended overlap or freeze. Generated-file equality is not native verification. The production compiler remains unchanged.
'''
with output.with_suffix('.md').open('x', encoding='utf-8') as target:
    target.write(note)
print(json.dumps(report, indent=2))
