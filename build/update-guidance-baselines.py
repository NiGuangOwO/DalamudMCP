"""Record pristine historical skill fingerprints for conservative migration; run before releases."""
import hashlib
import json
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parent.parent
prefix = '.agents/skills/dalamud-development/'


def git(*args):
    return subprocess.check_output(['git', *args], cwd=root)


def canonical(data):
    return data.decode('utf-8-sig').replace('\r\n', '\n').replace('\r', '\n').encode()


path = root / 'build/guidance-baselines.json'
prior = json.loads(path.read_text()) if path.exists() else []
known = {entry['revision']: entry for entry in prior}
for commit in git('log', '--format=%H', '--', prefix).decode().splitlines():
    files = {}
    for name in git('ls-tree', '-r', '--name-only', commit, '--', prefix).decode().splitlines():
        data = canonical(git('show', commit + ':' + name))
        files[name[len(prefix):]] = hashlib.sha256(data).hexdigest()
    if 'SKILL.md' not in files:
        continue
    revision = hashlib.sha256('\n'.join(k + ':' + files[k] for k in sorted(files)).encode()).hexdigest()
    known.setdefault(revision, {'revision': revision, 'sourceCommit': commit, 'files': files})
assert len(known) <= 128
path.write_text(json.dumps(list(known.values()), indent=2) + '\n', encoding='utf-8')
print('Recorded', len(known), 'pristine skill revisions')
