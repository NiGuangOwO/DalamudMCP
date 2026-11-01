"""Verify the actual MCP bootstrap and client-side updater in isolated client directories."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument('--report', required=True)
args = parser.parse_args()
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
headers = {'Content-Type': 'application/json'}
report = {'checks': [], 'gameplayInputUsed': False, 'gameMutationsDispatched': False,
          'realClientSkillOverwritten': False, 'independentModelComplianceTested': False}


def check(ok, label):
    if not ok:
        raise AssertionError(label)
    report['checks'].append(label)
    print('PASS ' + label, flush=True)


def rpc(method, params):
    request = urllib.request.Request('http://127.0.0.1:18777/mcp',
        json.dumps({'jsonrpc': '2.0', 'id': 1, 'method': method, 'params': params}).encode(), headers)
    with opener.open(request, timeout=30) as response:
        if response.headers.get('Mcp-Session-Id'):
            headers['Mcp-Session-Id'] = response.headers['Mcp-Session-Id']
        value = json.load(response)
    if 'error' in value:
        raise RuntimeError(value['error'])
    return value['result']


def call(name, params=None):
    result = rpc('tools/call', {'name': name, 'arguments': params or {}})
    if result.get('isError'):
        raise RuntimeError(result['content'])
    return json.loads(result['content'][0]['text'])


try:
    init = rpc('initialize', {'protocolVersion': '2025-06-18', 'capabilities': {},
        'clientInfo': {'name': 'guidance-end-to-end-client', 'version': '1'}})
    call('get_client_state')
    call('get_local_player')
    tools, pages, params = [], [], {}
    while True:
        page = rpc('tools/list', params)
        tools.extend(page['tools']); pages.append(len(page['tools']))
        if not page.get('nextCursor'):
            break
        params = {'cursor': page['nextCursor']}
    report['catalogPages'] = pages
    check(len(tools) == 163, 'complete running catalog has 163 tools')
    check(next(t for t in tools if t['name'] == 'get_agent_guidance')['annotations']['readOnlyHint'], 'guidance transfer is advertised read-only')
    metadata = call('get_agent_guidance', {'knownRevision': 'old'})
    manifest = metadata['manifest']
    report['manifest'] = manifest
    check(manifest['revision'] in init['instructions'] and 'includeBundle=true' in init['instructions'], 'handshake advertises current revision and bootstrap independently of old skill')
    check(not metadata['revisionMatches'] and manifest['clientInstallState'] == 'unknown' and 'bundleBase64' not in metadata,
          'metadata detects stale reported revision without claiming client installation or transferring bundle')
    health = json.load(opener.open('http://127.0.0.1:18777/health'))
    report['health'] = health
    check(health['agentGuidance'] == manifest, 'health exposes the same current bundled manifest')
    state = call('get_dev_plugin_state', {'filePath': str(ROOT / 'src/DalamudMCP/bin/x64/Debug/DalamudMCP.dll')})
    check(state['buildMatches'] and state['fileBuildMvid'] == health['buildMvid'], 'running MVID matches final tested debug assembly')
    payload = call('get_agent_guidance', {'includeBundle': True})
    check(hashlib.sha256(payload['updaterScript'].encode()).hexdigest() == manifest['files']['scripts/sync_skill.py'], 'transferred bootstrap script matches manifest hash before execution')
    release = json.loads((ROOT / 'tools/AgentGuidancePack/bin/guidance/dalamud-development.payload.json').read_text())
    check(payload['bundleBase64'] == release['bundleBase64'] and payload['manifest'] == release['manifest'], 'running bundle and offline release package are byte-for-byte equivalent')
    check(len(payload['baselines']) == 40, 'running package carries all 40 original legacy fingerprints')
    with tempfile.TemporaryDirectory(prefix='mcp-guidance-live-') as directory:
        directory = Path(directory)
        payload_file = directory / 'payload.json'; script = directory / 'sync.py'
        payload_file.write_text(json.dumps(payload), encoding='utf-8')
        script.write_text(payload['updaterScript'], encoding='utf-8', newline='\n')
        target = directory / 'legacy' / 'dalamud-development'
        baseline = next(b for b in payload['baselines'] if b['sourceCommit'].startswith('5b39730'))
        for name in baseline['files']:
            path = target / name; path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(subprocess.check_output(['git', 'show', baseline['sourceCommit'] + ':.agents/skills/dalamud-development/' + name], cwd=ROOT))
        (target / 'personal-notes.txt').write_text('keep local notes')

        def sync(where):
            result = subprocess.run([sys.executable, str(script), '--payload', str(payload_file), '--target', str(where), '--apply'],
                                    capture_output=True, text=True)
            return json.loads(result.stdout)

        upgraded = sync(target)
        check(upgraded['status'] == 'updated', 'published 1.0.0.40 pristine client copy upgrades automatically from live MCP payload')
        check(all(hashlib.sha256((target / name).read_bytes()).hexdigest() == digest for name, digest in manifest['files'].items()), 'all installed file hashes match live manifest')
        check((target / 'personal-notes.txt').read_text() == 'keep local notes', 'unmanaged client files are preserved')
        times = {p: p.stat().st_mtime_ns for p in target.rglob('*') if p.is_file()}
        check(sync(target)['status'] == 'current' and all(p.stat().st_mtime_ns == time for p, time in times.items()), 'repeat synchronization makes no writes or routine changes')
        (target / 'SKILL.md').write_text('custom local instructions')
        check(sync(target)['status'] == 'conflict' and (target / 'SKILL.md').read_text() == 'custom local instructions', 'customized client workflow is preserved and reported as conflict')
        check(sync(directory / 'fresh' / 'dalamud-development')['status'] == 'installed', 'client without an installed copy can bootstrap into its selected directory')
    served = call('get_agent_guidance', {'file': 'SKILL.md'})
    check(served['sha256'] == manifest['files']['SKILL.md'] and 'Keep an installed copy current' in served['text'], 'clients without local execution can read matching current instructions')
    current = call('get_agent_guidance', {'knownRevision': manifest['revision']})
    check(current['revisionMatches'] and current['manifest']['clientInstallState'] == 'unknown', 'current reported revision remains distinct from server-certified installation')
    check(json.load(opener.open('http://127.0.0.1:18777/health'))['mutatingToolsEnabled'] == health['mutatingToolsEnabled'], 'validation did not change the global mutation gate')
    report['completed'] = True
finally:
    path = Path(args.report); path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
