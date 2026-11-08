"""Deploy with tools/telemetry/deploy.py; no credentials or decrypted run content in the image."""
import os
from pathlib import Path
import modal

app = modal.App('solvra-telemetry')
image = (modal.Image.debian_slim(python_version='3.12')
         .pip_install('fastapi==0.115.12', 'httpx==0.28.1', 'boto3==1.38.46')
         .add_local_file(Path(__file__).with_name('receiver.py'), '/root/receiver.py'))
keys = ['SCW_ACCESS_KEY', 'SCW_SECRET_KEY', 'SCW_DEFAULT_REGION', 'LAKE_BUCKET',
        'GRAFANA_CLOUD_OTLP_ENDPOINT', 'GRAFANA_CLOUD_INSTANCE_ID', 'GRAFANA_CLOUD_TOKEN', 'SOLVRA_DEVICE_HASHES']
secret = modal.Secret.from_dict({k: os.environ[k] for k in keys}) if modal.is_local() else modal.Secret.from_dict({})

@app.function(image=image, secrets=[secret], cpu=0.125, memory=256, timeout=120,
              min_containers=0, max_containers=1, scaledown_window=2)
@modal.concurrent(max_inputs=1)
@modal.asgi_app()
def web():
    from receiver import create_app
    return create_app()
