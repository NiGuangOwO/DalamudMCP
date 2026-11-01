"""Generate a fixed elpis schema snapshot. Requires Python + PyYAML and Node >=22.
No npm lifecycle scripts. Every npm tarball is verified against a reviewed SHA-512.
Run from the repository root: python tools/import-elpis.py
"""
import base64
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import subprocess
import tarfile
import tempfile
import urllib.request
import yaml

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'src/DalamudMCP/Network/Data'
PIN = '06df8d0ddf52e230c73826006cfbc50e6c2891a6'
CATALOG_URL = f'https://raw.githubusercontent.com/zhyupe/ffxiv-opcode-worker/{PIN}/packets.yaml'
PACKAGES = [
    ('@ffcafe/elpis-network', '0.1.0', 'h5B6cVd3/ij3CeeQ+tKCQzGdsCn+jyXfoeC+fw/3/UJf3WzuSdNi4+Xp419R5Onh4sQ/dTbnLbd9WgqBDXOAhA=='),
    ('@ffcafe/elpis-struct', '0.1.0', '/BUfpRsFqKbNedTo/u/7dX1jMfgH5dJtYM1d0m1evsQ8veX8xLqPWe9Bgtf9C/pDR8qy6PEXa+rHZDuoT7Zmtg=='),
    ('reflect-metadata', '0.2.2', 'urBwgfrvVP/eAyXx4hluJivBKzuEbSQs9rKWCrCkbSxNv8mxPcUZKeuoF3Uy4mJl3Lwprp6yy5/39VWigZ4K6Q=='),
]

with tempfile.TemporaryDirectory(prefix='dalamud-elpis-') as temporary:
    work = Path(temporary)
    artifacts = []
    for name, version, digest in PACKAGES:
        leaf = name.split('/')[-1]
        url = f'https://registry.npmjs.org/{name}/-/{leaf}-{version}.tgz'
        data = urllib.request.urlopen(url, timeout=30).read()
        if base64.b64encode(hashlib.sha512(data).digest()).decode() != digest:
            raise ValueError(f'Integrity mismatch: {name}')
        target = work / 'node_modules' / name
        with tarfile.open(fileobj=io.BytesIO(data), mode='r:gz') as archive:
            for member in archive.getmembers():
                if not member.isfile():
                    continue
                rel = PurePosixPath(member.name).relative_to('package')
                if '..' in rel.parts or rel.is_absolute():
                    raise ValueError('Unsafe archive member')
                path = target / rel
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(archive.extractfile(member).read())
        artifacts.append({'name': name, 'version': version, 'url': url, 'integrity': 'sha512-' + digest})
    raw = urllib.request.urlopen(CATALOG_URL, timeout=30).read()
    if hashlib.sha256(raw).hexdigest().upper() != '5676E22E0C9ED24BE698A893615BFF60F382815497A4DA73831FF981CBAF6EAF':
        raise ValueError('Alias catalog hash mismatch')
    catalog = yaml.safe_load(raw)
    packets = {}
    for category, group in catalog.items():
        direction = {'client-to-server': 'send', 'server-to-client': 'receive'}[group['direction']]
        for name, aliases in group['packets'].items():
            if name in packets:
                raise ValueError('Duplicate packet name')
            packets[name] = {'category': category, 'direction': direction,
                             'aliases': [aliases.get('FFXIVOpcodes', name)]}
    source = {'project': 'https://github.com/thewakingsands/elpis', 'version': '0.1.0',
              'license': 'GPL-3.0-only', 'artifacts': artifacts[:2],
              # Independently reviewed directory, not an asserted npm release commit.
              'reviewedReferenceSource': {'commit': 'c2ffacb0335cc080ab6f014f57be85632be610d1',
                                          'path': 'packages/network/src/definitions/ipc',
                                          'treeSha': 'd091bd7af1ad9a6bc60fa2d9405c63cc00460140',
                                          'directorySha256': 'D631F19C179C17D447936E633153834880DEEFB8E715C53382603508D0FD8A24',
                                          'digestAlgorithm': 'sha256(sorted(relativePath + NUL + lowercaseFileSha256 + LF))'},
              'aliasCatalogSource': CATALOG_URL,
              'aliasCatalogLicense': 'AGPL-3.0',
              'aliasCatalogSha256': hashlib.sha256(raw).hexdigest().upper()}
    path = work / 'catalog.json'
    path.write_text(json.dumps({'source': source, 'packets': packets}), encoding='utf-8')
    subprocess.run(['node', str(ROOT / 'tools/export-elpis.mjs'), str(work / 'node_modules'),
                    str(path), str(DATA / 'elpis-schemas.json')], check=True)
    for source_name, dest_name in [('LICENSE', 'elpis.LICENSE'), ('FFXIVClientStructs.LICENSE', 'elpis-FFXIVClientStructs.LICENSE')]:
        (DATA / dest_name).write_bytes((work / 'node_modules/@ffcafe/elpis-network' / source_name).read_bytes())
    notice = urllib.request.urlopen(f'https://raw.githubusercontent.com/zhyupe/ffxiv-opcode-worker/{PIN}/LICENSE', timeout=30).read()
    (DATA / 'elpis-opcode-worker.LICENSE').write_bytes(notice)
