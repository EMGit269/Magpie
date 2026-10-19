from __future__ import annotations

import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch

from app.context_store import SessionContextStore
from app.runtime import LangChainRuntimeService
from app.schemas import AgentInvokeRequest
from app.task_store import TaskStore


class RuntimeFailureTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        root = Path(directory.name)
        self.store = SessionContextStore(root)
        self.registry = SimpleNamespace(build_langchain_tools=AsyncMock(return_value=[]))
        self.service = LangChainRuntimeService(
            settings=SimpleNamespace(model_run_limit=8), context_store=self.store,
            task_store=TaskStore(root), tool_registry=self.registry,
        )
        self.enterContext(patch("app.runtime.load_model_config", return_value=("test", None, "test")))
        self.enterContext(patch.object(self.service, "_build_model", return_value=object()))

    async def test_stream_limit_preserves_last_error_and_does_not_restart_without_tools(self):
        seen_config = []

        class Agent:
            async def astream_events(self, *args, **kwargs):
                seen_config.append(kwargs["config"])
                yield {"event": "on_tool_end", "name": "create_component_graph", "run_id": "circle-1",
                       "data": {"output": {"status": "error", "error": "Radius is out of range"}}}
                raise RuntimeError("Recursion limit of 25 reached")

        with patch("app.runtime.create_agent", return_value=Agent()), \
             patch.object(self.service, "_invoke_plain_chat", new_callable=AsyncMock) as fallback:
            events = [e async for e in self.service.ainvoke_stream(AgentInvokeRequest(session_id="circle", user_input="画一个圆"))]
        fallback.assert_not_awaited()
        self.assertGreater(seen_config[0]["recursion_limit"], 25)
        self.assertEqual(events[-1].status, "failed")
        self.assertIn("Radius is out of range", events[-1].text)
        self.assertNotIn("没有连接", events[-1].text)
        self.assertEqual(self.store.get("circle").recent_messages[-1]["content"], events[-1].text)

    async def test_non_stream_failure_also_avoids_plain_chat_fallback(self):
        agent = SimpleNamespace(ainvoke=AsyncMock(side_effect=RuntimeError("Model execution failed")))
        with patch("app.runtime.create_agent", return_value=agent), \
             patch.object(self.service, "_invoke_plain_chat", new_callable=AsyncMock) as fallback:
            response = await self.service.invoke(AgentInvokeRequest(session_id="circle", user_input="画一个圆"))
        fallback.assert_not_awaited()
        self.assertEqual(response.status, "failed")

    async def test_connection_fallback_keeps_follow_up_history(self):
        self.store.append_message("circle", "user", "画一个随机的圆")
        self.store.append_message("circle", "assistant", "正在检查")
        self.registry.build_langchain_tools.side_effect = ConnectionError("bridge unavailable")
        model = SimpleNamespace(ainvoke=AsyncMock(return_value=SimpleNamespace(content="连接中断，请重连后继续画圆。")))
        with patch.object(self.service, "_build_model", return_value=model):
            await self.service.invoke(AgentInvokeRequest(session_id="circle", user_input="你帮我画一下"))
        messages = model.ainvoke.call_args.args[0]
        self.assertEqual([m["content"] for m in messages[1:]], ["画一个随机的圆", "正在检查", "你帮我画一下"])
