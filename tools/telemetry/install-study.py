#!/usr/bin/env python3
"""Install an isolated Protocol 19 study server and localhost collector on Linux.

Run on the destination as the service user with passwordless sudo. Existing public
server/directory units are never changed. Matching clients use --port explicitly.
The caller stages a self-contained runtime before running this script.
"""
import argparse
import getpass
import json
from pathlib import Path
import secrets
import shutil
import subprocess


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--root',type=Path,required=True);p.add_argument('--runtime',type=Path,required=True)
    p.add_argument('--paths',type=Path,required=True);p.add_argument('--port',type=int,default=27921)
    p.add_argument('--collector-port',type=int,default=8099);p.add_argument('--open-firewall',action='store_true')
    a=p.parse_args();root=a.root.resolve();runtime=a.runtime.resolve();user=getpass.getuser()
    if not 1024<=a.port<=65535 or not 1024<=a.collector_port<=65535:p.error('ports must be 1024–65535')
    if not (runtime/'ProjectPrime').is_file() or not a.paths.is_file():p.error('staged runtime and paths.txt required')
    if any(c in str(root)+str(runtime)+user for c in '\n\r%"'):p.error('unsupported path characters')
    subprocess.run(['sudo','-n','true'],check=True)
    for name in ('state','state/empty-maps','telemetry','collected','reports','tools'):(root/name).mkdir(parents=True,exist_ok=True)
    shutil.copyfile(a.paths,root/'state/paths.txt')
    (root/'state/maprotation.txt').write_text('MP1 SANCTORUS | Battle | 8 | 99\n')
    for name in ('collector.py','report.py'):shutil.copyfile(Path(__file__).with_name(name),root/'tools'/name)
    env=root/'state/telemetry.env'
    if not env.exists():
        token=secrets.token_urlsafe(48)
        env.write_text(f'PRIME_TELEMETRY_TOKEN={token}\nPRIME_TELEMETRY_COLLECTOR_TOKEN={token}\n')
        env.chmod(0o600)
    config={'enabled':True,'detail':'Study','localRaw':True,'upload':True,'directory':str(root/'telemetry'),
            'endpoint':f'http://127.0.0.1:{a.collector_port}/api/net-telemetry/v1/matches','retentionDays':14}
    (root/'state/telemetry.json').write_text(json.dumps(config,indent=2)+'\n')
    q=lambda value:'"'+str(value).replace('\\','\\\\')+'"'
    def service(description,command,extra=''):
        return f'''[Unit]
Description={description}
After=network.target
[Service]
User={user}
WorkingDirectory={runtime}
EnvironmentFile={env}
Environment=PROJECT_PRIME_USER_DATA={q(root/'state')}
Environment=PRIME_TELEMETRY_CONFIG={q(root/'state/telemetry.json')}
ExecStart={command}
Restart=on-failure
RestartSec=5
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ReadWritePaths={q(root)}
{extra}
[Install]
WantedBy=multi-user.target
'''
    units={
      'projectprime-study.service':service('Project Prime Protocol 19 study (anonymous telemetry)',
        f'{q(runtime/"ProjectPrime")} -server -port {a.port} -players 8 -servername "Protocol 19 study: anonymous network telemetry" -nomaster -hostports none -noautoupdate -noupdate -serverreplays off -mapdir {q(root/"state/empty-maps")} -rotation {q(root/"state/maprotation.txt")}'),
      'projectprime-telemetry.service':service('Project Prime anonymous aggregate collector',
        f'/usr/bin/python3 {q(root/"tools/collector.py")} --directory {q(root/"collected")} --listen 127.0.0.1 --port {a.collector_port}'),
      'projectprime-study-report.service':f'''[Unit]
Description=Refresh Project Prime study report
[Service]
Type=oneshot
User={user}
ExecStart=/usr/bin/python3 {q(root/'tools/report.py')} {q(root/'collected')} --output {q(root/'reports/index.html')}
NoNewPrivileges=true
ProtectSystem=strict
ReadWritePaths={q(root/'reports')}
''',
      'projectprime-study-report.timer':'''[Unit]
Description=Refresh Project Prime study report after completed matches
[Timer]
OnBootSec=60
OnUnitActiveSec=60
[Install]
WantedBy=timers.target
'''}
    for name,value in units.items():
        path=root/'state'/name;path.write_text(value)
        subprocess.run(['sudo','-n','install','-m','644',str(path),'/etc/systemd/system/'+name],check=True)
    subprocess.run(['sudo','-n','systemd-analyze','verify',*[str(root/'state'/name) for name in units]],check=True)
    subprocess.run(['sudo','-n','systemctl','daemon-reload'],check=True)
    subprocess.run(['sudo','-n','systemctl','enable','--now','projectprime-telemetry','projectprime-study','projectprime-study-report.timer'],check=True)
    if a.open_firewall:subprocess.run(['sudo','-n','ufw','allow',f'{a.port}/udp','comment','Protocol 19 study'],check=True)
    subprocess.run(['sudo','-n','systemctl','start','projectprime-study-report'],check=True)
    print(f'Study port {a.port}/udp; collector localhost:{a.collector_port}; report {root}/reports/index.html')
    print('Rollback: sudo systemctl disable --now projectprime-study projectprime-telemetry projectprime-study-report.timer')
    print(f'Remove added firewall rule if enabled: sudo ufw delete allow {a.port}/udp')

if __name__=='__main__':main()
