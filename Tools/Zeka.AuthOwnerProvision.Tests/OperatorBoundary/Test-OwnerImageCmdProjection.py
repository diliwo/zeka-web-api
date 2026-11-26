"""Credential-free Docker Go-template and owner-policy Cmd regression fixtures.

The CLI's --host points only at an ephemeral loopback fake API. No Docker Engine,
container, private directory, owner-command execution or third-party package is used.
"""
import argparse
import copy
import http.server
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import threading


class FixtureApi(http.server.BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_HEAD(self):
        self.do_GET()

    def do_GET(self):
        self.server.requests.append(self.path)
        if self.path.endswith('/_ping'):
            body = b'OK'
        elif self.path.endswith('/version'):
            body = json.dumps({'ApiVersion': '1.54', 'MinAPIVersion': '1.44', 'Os': 'linux'}).encode()
        elif re.fullmatch(r'(?:/v[0-9.]+)?/images/[^/]+/json', self.path):
            body = json.dumps(self.server.image).encode()
        else:
            self.server.unexpected.append(self.path)
            self.send_error(404)
            return
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.send_header('API-Version', '1.54')
        self.send_header('OSType', 'linux')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        if self.command != 'HEAD':
            self.wfile.write(body)


def run(argv, env):
    result = subprocess.run(argv, env=env, capture_output=True, text=True, timeout=15, check=False)
    return {'exit': result.returncode, 'stdout': result.stdout.strip(), 'stderr': result.stderr.strip()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report', required=True, type=Path)
    args = parser.parse_args()
    if args.report.exists() or not args.report.parent.is_dir():
        raise RuntimeError('Report destination must exist and the file must be new.')
    repo = Path(__file__).resolve().parents[3]
    wrapper = repo / 'Deployments/local-stack/Invoke-OwnerFirstAdmin.ps1'
    source = wrapper.read_text(encoding='utf-8-sig')
    matches = re.findall(r"^\s*\$imageFormat='([^'\r\n]+)'\s*$", source, re.MULTILINE)
    if len(matches) != 1 or matches[0].count('{{json (index .Config "Cmd")}}') != 1:
        raise RuntimeError('Expected one production image projection using index.')
    image_format = matches[0]
    docker = shutil.which('docker')
    powershell = Path(r'C:\Program Files\PowerShell\7\pwsh.exe')
    if not docker or not powershell.is_file():
        raise RuntimeError('Native Docker CLI and installed PowerShell are required.')
    child_env = {k: v for k, v in os.environ.items() if not k.upper().startswith('DOCKER_')}
    child_env['DOCKER_API_VERSION'] = '1.54'
    child_env['NO_PROXY'] = '127.0.0.1,localhost'
    child_env['no_proxy'] = child_env['NO_PROXY']
    base = {'Id': 'sha256:' + 'c' * 64, 'Config': {'User': 'app', 'WorkingDir': '/app',
        'Entrypoint': ['dotnet', '/app/zeka-auth-owner-provision.dll'], 'Labels': {
            'org.opencontainers.image.revision': 'a' * 40,
            'com.diliwo.zeka.binary-sha256': 'b' * 64,
            'com.diliwo.zeka.scope': 'm1-owner-provision',
            'com.diliwo.zeka.owner': 'windows-codex-m1', 'com.docker.compose.project': 'zeka-m1'}}}
    cases = [('missing', None, True), ('null', None, True), ('empty-array', [], True),
             ('nonempty-array', ['fixture-command'], False), ('nonempty-string', 'fixture-command', False)]
    server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), FixtureApi)
    server.requests = []
    server.unexpected = []
    thread = threading.Thread(target=server.serve_forever, kwargs={'poll_interval': .05}, daemon=True)
    thread.start()
    results = []
    try:
        with tempfile.TemporaryDirectory(prefix='zeka-owner-cmd-fixture-') as temporary:
            temp = Path(temporary)
            config = temp / 'docker-config'
            config.mkdir()
            policy = temp / 'AssertCmd.ps1'
            policy.write_text(r"""param([string]$Wrapper,[string]$InputPath)
$ErrorActionPreference='Stop'
. $Wrapper -DefinitionsOnly
$image=Get-Content -LiteralPath $InputPath -Raw|ConvertFrom-Json
try {
    Assert-OwnerImageFacts $image ('a'*40) ('b'*64) ('sha256:'+('c'*64))
    @{accepted=$true;code='ACCEPTED'}|ConvertTo-Json -Compress
} catch {
    if($_.Exception.Message-ceq'OWNER_IMAGE_PROVENANCE_REJECTED') {
        @{accepted=$false;code='OWNER_IMAGE_PROVENANCE_REJECTED'}|ConvertTo-Json -Compress
        exit 1
    }
    @{accepted=$false;code='UNEXPECTED_FIXTURE_EXCEPTION'}|ConvertTo-Json -Compress
    exit 2
}
""", encoding='utf-8')
            for name, cmd, accepted in cases:
                image = copy.deepcopy(base)
                if name != 'missing':
                    image['Config']['Cmd'] = cmd
                server.image = image
                argv = [docker, '--host', f'tcp://127.0.0.1:{server.server_port}', '--config', str(config),
                        'image', 'inspect', '--format', image_format, image['Id']]
                rendered = run(argv, child_env)
                if name == 'nonempty-string':
                    # Cmd is an array in the Docker API contract. The real CLI rejects a scalar before Go rendering.
                    error = rendered['stderr'].lower()
                    if rendered['exit'] == 0 or 'unmarshal' not in error or 'string' not in error:
                        raise RuntimeError('Native CLI did not refuse malformed scalar Cmd input.')
                    projected = json.loads(json.dumps(image))
                    projection_outcome = 'REFUSED_AT_NATIVE_JSON_PARSER'
                    policy_input = 'parsed synthetic map; not a successful Docker projection'
                else:
                    if rendered['exit'] != 0 or rendered['stderr']:
                        raise RuntimeError('Docker projection failed for controlled case: ' + name)
                    projected = json.loads(rendered['stdout'])
                    projection_outcome = 'RENDERED'
                    policy_input = 'actual Docker CLI projection'
                    expected = None if name == 'missing' else cmd
                    if 'Cmd' not in projected['Config'] or projected['Config']['Cmd'] != expected:
                        raise RuntimeError('Projection changed Cmd value or omitted its key: ' + name)
                input_path = temp / (name + '.json')
                input_path.write_text(json.dumps(projected), encoding='utf-8')
                checked = run([str(powershell), '-NoProfile', '-File', str(policy),
                               '-Wrapper', str(wrapper), '-InputPath', str(input_path)], child_env)
                decision = json.loads(checked['stdout'])
                if checked['stderr'] or decision['accepted'] != accepted or checked['exit'] != (0 if accepted else 1):
                    raise RuntimeError('Owner policy decision mismatch: ' + name)
                results.append({'case': name, 'parsedCmd': projected['Config']['Cmd'],
                                'projectionOutcome': projection_outcome, 'policyInput': policy_input,
                                'dockerExit': rendered['exit'], 'dockerStderr': rendered['stderr'],
                                'policyExit': checked['exit'], 'policyCode': decision['code'], 'passed': True})
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=1)
    if server.unexpected:
        raise RuntimeError('Unexpected fixture API route.')
    report = {'schema': 'zeka-owner-image-cmd-fixture/v1', 'status': 'PASS', 'count': len(results),
              'checks': results, 'renderer': 'installed Docker CLI Go-template renderer',
              'policy': 'unchanged production Assert-OwnerImageFacts via definitions-only import',
              'endpoint': 'ephemeral 127.0.0.1 fixture API only', 'requests': server.requests,
              'realEngineContact': False, 'privateAccess': False, 'ownerCommandExecuted': False}
    args.report.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'status': report['status'], 'count': report['count'], 'report': str(args.report)}))


if __name__ == '__main__':
    main()
