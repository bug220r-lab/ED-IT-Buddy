"""Prepare and verify a copied RCC with exactly one changed button label. Never launches MAGIX."""
import hashlib
import json
from pathlib import Path
import struct
import zlib
from inspect_qt_resource import inspect

SOURCE = Path(r'C:\Program Files (x86)\Steam\steamapps\common\MAGIX\Video deluxe 2021 Plus Steam Edition\qml\resource.rcc')
EXPECTED = '36c61ceb9901f9b5cdd3d8a66888f36bdbdd7537f8a7ef87d8157dc868a03e8a'
TARGET = '/_resource/Dialogs/ObjectEditorDialog/Placement/ControlsBottom.qml'
OUTPUT = Path(__file__).resolve().parents[1] / '.build/research/ui-label-test'

def main():
    original = SOURCE.read_bytes()
    assert hashlib.sha256(original).hexdigest() == EXPECTED, 'Unexpected source build'
    before = inspect(original)
    matches = [e for e in before['entries'] if e['path'] == TARGET]
    assert len(matches) == 1
    entry = matches[0]
    assert entry['text'].count('text: allStrings.strOK;') == 1
    replacement = entry['text'].replace('text: allStrings.strOK;', 'text: "OK *";').encode('utf-8')
    version, tree, payloads, names = struct.unpack_from('>4I', original, 4)
    raw = struct.pack('>I', len(replacement)) + zlib.compress(replacement) if entry['compressed'] else replacement
    addition = struct.pack('>I', len(raw)) + raw
    modified = bytearray(original[:names] + addition + original[names:])
    struct.pack_into('>4I', modified, 4, version, tree + len(addition), payloads, names + len(addition))
    struct.pack_into('>I', modified, tree + len(addition) + entry['node'] * 14 + 10, names - payloads)
    after = inspect(modified)
    assert before['fileCount'] == after['fileCount']
    changed = []
    for old, new in zip(before['entries'], after['entries']):
        assert (old['path'], old['node'], old['country'], old['language']) == (new['path'], new['node'], new['country'], new['language'])
        if old['sha256'] != new['sha256']:
            changed.append(old['path'])
    assert changed == [TARGET], changed
    assert SOURCE.read_bytes() == original, 'Source changed during inspection'
    OUTPUT.mkdir(parents=True, exist_ok=False)
    (OUTPUT / 'resource.original.rcc').write_bytes(original)
    (OUTPUT / 'resource.label-test.rcc').write_bytes(modified)
    report = dict(sourceSha256=EXPECTED, modifiedSha256=hashlib.sha256(modified).hexdigest(), entriesVerified=before['fileCount'], changedEntries=changed, label='OK -> OK *', runtimeTested=False, installedSourceUnchanged=True)
    (OUTPUT / 'verification.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report, indent=2))

if __name__ == '__main__':
    main()
