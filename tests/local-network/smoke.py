"""Real standalone agent HTTP checks. Never probes the user's LAN or external services."""
import json
import os
import pathlib
import secrets
import socket
import subprocess
import time
import urllib.error
import urllib.request

root=pathlib.Path(__file__).resolve().parents[2]
runtime=os.getenv('JARVIS_DOTNET','dotnet')
dll=root/'services/local-network/bin/Release/net10.0/Jarvis.LocalNetwork.dll'
assert dll.is_file(), 'Build services/local-network/Jarvis.LocalNetwork.csproj -c Release first'
token=secrets.token_hex(32)

def run(enabled):
    with socket.socket() as sock:
        sock.bind(('127.0.0.1',0))
        port=sock.getsockname()[1]
    base='http://127.0.0.1:'+str(port)
    env={**os.environ,'ASPNETCORE_URLS':base,'LOCAL_NETWORK_TOKEN':token,
         'LOCAL_NETWORK_ENABLED':str(enabled).lower(),'LOCAL_NETWORK_ALLOWLIST':'192.168.178.0/24',
         'LOCAL_NETWORK_PORTS':'80,443','LOCAL_NETWORK_PROBES_ENABLED':'false','Logging__LogLevel__Default':'Warning'}
    flags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0
    proc=subprocess.Popen([runtime,str(dll)],env=env,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,creationflags=flags)
    try:
        def request(path,body=None,auth=True):
            headers={'Content-Type':'application/json'}
            if auth: headers['X-Service-Token']=token
            req=urllib.request.Request(base+path,data=None if body is None else json.dumps(body).encode(),headers=headers)
            try:
                with urllib.request.urlopen(req,timeout=5) as result:return result.status,result.read()
            except urllib.error.HTTPError as e:return e.code,e.read()
        for attempt in range(60):
            try:
                if request('/health/live',auth=False)[0]==200:break
            except (OSError,urllib.error.URLError):pass
            assert proc.poll() is None, 'Agent startup failed'
            time.sleep(0.1)
        else:raise AssertionError('Agent startup timed out')
        assert request('/policy',auth=False)[0]==401
        status,body=request('/policy');assert status==200
        assert json.loads(body)['enabled']==enabled and token.encode() not in body
        blocked={'url':'http://127.0.0.1/','allowlist':['192.168.178.0/24'],'ports':[80]}
        assert request('/execute',blocked)[0]==403
        # Third authenticated request exceeds the burst allowance before any outbound connection.
        assert request('/execute',blocked)[0]==429
        print('PASS: real agent HTTP, service auth, '+('loopback policy' if enabled else 'disabled by default')+', rate limit; no LAN probe')
    finally:
        proc.terminate()
        try:proc.wait(timeout=5)
        except subprocess.TimeoutExpired:proc.kill();proc.wait(timeout=5)

run(False)
run(True)
