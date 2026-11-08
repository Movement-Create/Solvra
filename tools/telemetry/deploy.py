#!/usr/bin/env python3
"""Provision private per-device configs, then deploy the serverless receiver using existing accounts.
Run in the telemetry venv. Credentials/private keys only live under ~/env/solvra-telemetry.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import subprocess
import sys
import uuid
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.hazmat.primitives import serialization
from environment import cloud_env, read_env

ROOT = Path.home() / 'env' / 'solvra-telemetry'

def private_write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, 'w') as f: f.write(data)
    path.chmod(0o600)

def main():
    ROOT.mkdir(parents=True, exist_ok=True, mode=0o700)
    key_path = ROOT / 'archive-private.pem'
    if not key_path.exists():
        key = rsa.generate_private_key(public_exponent=65537, key_size=3072)
        private_write(key_path, key.private_bytes(serialization.Encoding.PEM,
            serialization.PrivateFormat.PKCS8, serialization.NoEncryption()).decode())
    key = serialization.load_pem_private_key(key_path.read_bytes(), password=None)
    public = key.public_key().public_bytes(serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo).decode()
    configs = {}
    for name in ('mac', 'windows', 'smoke', 'ci'):
        path = ROOT / (name + '.json')
        config = json.loads(path.read_text()) if path.exists() else {
            'endpoint': 'https://not-configured.invalid', 'device_id': uuid.uuid4().hex,
            'token': secrets.token_urlsafe(48), 'public_key': public}
        if config['public_key'] != public: raise RuntimeError('archive key changed; preserve old keys before rotating')
        configs[name] = config
        private_write(path, json.dumps(config))
    env = {**os.environ, **cloud_env(), **read_env(Path.home() / 'env/modal.env')}
    env['SOLVRA_DEVICE_HASHES'] = json.dumps({c['device_id']: hashlib.sha256(c['token'].encode()).hexdigest() for c in configs.values()})
    # modal deploy gets only an explicitly enumerated secret at runtime (see modal_app.py).
    proc = subprocess.run([str(Path.home() / '.local/bin/modal'), 'deploy', str(Path(__file__).with_name('modal_app.py'))],
                          env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    output = proc.stdout
    for value in list(env.values()) + [c['token'] for c in configs.values()]:
        if len(value) >= 16: output = output.replace(value, '[redacted]')
    print(output)
    if proc.returncode: return proc.returncode
    urls = re.findall(r'https://[a-zA-Z0-9.-]+\.modal\.run', proc.stdout)
    if not urls: raise RuntimeError('deployment succeeded but no endpoint URL returned')
    endpoint = urls[-1]
    for name, c in configs.items():
        c['endpoint'] = endpoint
        private_write(ROOT / (name + '.json'), json.dumps(c))
    print('Collector:', endpoint)
    print('Private device files and archive key stored under ~/env/solvra-telemetry; never publish them.')
    return 0

if __name__ == '__main__': sys.exit(main())
