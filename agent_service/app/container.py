from __future__ import annotations

from dataclasses import dataclass

from .config import Settings, settings
from .context_store import SessionContextStore
from .host_bridge import HostBridgeClient
from .runtime import LangChainRuntimeService
from .task_store import TaskStore
from .tools import ToolRegistryService
from .workflow import MinimalWorkflowService


@dataclass(frozen=True)
class ServiceContainer:
    settings: Settings
    context_store: SessionContextStore
    task_store: TaskStore
    host_bridge: HostBridgeClient
    tool_registry: ToolRegistryService
    runtime: LangChainRuntimeService
    workflow_service: MinimalWorkflowService


def build_container() -> ServiceContainer:
    context_store = SessionContextStore(settings.state_dir)
    task_store = TaskStore(settings.state_dir)
    host_bridge = HostBridgeClient(settings.host_bridge_base_url)
    tool_registry = ToolRegistryService(
        host_bridge=host_bridge,
        task_store=task_store,
        context_store=context_store,
    )
    runtime = LangChainRuntimeService(
        settings=settings,
        context_store=context_store,
        task_store=task_store,
        tool_registry=tool_registry,
    )
    workflow_service = MinimalWorkflowService(
        host_bridge=host_bridge,
        context_store=context_store,
        task_store=task_store,
    )
    return ServiceContainer(
        settings=settings,
        context_store=context_store,
        task_store=task_store,
        host_bridge=host_bridge,
        tool_registry=tool_registry,
        runtime=runtime,
        workflow_service=workflow_service,
    )

