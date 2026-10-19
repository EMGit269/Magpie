#! python 3
"""Run inside Rhino via rhinocode; temporarily switch to a dedicated test canvas."""
import json
import traceback
from pathlib import Path

report = Path(__file__).with_name('setup-result.json')
try:
    import clr
    import Rhino
    import scriptcontext as sc
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    clr.AddReference('Grasshopper')
    from Grasshopper import Instances
    from Grasshopper.Kernel import GH_Document
    from System.Reflection import Assembly

    assert Instances.ActiveCanvas is not None, 'Open Grasshopper first'
    assert 'magpie_mac_test_document' not in sc.sticky, 'Clean up the previous test first'
    path = Path(__file__).resolve().parents[1] / 'bin/Release/net8.0/Magpie.MacBridge.gha'
    assembly = Assembly.LoadFrom(str(path))
    bridge = assembly.GetType('Magpie.MacBridge.Bridge')
    bridge.GetMethod('Start').Invoke(None, None)
    sc.sticky['magpie_mac_previous_document'] = Instances.ActiveCanvas.Document
    sc.sticky['magpie_mac_test_document'] = GH_Document()
    Instances.ActiveCanvas.Document = sc.sticky['magpie_mac_test_document']
    report.write_text(json.dumps({'ready': True}))
except BaseException:
    report.write_text(json.dumps({'ready': False, 'error': traceback.format_exc()}))
