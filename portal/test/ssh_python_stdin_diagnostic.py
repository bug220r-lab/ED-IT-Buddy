"""Bounded native echo test only: no Buddy/MAGIX actions."""
import json
from pathlib import Path
import subprocess
import threading
import time

config=json.loads((Path(__file__).parents[1]/'config.example.json').read_text())
remote=r'C:\Users\jlewi\AppData\Local\ED-IT Buddy\portal\transport-diagnostic-echo\EchoBridge.exe'
command=[config['sshExecutable'],'-T','-i',config['identityFile'],'-o','UserKnownHostsFile='+config['knownHostsFile'],'-o','HostKeyAlias='+config['hostKeyAlias'],'-o','IdentitiesOnly=yes','-o','BatchMode=yes','-o','StrictHostKeyChecking=yes',config['host'],'"'+remote+'"']
started=time.monotonic()
child=subprocess.Popen(command,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,creationflags=subprocess.CREATE_NO_WINDOW)
events=[]
def read_output():
    for line in child.stdout:
        events.append({'ms':round((time.monotonic()-started)*1000),'text':line.decode()})
thread=threading.Thread(target=read_output,daemon=True);thread.start()
child.stdin.write(b'python-probe-one\r\n');child.stdin.flush()
time.sleep(1.5)
child.stdin.write(b'python-probe-two\r\n');child.stdin.flush()
time.sleep(1)
child.stdin.close()
code=child.wait(timeout=8)
thread.join(1)
print(json.dumps({'mode':'python-anonymous-pipe','code':code,'elapsedMs':round((time.monotonic()-started)*1000),'events':events,'stderr':child.stderr.read().decode()}))
