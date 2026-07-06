from __future__ import annotations

from datetime import datetime
from typing import Any, Literal

from pydantic import BaseModel, Field


TaskStatus = Literal["todo", "in_progress", "blocked", "done"]


class TaskItem(BaseModel):
    id: str
    title: str
    description: str = ""
    status: TaskStatus = "todo"
    notes: str = ""
    created_at: datetime
    updated_at: datetime


class SessionContext(BaseModel):
    session_id: str
    title: str = ""
    user_goal: str = ""
    facts: dict[str, Any] = Field(default_factory=dict)
    host_capabilities: list[dict[str, Any]] = Field(default_factory=list)
    recent_messages: list[dict[str, str]] = Field(default_factory=list)
    updated_at: datetime


class RenameSessionRequest(BaseModel):
    title: str


class AddTaskRequest(BaseModel):
    title: str
    description: str = ""


class UpdateTaskRequest(BaseModel):
    status: TaskStatus | None = None
    notes: str | None = None
    title: str | None = None
    description: str | None = None


class SetFactRequest(BaseModel):
    key: str
    value: Any


class AgentInvokeRequest(BaseModel):
    session_id: str = "default"
    user_input: str
    user_goal: str = ""


class AgentEvent(BaseModel):
    type: str
    text: str = ""
    tool: str = ""
    status: str = ""
    data: dict[str, Any] = Field(default_factory=dict)


class AgentInvokeResponse(BaseModel):
    session_id: str
    mode: str = "agent"
    status: str = "completed"
    events: list[AgentEvent] = Field(default_factory=list)
    final_text: str = ""
    output_text: str
    tool_names: list[str]
    message_count: int
    raw: dict[str, Any]
