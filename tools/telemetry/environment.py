"""Read explicit secret files without shell evaluation. Never print their contents."""
import os
import shlex
from pathlib import Path

def read_env(path):
    result = {}
    for line in Path(path).read_text().splitlines():
        line = line.strip().removeprefix('export ')
        if not line or line.startswith('#'): continue
        key, sep, raw = line.partition('=')
        if not sep: raise ValueError('invalid environment file')
        words = shlex.split(raw, comments=True)
        if len(words) > 1: raise ValueError('quote environment values containing spaces')
        result[key.strip()] = words[0] if words else ''
    return result

def cloud_env():
    root = Path.home() / 'env'
    result = {}
    for name in ('scaleway.env', 'grafana-cloud.env'):
        result.update(read_env(root / name))
    return result
