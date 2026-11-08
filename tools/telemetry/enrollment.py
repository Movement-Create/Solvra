#!/usr/bin/env python3
"""Private setup download behind Tailscale Serve, bound to loopback. No directory serving.
The authenticated tailnet login must match enrollment.json. Never log credentials or bodies.
"""
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
import json
import time

ROOT = Path.home()/'env/solvra-telemetry'

class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        settings=json.loads((ROOT/'enrollment.json').read_text())
        if self.headers.get('Tailscale-User-Login') != settings['login'] or time.time()>settings['expires']:
            self.send_error(403); return
        if self.path not in ('/mac.json','/windows.json'):
            self.send_error(404); return
        body=(ROOT/self.path.lstrip('/')).read_bytes()
        self.send_response(200)
        self.send_header('Content-Type','application/json')
        self.send_header('Content-Disposition','attachment; filename="'+self.path.lstrip('/')+'"')
        self.send_header('Cache-Control','no-store')
        self.send_header('Content-Length',str(len(body)))
        self.end_headers(); self.wfile.write(body)
    def log_message(self,*args): pass

if __name__=='__main__': HTTPServer(('127.0.0.1',8349),Handler).serve_forever()
