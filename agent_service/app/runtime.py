from __future__ import annotations

import json
from typing import Any, AsyncIterator, TypedDict

from langchain.agents import create_agent
from langchain.agents.middleware import ModelCallLimitMiddleware, dynamic_prompt
from langchain_core.messages import HumanMessage, SystemMessage
from langchain_openai import ChatOpenAI

from .config import Settings, load_model_config
from .context_store import SessionContextStore
from .logging_utils import get_logger, structured
from .schemas import AgentEvent, AgentInvokeRequest, AgentInvokeResponse
from .task_store import TaskStore
from .tools import ToolRegistryService


class AgentRuntimeContext(TypedDict):
    session_id: str


logger = get_logger("magpie.agent_service.runtime")


class LangChainRuntimeService:
    def __init__(
        self,
        *,
        settings: Settings,
        context_store: SessionContextStore,
        task_store: TaskStore,
        tool_registry: ToolRegistryService,
    ) -> None:
        self._settings = settings
        self._context_store = context_store
        self._task_store = task_store
        self._tool_registry = tool_registry

    async def invoke(self, request: AgentInvokeRequest) -> AgentInvokeResponse:
        events: list[AgentEvent] = []
        if request.user_goal:
            self._context_store.set_goal(request.session_id, request.user_goal)

        api_key, _, model_name = load_model_config(self._settings)
        if not api_key or not model_name:
            raise RuntimeError("OPENAI_API_KEY and OPENAI_MODEL must be configured before agent invocation.")

        self._context_store.append_message(request.session_id, "user", request.user_input)

        try:
            tools = await self._tool_registry.build_langchain_tools(request.session_id)
            tool_names = [tool.name for tool in tools]
            structured(
                logger,
                20,
                "agent_invoke_started",
                session_id=request.session_id,
                tool_count=len(tool_names),
            )
            events.append(
                AgentEvent(
                    type="plan",
                    text="Preparing tool-enabled agent run.",
                    status="completed",
                    data={"tool_count": len(tool_names), "tool_names": tool_names},
                )
            )

            prompt_middleware = self._build_prompt_middleware()
            agent = create_agent(
                model=self._build_model(),
                tools=tools,
                middleware=[
                    prompt_middleware,
                    ModelCallLimitMiddleware(
                        run_limit=self._settings.model_run_limit,
                        exit_behavior="end",
                    ),
                ],
                context_schema=AgentRuntimeContext,
            )

            context = self._context_store.get(request.session_id)
            history_messages = [
                {"role": m.get("role", "user"), "content": m.get("content", "")}
                for m in context.recent_messages
            ]
            result = await agent.ainvoke(
                {"messages": history_messages},
                context={"session_id": request.session_id},
            )

            output_text = self._extract_output_text(result)
            events.extend(self._extract_agent_events(result))
            events.append(AgentEvent(type="final", text=output_text, status="completed"))
            self._context_store.append_message(request.session_id, "assistant", output_text)
            structured(
                logger,
                20,
                "agent_invoke_completed",
                session_id=request.session_id,
                message_count=len(result.get("messages", [])),
                output_preview=output_text[:200],
            )
            return AgentInvokeResponse(
                session_id=request.session_id,
                mode="agent",
                status="completed",
                events=events,
                final_text=output_text,
                output_text=output_text,
                tool_names=tool_names,
                message_count=len(result.get("messages", [])),
                raw=self._serialize_result(result),
            )
        except Exception as exc:
            structured(
                logger,
                30,
                "agent_invoke_fallback",
                session_id=request.session_id,
                error=str(exc),
            )
            return await self._invoke_plain_chat(request, str(exc))

    async def ainvoke_stream(self, request: AgentInvokeRequest) -> AsyncIterator[AgentEvent]:
        if request.user_goal:
            self._context_store.set_goal(request.session_id, request.user_goal)

        api_key, _, model_name = load_model_config(self._settings)
        if not api_key or not model_name:
            raise RuntimeError("OPENAI_API_KEY and OPENAI_MODEL must be configured before agent invocation.")

        self._context_store.append_message(request.session_id, "user", request.user_input)

        try:
            tools = await self._tool_registry.build_langchain_tools(request.session_id)
            tool_names = [tool.name for tool in tools]
            structured(
                logger,
                20,
                "agent_invoke_stream_started",
                session_id=request.session_id,
                tool_count=len(tool_names),
            )
            yield AgentEvent(
                type="plan",
                text="Preparing tool-enabled agent run.",
                status="running",
                data={"tool_count": len(tool_names), "tool_names": tool_names},
            )

            prompt_middleware = self._build_prompt_middleware()
            agent = create_agent(
                model=self._build_model(),
                tools=tools,
                middleware=[
                    prompt_middleware,
                    ModelCallLimitMiddleware(
                        run_limit=self._settings.model_run_limit,
                        exit_behavior="end",
                    ),
                ],
                context_schema=AgentRuntimeContext,
            )

            final_text = ""
            emitted_delta = False
            context = self._context_store.get(request.session_id)
            history_messages = [
                {"role": m.get("role", "user"), "content": m.get("content", "")}
                for m in context.recent_messages
            ]
            async for event in agent.astream_events(
                {"messages": history_messages},
                config={},
                context={"session_id": request.session_id},
                version="v2",
            ):
                kind = event.get("event")
                data = event.get("data") or {}
                if kind == "on_chat_model_stream":
                    chunk = data.get("chunk")
                    content = getattr(chunk, "content", None) if chunk is not None else None
                    if isinstance(content, str) and content:
                        yield AgentEvent(type="assistant_delta", text=content, status="streaming")
                        emitted_delta = True
                elif kind == "on_tool_start":
                    name = event.get("name") or "tool"
                    inputs = data.get("input") if isinstance(data.get("input"), dict) else {}
                    yield AgentEvent(
                        type="tool_call",
                        tool=name,
                        text=f"Calling {name}.",
                        status="running",
                        data=inputs,
                    )
                elif kind == "on_tool_end":
                    name = event.get("name") or ""
                    output = data.get("output")
                    result_text, result_data = self._normalize_tool_output(output)
                    yield AgentEvent(
                        type="tool_result",
                        tool=name,
                        text=result_text,
                        status="completed",
                        data=result_data,
                    )
                elif kind == "on_tool_error":
                    name = event.get("name") or ""
                    error = data.get("error") or "Tool failed."
                    yield AgentEvent(
                        type="tool_result",
                        tool=name,
                        text=str(error),
                        status="failed",
                        data={"error": str(error)},
                    )
                elif kind == "on_chain_end":
                    candidate = self._extract_output_text(data.get("output") or {})
                    if candidate:
                        final_text = candidate

            if not emitted_delta and final_text:
                for i in range(0, len(final_text), 2):
                    yield AgentEvent(type="assistant_delta", text=final_text[i : i + 2], status="streaming")

            self._context_store.append_message(request.session_id, "assistant", final_text)
            yield AgentEvent(type="final", text=final_text, status="completed")
            structured(
                logger,
                20,
                "agent_invoke_stream_completed",
                session_id=request.session_id,
                output_preview=final_text[:200],
            )
        except Exception as exc:
            structured(
                logger,
                30,
                "agent_invoke_stream_failed",
                session_id=request.session_id,
                error=str(exc),
            )
            response = await self._invoke_plain_chat(request, str(exc))
            for evt in response.events:
                yield evt

    def _build_model(self) -> ChatOpenAI:
        api_key, base_url, model_name = load_model_config(self._settings)
        kwargs: dict[str, Any] = {
            "model": model_name,
            "api_key": api_key,
        }
        if base_url:
            kwargs["base_url"] = base_url
        return ChatOpenAI(**kwargs)

    def _build_prompt_middleware(self):
        context_store = self._context_store
        task_store = self._task_store

        @dynamic_prompt
        def build_prompt(request) -> str:
            session_id = request.runtime.context["session_id"]
            context = context_store.get(session_id)
            tasks = task_store.list(session_id)
            open_tasks = [task for task in tasks if task.status != "done"]
            host_tools = context.host_capabilities
            lines = [
                "You are the external Magpie agent.",
                "The Grasshopper plugin host is the only process allowed to mutate Rhino or Grasshopper state.",
                "When you need canvas state or mutations, use the registered host bridge tools.",
                "",
                f"Session id: {session_id}",
                f"User goal: {context.user_goal or '(not set)'}",
                "",
                "Session facts:",
            ]
            if context.facts:
                for key, value in context.facts.items():
                    lines.append(f"- {key}: {value}")
            else:
                lines.append("- (none)")
            lines.append("")
            lines.append("Open tasks:")
            if open_tasks:
                for task in open_tasks:
                    lines.append(f"- {task.id} [{task.status}] {task.title}")
            else:
                lines.append("- (none)")
            lines.append("")
            lines.append(f"Host tool count: {len(host_tools)}")
            lines.append("Prefer concise planning, keep task list updated, and verify host results before claiming completion.")
            return "\n".join(lines)

        return build_prompt

    async def _invoke_plain_chat(self, request: AgentInvokeRequest, fallback_reason: str) -> AgentInvokeResponse:
        context = self._context_store.get(request.session_id)
        system_lines = [
            "You are Magpie, an assistant embedded in Grasshopper.",
            "If local Grasshopper host tools are unavailable, continue as a normal conversational assistant.",
            "Do not claim to have inspected or modified the Grasshopper canvas unless tool results explicitly confirm it.",
            f"User goal: {context.user_goal or request.user_goal or '(not set)'}",
        ]
        model = self._build_model()
        response = await model.ainvoke(
            [
                SystemMessage(content="\n".join(system_lines)),
                HumanMessage(content=request.user_input),
            ]
        )
        output_text = response.content if isinstance(response.content, str) else str(response.content)
        self._context_store.append_message(request.session_id, "assistant", output_text)
        return AgentInvokeResponse(
            session_id=request.session_id,
            mode="agent",
            status="degraded",
            events=[
                AgentEvent(
                    type="warning",
                    text="Tool-enabled agent mode degraded. Continuing without host tools.",
                    status="degraded",
                    data={"reason": fallback_reason},
                ),
                AgentEvent(type="final", text=output_text, status="completed"),
            ],
            final_text=output_text,
            output_text=output_text,
            tool_names=[],
            message_count=2,
            raw={
                "mode": "plain_chat_fallback",
                "fallback_reason": fallback_reason,
                "content": output_text,
            },
        )

    @staticmethod
    def _normalize_tool_output(output: Any) -> tuple[str, dict[str, Any]]:
        if hasattr(output, "content"):
            content = output.content
            text = content if isinstance(content, str) else str(content)
            data: dict[str, Any] = {"content": text}
            if hasattr(output, "artifact") and output.artifact is not None:
                data["artifact"] = output.artifact
            if hasattr(output, "status"):
                data["status"] = output.status
            return text, data
        if isinstance(output, dict):
            text = output.get("content") if isinstance(output.get("content"), str) else json.dumps(output, ensure_ascii=False)
            return text, output
        text = str(output)
        return text, {"content": text}

    @staticmethod
    def _extract_agent_events(result: dict[str, Any]) -> list[AgentEvent]:
        items: list[AgentEvent] = []
        for message in result.get("messages", []):
            kind = getattr(message, "type", None)
            if kind is None and isinstance(message, dict):
                kind = message.get("role")

            content = getattr(message, "content", None)
            if isinstance(message, dict):
                content = message.get("content")
            text = content if isinstance(content, str) else ""

            tool_calls = getattr(message, "tool_calls", None)
            if tool_calls is None and isinstance(message, dict):
                tool_calls = message.get("tool_calls")
            if isinstance(tool_calls, list):
                for call in tool_calls:
                    if isinstance(call, dict):
                        name = str(call.get("name", ""))
                        args = call.get("args", {})
                    else:
                        name = str(getattr(call, "name", ""))
                        args = getattr(call, "args", {})
                    items.append(
                        AgentEvent(
                            type="tool_call",
                            tool=name,
                            text=f"Calling {name}.",
                            status="running",
                            data=args if isinstance(args, dict) else {"args": str(args)},
                        )
                    )

            if kind in ("tool", "ToolMessage"):
                tool_name = ""
                if isinstance(message, dict):
                    tool_name = str(message.get("name", "") or message.get("tool_name", ""))
                else:
                    tool_name = str(getattr(message, "name", "") or getattr(message, "tool_name", ""))
                items.append(
                    AgentEvent(
                        type="tool_result",
                        tool=tool_name,
                        text=text.strip() or f"{tool_name} completed.",
                        status="completed",
                    )
                )
                continue

            if kind in ("ai", "AIMessage") and text.strip():
                items.append(AgentEvent(type="message", text=text.strip(), status="completed"))

        return items

    @staticmethod
    def _extract_output_text(result: Any) -> str:
        if isinstance(result, list):
            messages = result
        elif isinstance(result, dict):
            messages = result.get("messages", [])
        else:
            return ""

        for message in reversed(messages):
            if isinstance(message, dict):
                raw_content = message.get("content")
                if isinstance(raw_content, str) and raw_content.strip():
                    return raw_content
            content = getattr(message, "content", None)
            if isinstance(content, str) and content.strip():
                return content
        return ""

    @staticmethod
    def _serialize_result(result: dict[str, Any]) -> dict[str, Any]:
        payload: dict[str, Any] = {}
        for key, value in result.items():
            if key == "messages" and isinstance(value, list):
                payload[key] = [LangChainRuntimeService._serialize_message(item) for item in value]
                continue
            if isinstance(value, (str, int, float, bool)) or value is None:
                payload[key] = value
            elif isinstance(value, dict):
                payload[key] = value
            else:
                payload[key] = str(value)
        return payload

    @staticmethod
    def _serialize_message(message: Any) -> dict[str, Any]:
        if isinstance(message, dict):
            role = str(message.get("role", "unknown"))
            content = message.get("content", "")
            return {"role": role, "content": content if isinstance(content, str) else str(content)}

        role = getattr(message, "type", None) or message.__class__.__name__
        content = getattr(message, "content", "")
        return {
            "role": str(role),
            "content": content if isinstance(content, str) else str(content),
        }
