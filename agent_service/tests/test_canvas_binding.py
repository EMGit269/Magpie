from __future__ import annotations

import asyncio
import json
import unittest
from types import SimpleNamespace
from unittest.mock import patch

import httpx

from app.host_bridge import HostBridgeClient
from app.runtime import LangChainRuntimeService
from app.tools import ToolRegistryService


class CanvasBindingTests(unittest.IsolatedAsyncioTestCase):
    async def test_each_concurrent_tool_keeps_its_document_envelope(self):
        received = []

        def respond(request):
            received.append(json.loads(request.content))
            return httpx.Response(200, json={"status": "ok", "result": {}})

        client_factory = httpx.AsyncClient
        transport = httpx.MockTransport(respond)
        registry = ToolRegistryService(
            host_bridge=HostBridgeClient("http://127.0.0.1:8765"),
            task_store=None, context_store=None,
        )
        manifest = [{"name": "get_canvas_summary"}]
        a = registry._build_host_tools(manifest, "document-a")[0]
        b = registry._build_host_tools(manifest, "document-b")[0]
        legacy = registry._build_host_tools(manifest)[0]
        with patch("app.host_bridge.httpx.AsyncClient", side_effect=lambda **kw: client_factory(transport=transport, **kw)):
            await asyncio.gather(
                a.ainvoke({"arguments": {"document_id": "cannot-override"}}),
                b.ainvoke({"arguments": {}}),
                legacy.ainvoke({"arguments": {}}),
            )
        self.assertEqual({r.get("document_id") for r in received}, {"document-a", "document-b", None})
        self.assertTrue(all(r["tool"] == "get_canvas_summary" for r in received))
        self.assertEqual(len({r["request_id"] for r in received}), 3)

    def test_bridge_failure_is_not_reported_as_a_completed_tool(self):
        content = json.dumps({"status": "error", "error": "Canvas binding mismatch"})
        for output in (content, SimpleNamespace(content=content, status="success")):
            _, data = LangChainRuntimeService._normalize_tool_output(output)
            self.assertEqual(data["status"], "error")
            self.assertEqual(data["error"], "Canvas binding mismatch")
