#!/usr/bin/env python3
"""Run the real API, Worker and Next.js against an isolated local demo database.

Requires a successful dotnet build and npm install in web. Never uses production
credentials. Email goes to a local pickup directory, not to real recipients.
"""
import json
import os
from pathlib import Path
import secrets
import subprocess
import time

ROOT = Path(__file__).resolve().parents[2]
RUNTIME = ROOT / '.demo-runtime'
RUNTIME.mkdir(exist_ok=True)
for folder in ('evidence', 'mail'):
    (RUNTIME / folder).mkdir(exist_ok=True)
db = ['psql', '-h', '127.0.0.1', '-U', os.environ.get('USER', 'user'),
      '-d', 'edusphere_submission_demo', '-v', 'ON_ERROR_STOP=1']
for migration in sorted((ROOT / 'src/EduSphere.Safety.Infrastructure/Migrations').glob('*.sql')):
    subprocess.run(db + ['-f', str(migration)], check=True, stdout=subprocess.DEVNULL)
subprocess.run(db + ['-c', """
INSERT INTO tenants(id,name) VALUES ('demo-school','EduSphere Demonstration School'),
('other-school','Tenant Isolation Test') ON CONFLICT DO NOTHING;
INSERT INTO tenant_members(tenant_id,user_id) VALUES ('demo-school','safety-officer'),
('other-school','other-officer') ON CONFLICT DO NOTHING;
"""], check=True, stdout=subprocess.DEVNULL)
config_path = RUNTIME / 'config.json'
if config_path.exists():
    config = json.loads(config_path.read_text())
else:
    config = {'authKey': secrets.token_urlsafe(48), 'storageKey': secrets.token_urlsafe(48)}
    config_path.write_text(json.dumps(config))
    config_path.chmod(0o600)
env = dict(os.environ, **{
    'ASPNETCORE_URLS': 'http://127.0.0.1:5080',
    'ASPNETCORE_ENVIRONMENT': 'Development',
    'Postgres__ConnectionString': f"Host=127.0.0.1;Database=edusphere_submission_demo;Username={os.environ.get('USER', 'user')}",
    'Storage__Backend': 'File', 'Storage__FileRoot': str(RUNTIME / 'evidence'),
    'Storage__TokenSecret': config['storageKey'],
    'Auth__DevStubEnabled': 'true', 'Auth__Issuer': 'edusphere-local-demo',
    'Auth__Audience': 'edusphere-local-demo', 'Auth__IssuerSigningKey': config['authKey'],
    'Email__PickupDirectory': str(RUNTIME / 'mail'), 'Email__From': 'safety@demo.example',
    'Notification__To__0': 'officer@demo.example', 'Outbox__IntervalSeconds': '1',
    'SAFETY_API_URL': 'http://127.0.0.1:5080',
})
services = [
    ('api', ['dotnet', str(ROOT / 'src/EduSphere.Safety.Api/bin/Debug/net10.0/EduSphere.Safety.Api.dll')], ROOT),
    ('worker', ['dotnet', str(ROOT / 'src/EduSphere.Safety.Worker/bin/Debug/net10.0/EduSphere.Safety.Worker.dll')], ROOT),
    ('web', ['npm', 'run', 'dev', '--', '--hostname', '127.0.0.1', '--port', '3000'], ROOT / 'web'),
]
pids = {}
for name, command, cwd in services:
    log = open(RUNTIME / f'{name}.log', 'a')
    proc = subprocess.Popen(command, cwd=cwd, env=env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
    pids[name] = proc.pid
(RUNTIME / 'pids.json').write_text(json.dumps(pids))
print('Demo services started:', pids)
print('Dashboard: http://127.0.0.1:3000 | School: demo-school | Staff: safety-officer')
time.sleep(4)
for name in pids:
    print(f'--- {name} ---')
    print((RUNTIME / f'{name}.log').read_text()[-1200:])
