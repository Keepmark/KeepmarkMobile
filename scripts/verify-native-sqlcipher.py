#!/usr/bin/env python3
"""Verify provenance, ELF ABI/alignment and optional final APK native packaging."""
import argparse
import hashlib
import io
import json
from pathlib import Path
import struct
import zipfile

ROOT = Path(__file__).resolve().parents[1]
NATIVE = ROOT / 'native/sqlcipher'
SOURCES = json.loads((NATIVE / 'sources.json').read_text())


def dex_classes(data):
    """Return defined class descriptors, not string references to missing classes."""
    if not data.startswith(b'dex\n'):
        raise SystemExit('Invalid APK DEX header')
    strings_count, strings_offset, types_count, types_offset = struct.unpack_from('<IIII', data, 56)
    definitions_count, definitions_offset = struct.unpack_from('<II', data, 96)
    strings = []
    for i in range(strings_count):
        offset = struct.unpack_from('<I', data, strings_offset + i * 4)[0]
        while data[offset] & 128:
            offset += 1
        offset += 1  # skip the final byte of the ULEB128 UTF-16 length
        strings.append(data[offset:data.index(b'\0', offset)])
    types = struct.unpack_from(f'<{types_count}I', data, types_offset)
    return {strings[types[struct.unpack_from('<I', data, definitions_offset + i * 32)[0]]]
            for i in range(definitions_count)}


def java_classes():
    data = (NATIVE / 'android' / 'sqlcipher-android.jar').read_bytes()
    if hashlib.sha256(data).hexdigest() != SOURCES['android']['classesJarSha256']:
        raise SystemExit('Unexpected SQLCipher Java runtime hash')
    with zipfile.ZipFile(io.BytesIO(data)) as jar:
        return {('L' + name[:-6] + ';').encode('ascii') for name in jar.namelist()
                if name.startswith('net/zetetic/') and name.endswith('.class')}


def verify(data, abi):
    if hashlib.sha256(data).hexdigest() != SOURCES['android']['libraries'][abi]:
        raise SystemExit(f'Unexpected SQLCipher hash for {abi}')
    if data[:6] != b'\x7fELF\x02\x01':
        raise SystemExit(f'Not a little-endian ELF64 library: {abi}')
    machine = struct.unpack_from('<H', data, 18)[0]
    if machine != {'arm64-v8a': 183, 'x86_64': 62}[abi]:
        raise SystemExit(f'Wrong architecture for {abi}: {machine}')
    offset = struct.unpack_from('<Q', data, 32)[0]
    size, count = struct.unpack_from('<HH', data, 54)
    loads = 0
    for i in range(count):
        header = offset + i * size
        if struct.unpack_from('<I', data, header)[0] != 1:
            continue
        loads += 1
        alignment = struct.unpack_from('<Q', data, header + 48)[0]
        if alignment < 16384:
            raise SystemExit(f'ELF segment does not support 16 KiB pages: {abi}')
    if not loads:
        raise SystemExit(f'No ELF load segments: {abi}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apk', type=Path)
    parser.add_argument('--abi', action='append', choices=tuple(SOURCES['android']['libraries']),
                        help='expected APK ABI; repeat for multi-ABI builds (default: both)')
    args = parser.parse_args()
    required_classes = java_classes()
    print('Pinned official SQLCipher Java runtime hash OK')
    for abi in SOURCES['android']['libraries']:
        verify((NATIVE / 'android' / abi / 'libsqlcipher.so').read_bytes(), abi)
        print(f'{abi}: pinned official hash and 16 KiB ELF alignment OK')
    if args.apk:
        with zipfile.ZipFile(args.apk) as apk, args.apk.open('rb') as raw:
            defined_classes = set()
            for name in apk.namelist():
                if name.startswith('classes') and name.endswith('.dex'):
                    defined_classes.update(dex_classes(apk.read(name)))
            required_interfaces = {b'Landroidx/sqlite/db/SupportSQLiteDatabase;',
                                   b'Landroidx/sqlite/SQLiteDriver;'}
            missing = (required_classes | required_interfaces) - defined_classes
            if missing:
                raise SystemExit('Missing SQLCipher Java classes in APK: ' +
                                 ', '.join(sorted(name.decode('ascii') for name in missing)))
            print(f'All {len(required_classes)} SQLCipher JNI runtime classes defined in APK OK')
            notice = 'assets/sqlcipher-THIRD-PARTY-NOTICES.txt'
            if apk.read(notice) != (NATIVE / 'THIRD-PARTY-NOTICES.txt').read_bytes():
                raise SystemExit('Missing or modified SQLCipher notices in APK')
            print('SQLCipher third-party notices bundled unchanged OK')
            for name in apk.namelist():
                if name.endswith(('/libe_sqlcipher.so', '/libe_sqlite3.so')):
                    raise SystemExit(f'Legacy/plain native library in APK: {name}')
            for abi in args.abi or SOURCES['android']['libraries']:
                name = f'lib/{abi}/libsqlcipher.so'
                info = apk.getinfo(name)
                verify(apk.read(name), abi)
                if info.compress_type != zipfile.ZIP_STORED:
                    raise SystemExit(f'Native is compressed: {name}')
                raw.seek(info.header_offset + 26)
                filename, extra = struct.unpack('<HH', raw.read(4))
                data_offset = info.header_offset + 30 + filename + extra
                if data_offset % 16384:
                    raise SystemExit(f'APK native entry is not aligned to 16 KiB: {name}')
                print(f'{name}: unchanged hash, stored and 16 KiB ZIP aligned OK')
