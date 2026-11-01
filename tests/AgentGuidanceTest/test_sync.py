import base64
import copy
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile
import xml.etree.ElementTree as ET

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('sync_skill', ROOT / '.agents/skills/dalamud-development/scripts/sync_skill.py')
sync = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(sync)


def payload(files, baselines=()):
    files = {name: value if isinstance(value, bytes) else value.encode() for name, value in files.items()}
    hashes = {name: sync.digest(data) for name, data in files.items()}
    archive = io.BytesIO()
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as bundle:
        for name, data in files.items():
            bundle.writestr(sync.NAME + '/' + name, data)
    data = archive.getvalue()
    return {'manifest': {'schemaVersion': 1, 'skill': sync.NAME, 'revision': sync.revision(hashes), 'files': hashes,
        'archiveSha256': sync.digest(data), 'archiveBytes': len(data)}, 'bundleBase64': base64.b64encode(data).decode(),
        'baselines': list(baselines)}


class SyncTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        package_parent = ROOT / 'tools/AgentGuidancePack/bin/guidance'
        package_parent.mkdir(parents=True, exist_ok=True)
        version = ET.parse(ROOT / 'src/DalamudMCP/DalamudMCP.csproj').find('.//Version').text
        subprocess.run(['dotnet', 'run', '--project', str(ROOT / 'tools/AgentGuidancePack'), '--', version,
                        str(package_parent)], cwd=ROOT, capture_output=True, text=True, check=True)
        cls.package_file = package_parent / 'dalamud-development.payload.json'

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='dalamud-skill-tests-')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.target = self.root / sync.NAME
        self.files = {'SKILL.md': 'new guide\n', 'scripts/sync_skill.py': 'new helper\n', 'agents/openai.yaml': 'metadata\n'}
        self.new = payload(self.files)

    def write(self, name, data):
        path = self.target / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data if isinstance(data, bytes) else data.encode())

    def snapshot(self):
        return {str(p.relative_to(self.root)): p.read_bytes() for p in self.root.rglob('*') if p.is_file()}

    def legacy(self):
        old = {'SKILL.md': 'old guide\n', 'agents/openai.yaml': 'metadata\n', 'references/retired.md': 'retired\n'}
        for name, data in old.items():
            self.write(name, data)
        hashes = {name: sync.digest(data.encode()) for name, data in old.items()}
        self.new['baselines'] = [{'revision': sync.revision(hashes), 'files': hashes}]
        return old

    def test_first_install(self):
        self.assertEqual('install_available', sync.synchronize(self.new, self.target)['status'])
        self.assertFalse(self.target.exists())
        self.assertEqual('installed', sync.synchronize(self.new, self.target, True)['status'])
        self.assertEqual(self.new['manifest']['revision'], json.loads((self.target / sync.STATE).read_text())['revision'])

    def test_idempotence(self):
        sync.synchronize(self.new, self.target, True)
        before = self.snapshot()
        times = {p: p.stat().st_mtime_ns for p in self.target.rglob('*') if p.is_file()}
        self.assertEqual('current', sync.synchronize(self.new, self.target, True)['status'])
        self.assertEqual(before, self.snapshot())
        self.assertTrue(all(p.stat().st_mtime_ns == t for p, t in times.items()))

    def test_migrate_pristine_legacy_preserves_extras_and_removes_retired_managed_file(self):
        self.legacy()
        self.write('my-notes.txt', 'keep me')
        self.assertEqual('updated', sync.synchronize(self.new, self.target, True)['status'])
        self.assertEqual('keep me', (self.target / 'my-notes.txt').read_text())
        self.assertFalse((self.target / 'references/retired.md').exists())

    def test_all_recorded_legacy_revisions_migrate(self):
        recorded = json.loads((ROOT / 'build/guidance-baselines.json').read_text())
        current = json.loads(self.package_file.read_text(encoding='utf-8'))
        for index, baseline in enumerate(recorded):
            target = self.root / str(index) / sync.NAME
            for name in baseline['files']:
                data = subprocess.check_output(['git', 'show', baseline['sourceCommit'] + ':.agents/skills/dalamud-development/' + name], cwd=ROOT)
                path = target / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(sync.canonical(data).replace(b'\n', b'\r\n'))
            self.assertEqual('updated', sync.synchronize(current, target, True)['status'], baseline['sourceCommit'])

    def test_modified_legacy_no_writes(self):
        self.legacy()
        self.write('SKILL.md', 'my customized workflow')
        before = self.snapshot()
        self.assertEqual('conflict', sync.synchronize(self.new, self.target, True)['status'])
        self.assertEqual(before, self.snapshot())

    def test_modified_managed_no_writes(self):
        sync.synchronize(self.new, self.target, True)
        self.write('SKILL.md', 'custom edit')
        before = self.snapshot()
        self.assertEqual('conflict', sync.synchronize(self.new, self.target, True)['status'])
        self.assertEqual(before, self.snapshot())

    def test_managed_upgrade(self):
        sync.synchronize(self.new, self.target, True)
        future_files = dict(self.files, **{'SKILL.md': 'future guide\n'})
        future = payload(future_files, [{'revision': self.new['manifest']['revision'], 'files': self.new['manifest']['files']}])
        self.assertEqual('updated', sync.synchronize(future, self.target, True)['status'])
        self.assertEqual('future guide\n', (self.target / 'SKILL.md').read_text())

    def test_no_automatic_downgrade(self):
        sync.synchronize(self.new, self.target, True)
        old = payload(dict(self.files, **{'SKILL.md': 'older guide\n'}))
        before = self.snapshot()
        self.assertEqual('unknown_or_newer_managed_revision', sync.synchronize(old, self.target, True)['reason'])
        self.assertEqual(before, self.snapshot())

    def test_upstream_file_collision(self):
        self.legacy()
        self.write('scripts/sync_skill.py', 'my custom script')
        before = self.snapshot()
        self.assertEqual('unmanaged_path_collision', sync.synchronize(self.new, self.target, True)['reason'])
        self.assertEqual(before, self.snapshot())

    def test_deleted_managed_file_is_customization(self):
        sync.synchronize(self.new, self.target, True)
        (self.target / 'SKILL.md').unlink()
        self.assertEqual('conflict', sync.synchronize(self.new, self.target, True)['status'])
        self.assertFalse((self.target / 'SKILL.md').exists())

    def test_unknown_copy_no_writes(self):
        self.write('notes.txt', 'not our installed skill')
        before = self.snapshot()
        self.assertEqual('conflict', sync.synchronize(self.new, self.target, True)['status'])
        self.assertEqual(before, self.snapshot())

    def test_current_unmanaged_copy_no_marker_write(self):
        for name, data in self.files.items():
            self.write(name, data)
        before = self.snapshot()
        self.assertEqual('current', sync.synchronize(self.new, self.target, True)['status'])
        self.assertEqual(before, self.snapshot())

    def test_corrupt_archive_refused_before_directory_creation(self):
        value = copy.deepcopy(self.new)
        value['bundleBase64'] = base64.b64encode(b'broken zip').decode()
        with self.assertRaises(ValueError):
            sync.synchronize(value, self.target, True)
        self.assertFalse(self.target.exists())

    def test_corrupt_file_hash_refused(self):
        value = copy.deepcopy(self.new)
        value['manifest']['files']['SKILL.md'] = '0' * 64
        value['manifest']['revision'] = sync.revision(value['manifest']['files'])
        with self.assertRaises(ValueError):
            sync.synchronize(value, self.target, True)

    def test_unsafe_paths_refused(self):
        for name in ['../outside', '/absolute', 'a/../../outside', 'C:/outside', 'scripts\\outside', 'CON', 'a//b', 'x.', sync.STATE]:
            with self.subTest(name=name), self.assertRaises(ValueError):
                value = payload({'SKILL.md': 'guide', 'scripts/sync_skill.py': 'helper', name: 'bad'})
                sync.synchronize(value, self.target, True)
        self.assertFalse(self.target.exists())

    def test_duplicate_case_paths_refused(self):
        value = payload(dict(self.files, **{'skill.md': 'duplicate'}))
        with self.assertRaises(ValueError):
            sync.synchronize(value, self.target, True)

    def test_wrong_target_name_refused(self):
        with self.assertRaises(ValueError):
            sync.synchronize(self.new, self.root, True)

    def test_reparse_point_refused(self):
        self.target.mkdir()
        with patch.object(sync, 'reparse', side_effect=lambda path: path == self.target):
            with self.assertRaises(ValueError):
                sync.synchronize(self.new, self.target, True)

    def test_canonical_authoring_checkout_is_not_auto_updated(self):
        repo = self.root / 'authoring'
        (repo / '.git').mkdir(parents=True)
        project = repo / 'src/DalamudMCP/DalamudMCP.csproj'
        project.parent.mkdir(parents=True); project.write_text('source project')
        self.target = repo / '.agents/skills/dalamud-development'
        self.legacy()
        before = self.snapshot()
        self.assertEqual('canonical_authoring_source', sync.synchronize(self.new, self.target, True)['reason'])
        self.assertEqual(before, self.snapshot())

    def test_file_directory_collision_refused(self):
        with self.assertRaises(ValueError):
            sync.synchronize(payload(dict(self.files, scripts='a file')), self.target, True)
        self.assertFalse(self.target.exists())

    def test_single_updater_lock(self):
        self.legacy()
        self.write(sync.LOCK, 'busy')
        before = self.snapshot()
        self.assertEqual('busy', sync.synchronize(self.new, self.target, True)['status'])
        self.assertEqual(before, self.snapshot())

    def test_failure_rolls_back_previous_replacements(self):
        self.legacy()
        before = self.snapshot()
        original = os.replace
        attempts = 0

        def fail_once(source, destination):
            nonlocal attempts
            attempts += 1
            if attempts == 2:
                raise OSError('simulated write failure')
            return original(source, destination)

        with patch.object(sync.os, 'replace', side_effect=fail_once), self.assertRaises(OSError):
            sync.synchronize(self.new, self.target, True)
        self.assertEqual(before, self.snapshot())

    def test_server_package_matches_embedded_updater_and_all_files(self):
        current = json.loads(self.package_file.read_text(encoding='utf-8'))
        manifest, files, _ = sync.unpack(current)
        self.assertEqual(current['updaterScript'].encode(), files['scripts/sync_skill.py'])
        self.assertEqual('installed', sync.synchronize(current, self.target, True)['status'])
        self.assertEqual('current', sync.synchronize(current, self.target, True)['status'])
        for name in manifest['files']:
            self.assertEqual(sync.digest((self.target / name).read_bytes()), manifest['files'][name])


if __name__ == '__main__':
    unittest.main(verbosity=2)
