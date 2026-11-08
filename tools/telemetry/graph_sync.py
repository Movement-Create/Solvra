#!/usr/bin/env python3
"""Idempotent encrypted-archive consumer. Runs on graph host; cloud receipt never depends on it.
Only this process has the decryption key. Batch receipt and graph writes share one transaction.
"""
import base64
import hashlib
import json
import os
from pathlib import Path
import sys
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import padding
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from neo4j import GraphDatabase
from environment import read_env
from receiver import Storage, PREFIX, Batch


def decrypt(batch, key):
    batch.check_encryption()
    e = {k: base64.b64decode(v, validate=True) for k, v in batch.envelope.model_dump().items()}
    aes_key = key.decrypt(e['key'], padding.OAEP(mgf=padding.MGF1(hashes.SHA256()), algorithm=hashes.SHA256(), label=None))
    raw = AESGCM(aes_key).decrypt(e['nonce'], e['ciphertext'] + e['tag'], batch.device_id.encode())
    payload = json.loads(raw)
    if not isinstance(payload['offset'], int) or payload['offset'] < 0: raise ValueError('invalid offset')
    data = base64.b64decode(payload['data'], validate=True)
    if not data.endswith(b'\n'): raise ValueError('incomplete record')
    return payload['source'], payload['offset'], data


def project(device, source, offset, content):
    rows = []
    for raw in content.splitlines(keepends=True):
        record_id = hashlib.sha256(f'{device}:{source}:{offset}:'.encode() + raw).hexdigest()
        try: event = json.loads(raw)
        except (ValueError, UnicodeError): event = {'type': 'malformed_record', 'raw_base64': base64.b64encode(raw).decode()}
        if not isinstance(event, dict): event = {'type': 'unknown_record', 'value': event}
        data = event.get('data') or {}
        if not isinstance(data, dict): data = {}
        session = source.removeprefix('sessions/').removesuffix('.jsonl') if source.startswith('sessions/') and not source.endswith('cost-ledger.jsonl') else event.get('session_id')
        trace = event.get('trace_id') or data.get('trace_id') or data.get('traceId')
        calls = []
        if event.get('type') == 'assistant_turn':
            for block in data.get('content', []):
                if isinstance(block, dict) and block.get('type') == 'tool_use':
                    calls.append({'id': block['id'], 'name': block['name'], 'input': json.dumps(block.get('input'))})
        rows.append({'id': record_id, 'device': device, 'source': source, 'offset': offset,
                     'type': event.get('type', event.get('event_type', 'unknown')), 'timestamp': event.get('timestamp'),
                     'json': raw.decode('utf-8', errors='replace'), 'session': session, 'trace': trace,
                     'calls': calls, 'result_id': data.get('call_id') if event.get('type') == 'tool_result' else None,
                     'output': data.get('output'), 'is_error': data.get('is_error')})
        offset += len(raw)
    return rows


def apply_batch(tx, batch, rows):
    if tx.run('MATCH (b:CloudTelemetryBatch {id: $id}) RETURN b.id', id=batch.id).single(): return False
    for row in rows:
        tx.run('''MERGE (d:CloudTelemetryDevice {id: $device})
            MERGE (r:CloudTelemetryRecord {id: $id})
            SET r.source=$source, r.offset=$offset, r.type=$type, r.timestamp=$timestamp, r.json=$json
            MERGE (d)-[:RECORDED]->(r)''', **{k: row[k] for k in ('device','id','source','offset','type','timestamp','json')}).consume()
        if row['session']:
            session_key = row['device'] + ':session:' + row['session']
            tx.run('''MATCH (r:CloudTelemetryRecord {id:$id})
                MERGE (c:Conversation:CloudTelemetry {key:$key}) SET c.session_id=$session
                MERGE (c)-[:HAS_RECORD]->(r)''', id=row['id'], key=session_key, session=row['session']).consume()
            for call in row['calls']:
                call_key = session_key + ':call:' + call['id']
                tx.run('''MATCH (r:CloudTelemetryRecord {id:$record})
                    MERGE (t:ToolCall:CloudTelemetry {key:$key})
                    SET t.call_id=$id, t.name=$name, t.input=$input
                    MERGE (r)-[:CALLED]->(t)''', record=row['id'], key=call_key, **call).consume()
                if call['name'] in ('decide', 'verify'):
                    label = 'Decision' if call['name'] == 'decide' else 'Verification'
                    details = json.loads(call['input']) or {}
                    tx.run(f'''MATCH (t:ToolCall:CloudTelemetry {{key:$key}})
                        MERGE (d:{label}:CloudTelemetry {{key:$decision_key}}) SET d.details=$input, d.question=$question, d.choice=$choice, d.rationale=$rationale, d.confidence=$confidence
                        MERGE (t)-[:RECORDED]->(d)''', key=call_key, decision_key=call_key+':decision', input=call['input'], question=details.get('question'),
                        choice=details.get('choice'), rationale=details.get('rationale'), confidence=details.get('confidence')).consume()
                    for evidence in details.get('evidence', []) or []:
                        if isinstance(evidence, str):
                            tx.run('''MATCH (d:CloudTelemetry {key:$decision})
                                MERGE (t:ToolCall:CloudTelemetry {key:$tool})
                                MERGE (d)-[:SUPPORTED_BY]->(t)''', decision=call_key+':decision', tool=session_key+':call:'+evidence).consume()
            if row['result_id']:
                tx.run('''MATCH (r:CloudTelemetryRecord {id:$id})
                    MERGE (t:ToolCall:CloudTelemetry {key:$key}) SET t.output=$output, t.is_error=$is_error
                    MERGE (t)-[:HAS_RESULT]->(r)''', id=row['id'], key=session_key+':call:'+row['result_id'], output=row['output'], is_error=row['is_error']).consume()
            if row['type'] == 'run_start' and row['trace']:
                tx.run('''MATCH (c:Conversation:CloudTelemetry {key:$session}), (r:CloudTelemetryRecord {id:$id})
                    MERGE (run:Run:CloudTelemetry {key:$key}) SET run.trace_id=$trace, run.start_offset=$offset, run.source=$source
                    MERGE (c)-[:HAS_RUN]->(run) MERGE (run)-[:HAS_RECORD]->(r)''',
                    session=session_key, id=row['id'], key=row['device']+':run:'+row['trace'], trace=row['trace'], offset=row['offset'], source=row['source']).consume()
        if row['trace']:
            tx.run('''MATCH (r:CloudTelemetryRecord {id:$id})
                MERGE (run:Run:CloudTelemetry {key:$key}) SET run.trace_id=$trace
                MERGE (run)-[:HAS_RECORD]->(r)''', id=row['id'], key=row['device']+':run:'+row['trace'], trace=row['trace']).consume()
    # Exact file order, independent of archive listing order; no timestamp-window guessing.
    for session, source in {(r['session'],r['source']) for r in rows if r['session'] and r['source'].startswith('sessions/')}:
        tx.run('''MATCH (c:Conversation:CloudTelemetry {key:$key})-[:HAS_RECORD]->(r:CloudTelemetryRecord)
            WHERE r.source=$source
            CALL (c,r) {
                MATCH (c)-[:HAS_RUN]->(run:CloudTelemetry)
                WHERE run.source=r.source AND run.start_offset <= r.offset
                RETURN run ORDER BY run.start_offset DESC LIMIT 1 }
            WITH run,r OPTIONAL MATCH (old:Run:CloudTelemetry)-[edge:HAS_RECORD]->(r)
            WHERE old <> run AND old.source = r.source
            DELETE edge
            MERGE (run)-[:HAS_RECORD]->(r)''', key=batch.device_id+':session:'+session, source=source).consume()
    tx.run('CREATE (b:CloudTelemetryBatch {id:$id, device:$device, imported_at:datetime()})', id=batch.id, device=batch.device_id).consume()
    return True


def main():
    env = Path.home() / 'env'
    os.environ.update(read_env(env / 'scaleway.env'))
    credentials = read_env(env / 'graph-memory.env')
    key = serialization.load_pem_private_key((env / 'solvra-telemetry/archive-private.pem').read_bytes(), password=None)
    storage = Storage()
    auth = (credentials.get('NEO4J_USER', 'neo4j'), credentials.get('NEO4J_PASSWORD', ''))
    with GraphDatabase.driver(credentials.get('NEO4J_URI', 'bolt://127.0.0.1:7687'), auth=auth) as driver:
        with driver.session() as session:
            for label, prop in [('CloudTelemetryRecord','id'), ('CloudTelemetryBatch','id'), ('CloudTelemetryDevice','id'), ('CloudTelemetry','key')]:
                session.run(f'CREATE CONSTRAINT IF NOT EXISTS FOR (n:{label}) REQUIRE n.{prop} IS UNIQUE').consume()
            done = {r['id'] for r in session.run('MATCH (b:CloudTelemetryBatch) RETURN b.id AS id')}
            count = 0
            failed = 0
            for page in storage.s3.get_paginator('list_objects_v2').paginate(Bucket=storage.bucket, Prefix=PREFIX):
                for obj in page.get('Contents', []):
                    ident = obj['Key'].rsplit('/',1)[-1].removesuffix('.json')
                    if ident in done: continue
                    try:
                        body = storage.s3.get_object(Bucket=storage.bucket, Key=obj['Key'])['Body'].read()
                        batch = Batch.model_validate_json(body)
                        source, offset, data = decrypt(batch, key)
                        rows = project(batch.device_id, source, offset, data)
                        if session.execute_write(apply_batch, batch, rows): count += 1
                    except Exception as error:
                        failed += 1
                        print(f'graph batch deferred ({type(error).__name__}); archive retained', file=sys.stderr)
            print(f'graph sync: {count} encrypted batches imported')
    return 1 if failed else 0

if __name__ == '__main__':
    try: sys.exit(main())
    except Exception as e:
        print(f'graph sync failed ({type(e).__name__}); archive retained, retry next run', file=sys.stderr)
        sys.exit(1)
