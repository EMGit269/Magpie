"""Write local Mac launcher paths without modifying model credentials."""
import json
import os
from pathlib import Path
import tempfile

root = Path(__file__).resolve().parent.parent
support = Path.home() / 'Library/Application Support/Magpie'
support.mkdir(parents=True, exist_ok=True)
settings = support / 'mac-agent.json'
data = json.loads(settings.read_text()) if settings.exists() else {}
candidates = [
    os.environ.get('MAGPIE_AGENT_PYTHON', ''),
    str(root / '.venv/bin/python'),
    str(root / 'agent_service/.venv/bin/python'),
]
python = next((path for path in candidates if path and Path(path).is_file()), '')
if not data.get('pythonPath') or not Path(data['pythonPath']).is_file():
    data['pythonPath'] = python
if not data.get('serviceDirectory') or not (Path(data['serviceDirectory']) / 'run_service.py').is_file():
    data['serviceDirectory'] = str(root / 'agent_service')
data.setdefault('serviceUrl', 'http://127.0.0.1:8123')
data.setdefault('invokePath', '/graph/invoke/stream')
fd, temporary = tempfile.mkstemp(prefix='mac-agent-', dir=support)
try:
    with os.fdopen(fd, 'w') as handle:
        json.dump(data, handle, indent=2)
    os.replace(temporary, settings)
finally:
    if os.path.exists(temporary):
        os.unlink(temporary)
print('Agent launcher configured:', settings)
if not data['pythonPath']:
    print('Prepare the Python environment from agent_service/README.md, then rerun this installer.')
