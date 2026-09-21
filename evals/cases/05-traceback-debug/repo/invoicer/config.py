import json, os

DEFAULTS = {"currency": "EUR", "tax": {"rate": 0.2, "inclusive": False}}


def load(path):
    cfg = dict(DEFAULTS)
    if os.path.exists(path):
        with open(path) as f:
            user = json.load(f)
        cfg.update(user)
    return cfg
