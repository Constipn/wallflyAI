#!/usr/bin/env python3
"""Verify our thin arm64 bundle's bytes, pinned local identity and detached CMS.

Does not alter macOS trust or privacy settings. This supplements native codesign
when the agent sandbox cannot contact trust services (even for /usr/bin/curl).
"""
import hashlib
import os
from pathlib import Path
import plistlib
import struct
import subprocess
import sys
import tempfile


def verify(bundle, certificate, requirement):
    bundle, certificate, requirement = map(Path, (bundle, certificate, requirement))
    info_bytes = (bundle / 'Contents/Info.plist').read_bytes()
    info = plistlib.loads(info_bytes)
    executable = bundle / 'Contents/MacOS' / info['CFBundleExecutable']
    binary = executable.read_bytes()
    assert binary[:4] == b'\xcf\xfa\xed\xfe', 'Expected thin little-endian Mach-O 64'
    ncmds = struct.unpack_from('<I', binary, 16)[0]
    cursor, signature = 32, None
    for _ in range(ncmds):
        cmd, size = struct.unpack_from('<II', binary, cursor)
        assert size >= 8 and cursor + size <= len(binary), 'Invalid load command'
        if cmd == 0x1d:
            offset, length = struct.unpack_from('<II', binary, cursor + 8)
            signature = binary[offset:offset + length]
        cursor += size
    assert signature is not None, 'Missing code signature'

    def blobs(data, magic):
        header, length, count = struct.unpack_from('>III', data)
        assert header == magic and length <= len(data), 'Invalid signature container'
        result = {}
        for index in range(count):
            kind, offset = struct.unpack_from('>II', data, 12 + index * 8)
            _, size = struct.unpack_from('>II', data, offset)
            assert offset + size <= length, 'Truncated signature slot'
            result[kind] = data[offset:offset + size]
        return result

    slots = blobs(signature, 0xfade0cc0)
    directory = slots[0]
    _, length, version, flags, hash_offset, ident_offset, special_count, code_count, code_limit = struct.unpack_from('>9I', directory)
    hash_size, hash_type, _, page_power = directory[36:40]
    assert hash_type == 2 and hash_size == 32, 'Expected SHA-256 code directory'
    assert not flags & 2, 'Ad-hoc signatures are not stable signing identities'
    identifier = directory[ident_offset:].split(b'\0', 1)[0].decode()
    assert identifier in ['io.github.constipn.wallfly', 'io.github.constipn.wallfly.detector'], 'Wrong bundle signing identifier'
    assert info['CFBundleIdentifier'] == identifier, 'Bundle identifier mismatch'
    if code_limit == 0xffffffff and version >= 0x20300:
        code_limit = struct.unpack_from('>Q', directory, 56)[0]
    page_size = 1 << page_power
    assert code_count == (code_limit + page_size - 1) // page_size
    for index in range(code_count):
        page = binary[index * page_size:min((index + 1) * page_size, code_limit)]
        expected = directory[hash_offset + index * hash_size:hash_offset + (index + 1) * hash_size]
        assert hashlib.sha256(page).digest() == expected, 'Executable page hash mismatch'

    resources_path = bundle / 'Contents/_CodeSignature/CodeResources'
    resources_bytes = resources_path.read_bytes()
    for kind, content in [(1, info_bytes), (2, slots[2]), (3, resources_bytes)]:
        assert kind <= special_count
        expected = directory[hash_offset - kind * hash_size:hash_offset - (kind - 1) * hash_size]
        assert hashlib.sha256(content).digest() == expected, 'Signature metadata hash mismatch'
    requirements = blobs(slots[2], 0xfade0c01)
    assert requirements[3] == requirement.read_bytes(), 'Wrong designated requirement'
    resources = plistlib.loads(resources_bytes)
    for relative, record in resources.get('files2', {}).items():
        path = bundle / 'Contents' / relative
        assert '..' not in Path(relative).parts, 'Invalid resource path'
        if 'hash2' in record:
            assert hashlib.sha256(path.read_bytes()).digest() == record['hash2'], 'Resource hash mismatch'
        elif 'symlink' in record:
            assert os.readlink(path) == record['symlink'], 'Resource symlink mismatch'
        elif 'cdhash' in record:
            assert identifier == 'io.github.constipn.wallfly', 'Unexpected nested code'
            assert relative in ['Library/LoginItems/Wallfly Detector.app',
                                'Library/LoginItems/Meeting Notes Detector.app'], 'Unexpected helper path'
            child_hash = verify(path, certificate, requirement.parent / 'detector-requirements.bin')
            assert child_hash == record['cdhash'], 'Nested helper code hash mismatch'
        else:
            raise AssertionError('Unsupported resource record')
    # Verifies the CMS signature with the local certificate as an explicit anchor
    # for this subprocess only. Does not import it into any trust store.
    with tempfile.TemporaryDirectory(prefix='meetingnotes-signature-') as temp:
        temp = Path(temp)
        (temp / 'cms.der').write_bytes(slots[0x10000][8:])
        (temp / 'directory.bin').write_bytes(directory)
        result = subprocess.run(['openssl', 'cms', '-verify', '-binary', '-inform', 'DER',
            '-in', str(temp / 'cms.der'), '-content', str(temp / 'directory.bin'),
            '-CAfile', str(certificate), '-purpose', 'any', '-out', os.devnull], capture_output=True)
        assert result.returncode == 0, 'CMS signature/certificate verification failed'
    print('Verified '+identifier+': executable, resources, certificate signature and stable designated requirement.')
    return hashlib.sha256(directory).digest()[:20]


if __name__ == '__main__':
    verify(*sys.argv[1:])
