"""Read-only static inspection of MAGIX's OFX wrapper; never loads the target DLL.

Requires the separately approved iced-x86 package in .build/research/python-deps.
The PE is read as bytes. Results are structural evidence, not runtime observations.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import struct
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / ".build/research/python-deps"))
from iced_x86 import Decoder, Formatter, FormatterSyntax


class PE:
    def __init__(self, path: Path):
        with path.open("rb") as source:
            self.data = source.read(16 * 1024 * 1024 + 1)
        if len(self.data) > 16 * 1024 * 1024:
            raise ValueError("Inspection limit is 16 MiB")
        if self.data[:2] != b"MZ":
            raise ValueError("Not a PE")
        start = self.u32(60)
        if self.data[start:start + 4] != b"PE\0\0":
            raise ValueError("Bad PE signature")
        optional = start + 24
        if self.u16(start + 4) != 0x8664 or self.u16(optional) != 0x20B:
            raise ValueError("Only x64 PE32+ is supported")
        self.base = self.u64(optional + 24)
        self.sections = []
        for index in range(self.u16(start + 6)):
            offset = optional + self.u16(start + 20) + 40 * index
            self.sections.append({
                "name": self.data[offset:offset + 8].rstrip(b"\0").decode("ascii"),
                "rva": self.u32(offset + 12), "size": self.u32(offset + 8),
                "raw": self.u32(offset + 20), "raw_size": self.u32(offset + 16),
            })
        self.functions = []
        exception_rva, exception_size = struct.unpack_from("<II", self.data, optional + 112 + 3 * 8)
        table = self.raw(exception_rva)
        for offset in range(table, table + exception_size - 11, 12):
            begin, end, unwind = struct.unpack_from("<III", self.data, offset)
            if begin and end > begin:
                self.functions.append((begin, end, unwind))

    def u16(self, offset):
        return struct.unpack_from("<H", self.data, offset)[0]

    def u32(self, offset):
        return struct.unpack_from("<I", self.data, offset)[0]

    def u64(self, offset):
        return struct.unpack_from("<Q", self.data, offset)[0]

    def raw(self, rva):
        for section in self.sections:
            if section["rva"] <= rva < section["rva"] + section["raw_size"]:
                return rva - section["rva"] + section["raw"]
        raise ValueError("RVA not backed by file bytes: %#x" % rva)

    def rva(self, offset):
        for section in self.sections:
            if section["raw"] <= offset < section["raw"] + section["raw_size"]:
                return offset - section["raw"] + section["rva"]
        raise ValueError("Offset is outside section bytes")

    def string(self, va):
        offset = self.raw(va - self.base)
        end = self.data.find(b"\0", offset, min(offset + 4096, len(self.data)))
        if end < 0:
            raise ValueError("Unterminated or excessive string")
        return self.data[offset:end].decode("ascii")

    def function(self, rva):
        return next((row for row in self.functions if row[0] <= rva < row[1]), None)

    def decode_function(self, rva):
        function = self.function(rva)
        if function is None:
            raise ValueError("No .pdata function covers %#x" % rva)
        begin, end, _ = function
        return list(Decoder(64, self.data[self.raw(begin):self.raw(end - 1) + 1], ip=self.base + begin))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dll", type=Path)
    parser.add_argument("--function-rva", type=lambda value: int(value, 0))
    parser.add_argument("--range-rva", type=lambda value: int(value, 0), help="Explicit bounded byte-range disassembly, including leaf functions without .pdata")
    parser.add_argument("--range-bytes", type=int, default=64)
    args = parser.parse_args()
    pe = PE(args.dll)
    formatter = Formatter(FormatterSyntax.INTEL)
    if args.range_rva is not None:
        if not 1 <= args.range_bytes <= 4096:
            raise ValueError("Range size must be 1..4096 bytes")
        start = pe.raw(args.range_rva)
        end = pe.raw(args.range_rva + args.range_bytes - 1) + 1
        for instruction in Decoder(64, pe.data[start:end], ip=pe.base + args.range_rva):
            print("%08X  %s" % (instruction.ip - pe.base, formatter.format(instruction)))
        return
    if args.function_rva is not None:
        for instruction in pe.decode_function(args.function_rva):
            print("%08X  %s" % (instruction.ip - pe.base, formatter.format(instruction)))
        return
    targets = {}
    for name in ("OfxParamHostPropSupportsCustomInteract", "OfxImageEffectPropSupportsOverlays", "OfxTimeLineSuite"):
        offset = pe.data.index(name.encode("ascii") + b"\0")
        targets[pe.base + pe.rva(offset)] = {"name": name, "string_offset": hex(offset), "string_rva": hex(pe.rva(offset)), "data_pointer_matches": [], "code_xrefs": []}
    for va, record in targets.items():
        needle = struct.pack("<Q", va)
        position = pe.data.find(needle)
        while position >= 0:
            entry = {"offset": hex(position), "rva": hex(pe.rva(position))}
            if position + 32 <= len(pe.data):
                type_id, dimension, readonly = struct.unpack_from("<IIB", pe.data, position + 8)
                default_va = pe.u64(position + 24)
                if type_id <= 3 and dimension <= 16 and readonly <= 1:
                    entry["prop_spec_candidate"] = {"type_id": type_id, "dimension": dimension, "readonly": bool(readonly), "default_string": pe.string(default_va) if default_va else None}
            record["data_pointer_matches"].append(entry)
            position = pe.data.find(needle, position + 1)
    text_section = next(section for section in pe.sections if section["name"] == ".text")
    decoder = Decoder(64, pe.data[text_section["raw"]:text_section["raw"] + text_section["raw_size"]], ip=pe.base + text_section["rva"])
    seen = set()
    for instruction in decoder:
        if instruction.is_ip_rel_memory_operand and instruction.ip_rel_memory_address in targets:
            rva = instruction.ip - pe.base
            function = pe.function(rva)
            # Validate the candidate against decoding from a .pdata function boundary.
            if function is None:
                continue
            body = pe.decode_function(rva)
            positions = [index for index, item in enumerate(body) if item.ip == instruction.ip]
            if not positions:
                continue
            index = positions[0]
            key = (rva, instruction.ip_rel_memory_address)
            if key in seen:
                continue
            seen.add(key)
            targets[instruction.ip_rel_memory_address]["code_xrefs"].append({
                "rva": hex(rva), "function_start": hex(function[0]), "function_end": hex(function[1]),
                "context": ["%08X  %s" % (item.ip - pe.base, formatter.format(item)) for item in body[max(0, index - 5):index + 12]],
            })
    print(json.dumps({"path": str(args.dll.resolve()), "sha256": hashlib.sha256(pe.data).hexdigest(), "size": len(pe.data), "image_base": hex(pe.base), "targets": list(targets.values())}, indent=2))


if __name__ == "__main__":
    main()
