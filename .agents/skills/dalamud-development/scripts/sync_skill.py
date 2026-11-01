"""Synchronize only the selected DalamudMCP skill. Python 3.9+, standard library only."""
import argparse
import base64
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import tempfile
import zipfile

NAME = 'dalamud-development'
STATE = '.dalamud-mcp-guidance.json'
LOCK = '.dalamud-mcp-guidance.lock'
LIMIT = 1024 * 1024


def canonical(data):
    return data.decode('utf-8-sig').replace('\r\n', '\n').replace('\r', '\n').encode('utf-8')


def digest(data):
    return hashlib.sha256(data).hexdigest()


def revision(files):
    return digest('\n'.join(key + ':' + files[key] for key in sorted(files)).encode())


def safe_name(name):
    if not isinstance(name, str) or not re.fullmatch(r'[A-Za-z0-9_.\-/]+', name) or '\\' in name:
        raise ValueError('Invalid skill path')
    parts = name.split('/')
    if any(p in ('', '.', '..') or p.endswith(('.', ' ')) or
           p.split('.')[0].upper() in {'CON', 'PRN', 'AUX', 'NUL', *(f'COM{i}' for i in range(1, 10)),
                                     *(f'LPT{i}' for i in range(1, 10))} for p in parts):
        raise ValueError('Unsafe skill path')
    if PurePosixPath(name).is_absolute() or name in (STATE, LOCK):
        raise ValueError('Reserved skill path')
    return name


def reparse(path):
    try:
        info = path.lstat()
    except FileNotFoundError:
        return False
    return stat.S_ISLNK(info.st_mode) or bool(getattr(info, 'st_file_attributes', 0) & 0x400)


def guard(path):
    # Check before resolve: resolve() alone would hide symlinks/junctions.
    for part in (path, *path.parents):
        if reparse(part):
            raise ValueError('Symlink/junction requires manual review: ' + str(part))


def read_file(path, limit=LIMIT):
    guard(path)
    if not path.exists():
        return None
    if not path.is_file() or path.stat().st_size > limit:
        raise ValueError('Non-file or oversized local file: ' + str(path))
    # The bounded package uses text files; binary/custom copies are conflicts.
    return path.read_bytes()


def unpack(payload):
    manifest = payload['manifest']
    hashes = manifest['files']
    if manifest.get('schemaVersion') != 1 or manifest.get('skill') != NAME or not isinstance(hashes, dict) or not 1 <= len(hashes) <= 64:
        raise ValueError('Unsupported skill manifest')
    if len({safe_name(k).casefold() for k in hashes}) != len(hashes) or any(not re.fullmatch('[0-9a-f]{64}', v) for v in hashes.values()):
        raise ValueError('Invalid or duplicate manifest path/hash')
    folded = {key.casefold() for key in hashes}
    if any('/'.join(key.split('/')[:i]).casefold() in folded for key in hashes for i in range(1, len(key.split('/')))):
        raise ValueError('A file cannot also be a parent directory')
    if revision(hashes) != manifest['revision']:
        raise ValueError('Manifest revision mismatch')
    encoded = payload['bundleBase64']
    if not isinstance(encoded, str) or len(encoded) > 2 * LIMIT:
        raise ValueError('Oversized encoded skill package')
    archive = base64.b64decode(encoded, validate=True)
    if len(archive) > LIMIT or len(archive) != manifest['archiveBytes'] or digest(archive) != manifest['archiveSha256']:
        raise ValueError('Skill package size/hash mismatch')
    files = {}
    with zipfile.ZipFile(io.BytesIO(archive)) as bundle:
        entries = bundle.infolist()
        if len(entries) != len(hashes) or sum(e.file_size for e in entries) > LIMIT:
            raise ValueError('Unbounded or incomplete ZIP')
        for entry in entries:
            if not entry.filename.startswith(NAME + '/') or entry.is_dir() or stat.S_ISLNK(entry.external_attr >> 16):
                raise ValueError('Unsafe ZIP entry')
            name = safe_name(entry.filename[len(NAME) + 1:])
            if name not in hashes or name in files or entry.file_size > 256 * 1024:
                raise ValueError('Unknown, duplicate or oversized ZIP file')
            data = bundle.read(entry)
            if digest(data) != hashes[name] or canonical(data) != data:
                raise ValueError('Skill file hash/encoding mismatch: ' + name)
            files[name] = data
    if set(files) != set(hashes) or 'SKILL.md' not in files or 'scripts/sync_skill.py' not in files:
        raise ValueError('Incomplete skill package')
    baselines = payload.get('baselines', [])
    if not isinstance(baselines, list) or len(baselines) > 128:
        raise ValueError('Unbounded migration baselines')
    for baseline in baselines:
        entries = baseline['files']
        if not isinstance(entries, dict) or not 1 <= len(entries) <= 64 or revision(entries) != baseline['revision']:
            raise ValueError('Invalid migration baseline')
        for key, value in entries.items():
            safe_name(key)
            if not isinstance(value, str) or not re.fullmatch('[0-9a-f]{64}', value):
                raise ValueError('Invalid migration hash')
    return manifest, files, baselines


def synchronize(payload, target, apply=False):
    manifest, files, baselines = unpack(payload)
    target = Path(os.path.abspath(os.path.expanduser(str(target))))
    if target.name != NAME:
        raise ValueError('Target must be the exact selected ' + NAME + ' directory')
    guard(target)
    state_bytes = read_file(target / STATE, 128 * 1024)
    state = json.loads(state_bytes) if state_bytes else None
    allowed = {b['revision']: b['files'] for b in baselines}
    allowed[manifest['revision']] = manifest['files']
    all_names = set(files)
    if state:
        if state.get('schemaVersion') != 1 or state.get('skill') != NAME or state.get('revision') not in allowed or state.get('files') != allowed[state['revision']]:
            return {'status': 'conflict', 'reason': 'unknown_or_newer_managed_revision', 'changed': False}
        prior = state['files']
    else:
        prior = None
    # Inspect only bounded known files, never recursively inventory arbitrary user data.
    for baseline in baselines:
        all_names.update(baseline['files'])
    if len(all_names) > 256:
        raise ValueError('Too many historical skill paths')
    actual = {name: read_file(target / name, 256 * 1024) for name in all_names}

    def matches(hashes):
        return all(actual.get(k) is not None and digest(canonical(actual[k])) == v for k, v in hashes.items())

    if matches(manifest['files']) and (not state or state['revision'] == manifest['revision']):
        return {'status': 'current', 'revision': manifest['revision'], 'changed': False}
    if state:
        if not matches(prior):
            return {'status': 'conflict', 'reason': 'locally_modified_managed_files', 'changed': False}
    elif target.exists() and any(target.iterdir()):
        prior = next((b['files'] for b in baselines if matches(b['files'])), None)
        if prior is None:
            return {'status': 'conflict', 'reason': 'unknown_or_custom_installation', 'changed': False}
    else:
        prior = {}
    # A new upstream path must not overwrite a pre-existing unmanaged file.
    conflicts = [name for name in files if name not in prior and actual.get(name) is not None and canonical(actual[name]) != files[name]]
    if conflicts:
        return {'status': 'conflict', 'reason': 'unmanaged_path_collision', 'files': sorted(conflicts), 'changed': False}
    changes = {name: data for name, data in files.items() if actual.get(name) != data}
    changes.update({name: None for name in prior if name not in files})
    new_state = {'schemaVersion': 1, 'skill': NAME, 'revision': manifest['revision'], 'files': manifest['files']}
    changes[STATE] = (json.dumps(new_state, indent=2, sort_keys=True) + '\n').encode()
    originals = {name: state_bytes if name == STATE else actual.get(name) for name in changes}
    source_root = target.parent.parent.parent
    if (source_root / '.agents' / 'skills' / NAME == target and
            (source_root / '.git').exists() and (source_root / 'src/DalamudMCP/DalamudMCP.csproj').is_file()):
        return {'status': 'conflict', 'reason': 'canonical_authoring_source', 'changed': False}
    if not apply:
        return {'status': 'update_available' if prior else 'install_available', 'revision': manifest['revision'], 'changed': False}
    target.mkdir(parents=True, exist_ok=True)
    guard(target)
    lock = target / LOCK
    try:
        fd = os.open(lock, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    except FileExistsError:
        return {'status': 'busy', 'changed': False}
    os.close(fd)
    written = []

    def write(name, data):
        path = target / name
        guard(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        guard(path)
        if data is None:
            path.unlink(missing_ok=True)
            return
        fd, temporary = tempfile.mkstemp(prefix='.dalamud-guidance-', dir=path.parent)
        try:
            with os.fdopen(fd, 'wb') as stream:
                stream.write(data)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, path)
        finally:
            Path(temporary).unlink(missing_ok=True)

    try:
        # A concurrent updater or editor must not invalidate the preflight decision.
        for name in changes:
            if read_file(target / name) != originals[name]:
                raise ValueError('Local files changed during update: ' + name)
        for name, data in changes.items():
            if read_file(target / name) != originals[name]:
                raise ValueError('Local file changed before replacement: ' + name)
            write(name, data)
            written.append(name)
        return {'status': 'updated' if prior else 'installed', 'revision': manifest['revision'], 'changed': True}
    except Exception:
        # Restore only files still containing our writes, never overwrite a racing editor.
        for name in reversed(written):
            if read_file(target / name) == changes[name]:
                write(name, originals[name])
        raise
    finally:
        lock.unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--payload', required=True, help='JSON result returned by get_agent_guidance(includeBundle=true)')
    parser.add_argument('--target', required=True, help='Exact selected skill directory, never a client config or repository root')
    parser.add_argument('--apply', action='store_true', help='Apply only a verified safe install/update')
    args = parser.parse_args()
    try:
        path = Path(args.payload)
        if path.stat().st_size > 4 * LIMIT:
            raise ValueError('Oversized guidance payload')
        result = synchronize(json.loads(path.read_text(encoding='utf-8-sig')), args.target, args.apply)
    except Exception as error:
        result = {'status': 'failed', 'changed': None, 'error': str(error)}
    print(json.dumps(result, ensure_ascii=True))
    return 0 if result['status'] in ('current', 'installed', 'updated', 'install_available', 'update_available') else 2


if __name__ == '__main__':
    raise SystemExit(main())
