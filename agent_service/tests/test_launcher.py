from __future__ import annotations

import importlib.util
import unittest
from pathlib import Path
from unittest.mock import patch


spec = importlib.util.spec_from_file_location(
    "magpie_launcher", Path(__file__).resolve().parents[1] / "run_service.py",
)
launcher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(launcher)


class LauncherTests(unittest.TestCase):
    def test_default_is_loopback_without_reload(self) -> None:
        with patch("sys.argv", ["run_service.py"]), patch.object(launcher.uvicorn, "run") as run:
            launcher.main()
        run.assert_called_once_with("app.main:app", host="127.0.0.1", port=8000, reload=False)

    def test_plugin_supplied_port_is_honored(self) -> None:
        with patch("sys.argv", ["run_service.py", "--port", "45678"]), patch.object(launcher.uvicorn, "run") as run:
            launcher.main()
        self.assertEqual(run.call_args.kwargs["port"], 45678)

    def test_development_reload_is_explicit(self) -> None:
        with patch("sys.argv", ["run_service.py", "--reload"]), patch.object(launcher.uvicorn, "run") as run:
            launcher.main()
        self.assertTrue(run.call_args.kwargs["reload"])

    def test_invalid_port_fails_before_starting(self) -> None:
        for port in ("0", "65536", "bad"):
            with self.subTest(port=port), patch("sys.argv", ["run_service.py", "--port", port]), \
                    patch.object(launcher.uvicorn, "run") as run, patch("sys.stderr"):
                with self.assertRaises(SystemExit) as error:
                    launcher.main()
                self.assertEqual(error.exception.code, 2)
                run.assert_not_called()
