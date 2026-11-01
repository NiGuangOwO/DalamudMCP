"""Observe automatic checks after a build reload; never dispatch refresh, capture or gameplay input."""
import argparse
import json
from pathlib import Path
import time
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('--expected-mvid', required=True)
parser.add_argument('--first-load', required=True)
parser.add_argument('--report', required=True)
options = parser.parse_args()
headers = {'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream'}
report = {'checks': [], 'refreshDispatched': False, 'captureStarted': False, 'gameplayInputUsed': False}


def check(ok, label):
    if not ok:
        raise AssertionError(label)
    report['checks'].append(label)
    print('PASS ' + label, flush=True)


def rpc(method, params):
    request = urllib.request.Request('http://127.0.0.1:18777/mcp', data=json.dumps({'jsonrpc': '2.0', 'id': 1, 'method': method, 'params': params}).encode(), headers=headers)
    with urllib.request.urlopen(request, timeout=15) as response:
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
    check(health['buildMvid'].lower() == options.expected_mvid.lower(), 'running MVID matches final tested debug DLL')
    report['health'] = health
    rpc('initialize', {'protocolVersion': '2025-06-18', 'capabilities': {}, 'clientInfo': {'name': 'startup-update-live-test', 'version': '1'}})
    report['loggedIn'] = call('get_client_state')['loggedIn']
    call('get_local_player')
    tools, pages, args = [], [], {}
    while True:
        page = rpc('tools/list', args)
        tools += page['tools']; pages.append(len(page['tools']))
        if not page.get('nextCursor'):
            break
        args = {'cursor': page['nextCursor']}
    report['catalogPages'] = pages
    check(len(tools) == 160, 'complete catalog still contains 160 tools')
    deadline = time.monotonic() + 12
    while True:
        state = call('network_capture_status')
        if state['startupUpdates']['state'] != 'running':
            break
        if time.monotonic() > deadline:
            raise TimeoutError('startup checks did not settle')
        time.sleep(0.1)
    startup = state['startupUpdates']
    report['startupUpdates'] = startup
    check(startup['enabledOnLoad'] and startup['state'] == 'completed' and startup['attempts'] == 1, 'one automatic attempt completed in this plugin load')
    check(startup['schemas']['state'] == 'completed' and startup['schemas']['result']['status'] == 'complete', 'automatic schema check completed every real upstream source')
    check(not startup['schemas']['result']['applied'], 'automatic schema check does not replace definitions')
    opcode = startup['opcodes']['result']
    check(startup['opcodes']['state'] == 'completed' and opcode['checked'] and opcode['refreshed'] and opcode['applied'], 'automatic opcode check applied valid data with open mutation gate')
    check(opcode['cacheSaved'] and state['opcodeCatalog']['origin'] == 'network' and state['opcodeCatalog']['sha256'] == opcode['catalog']['sha256'], 'automatic opcode refresh saved cache and published matching snapshot')
    check(opcode['catalog']['source'] == 'https://cdn.jsdelivr.net/gh/karashiiro/FFXIVOpcodes@latest/opcodes.min.json', 'automatic refresh uses exact user-selected opcode JSON source')
    check(not state['hooks']['sendEnabled'] and not state['hooks']['receiveEnabled'] and state['capture'] is None, 'automatic checks install no capture hooks')
    cached = call('network_packet_schema_check_updates')
    check(cached['cached'] and cached['checkedAt'] == startup['schemas']['result']['checkedAt'], 'manual schema lookup reuses startup result without another online check')
    first = json.loads(Path(options.first_load).read_text(encoding='utf-8'))
    report['previousBuildMvid'] = first['buildMvid']
    report['previousStartedAt'] = first['startupUpdates']['startedAt']
    check(first['buildMvid'] != state['buildMvid'] and first['startupUpdates']['startedAt'] != startup['startedAt'], 'actual plugin reload creates a fresh automatic startup attempt')
    check(first['startupUpdates']['attempts'] == 1 and first['startupUpdates']['state'] == 'completed', 'previous observed plugin load also completed exactly one attempt')
    again = call('network_capture_status')
    check(again['startupUpdates'] == startup, 'status and discovery reads do not retrigger startup checks')
    report['completed'] = True
finally:
    Path(options.report).parent.mkdir(parents=True, exist_ok=True)
    Path(options.report).write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
