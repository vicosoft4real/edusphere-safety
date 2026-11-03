#!/usr/bin/env python3
"""Prepare a real recorded-clip SAM incident for the submission video.

Reuses the verified SAM API result; makes no model request. Cloud ingestion and
evidence upload are an explicit demo handoff, not an automatic edge uploader.
Runtime credentials remain in ignored .demo-runtime, never in narration or logs.
"""
import datetime as dt
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import re

ROOT = Path(__file__).resolve().parents[2]
RUNTIME = ROOT/'.demo-runtime'
SOURCE = RUNTIME/'real-footage'
ASSETS = ROOT/'submission/assets'
spec = importlib.util.spec_from_file_location('demo_api', Path(__file__).with_name('prepare-demo.py'))
api = importlib.util.module_from_spec(spec); spec.loader.exec_module(api)
_call = api.call
def safe_call(path, *args, **kwargs):
    try:return _call(path,*args,**kwargs)
    except RuntimeError as error:
        status=re.search(r'HTTP (\d+)',str(error))
        raise RuntimeError(f"Local API {path.split('?')[0]} failed" + (f' (HTTP {status[1]})' if status else '')) from None
api.call=safe_call

def main():
    token = api.call('/v1/auth/dev-token', {'tenantId':'demo-school','userId':'safety-officer'})['accessToken']
    code = api.call('/v1/devices/enrol-codes', {'campusId':'recorded-sample'}, token=token)['code']
    device = api.call('/v1/devices/enrol', {'code':code})
    camera = api.call('/v1/cameras', {'campusId':'recorded-sample','deviceId':device['deviceId'],
        'mode':'manual','host':'192.168.1.49','port':554,'path':'/recorded-sample-not-live-camera'},token=token)['id']
    out = ASSETS/'sam-edge'; out.mkdir(exist_ok=True)
    bundle = json.loads((SOURCE/'sam-rules.json').read_text())
    bundle['schedules']=[{'scheduleId':'sample-test','ianaTimezone':'Africa/Lagos',
        'closedWindows':[{'start':'00:00','end':'23:59:59'}]}]
    for rule in bundle['rules']:rule['scheduleId']='sample-test'
    (out/'rules.json').write_text(json.dumps(bundle,indent=2))
    version = api.call('/v1/config/bundles', {'document':json.dumps(bundle)},token=token)['version']
    api.call('/v1/config/policies', {'id':'default','recipients':['officer@demo.example'],
        'levels':[{'afterSeconds':300,'recipients':['headteacher@demo.example']}]},token=token)
    # Unique output folder avoids reusing a prior outbox on a repeat take.
    run_dir = out/dt.datetime.now(dt.timezone.utc).strftime('%Y%m%dT%H%M%S')
    cmd = ['dotnet',str(ROOT/'src/EduSphere.Safety.EdgeAgent/bin/Debug/net10.0/EduSphere.Safety.EdgeAgent.dll'),
        '--source',str(SOURCE/'pedestrians-12s.mp4'),'--camera',camera,'--rules',str(out/'rules.json'),
        '--sam-results',str(SOURCE/'sam-api'),'--fps','10','--width','768','--height','576',
        '--first-frame-time',(dt.datetime.now(dt.timezone.utc)-dt.timedelta(seconds=15)).isoformat(),
        '--out',str(run_dir)]
    completed = subprocess.run(cmd,capture_output=True,text=True,check=True)
    (out/'run-result.txt').write_text(completed.stdout)
    events=[json.loads(line) for line in (run_dir/'outbox.jsonl').read_text().splitlines()
        if json.loads(line).get('ruleId')=='demonstration-zone']
    assert len(events)==6
    event=events[0]; cloud_id=hashlib.sha256(event['edgeEventId'].encode()).hexdigest()
    body={'edgeEventId':cloud_id,'campusId':'recorded-sample','eventType':'ZoneIntrusion','cameraId':camera,
        'detectedAtUtc':event['detectionTimeUtc'],'ruleVersion':str(event['ruleVersion']),'modelVersion':'sam-3.1'}
    created=api.call('/v1/ingest/events',body,device=device)
    duplicate=api.call('/v1/ingest/events',body,device=device)
    assert created['id']==duplicate['id']
    snapshot=out/'snapshot.jpg'
    subprocess.run(['ffmpeg','-y','-loglevel','error','-ss','4','-i',str(SOURCE/'pedestrians-12s.mp4'),'-frames:v','1',str(snapshot)],check=True)
    playback=out/'playback.mp4'
    subprocess.run(['ffmpeg','-y','-loglevel','error','-i',str(SOURCE/'pedestrians-12s.mp4'),
        '-an','-c:v','libx264','-crf','29','-maxrate','450k','-bufsize','900k','-pix_fmt','yuv420p','-movflags','+faststart',str(playback)],check=True)
    assert playback.stat().st_size<1048576
    for kind,path in [('clip',playback),('snapshot',snapshot)]:
        content=path.read_bytes()
        manifest=api.call('/v1/evidence/manifest',{'campusId':'recorded-sample','cameraId':camera,
            'edgeEventId':cloud_id,'kind':kind,'bytes':len(content),'sha256':hashlib.sha256(content).hexdigest()},device=device)
        api.call(manifest['putUrl'],device=device,method='PUT',raw=content)
        api.call('/v1/evidence/manifest/complete',{'edgeEventId':cloud_id,'kind':kind},device=device)
    state={'token':token,'device':device,'cameraId':camera,'bundleVersion':version,'incidentId':created['id'],
        'edgeEventId':cloud_id,'sourceClip':str(SOURCE/'pedestrians-12s.mp4'),'playbackClip':str(playback),'sourceModel':'sam-3.1'}
    path=RUNTIME/'sam-demo-state.json';path.write_text(json.dumps(state));path.chmod(0o600)
    api.call('/v1/devices/heartbeat',{'softwareVersion':'sam-recorded-clip-1.0','modelVersion':'sam-3.1',
        'configVersion':version,'queueDepth':0,'frameAgeSeconds':0,'diskUsedBytes':0},device=device)
    proof={'model':'sam-3.1','source':'OpenCV public real pedestrian recording; recorded clip, not local CCTV',
        'frames':120,'trackIds':8,'zoneEvents':6,'selectedIncidentId':created['id'],
        'duplicateSuppressed':created['id']==duplicate['id'],'sourceSha256':hashlib.sha256((SOURCE/'pedestrians-12s.mp4').read_bytes()).hexdigest(),
        'playbackSha256':hashlib.sha256(playback.read_bytes()).hexdigest(),
        'playbackDescription':'transcoded copy of the same real source recording',
        'transport':'explicit demo handoff','additionalSamApiCalls':0}
    (out/'handoff-proof.json').write_text(json.dumps(proof,indent=2))
    print(completed.stdout.strip());print('Real SAM event ingested; duplicate suppressed; transcoded playback copy uploaded. Credentials saved only in ignored runtime state.')

if __name__=='__main__':main()
