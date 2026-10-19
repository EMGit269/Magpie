"""Exercise the actual Eto/WebKit chat UI and real model inside a running Rhino.

Requires a configured model. Uses a temporary GH canvas and cleans up test sessions.
"""
import argparse
import json
from pathlib import Path
import subprocess
import tempfile
import time
import urllib.request

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--rhino', required=True)
parser.add_argument('--assembly', type=Path, default=Path(__file__).resolve().parents[1] / 'bin/Release/net8.0/Magpie.MacBridge.gha')
parser.add_argument('--rhinocode', default='/Applications/Rhino 8.app/Contents/Resources/bin/rhinocode')
args = parser.parse_args()

with tempfile.TemporaryDirectory(prefix='magpie-ui-smoke-') as temporary:
    directory = Path(temporary)
    output = directory / 'result.json'
    script = directory / 'probe.py'
    sessions = set()

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

    def javascript(code):
        run('web = sc.sticky["magpie_ui_smoke_web"]\nweb.ExecuteScript(' + repr(code) + ')\nresult = True')

    def snapshot(expression):
        marker = str(time.monotonic()) + ':'
        javascript('document.title = ' + json.dumps(marker) + ' + JSON.stringify(' + expression + ')')
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            title = run('result = sc.sticky["magpie_ui_smoke_web"].DocumentTitle')
            if title and title.startswith(marker):
                return json.loads(title[len(marker):])
            time.sleep(0.1)
        raise RuntimeError('WebView did not execute the probe')

    def state():
        return snapshot('({ready:state.serviceReady,bridge:state.hostBridgeReady,sending:state.sending,'
                        'session:state.sessionId,errors:[...document.querySelectorAll(".bubble.error")].map(n=>n.textContent.slice(0,120)),'
                        'answers:[...document.querySelectorAll(".bubble.assistant")].map(n=>n.textContent.slice(0,120)),'
                        'tools:[...document.querySelectorAll(".agent-event.tool_result")].map(n=>n.dataset.tool)})')

    def wait_for(predicate, timeout=60):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            value = state()
            if predicate(value):
                return value
            time.sleep(0.5)
        raise AssertionError('UI condition timed out: ' + json.dumps(value, ensure_ascii=False))

    try:
        run('''import clr
import Rhino
from System import AppDomain
from System.Reflection import Assembly, BindingFlags
Rhino.RhinoApp.RunScript('_Grasshopper', False)
clr.AddReference('Grasshopper')
from Grasshopper import Instances
from Grasshopper.Kernel import GH_Document
assembly = Assembly.LoadFrom(''' + repr(str(args.assembly.resolve())) + ''')
for old in AppDomain.CurrentDomain.GetAssemblies():
    if old == assembly or not old.GetName().Name.startswith('Magpie.MacBridge'):
        continue
    window_type = old.GetType('Magpie.MacBridge.MacAgentWindow')
    if window_type:
        window = window_type.GetField('_current', BindingFlags.Static | BindingFlags.NonPublic).GetValue(None)
        if window: window.Close()
    bridge = old.GetType('Magpie.MacBridge.Bridge')
    if bridge: bridge.GetMethod('Stop').Invoke(None, None)
assert 'magpie_ui_smoke_document' not in sc.sticky, 'Previous UI test not cleaned up'
sc.sticky['magpie_ui_smoke_previous'] = Instances.ActiveCanvas.Document
sc.sticky['magpie_ui_smoke_document'] = GH_Document()
Instances.ActiveCanvas.Document = sc.sticky['magpie_ui_smoke_document']
t = assembly.GetType('Magpie.MacBridge.MacAgentWindow')
t.GetMethod('ShowAgent').Invoke(None, None)
w = t.GetField('_current', BindingFlags.Static | BindingFlags.NonPublic).GetValue(None)
sc.sticky['magpie_ui_smoke_window'] = w
sc.sticky['magpie_ui_smoke_web'] = t.GetField('_web', BindingFlags.Instance | BindingFlags.NonPublic).GetValue(w)
result = True''')
        initial = wait_for(lambda s: s['ready'] and s['bridge'])
        assert not initial['errors'], initial
        sessions.add(initial['session'])
        assert snapshot('document.getElementById("model-provider").options.length') == 4
        assert snapshot('document.getElementById("model-api-key").value') == ''
        assert snapshot('document.querySelector(".hint").textContent') == '⌘ + Enter to send'
        print('PASS: native window, WebKit transport, service status, providers and Mac shortcut', flush=True)

        # Exercise retained-key settings without exposing or changing the API key.
        javascript('document.getElementById("save-model-settings").click()')
        wait_for(lambda s: not s['errors'])
        assert snapshot('document.getElementById("model-api-key").value') == ''

        # Render model output as text, even if it contains HTML.
        safe = snapshot('(() => { const card=renderEventCard({type:"tool_result",text:"<img src=x onerror=alert(1)>"});'
                        'return card.querySelector("img")===null && card.textContent.includes("<img"); })()')
        assert safe, 'Tool output was interpreted as HTML'
        print('PASS: model settings retain key; tool content is rendered as plain text', flush=True)

        # Deterministic multi-stage replay: identical tool names may complete out of order.
        fixture = [
            {"type": "assistant_delta", "text": "先检查画布。"},
            {"type": "tool_call", "tool": "get_canvas_summary", "status": "running", "data": {"call_id": "a"}},
            {"type": "tool_call", "tool": "get_canvas_summary", "status": "running", "data": {"call_id": "b"}},
            {"type": "tool_result", "tool": "get_canvas_summary", "status": "completed", "text": "second", "data": {"call_id": "b"}},
            {"type": "tool_result", "tool": "get_canvas_summary", "status": "completed", "text": "first", "data": {"call_id": "a"}},
            {"type": "assistant_delta", "text": "## 结果\n\n**完成**\n\n- 一项\n\n```python\nx = 1\n```\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n<img src=x onerror=alert(1)>[bad](javascript:alert(1))"},
            {"type": "final"},
        ]
        javascript('(' + json.dumps(fixture) + ').forEach(appendAgentEvent)')
        replay = snapshot('({order:[...messages.children].filter(n=>!n.classList.contains("empty")).map(n=>n.className),'
                          'tools:[...messages.querySelectorAll(".tool_result")].map(n=>({id:n.dataset.callId,text:n.textContent,open:n.open})),'
                          'rich:!!messages.querySelector("h2")&&!!messages.querySelector("pre code")&&!!messages.querySelector("table")&&!!messages.querySelector("strong"),'
                          'unsafe:!!messages.querySelector("img,script,[onerror],a[href^=javascript]"),'
                          'streaming:!!messages.querySelector(".streaming")})')
        assert replay['order'] == ['bubble assistant', 'agent-block', 'bubble assistant'], replay
        assert [t['id'] for t in replay['tools']] == ['a', 'b'], replay
        assert 'first' in replay['tools'][0]['text'] and 'second' in replay['tools'][1]['text'], replay
        assert all(not t['open'] for t in replay['tools']) and replay['rich'] and not replay['unsafe'] and not replay['streaming'], replay
        # A failed final must not mark the pending plan or tool as completed.
        javascript('appendAgentEvent({type:"plan",status:"running",text:"准备执行"});'
                   'appendAgentEvent({type:"tool_call",tool:"search_component_catalog",status:"running",data:{call_id:"limit"}});'
                   'appendAgentEvent({type:"final",status:"failed",text:"本次任务尚未完成：已达到本轮执行次数上限。"})')
        stopped = snapshot('({plan:messages.querySelector(".plan .agent-event-status").textContent,'
                           'tool:messages.querySelector("[data-call-id=limit] .agent-event-status").textContent,'
                           'completed:[...messages.querySelectorAll(".tool_result .agent-event-status")].map(n=>n.textContent)})')
        assert stopped['plan'] == stopped['tool'] == '已中断', stopped
        assert stopped['completed'] == ['已完成', '已完成'], stopped
        assert snapshot('(() => { state.sending=true; state.acked=true; resetWatchdog();'
                        'const unlimited=state.watchdog===null && state.sending; setSending(false); return unlimited; })()')
        assert snapshot('!!state.canvas?.matches && !document.getElementById("canvas-reference").hidden')
        # Long multilingual payloads must traverse all URI chunks without breaking the channel.
        javascript('post("theme_changed",{mode:"dark",extra:"中文😀".repeat(12000)})')
        deadline = time.monotonic() + 20
        while snapshot('state.themeMode') != 'dark':
            assert time.monotonic() < deadline, 'Chunked Unicode transport stalled'
        javascript('post("theme_changed",{mode:"system"}); document.getElementById("new-chat").click()')
        fresh = wait_for(lambda s: s['session'] != initial['session'])
        sessions.add(fresh['session'])
        initial = fresh
        print('PASS: Markdown and sanitization, ordered stages, concurrent same-name tools, collapsed details, Unicode transport', flush=True)

        javascript('document.getElementById("input").value="请先调用 get_canvas_summary 工具，再用一句中文报告当前画布组件数量。不要修改画布。";'
                   'document.getElementById("send").click()')
        reply = wait_for(lambda s: bool(s['answers']) and not s['sending'], 120)
        assert not reply['errors'], reply
        assert any('get_canvas_summary' in tool for tool in reply['tools']), reply
        print('PASS: send button → streaming Agent → Grasshopper tool → rendered reply', flush=True)

        javascript('document.getElementById("toggle-history").click()')
        history = snapshot('document.getElementById("history-list").children.length')
        assert history, 'Conversation history was empty'
        javascript('post("history_rename", {sessionId:state.sessionId,title:"Mac UI smoke conversation"})')
        time.sleep(1)
        javascript('document.getElementById("new-chat").click()')
        new = wait_for(lambda s: s['session'] != initial['session'])
        sessions.add(new['session'])
        javascript('post("history_load", {sessionId:' + json.dumps(initial['session']) + '})')
        loaded = wait_for(lambda s: s['session'] == initial['session'] and bool(s['answers']))
        assert not loaded['errors'], loaded
        print('PASS: history list, rename, new conversation and reload', flush=True)

        javascript('document.getElementById("input").value="请写一篇关于参数化建模的长文。";'
                   'document.getElementById("send").click()')
        wait_for(lambda s: s['sending'])
        javascript('document.getElementById("send").click()')
        wait_for(lambda s: not s['sending'])
        print('PASS: stop-generation button cancels an active stream', flush=True)

        # Switch canvases while preserving the conversation binding. HTTP calls must be rejected too.
        old_binding = snapshot('state.canvas.bound.id')
        run("from Grasshopper import Instances\nfrom Grasshopper.Kernel import GH_Document\nsc.sticky['magpie_ui_other_document'] = GH_Document()\nInstances.ActiveCanvas.Document=sc.sticky['magpie_ui_other_document']\nresult=True")
        time.sleep(1.5)
        assert snapshot('state.canvas.matches') is False
        req = urllib.request.Request('http://127.0.0.1:8765/tools/invoke',
              data=json.dumps({'document_id': old_binding, 'tool': 'create_component_graph', 'args': {'components': [{'name':'Panel','x':0,'y':0}], 'connections': []}}).encode(),
              headers={'Content-Type':'application/json'})
        with urllib.request.urlopen(req, timeout=15) as response:
            blocked = json.load(response)
        assert blocked['status'] == 'error' and 'binding mismatch' in blocked['error'], blocked
        assert run("result=sc.sticky['magpie_ui_other_document'].Objects.Count") == 0
        # Returning to a bound canvas restores the same session; explicit reference to another starts a fresh one.
        run("from Grasshopper import Instances\nInstances.ActiveCanvas.Document=sc.sticky['magpie_ui_smoke_document']\nresult=True")
        time.sleep(1.5)
        assert snapshot('state.canvas.matches') is True
        assert snapshot('state.sessionId') == initial['session']
        run("from Grasshopper import Instances\nInstances.ActiveCanvas.Document=sc.sticky['magpie_ui_other_document']\nresult=True")
        javascript('document.getElementById("reference-canvas").click()')
        rebound = wait_for(lambda s: s['session'] != initial['session'])
        sessions.add(rebound['session'])
        assert snapshot('state.canvas.matches') is True
        assert snapshot('messages.querySelectorAll(".bubble.assistant").length') == 0
        # Reloading old history restores its original canvas reference.
        javascript('post("history_load",{sessionId:' + json.dumps(initial['session']) + '})')
        wait_for(lambda s: s['session'] == initial['session'] and bool(s['answers']))
        assert snapshot('state.canvas.bound.id') == old_binding
        assert snapshot('state.canvas.matches') is False
        run("from Grasshopper import Instances\nInstances.ActiveCanvas.Document=sc.sticky['magpie_ui_smoke_document']\nsc.sticky.pop('magpie_ui_other_document').Dispose()\nresult=True")
        print('PASS: canvas reference, switch guard at HTTP mutation boundary, return/rebind and history binding', flush=True)

        javascript('post("history_delete", {sessionId:state.sessionId})')
        wait_for(lambda s: s['session'] != initial['session'])
        print('PASS: delete conversation and reset active session', flush=True)
    finally:
        try:
            # Leave the usable conversation window open for the user, with a fresh chat.
            javascript('document.getElementById("new-chat").click(); document.title="Magpie"')
            run('''from Grasshopper import Instances
if 'magpie_ui_smoke_document' in sc.sticky:
    doc=sc.sticky.pop('magpie_ui_smoke_document')
    previous=sc.sticky.pop('magpie_ui_smoke_previous', None)
    if Instances.ActiveCanvas.Document == doc or Instances.ActiveCanvas.Document == sc.sticky.get('magpie_ui_other_document'):
        Instances.ActiveCanvas.Document=previous
    doc.Dispose()
if 'magpie_ui_other_document' in sc.sticky:
    sc.sticky.pop('magpie_ui_other_document').Dispose()
result=True''')
        finally:
            bindings = Path.home() / 'Library/Application Support/Magpie/canvas-bindings.json'
            if bindings.exists():
                saved = json.loads(bindings.read_text())
                for session in sessions:
                    saved.pop(session, None)
                bindings.write_text(json.dumps(saved))
            for session in sessions:
                req = urllib.request.Request('http://127.0.0.1:8123/sessions/' + session, method='DELETE')
                try:
                    with urllib.request.urlopen(req, timeout=5):
                        pass
                except urllib.error.HTTPError as error:
                    if error.code != 404:
                        raise
