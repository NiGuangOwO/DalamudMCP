"""Passive validation against owned real capture bytes. Never saves payloads or decoded identities."""
import argparse
import collections
import json
from pathlib import Path
import struct
import time
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('--expected-mvid', required=True)
parser.add_argument('--report', required=True)
options = parser.parse_args()
headers = {'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream'}
report = {'checks': [], 'gameplayInputUsed': False, 'rawPayloadsSaved': False, 'decodedValuesSaved': False}
session = None


def check(ok, label):
    if not ok:
        raise AssertionError(label)
    report['checks'].append(label)
    print('PASS ' + label, flush=True)


def rpc(method, params):
    data = json.dumps({'jsonrpc': '2.0', 'id': 1, 'method': method, 'params': params}).encode()
    request = urllib.request.Request('http://127.0.0.1:18777/mcp', data=data, headers=headers)
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
    health = json.load(urllib.request.urlopen('http://127.0.0.1:18777/health', timeout=10))
    check(health['buildMvid'].lower() == options.expected_mvid.lower(), 'running MVID matches tested debug DLL')
    report['health'] = health
    rpc('initialize', {'protocolVersion': '2025-06-18', 'capabilities': {}, 'clientInfo': {'name': 'schema-live-test', 'version': '1'}})
    client = call('get_client_state')
    player = call('get_local_player')
    tools, pages, args = [], [], {}
    while True:
        page = rpc('tools/list', args)
        tools += page['tools']; pages.append(len(page['tools']))
        if not page.get('nextCursor'):
            break
        args = {'cursor': page['nextCursor']}
    report['catalogPages'] = pages
    check(len(tools) == 159, 'all catalog pages contain 159 tools')
    for name in ['network_packet_schema', 'network_capture_parse']:
        tool = next(t for t in tools if t['name'] == name)
        check(tool['annotations']['readOnlyHint'], name + ' is read-only')
    catalog = call('network_packet_schema', {'name': 'NpcYell'})
    report['schemaCatalog'] = catalog['catalog']
    check(catalog['packet']['minimumPayloadLength'] == 32 and catalog['catalog']['zoneMappings'] == 170, 'live catalog loads pinned schema and directional metadata')
    state = call('network_capture_status')
    check(not state['hooks']['sendEnabled'] and not state['hooks']['receiveEnabled'], 'schema discovery leaves hooks disabled')
    check(not state['capture'] or not state['capture']['active'], 'no existing active capture is replaced')
    if not client['loggedIn'] or not player.get('loggedIn'):
        report['liveGap'] = 'Logged out: real packet parsing not validated.'
    else:
        state = call('network_capture_start', {'direction': 'both', 'durationSeconds': 10, 'maxPackets': 256, 'maxBytesPerPacket': 1024})
        session = state['capture']['sessionId']
        deadline = time.monotonic() + 13
        while time.monotonic() < deadline:
            state = call('network_capture_status')
            if not state['capture']['active']:
                break
            time.sleep(0.2)
        check(not state['capture']['active'] and not state['hooks']['sendEnabled'] and not state['hooks']['receiveEnabled'], 'bounded real capture stops and disables both hooks')
        rows, after = [], 0
        while True:
            page = call('network_capture_list', {'sessionId': session, 'afterId': after, 'count': 100})
            rows += page['packets']
            if not page['hasMore']:
                break
            after = page['lastId']
        check(bool(rows), 'real zone IPC traffic observed')
        statuses, matched, mismatches, available, unavailable = collections.Counter(), collections.Counter(), collections.Counter(), 0, 0
        for packet in rows[:80]:
            args = {'sessionId': session, 'packetId': packet['packetId'], 'count': 128}
            parsed = call('network_capture_parse', args)
            statuses[parsed['status']] += 1
            check(parsed['versionMatch'] == 'unverified' and not parsed['semanticsVerified'], 'real packet parser does not promote layout to verified semantics')
            if 'schema' not in parsed:
                continue
            matched[parsed['schema']['name']] += 1
            if parsed['status'] == 'short_payload':
                key = (parsed['schema']['name'], parsed['declaredPayloadLength'], parsed['schema']['minimumPayloadLength'])
                mismatches[key] += 1
            check(parsed['schema']['direction'] == packet['direction'], 'real automatic schema respects captured direction')
            raw = call('network_capture_read', {**args, 'count': 1024})
            data = bytes.fromhex(raw['hex'])
            fields = list(parsed['fields'])
            cursor = parsed['nextCursor']
            while cursor is not None:
                page = call('network_capture_parse', {**args, 'cursor': cursor})
                fields += page['fields']; cursor = page['nextCursor']
            check(len(fields) == parsed['totalFields'], 'real field paging yields all retained field paths')
            for field in fields:
                if not field['available']:
                    unavailable += 1
                    continue
                offset, length = field['offset'], field['length']
                value = data[offset:offset + length]
                check(len(value) == length, 'available field stays inside real captured bytes')
                kind = field['type']
                endian = 'big' if field.get('format', {}).get('add_le') is False else 'little'
                if kind in ['byte', 'uint', 'int', 'bigint', 'biguint']:
                    expected = int.from_bytes(value, endian, signed=kind in ['int', 'bigint'])
                    if kind in ['bigint', 'biguint']:
                        expected = str(expected)
                elif kind == 'bytes':
                    expected = value.hex().upper()
                elif kind == 'string':
                    expected = value.split(b'\0')[0].decode('utf-8', errors='strict')
                elif kind == 'bitset':
                    expected = [i * 8 + b for i, v in enumerate(value) for b in range(8) if v & (1 << b)]
                elif kind in ['float', 'double']:
                    expected = struct.unpack(('>' if endian == 'big' else '<') + ('f' if kind == 'float' else 'd'), value)[0]
                    if not isinstance(field['value'], (int, float)):
                        continue
                else:
                    raise AssertionError('Unexpected available type ' + kind)
                check(expected == field['value'], 'independent Python decoder matches real raw field')
                available += 1
        check(bool(matched) and available > 0, 'at least one real packet automatically parsed and independently checked')
        report.update({'capture': state['capture'], 'directions': dict(collections.Counter(p['direction'] for p in rows)),
                       'parseStatuses': dict(statuses), 'matchedSchemas': dict(matched), 'fieldsCompared': available, 'unavailableFields': unavailable,
                       'lengthMismatches': [{'schema': name, 'declaredPayloadLength': actual, 'minimumSchemaLength': expected, 'packets': number}
                                            for (name, actual, expected), number in mismatches.items()]})
        check(call('get_client_state')['loggedIn'], 'client remains logged in after passive validation')
    report['completed'] = True
finally:
    if session:
        report['cleanup'] = call('network_capture_stop', {'sessionId': session})
    report['checkCount'] = len(report['checks'])
    report['checks'] = dict(collections.Counter(report['checks']))
    Path(options.report).parent.mkdir(parents=True, exist_ok=True)
    Path(options.report).write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
