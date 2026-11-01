"""Read-only validation; never dispatches load_dev_plugin, lifecycle or gameplay input."""
import argparse
import json
from pathlib import Path
import tempfile
import shutil
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('--file', required=True)
parser.add_argument('--report', required=True)
options = parser.parse_args()
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
headers = {'Content-Type': 'application/json'}
report = {'checks': [], 'loadDevPluginCalled': False, 'configurationMutated': False, 'gameplayInputUsed': False}


def check(ok, label):
    if not ok:
        raise AssertionError(label)
    report['checks'].append(label)
    print('PASS ' + label, flush=True)


def rpc(method, params):
    request = urllib.request.Request('http://127.0.0.1:18777/mcp',
        json.dumps({'jsonrpc': '2.0', 'id': 1, 'method': method, 'params': params}).encode(), headers)
    with opener.open(request, timeout=35) as response:
        if response.headers.get('Mcp-Session-Id'):
            headers['Mcp-Session-Id'] = response.headers['Mcp-Session-Id']
        value = json.load(response)
    if 'error' in value:
        raise RuntimeError(value['error'])
    return value['result']


def call(name, args=None, expect_error=False):
    result = rpc('tools/call', {'name': name, 'arguments': args or {}})
    if expect_error:
        check(bool(result.get('isError')), 'mismatched expectedBuildMvid is refused in game')
        return
    if result.get('isError'):
        raise RuntimeError(result['content'])
    return json.loads(result['content'][0]['text'])


try:
    init = rpc('initialize', {'protocolVersion': '2025-06-18', 'capabilities': {},
        'clientInfo': {'name': 'dev-plugin-readonly-live-test', 'version': '1'}})
    report['loggedIn'] = call('get_client_state')['loggedIn']
    call('get_local_player')
    tools, pages, args = [], [], {}
    while True:
        page = rpc('tools/list', args)
        tools += page['tools']
        pages.append(len(page['tools']))
        if not page.get('nextCursor'):
            break
        args = {'cursor': page['nextCursor']}
    report['catalogPages'] = pages
    check(len(tools) == 162, 'complete running catalog has 162 tools')
    for name in ('get_dev_plugin_state', 'wait_for_dev_plugin'):
        tool = next(t for t in tools if t['name'] == name)
        check(tool['annotations']['readOnlyHint'], name + ' is advertised as read-only')
    check('wait_for_dev_plugin' in init['instructions'] and 'autoReload=true' in init['instructions'],
        'running initialization instructions route auto-reload to read-only wait')
    state = call('get_dev_plugin_state', {'filePath': options.file})
    report['before'] = state
    check(state['registered'] and state['isDev'] and state['isLoaded'] and state['autoReload'],
        'actual MCP developer registration is loaded with auto-reload enabled')
    health = json.load(opener.open('http://127.0.0.1:18777/health'))
    report['health'] = health
    check(state['buildMatches'] and state['loadedBuildMvid'] == state['fileBuildMvid'] == health['buildMvid'],
        'file, loaded assembly and health MVID match the new running build')
    ready = call('wait_for_dev_plugin', {'filePath': options.file,
        'expectedBuildMvid': state['fileBuildMvid'], 'buildWaitMs': 3000})
    report['ready'] = ready
    check(ready['status'] == 'ready' and ready['buildVerified'] and ready['buildMvid'] == health['buildMvid']
        and ready['elapsedMs'] >= 750, 'read-only wait verifies exact running build after stable 750ms')
    after = call('get_dev_plugin_state', {'filePath': options.file})
    check(after == state, 'read-only inspection and wait preserve observed watcher, registration and build state')
    call('wait_for_dev_plugin', {'filePath': options.file,
        'expectedBuildMvid': '00000000-0000-0000-0000-000000000001'}, expect_error=True)
    with tempfile.TemporaryDirectory(prefix='mcp-readonly-dev-wait-') as directory:
        copy = Path(directory) / 'Unregistered.dll'
        shutil.copyfile(options.file, copy)
        absent = call('get_dev_plugin_state', {'filePath': str(copy)})
        check(not absent['registered'] and absent['autoReload'] is None, 'unregistered DLL reports unknown watcher without registration')
        timeout = call('wait_for_dev_plugin', {'filePath': str(copy), 'buildWaitMs': 1000})
        report['timeout'] = timeout
        check(timeout['status'] == 'timeout' and not timeout['buildVerified'] and timeout['buildMvid'] is None
            and not timeout['registered'], 'missing registration times out without loading the DLL')
        check(not call('get_dev_plugin_state', {'filePath': str(copy)})['registered'], 'timed-out wait leaves the copied DLL unregistered')
    report['completed'] = True
finally:
    Path(options.report).parent.mkdir(parents=True, exist_ok=True)
    Path(options.report).write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
