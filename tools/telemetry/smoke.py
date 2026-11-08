#!/usr/bin/env python3
"""Real synthetic end-to-end check; prints IDs/counts only, never credentials or run content."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import httpx
from cryptography.hazmat.primitives import serialization
from neo4j import GraphDatabase
from environment import read_env
from receiver import Storage, PREFIX, Batch
from graph_sync import decrypt


def main():
    ap=argparse.ArgumentParser(); ap.add_argument('--dll',required=True); args=ap.parse_args()
    root=Path.home()/'env/solvra-telemetry'
    config=json.loads((root/'smoke.json').read_text())
    spool=Path.home()/'.local/share/solvra-telemetry-smoke'
    config.update(logs_directory=str(spool/'logs'),sessions_directory=str(spool/'sessions'))
    # Keep credential config in ~/env, independent of public artifacts.
    (root/'smoke.json').write_text(json.dumps(config)); (root/'smoke.json').chmod(0o600)
    env={**os.environ,'SOLVRA_TELEMETRY_CONFIG':str(root/'smoke.json')}
    with httpx.Client(timeout=90) as c:
        assert c.get(config['endpoint']+'/health').status_code==200
        assert c.post(config['endpoint']+'/v1/batches',json={}).status_code==401
    subprocess.run(['dotnet',args.dll,'telemetry','test'],env=env,check=True)
    subprocess.run(['dotnet',args.dll,'telemetry','status'],env=env,check=True)
    os.environ.update(read_env(Path.home()/'env/scaleway.env'))
    storage=Storage()
    key=serialization.load_pem_private_key((root/'archive-private.pem').read_bytes(),password=None)
    objects=storage.s3.list_objects_v2(Bucket=storage.bucket,Prefix=PREFIX+config['device_id']+'/').get('Contents',[])
    assert objects
    for obj in objects:
        raw=storage.s3.get_object(Bucket=storage.bucket,Key=obj['Key'])['Body'].read()
        assert b'Synthetic telemetry delivery check' not in raw
        decrypt(Batch.model_validate_json(raw),key)
    subprocess.run([sys.executable,str(Path(__file__).with_name('graph_sync.py'))],check=True)
    creds=read_env(Path.home()/'env/graph-memory.env')
    with GraphDatabase.driver(creds.get('NEO4J_URI','bolt://127.0.0.1:7687'),auth=(creds.get('NEO4J_USER','neo4j'),creds.get('NEO4J_PASSWORD',''))) as driver:
        with driver.session() as s:
            counts=s.run('''MATCH (d:Decision:CloudTelemetry) WHERE d.key STARTS WITH $device RETURN count(d) AS n''',device=config['device_id']).single()['n']
            assert counts>0
            before=s.run('MATCH (r:CloudTelemetryRecord) RETURN count(r) AS n').single()['n']
        subprocess.run([sys.executable,str(Path(__file__).with_name('graph_sync.py'))],check=True)
        with driver.session() as s:
            assert s.run('MATCH (r:CloudTelemetryRecord) RETURN count(r) AS n').single()['n']==before
    print(f'PUBLIC-SMOKE-OK: {len(objects)} encrypted objects; {counts} synthetic decisions; graph replay idempotent')

if __name__=='__main__': main()
