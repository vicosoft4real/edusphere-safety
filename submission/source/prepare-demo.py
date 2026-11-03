#!/usr/bin/env python3
"""Reproducible synthetic demo harness. Uses real services; no mocked APIs.

The current edge host writes an outbox rather than uploading. This script is the
explicit demo handoff: normalize the edge ID, ingest its event, then upload the
synthetic source footage through the evidence manifest API. It does not claim
automatic edge delivery or automatic clip extraction.
"""
import argparse
import datetime as dt
import hashlib
import json
from pathlib import Path
import subprocess
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
RUNTIME = ROOT / '.demo-runtime'
ASSETS = ROOT / 'submission/assets'
API = 'http://127.0.0.1:5080'
STATE = RUNTIME / 'demo-state.json'

def call(path, body=None, token=None, device=None, method=None, raw=None):
    headers = {'Content-Type': 'application/json'}
    if token:
        headers['Authorization'] = 'Bearer ' + token
    if device:
        headers['X-Device-Key'] = device['deviceId'] + '.' + device['secret']
    data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
    if raw is not None:
        headers['Content-Type'] = 'application/octet-stream'
    req = urllib.request.Request(API + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=30) as res:
            result = res.read()
            return json.loads(result) if result else None
    except urllib.error.HTTPError as error:
        raise RuntimeError(f'{path}: HTTP {error.code}: {error.read().decode()}') from error

def heartbeat(state):
    call('/v1/devices/heartbeat', {'softwareVersion': 'demo-replay-1.0',
        'modelVersion': 'manifest-replay-1', 'configVersion': state['bundleVersion'],
        'queueDepth': 0, 'frameAgeSeconds': 0, 'diskUsedBytes': 0}, device=state['device'])

def setup():
    token = call('/v1/auth/dev-token', {'tenantId': 'demo-school', 'userId': 'safety-officer'})['accessToken']
    code = call('/v1/devices/enrol-codes', {'campusId': 'main-campus'}, token=token)['code']
    device = call('/v1/devices/enrol', {'code': code})
    camera = call('/v1/cameras', {'campusId': 'main-campus', 'deviceId': device['deviceId'],
        'mode': 'manual', 'host': '192.168.10.20', 'port': 554, 'path': '/corridor'}, token=token)['id']
    bundle = {'format': 'edusphere-rule-bundle/1', 'bundleVersion': 1,
        'modelVersion': 'manifest-replay-1',
        'schedules': [{'scheduleId': 'after-hours', 'ianaTimezone': 'Africa/Lagos',
            'closedWindows': [{'start': '00:00', 'end': '23:59:59'}]}],
        'rules': [{'ruleId': 'restricted-corridor', 'type': 'ZoneIntrusion', 'version': 1,
            'confidenceThreshold': 0.5, 'persistenceFrames': 3, 'cooldownSeconds': 60,
            'polygon': [[0.35,0],[0.65,0],[0.65,1],[0.35,1]], 'scheduleId': 'after-hours'}]}
    version = call('/v1/config/bundles', {'document': json.dumps(bundle)}, token=token)['version']
    call('/v1/config/policies', {'id': 'default', 'recipients': ['officer@demo.example'],
        'levels': [{'afterSeconds': 300, 'recipients': ['headteacher@demo.example']}]}, token=token)
    state = {'token': token, 'device': device, 'cameraId': camera, 'bundleVersion': version}
    STATE.write_text(json.dumps(state))
    STATE.chmod(0o600)
    (ASSETS / 'rules.json').write_text(json.dumps(bundle, indent=2))
    heartbeat(state)
    print('Enrolled demo device; registered a camera descriptor (no physical camera); published rule + notification policy.')
    output = subprocess.check_output(['dotnet', str(ROOT/'tools/Simulator/bin/Debug/net10.0/Simulator.dll'),
        '--scenario', 'walker-crossing-zone', '--out', str(ASSETS/'sim'), '--frames','60'], text=True)
    (ASSETS/'simulator-output.txt').write_text(output)
    print(output.strip())
    first = dt.datetime.now(dt.timezone.utc) - dt.timedelta(seconds=15)
    cmd = ['dotnet', str(ROOT/'src/EduSphere.Safety.EdgeAgent/bin/Debug/net10.0/EduSphere.Safety.EdgeAgent.dll'),
        '--source', (ASSETS/'sim/sim.mp4').as_uri(), '--camera', camera,
        '--rules', str(ASSETS/'rules.json'), '--manifest', str(ASSETS/'sim/manifest.json'),
        '--out', str(ASSETS/'edge'), '--first-frame-time', first.isoformat()]
    edge = subprocess.check_output(cmd, text=True, stderr=subprocess.STDOUT)
    (ASSETS/'edge-output.txt').write_text(edge)
    print(edge.strip())

def ingest():
    state = json.loads(STATE.read_text())
    event = next(json.loads(line) for line in (ASSETS/'edge/outbox.jsonl').read_text().splitlines()
        if json.loads(line).get('ruleId') == 'restricted-corridor')
    # Cloud evidence IDs have a 64-character alphanumeric contract; the edge
    # outbox currently emits slash-separated IDs. Preserve a deterministic map.
    event_id = hashlib.sha256(event['edgeEventId'].encode()).hexdigest()
    body = {'edgeEventId': event_id, 'campusId': 'main-campus', 'eventType': 'ZoneIntrusion',
        'cameraId': state['cameraId'], 'detectedAtUtc': event['detectionTimeUtc'],
        'ruleVersion': str(event['ruleVersion']), 'modelVersion': event['modelVersion']}
    created = call('/v1/ingest/events', body, device=state['device'])
    duplicate = call('/v1/ingest/events', body, device=state['device'])
    assert duplicate['id'] == created['id'], 'duplicate ingest must return the same incident'
    for kind, path in [('clip', ASSETS/'sim/sim.mp4'), ('snapshot', ASSETS/'snapshot.jpg')]:
        if kind == 'snapshot':
            subprocess.run(['ffmpeg','-y','-loglevel','error','-ss','5','-i',str(ASSETS/'sim/sim.mp4'),
                '-frames:v','1',str(path)], check=True)
        payload = path.read_bytes()
        manifest = call('/v1/evidence/manifest', {'campusId':'main-campus','cameraId':state['cameraId'],
            'edgeEventId':event_id,'kind':kind,'bytes':len(payload),
            'sha256':hashlib.sha256(payload).hexdigest()},device=state['device'])
        call(manifest['putUrl'],device=state['device'],method='PUT',raw=payload)
        call('/v1/evidence/manifest/complete',{'edgeEventId':event_id,'kind':kind},device=state['device'])
    state.update(incidentId=created['id'],edgeEventId=event_id)
    STATE.write_text(json.dumps(state))
    heartbeat(state)
    print(f"Real API created incident {created['id']}; duplicate returned the same ID; synthetic clip + snapshot uploaded.")
    (ASSETS/'handoff-result.json').write_text(json.dumps({'incidentId':created['id'],
        'duplicateId':duplicate['id'],'duplicateSuppressed':True,'source':'manifest replay; demo handoff',
        'sourceEdgeEventId':event['edgeEventId'],'cloudEdgeEventId':event_id},indent=2))

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('action', choices=['setup','ingest','heartbeat'])
    action = parser.parse_args().action
    ASSETS.mkdir(parents=True,exist_ok=True)
    if action == 'setup': setup()
    elif action == 'ingest': ingest()
    else: heartbeat(json.loads(STATE.read_text()))
