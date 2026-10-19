"""Check Mac palette layering and duplicate-build reuse without modifying the canvas.

Pass a uniquely named build when Rhino already has an older assembly loaded.
"""
import argparse
import json
from pathlib import Path
import socket
import subprocess
import tempfile
import time
import urllib.request

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--rhino', required=True)
parser.add_argument('--assembly', type=Path, required=True)
parser.add_argument('--rhinocode', default='/Applications/Rhino 8.app/Contents/Resources/bin/rhinocode')
args = parser.parse_args()

with tempfile.TemporaryDirectory(prefix='magpie-window-smoke-') as temporary:
    directory = Path(temporary)
    output = directory / 'result.json'
    script = directory / 'probe.py'

    def run(code):
        output.unlink(missing_ok=True)
        script.write_text('#! python 3\nimport json, traceback\ntry:\n' +
                          '\n'.join('    ' + line for line in code.splitlines()) +
                          '\n    data = {"ok": True, "result": result}\nexcept BaseException:\n' +
                          '    data = {"ok": False, "error": traceback.format_exc()}\n' +
                          'open(' + repr(str(output)) + ', "w").write(json.dumps(data))\n')
        subprocess.run([args.rhinocode, '-r', args.rhino, 'script', str(script)],
                       check=True, timeout=30, stdout=subprocess.DEVNULL)
        deadline = time.monotonic() + 20
        while not output.exists() and time.monotonic() < deadline:
            time.sleep(0.1)
        data = json.loads(output.read_text())
        if not data['ok']:
            raise RuntimeError(data['error'])
        return data['result']

    common = '''import clr
clr.AddReference('Grasshopper')
clr.AddReference('Microsoft.macOS')
from AppKit import NSApplication
from Grasshopper import Instances
from System import AppDomain, Convert, Environment, Object
from System.Reflection import Assembly, BindingFlags
assembly = Assembly.LoadFrom(''' + repr(str(args.assembly.resolve())) + ''')
bridge = assembly.GetType('Magpie.MacBridge.Bridge')
window_type = assembly.GetType('Magpie.MacBridge.MacAgentWindow')
flags = BindingFlags.NonPublic | BindingFlags.Static
'''
    result = run(common + '''doc = Instances.ActiveCanvas.Document
before = (str(doc.DocumentID), doc.ObjectCount)
existing = AppDomain.CurrentDomain.GetData('Magpie.MacBridge.AgentWindow')
if existing is None:
    for old in AppDomain.CurrentDomain.GetAssemblies():
        if not old.GetName().Name.startswith('Magpie.MacBridge'): continue
        t = old.GetType('Magpie.MacBridge.MacAgentWindow')
        candidate = t.GetField('_current', flags).GetValue(None) if t else None
        if candidate is not None:
            existing = candidate
            break
window_type.GetMethod('ShowAgent').Invoke(None, None)
window = AppDomain.CurrentDomain.GetData('Magpie.MacBridge.AgentWindow')
assert window is not None
assert existing is None or Object.ReferenceEquals(existing, window), 'Existing conversation replaced'
session_field = window.GetType().GetField('_session', BindingFlags.NonPublic | BindingFlags.Instance)
session = session_field.GetValue(window)
for _ in range(3):
    window_type.GetMethod('ShowAgent').Invoke(None, None)
    assert Object.ReferenceEquals(window, AppDomain.CurrentDomain.GetData('Magpie.MacBridge.AgentWindow'))
assert session_field.GetValue(window) == session, 'Conversation reset on repeated opening'
native = window.ControlObject
gh = next(w for w in NSApplication.SharedApplication.DangerousWindows if w.Title.startswith('Grasshopper -'))
gh.MakeKeyAndOrderFront(None)
assert gh.IsKeyWindow and not native.IsKeyWindow, 'Agent stole canvas keyboard focus'
assert window.Topmost and native.HidesOnDeactivate
assert Convert.ToInt64(native.Level) > Convert.ToInt64(gh.Level), 'GH can cover Agent'
assert Convert.ToInt64(native.Level) < 8, 'Agent should stay below modal dialogs'
assert before == (str(Instances.ActiveCanvas.Document.DocumentID), Instances.ActiveCanvas.Document.ObjectCount)
assert bridge.GetProperty('IsRunning').GetValue(None)
port = bridge.GetProperty('Port').GetValue(None)
owned_runtime = bridge.GetField('_runtime', flags).GetValue(None)
result = {'port':port, 'agent_level':Convert.ToInt64(native.Level), 'gh_level':Convert.ToInt64(gh.Level),
          'reused_window':existing is not None, 'reused_bridge':owned_runtime is None}
''')
    with urllib.request.urlopen(f'http://127.0.0.1:{result["port"]}/health', timeout=10) as response:
        assert json.load(response)['service'] == 'magpie-host-bridge'
    print('PASS: repeated opening retains the conversation and canvas; bridge healthy', flush=True)
    print(f'PASS: Agent level {result["agent_level"]} > GH level {result["gh_level"]}; GH retains keyboard focus', flush=True)

    # A foreign listener must remain an error, never be accepted as our bridge.
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        listener.listen()
        foreign_port = listener.getsockname()[1]
        run(common + f'''saved = Environment.GetEnvironmentVariable('MAGPIE_HOST_BRIDGE_PORT')
try:
    Environment.SetEnvironmentVariable('MAGPIE_HOST_BRIDGE_PORT', '{foreign_port}')
    rejected = False
    try:
        bridge.GetMethod('Start').Invoke(None, None)
    except Exception:
        rejected = True
    assert rejected, 'Accepted a foreign listener'
finally:
    Environment.SetEnvironmentVariable('MAGPIE_HOST_BRIDGE_PORT', '{result["port"]}')
    bridge.GetMethod('Start').Invoke(None, None)
    Environment.SetEnvironmentVariable('MAGPIE_HOST_BRIDGE_PORT', saved)
assert bridge.GetProperty('IsRunning').GetValue(None)
result = True
''')
    print('PASS: foreign port conflict rejected; original bridge restored', flush=True)
