from __future__ import annotations

import json
from typing import Any
from uuid import uuid4

from langchain_core.tools import StructuredTool
from pydantic import BaseModel, Field, create_model

from .context_store import SessionContextStore
from .host_bridge import HostBridgeClient
from .task_store import TaskStore


class AddTaskToolInput(BaseModel):
    title: str = Field(description="Short task title.")
    description: str = Field(default="", description="Optional task description.")


class UpdateTaskToolInput(BaseModel):
    task_id: str = Field(description="Task id to update.")
    status: str | None = Field(default=None, description="todo, in_progress, blocked, or done.")
    notes: str | None = Field(default=None, description="Optional task notes.")
    title: str | None = Field(default=None, description="Optional replacement task title.")
    description: str | None = Field(default=None, description="Optional replacement task description.")


class SetFactToolInput(BaseModel):
    key: str = Field(description="Context fact key.")
    value: str = Field(description="Context fact value serialized as text.")


class ToolRegistryService:
    def __init__(
        self,
        *,
        host_bridge: HostBridgeClient,
        task_store: TaskStore,
        context_store: SessionContextStore,
    ) -> None:
        self._host_bridge = host_bridge
        self._task_store = task_store
        self._context_store = context_store

    async def describe_tools(self, session_id: str) -> list[dict[str, Any]]:
        manifest = await self._host_bridge.manifest()
        host_tools = manifest.get("tools", [])
        local_tools = [
            {
                "name": "list_tasks",
                "read_only": True,
                "description": "List the current session task list.",
            },
            {
                "name": "add_task",
                "read_only": False,
                "description": "Add a task to the current session task list.",
            },
            {
                "name": "update_task",
                "read_only": False,
                "description": "Update one existing task status, notes, or text.",
            },
            {
                "name": "get_session_context",
                "read_only": True,
                "description": "Read current session goal, facts, and recent messages.",
            },
            {
                "name": "set_session_fact",
                "read_only": False,
                "description": "Persist a session fact for future turns.",
            },
        ]
        return [*local_tools, *host_tools]

    async def build_langchain_tools(self, session_id: str) -> list[StructuredTool]:
        host_manifest = await self._host_bridge.manifest()
        self._context_store.set_host_capabilities(session_id, host_manifest.get("tools", []))
        return [
            *self._build_local_tools(session_id),
            *self._build_host_tools(host_manifest.get("tools", [])),
        ]

    def _build_local_tools(self, session_id: str) -> list[StructuredTool]:
        def list_tasks() -> str:
            items = [task.model_dump(mode="json") for task in self._task_store.list(session_id)]
            return json.dumps(items, ensure_ascii=False)

        def add_task(title: str, description: str = "") -> str:
            item = self._task_store.add(session_id, title=title, description=description)
            return item.model_dump_json()

        def update_task(
            task_id: str,
            status: str | None = None,
            notes: str | None = None,
            title: str | None = None,
            description: str | None = None,
        ) -> str:
            item = self._task_store.update(
                session_id,
                task_id,
                status=status,  # type: ignore[arg-type]
                notes=notes,
                title=title,
                description=description,
            )
            return item.model_dump_json()

        def get_session_context() -> str:
            context = self._context_store.get(session_id)
            return context.model_dump_json()

        def set_session_fact(key: str, value: str) -> str:
            context = self._context_store.set_fact(session_id, key, value)
            return context.model_dump_json()

        return [
            StructuredTool.from_function(
                name="list_tasks",
                description="List the current task list for this session.",
                func=list_tasks,
            ),
            StructuredTool.from_function(
                name="add_task",
                description="Add one task to the current task list.",
                func=add_task,
                args_schema=AddTaskToolInput,
            ),
            StructuredTool.from_function(
                name="update_task",
                description="Update one task status, notes, title, or description.",
                func=update_task,
                args_schema=UpdateTaskToolInput,
            ),
            StructuredTool.from_function(
                name="get_session_context",
                description="Read current session goal, facts, host capabilities, and recent messages.",
                func=get_session_context,
            ),
            StructuredTool.from_function(
                name="set_session_fact",
                description="Store a reusable session fact for future turns.",
                func=set_session_fact,
                args_schema=SetFactToolInput,
            ),
        ]

    def _build_host_tools(self, manifest_tools: list[dict[str, Any]]) -> list[StructuredTool]:
        tools: list[StructuredTool] = []
        for item in manifest_tools:
            tool_name = str(item.get("name", "")).strip()
            if not tool_name:
                continue
            description = str(item.get("description", "")).strip() or f"Forward call to host tool {tool_name}."
            args_schema = create_model(
                f"{self._safe_model_name(tool_name)}Input",
                arguments=(dict[str, Any], Field(default_factory=dict, description="Arguments forwarded to the plugin host tool.")),
            )

            async def _invoke(arguments: dict[str, Any], _tool_name: str = tool_name) -> str:
                response = await self._host_bridge.invoke(
                    tool=_tool_name,
                    args=arguments,
                    request_id=f"host_{uuid4().hex[:12]}",
                )
                return json.dumps(response, ensure_ascii=False)

            tools.append(
                StructuredTool.from_function(
                    name=tool_name,
                    description=description,
                    coroutine=_invoke,
                    args_schema=args_schema,
                )
            )
        return tools

    @staticmethod
    def _safe_model_name(tool_name: str) -> str:
        chars = [ch if ch.isalnum() else "_" for ch in tool_name]
        return "".join(chars).strip("_") or "HostTool"

