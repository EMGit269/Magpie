"""Usage: python3 MacBridge/tests/run_smoke.py --rhino rhinocode_remotepipe_PID."""
import argparse
import json
from pathlib import Path
import subprocess
import time
import unittest

from smoke_test import MacBridgeSmoke

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--rhino', required=True, help='Instance ID from rhinocode list')
parser.add_argument('--rhinocode', default='/Applications/Rhino 8.app/Contents/Resources/bin/rhinocode')
args = parser.parse_args()
folder = Path(__file__).resolve().parent
report = folder / 'setup-result.json'


def script(name):
    subprocess.run([args.rhinocode, '-r', args.rhino, 'script', str(folder / name)], check=True, timeout=60)


report.unlink(missing_ok=True)
try:
    script('setup_canvas.py')
    deadline = time.monotonic() + 60
    while not report.exists() and time.monotonic() < deadline:
        time.sleep(0.25)
    if not report.exists():
        raise RuntimeError('Rhino setup timed out; inspect Rhino command history')
    setup = json.loads(report.read_text())
    if not setup.get('ready'):
        raise RuntimeError(setup.get('error'))
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(MacBridgeSmoke)
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    if not result.wasSuccessful():
        raise SystemExit(1)
finally:
    script('cleanup_canvas.py')
    report.unlink(missing_ok=True)
