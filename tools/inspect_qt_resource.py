"""Read-only Qt RCC v1 inventory for local interoperability research.

Does not load Qt, execute QML/JavaScript, or modify/repack the input archive.
Format reference: qt/qtbase 5.12 src/corelib/io/qresource.cpp.
"""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import struct
import zlib

MAX_ARCHIVE = 32 * 1024 * 1024
MAX_PAYLOAD = 8 * 1024 * 1024
MAX_EXPANDED = 64 * 1024 * 1024


def inspect(data):
    if len(data) < 20 or len(data) > MAX_ARCHIVE or data[:4] != b'qres':
        raise ValueError('Expected a bounded Qt RCC archive')
    version, tree, payloads, names = struct.unpack_from('>4I', data, 4)
    if version != 1 or not 20 <= payloads < names < tree < len(data):
        raise ValueError('Only the observed RCC v1 section ordering is supported')
    count, remainder = divmod(len(data) - tree, 14)
    if remainder or count > 20000:
        raise ValueError('Invalid or oversized node table')
    pending, seen, entries = [(0, '', 0)], set(), []
    total_expanded = 0
    while pending:
        index, prefix, depth = pending.pop()
        if index in seen or not 0 <= index < count or depth > 40:
            raise ValueError('Invalid, repeated, or excessively nested node')
        seen.add(index)
        at = tree + index * 14
        name_offset, flags = struct.unpack_from('>IH', data, at)
        if flags & ~3:
            raise ValueError('Unsupported resource flags')
        name = ''
        if index:
            name_at = names + name_offset
            if not names <= name_at <= tree - 6:
                raise ValueError('Name header outside name table')
            chars = struct.unpack_from('>H', data, name_at)[0]
            end = name_at + 6 + chars * 2
            if end > tree:
                raise ValueError('Name outside name table')
            name = data[name_at + 6:end].decode('utf-16be', errors='strict')
        path = prefix + '/' + name if index else ''
        if flags & 2:
            children, first = struct.unpack_from('>II', data, at + 6)
            if first + children > count:
                raise ValueError('Children outside node table')
            pending.extend((child, path, depth + 1) for child in reversed(range(first, first + children)))
            continue
        country, language, relative = struct.unpack_from('>HHI', data, at + 6)
        start = payloads + relative
        if not payloads <= start <= names - 4:
            raise ValueError('Payload header outside payload table')
        length = struct.unpack_from('>I', data, start)[0]
        if length > MAX_PAYLOAD or start + 4 + length > names:
            raise ValueError('Payload outside table or over limit')
        raw = data[start + 4:start + 4 + length]
        if flags & 1:
            if len(raw) < 4:
                raise ValueError('Missing decompressed size')
            expected = struct.unpack_from('>I', raw)[0]
            if expected > MAX_PAYLOAD:
                raise ValueError('Decompressed payload over limit')
            inflater = zlib.decompressobj()
            unpacked = inflater.decompress(raw[4:], expected + 1)
            if len(unpacked) != expected or not inflater.eof or inflater.unconsumed_tail or inflater.unused_data:
                raise ValueError('Compressed payload size or stream mismatch')
        else:
            unpacked = raw
        total_expanded += len(unpacked)
        if total_expanded > MAX_EXPANDED:
            raise ValueError('Total decompressed data over limit')
        item = dict(path=path, node=index, country=country, language=language,
                    compressed=bool(flags & 1), storedBytes=length, bytes=len(unpacked),
                    sha256=hashlib.sha256(unpacked).hexdigest())
        if Path(name).suffix.lower() in ('.qml', '.js', '.json', '.qrc') or name == 'qmldir':
            item['text'] = unpacked.decode('utf-8-sig', errors='strict')
        entries.append(item)
    if len(seen) != count:
        raise ValueError('Unvisited nodes remain in archive')
    return dict(format='Qt RCC v1', nodeCount=count, fileCount=len(entries),
                expandedBytes=total_expanded, extensions=dict(Counter(Path(e['path']).suffix.lower() or '<none>' for e in entries)),
                entries=entries)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('archive', type=Path)
    parser.add_argument('--output', type=Path, required=True, help='New JSON inventory file; never overwrite')
    args = parser.parse_args()
    try:
        with args.archive.open('rb') as stream:
            data = stream.read(MAX_ARCHIVE + 1)
        result = inspect(data)
        result['source'] = dict(path=str(args.archive.resolve()), bytes=len(data), sha256=hashlib.sha256(data).hexdigest())
        if args.output.suffix.lower() != '.json':
            raise ValueError('Output must be a new .json file')
        with args.output.open('x', encoding='utf-8') as stream:
            json.dump(result, stream, ensure_ascii=False, indent=2)
        print(json.dumps({k: result[k] for k in ('format', 'nodeCount', 'fileCount', 'expandedBytes', 'extensions')}, indent=2))
    except (OSError, ValueError, struct.error, zlib.error) as error:
        parser.exit(2, 'Inspection failed: ' + str(error) + '\n')


if __name__ == '__main__':
    main()
