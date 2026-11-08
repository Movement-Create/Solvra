import base64
import hashlib
import json
import pytest
from fastapi.testclient import TestClient
from cryptography.hazmat.primitives.asymmetric import rsa, padding
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from receiver import create_app, Batch, signals
from graph_sync import decrypt, project

DEVICE = 'a'*32
TOKEN = 'private-device-token-for-tests-only'
KEY = rsa.generate_private_key(public_exponent=65537, key_size=2048)


def batch():
    raw = b'{"type":"assistant_turn","data":{"content":[{"type":"tool_use","id":"call1","name":"decide","input":{"question":"private-question"}}]}}\n'
    payload = json.dumps({'source':'sessions/session1.jsonl','offset':0,'data':base64.b64encode(raw).decode()}).encode()
    key, nonce = b'x'*32, b'y'*12
    cipher = AESGCM(key).encrypt(nonce, payload, DEVICE.encode())
    wrap = KEY.public_key().encrypt(key,padding.OAEP(mgf=padding.MGF1(hashes.SHA256()),algorithm=hashes.SHA256(),label=None))
    return {'version':1,'device_id':DEVICE,'id':hashlib.sha256(cipher[:-16]).hexdigest(),
            'envelope':{k:base64.b64encode(v).decode() for k,v in {'key':wrap,'nonce':nonce,'ciphertext':cipher[:-16],'tag':cipher[-16:]}.items()},
            'metadata':[{'name':'tool.execute','kind':'span','time':100,'start':10,'trace':'b'*16,'span':'c'*16,'parent':None,'numbers':{'duration_ms':90}}]}


class Store:
    def __init__(self): self.items={}
    def put(self,b): self.items[b.id]=b


def client(store=None, sender=None):
    async def ok(_): pass
    return TestClient(create_app(store or Store(),sender or ok,{DEVICE:hashlib.sha256(TOKEN.encode()).hexdigest()}))


def test_requires_auth_and_matching_device():
    c=client()
    assert c.post('/v1/batches',json=batch()).status_code==401
    b=batch(); b['device_id']='d'*32
    assert c.post('/v1/batches',json=b,headers={'authorization':'Bearer '+TOKEN}).status_code==403


def test_duplicate_delivery_keeps_one_archive_and_acknowledges():
    store=Store(); c=client(store)
    b=batch()
    for _ in range(2):
        r=c.post('/v1/batches',json=b,headers={'authorization':'Bearer '+TOKEN})
        assert r.status_code==200
        assert r.json()=={'id':b['id'],'archived':True,'metadata_delivered':True}
    assert len(store.items)==1
    assert 'private-question' not in json.dumps(b)


def test_archive_survives_grafana_failure_and_client_must_retry():
    store=Store()
    async def fail(_): raise RuntimeError('sensitive upstream error')
    r=client(store,fail).post('/v1/batches',json=batch(),headers={'authorization':'Bearer '+TOKEN})
    assert r.status_code==503 and len(store.items)==1
    assert 'sensitive' not in r.text


@pytest.mark.parametrize('change',[lambda b:b['metadata'][0]['numbers'].update(prompt='private'),
    lambda b:b['metadata'][0].update(name='private-prompt'), lambda b:b.update(id='0'*64),
    lambda b:b['envelope'].update(nonce='invalid')])
def test_rejects_plaintext_and_tampering(change):
    b=batch(); change(b)
    assert client().post('/v1/batches',json=b,headers={'authorization':'Bearer '+TOKEN}).status_code==400


def test_encryption_graph_projection_and_otlp_ids():
    b=Batch.model_validate(batch())
    source,offset,raw=decrypt(b,KEY)
    rows=project(DEVICE,source,offset,raw)
    assert rows[0]['calls'][0]['name']=='decide'
    assert 'private-question' in rows[0]['calls'][0]['input']
    assert rows==project(DEVICE,source,offset,raw) # deterministic dedup keys
    span=signals(b)['traces']['resourceSpans'][0]['scopeSpans'][0]['spans'][0]
    assert len(span['traceId'])==32
    assert span['spanId']=='c'*16
    assert 'private-question' not in json.dumps(signals(b))
