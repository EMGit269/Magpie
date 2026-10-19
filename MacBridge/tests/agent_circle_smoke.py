"""Real model -> host tools -> valid Circle geometry, on an isolated Rhino canvas.

Requires a configured Agent service. Also verifies the direct Grasshopper menu.
"""
import argparse
import json
from pathlib import Path
import subprocess
import tempfile
import time
import urllib.request
import uuid

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--rhino', required=True)
parser.add_argument('--assembly', type=Path, default=Path(__file__).resolve().parents[1] / 'bin/Release/net8.0/Magpie.MacBridge.gha')
parser.add_argument('--rhinocode', default='/Applications/Rhino 8.app/Contents/Resources/bin/rhinocode')
parser.add_argument('--service', default='http://127.0.0.1:8123')
parser.add_argument('--report', type=Path, help='Save tool events and actual output for diagnosis')
args = parser.parse_args()
session = 'circle-smoke-' + uuid.uuid4().hex

with tempfile.TemporaryDirectory(prefix='magpie-circle-') as temporary:
    directory = Path(temporary)
    script, output = directory / 'probe.py', directory / 'result.json'

    def run(code):
        output.unlink(missing_ok=True)
        script.write_text('#! python 3\nimport json, traceback\nimport scriptcontext as sc\ntry:\n' +
                          '\n'.join('    ' + line for line in code.splitlines()) +
                          '\n    data = {"ok": True, "result": result}\nexcept BaseException:\n' +
                          '    data = {"ok": False, "error": traceback.format_exc()}\n' +
                          'open(' + repr(str(output)) + ', "w").write(json.dumps(data))\n')
        subprocess.run([args.rhinocode, '-r', args.rhino, 'script', str(script)], check=True, timeout=30,
                       stdout=subprocess.DEVNULL)
        deadline = time.monotonic() + 20
        while not output.exists() and time.monotonic() < deadline:
            time.sleep(0.1)
        data = json.loads(output.read_text())
        if not data['ok']:
            raise RuntimeError(data['error'])
        return data['result']

    try:
        document = run('''import clr
import Rhino
from System import AppDomain
from System.Reflection import Assembly, BindingFlags
clr.AddReference('Grasshopper')
from Grasshopper import Instances
from Grasshopper.Kernel import GH_Document
assert Instances.ActiveCanvas is not None
assert 'magpie_circle_document' not in sc.sticky, 'Previous test was not cleaned up'
assembly = Assembly.LoadFrom(''' + repr(str(args.assembly.resolve())) + ''')
for old in AppDomain.CurrentDomain.GetAssemblies():
    if old == assembly or not old.GetName().Name.startswith('Magpie.MacBridge'): continue
    t = old.GetType('Magpie.MacBridge.MacAgentWindow')
    if t:
        w = t.GetField('_current', BindingFlags.Static | BindingFlags.NonPublic).GetValue(None)
        if w: w.Close()
    b = old.GetType('Magpie.MacBridge.Bridge')
    if b: b.GetMethod('Stop').Invoke(None, None)
assembly.GetType('Magpie.MacBridge.Bridge').GetMethod('Start').Invoke(None, None)
sc.sticky['magpie_circle_previous'] = Instances.ActiveCanvas.Document
sc.sticky['magpie_circle_document'] = GH_Document()
Instances.ActiveCanvas.Document = sc.sticky['magpie_circle_document']
menu = next(i for i in Instances.DocumentEditor.MainMenuStrip.Items if i.Name == 'Magpie.AgentMenu')
entry = next(i for i in menu.DropDownItems if i.Name == 'Magpie.OpenAgent')
entry.PerformClick()
t = assembly.GetType('Magpie.MacBridge.MacAgentWindow')
w = t.GetField('_current', BindingFlags.Static | BindingFlags.NonPublic).GetValue(None)
sc.sticky['magpie_circle_window'] = w
assert w is not None and w.Visible, 'Menu did not open the Agent'
entry.PerformClick()
assert w == t.GetField('_current', BindingFlags.Static | BindingFlags.NonPublic).GetValue(None), 'Menu duplicated the window'
result = {'document_id': str(Instances.ActiveCanvas.Document.DocumentID)}''')
        print('PASS: Magpie > 打开 Agent opens and reuses the native window', flush=True)
        payload = {'session_id': session, 'user_input': '画一个随机的圆，直接在当前 Grasshopper 画布完成。',
                   'document_id': document['document_id'],
                   'canvas_context': {**document, 'name': 'Circle integration test', 'component_count': 0}}
        request = urllib.request.Request(args.service + '/graph/invoke/stream', data=json.dumps(payload).encode(),
                                         headers={'Content-Type': 'application/json'})
        events = []
        with urllib.request.urlopen(request, timeout=240) as response:
            for line in response:
                if not line.startswith(b'data: '): continue
                event = json.loads(line[6:])
                assert event['type'] != 'error', event
                if event['type'] != 'agent_event': continue
                value = event['payload']
                events.append(value)
                if value['type'] in ('tool_call', 'tool_result', 'final'):
                    print(value['type'], value.get('tool'), value.get('status'),
                          value.get('text', '')[:400] if value['type'] == 'final' or value.get('status') == 'failed' else '', flush=True)
        assert any(e['type'] == 'final' and e['status'] == 'completed' for e in events), events
        assert any(e['tool'] == 'create_component_graph' and e['type'] == 'tool_result' and e['status'] == 'completed'
                   for e in events), 'Model did not create geometry'
        geometry = run('''import Rhino
from Grasshopper import Instances
from Grasshopper.Kernel import IGH_Component, GH_RuntimeMessageLevel
doc = sc.sticky['magpie_circle_document']
assert Instances.ActiveCanvas.Document == doc
doc.NewSolution(False)
circles, errors = [], []
for obj in doc.Objects:
    if not isinstance(obj, IGH_Component): continue
    errors.extend(str(e) for e in obj.RuntimeMessages(GH_RuntimeMessageLevel.Error))
    for port in obj.Params.Output:
        for goo in port.VolatileData.AllData(True):
            value = goo.ScriptVariable()
            if isinstance(value, Rhino.Geometry.Circle):
                circle = value
            elif isinstance(value, Rhino.Geometry.Curve):
                ok, circle = value.TryGetCircle()
                if not ok: continue
            else: continue
            if circle.IsValid and circle.Radius > 0:
                circles.append({'radius': circle.Radius, 'center': [circle.Center.X, circle.Center.Y, circle.Center.Z],
                                'component': obj.Name})
result = {'circles': circles, 'errors': errors, 'objects': doc.ObjectCount}
assert circles, 'No valid Circle output: ' + str(result)
assert not errors, result''')
        if args.report:
            args.report.write_text(json.dumps({'events': events, 'geometry': geometry}, ensure_ascii=False, indent=2))
        assert len(geometry['circles']) == 1, 'Expected exactly one circle: ' + str(geometry)
        print('PASS: actual Grasshopper output = ' + json.dumps(geometry), flush=True)
    finally:
        run('''from Grasshopper import Instances
w = sc.sticky.pop('magpie_circle_window', None)
if w: w.Close()
doc = sc.sticky.pop('magpie_circle_document', None)
if doc:
    if Instances.ActiveCanvas.Document == doc:
        Instances.ActiveCanvas.Document = sc.sticky.get('magpie_circle_previous')
    doc.Dispose()
sc.sticky.pop('magpie_circle_previous', None)
result = True''')
        request = urllib.request.Request(args.service + '/sessions/' + session, method='DELETE')
        with urllib.request.urlopen(request, timeout=10):
            pass
