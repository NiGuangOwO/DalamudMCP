"""Validate on-demand upstream metadata checks without capture/input or applying definitions."""
import argparse
import json
from pathlib import Path
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('--expected-mvid', required=True)
parser.add_argument('--report', required=True)
options = parser.parse_args()
headers = {'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream'}
report = {'checks': [], 'gameplayInputUsed': False, 'packetCaptureStarted': False, 'definitionsApplied': False}


def check(ok, label):
    if not ok:
        raise AssertionError(label)
    report['checks'].append(label)
    print('PASS ' + label, flush=True)


def rpc(method, params):
    request = urllib.request.Request('http://127.0.0.1:18777/mcp', data=json.dumps({'jsonrpc': '2.0', 'id': 1, 'method': method, 'params': params}).encode(), headers=headers)
    with urllib.request.urlopen(request, timeout=20) as response:
        if response.headers.get('Mcp-Session-Id'):
            headers['Mcp-Session-Id'] = response.headers['Mcp-Session-Id']
        result = json.load(response)
    if 'error' in result:
        raise RuntimeError(result['error'])
    return result['result']


def call(name, args=None):
    result = rpc('tools/call', {'name': name, 'arguments': args or {}})
    if result.get('isError'):
        raise RuntimeError(result['content'])
    return json.loads(result['content'][0]['text'])


try:
    health = json.load(urllib.request.urlopen('http://127.0.0.1:18777/health'))
    check(health['buildMvid'].lower() == options.expected_mvid.lower(), 'running MVID matches tested debug DLL')
    report['health'] = health
    rpc('initialize', {'protocolVersion': '2025-06-18', 'capabilities': {}, 'clientInfo': {'name': 'schema-update-live-test', 'version': '1'}})
    report['loggedIn'] = call('get_client_state')['loggedIn']
    call('get_local_player')  # Identity is not persisted.
    tools, pages, args = [], [], {}
    while True:
        page = rpc('tools/list', args)
        tools += page['tools']; pages.append(len(page['tools']))
        if not page.get('nextCursor'):
            break
        args = {'cursor': page['nextCursor']}
    report['catalogPages'] = pages
    check(len(tools) == 160, 'complete catalog has 160 tools')
    tool = next(t for t in tools if t['name'] == 'network_packet_schema_check_updates')
    check(tool['annotations']['readOnlyHint'] and not tool['annotations']['destructiveHint'], 'online check classified read-only')
    before = call('network_capture_status')
    result = call('network_packet_schema_check_updates', {'force': True})
    report['result'] = result
    check(result['status'] == 'complete', 'all four real upstream metadata sources checked successfully')
    check(not result['applied'] and not result['compatibilityVerified'], 'upstream result applies no definitions or compatibility claims')
    check({p['name'] for p in result['packages']} == {'@ffcafe/elpis-network', '@ffcafe/elpis-struct'}, 'both fixed npm packages returned')
    for package in result['packages']:
        check(package['status'] == 'ok' and package['latestIntegrity'].startswith('sha512-'), 'registry version and integrity validated for ' + package['name'])
    check(result['referenceStructures']['status'] == 'ok' and len(result['referenceStructures']['latestDirectorySha256']) == 64, 'real GitHub source directory compared to reviewed baseline')
    check(result['aliasCatalog']['status'] == 'ok' and len(result['aliasCatalog']['latestSha256']) == 64, 'real alias catalog compared by content hash')
    cached = call('network_packet_schema_check_updates')
    check(cached['cached'] and cached['checkedAt'] == result['checkedAt'], 'second call uses unchanged cached result')
    after = call('network_capture_status')
    check(before['packetSchemas']['sha256'] == after['packetSchemas']['sha256'] == result['currentSchemaSha256'], 'online checks preserve exact active schema hash')
    check(before['capture'] == after['capture'] and before['hooks'] == after['hooks'], 'online checks preserve capture and hook state')
    check(before['opcodeCatalog'] == after['opcodeCatalog'], 'online checks do not refresh opcode catalog')
    report['completed'] = True
finally:
    Path(options.report).parent.mkdir(parents=True, exist_ok=True)
    Path(options.report).write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
