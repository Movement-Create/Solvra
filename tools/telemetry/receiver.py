"""Public ingestion. Full run content is ciphertext; this service never has the private key."""
import base64
import hashlib
import hmac
import json
import os
import re
import time

import boto3
import httpx
from botocore.config import Config
from fastapi import FastAPI, HTTPException, Request
from pydantic import BaseModel, ConfigDict, Field, field_validator

NAMES = set('agent.session agent.turn llm.call tool.execute SessionStart SessionEnd model_call_started model_call_completed model_call_failed tool_call_started tool_call_completed tool_execution_metrics operation_interrupted run_start run_end user_message assistant_message assistant_turn tool_result session_start system_prompt'.split())
NUMBERS = set('turn turns duration_ms input_tokens output_tokens cache_read_tokens cache_write_tokens total_tokens cost_usd decisions verifications bytes output_bytes exit_code is_error success'.split())
MAX_BODY = 16 * 1024 * 1024
PREFIX = 'solvra-telemetry/v1/'

class Strict(BaseModel):
    model_config = ConfigDict(extra='forbid', strict=True, allow_inf_nan=False)

class Record(Strict):
    name: str
    kind: str
    time: int = Field(ge=0, le=32_503_680_000_000)
    start: int = Field(ge=0, le=32_503_680_000_000)
    trace: str | None = None
    span: str | None = None
    parent: str | None = None
    numbers: dict[str, int | float | bool] = Field(default_factory=dict, max_length=30)

    @field_validator('name')
    @classmethod
    def name_allowed(cls, v):
        if v not in NAMES: raise ValueError('unknown event')
        return v

    @field_validator('kind')
    @classmethod
    def kind_allowed(cls, v):
        if v not in ('span', 'log'): raise ValueError('unknown kind')
        return v

    @field_validator('trace', 'span', 'parent')
    @classmethod
    def hex_id(cls, v, info):
        lengths = (16, 32) if info.field_name == 'trace' else (16,)
        if v is not None and (len(v) not in lengths or not re.fullmatch('[0-9a-f]+', v) or not int(v, 16)):
            raise ValueError('invalid id')
        return v

    @field_validator('numbers')
    @classmethod
    def number_names(cls, v):
        if not set(v).issubset(NUMBERS): raise ValueError('unknown attribute')
        return v

class Envelope(Strict):
    key: str
    nonce: str
    ciphertext: str
    tag: str

class Batch(Strict):
    version: int
    device_id: str = Field(pattern=r'^[0-9a-f]{32}$')
    id: str = Field(pattern=r'^[0-9a-f]{64}$')
    envelope: Envelope
    metadata: list[Record] = Field(max_length=25000)

    def check_encryption(self):
        if self.version != 1: raise ValueError('unsupported version')
        e = {k: base64.b64decode(v, validate=True) for k, v in self.envelope.model_dump().items()}
        if len(e['key']) not in (256, 384, 512) or len(e['nonce']) != 12 or len(e['tag']) != 16 or not e['ciphertext']:
            raise ValueError('invalid envelope')
        if hashlib.sha256(e['ciphertext']).hexdigest() != self.id: raise ValueError('invalid batch hash')


def value(v):
    if isinstance(v, bool): return {'boolValue': v}
    if isinstance(v, int): return {'intValue': str(v)}
    if isinstance(v, float): return {'doubleValue': v}
    return {'stringValue': str(v)}


def attr(k, v): return {'key': k, 'value': value(v)}


def signals(batch):
    resource = {'attributes': [attr('service.name', 'solvra'), attr('service.namespace', 'personal-agents'),
                               attr('solvra.device.id', batch.device_id)]}
    logs, spans = [], []
    received_ns = time.time_ns()
    for index, r in enumerate(batch.metadata):
        attributes = [attr(k, v) for k, v in r.numbers.items()]
        attributes += [attr("solvra.batch.id", batch.id), attr("solvra.record.index", index)]
        trace = hashlib.sha256(f'{batch.device_id}:{r.trace}'.encode()).hexdigest()[:32] if r.trace else None
        if r.trace: attributes.append(attr('solvra.trace.id', r.trace))
        if r.kind == 'span' and trace and r.span:
            span = {'traceId': trace, 'spanId': r.span, 'name': r.name, 'kind': 1,
                    'startTimeUnixNano': str(r.start * 1_000_000),
                    'endTimeUnixNano': str(max(r.start, r.time) * 1_000_000), 'attributes': attributes}
            if r.parent: span['parentSpanId'] = r.parent
            spans.append(span)
        # Logs use receipt time so an offline laptop/backfill cannot wedge the stream on
        # Loki's age/out-of-order limits. Preserve actual event time as numeric metadata.
        attributes += [attr('solvra.event_time_unix_ms', r.time)]
        log = {'timeUnixNano': str(received_ns + index), 'observedTimeUnixNano': str(received_ns), 'body': value(r.name), 'attributes': attributes}
        if trace: log['traceId'] = trace
        if r.span: log['spanId'] = r.span
        logs.append(log)
    result = {}
    if logs: result['logs'] = {'resourceLogs': [{'resource': resource, 'scopeLogs': [{'scope': {'name': 'solvra.telemetry'}, 'logRecords': logs}]}]}
    if spans: result['traces'] = {'resourceSpans': [{'resource': resource, 'scopeSpans': [{'scope': {'name': 'solvra.telemetry'}, 'spans': spans}]}]}
    return result


class Storage:
    def __init__(self):
        self.bucket = os.environ['LAKE_BUCKET']
        self.s3 = boto3.client('s3', endpoint_url=f"https://s3.{os.environ['SCW_DEFAULT_REGION']}.scw.cloud",
                              region_name=os.environ['SCW_DEFAULT_REGION'],
                              aws_access_key_id=os.environ['SCW_ACCESS_KEY'], aws_secret_access_key=os.environ['SCW_SECRET_KEY'],
                              config=Config(connect_timeout=10, read_timeout=30, retries={'max_attempts': 3}))

    def put(self, batch):
        # Content-addressed: an identical retry overwrites the same object. No plaintext archive.
        self.s3.put_object(Bucket=self.bucket, Key=f'{PREFIX}{batch.device_id}/{batch.id}.json',
                           Body=batch.model_dump_json().encode(), ContentType='application/json')


async def forward(batch):
    endpoint = os.environ['GRAFANA_CLOUD_OTLP_ENDPOINT'].rstrip('/')
    async with httpx.AsyncClient(timeout=30, follow_redirects=False) as client:
        for signal, payload in signals(batch).items():
            response = await client.post(endpoint + '/v1/' + signal, json=payload,
                auth=(os.environ['GRAFANA_CLOUD_INSTANCE_ID'], os.environ['GRAFANA_CLOUD_TOKEN']))
            response.raise_for_status()
            result = response.json() if response.content else {}
            partial = result.get('partialSuccess', {})
            if any(int(partial.get(k, 0)) for k in ('rejectedSpans', 'rejectedLogRecords', 'rejectedDataPoints')):
                raise RuntimeError('collector rejected records')


def create_app(storage=None, sender=forward, device_hashes=None):
    app = FastAPI(docs_url=None, redoc_url=None, openapi_url=None)
    hashes = device_hashes if device_hashes is not None else json.loads(os.environ['SOLVRA_DEVICE_HASHES'])
    # Small per-container burst guard; Modal deployment limits concurrency and container count too.
    recent = {}

    @app.get('/health')
    async def health(): return {'service': 'solvra-telemetry', 'version': 1}

    @app.post('/v1/batches')
    async def ingest(request: Request):
        authorization = request.headers.get('authorization', '')
        digest = hashlib.sha256(authorization.removeprefix('Bearer ').encode()).hexdigest()
        devices = [d for d, h in hashes.items() if hmac.compare_digest(h, digest)] if authorization.startswith('Bearer ') else []
        if not devices: raise HTTPException(401, 'device authentication required')
        now = time.monotonic()
        times = [t for t in recent.get(devices[0], []) if now - t < 60]
        if len(times) >= 120: raise HTTPException(429, 'retry later')
        recent[devices[0]] = times + [now]
        body = bytearray()
        async for part in request.stream():
            body.extend(part)
            if len(body) > MAX_BODY: raise HTTPException(413, 'batch too large')
        try:
            batch = Batch.model_validate_json(body)
            batch.check_encryption()
        except (ValueError, TypeError): raise HTTPException(400, 'invalid telemetry batch')
        if batch.device_id not in devices: raise HTTPException(403, 'device mismatch')
        try:
            from starlette.concurrency import run_in_threadpool
            target = storage or Storage()
            await run_in_threadpool(target.put, batch)
            await sender(batch)
        except Exception:
            # Avoid echoing exception bodies or credentials into cloud logs or HTTP responses.
            raise HTTPException(503, 'delivery incomplete; retry the same batch')
        return {'id': batch.id, 'archived': True, 'metadata_delivered': True}

    return app
