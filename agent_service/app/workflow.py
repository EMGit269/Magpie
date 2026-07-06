from __future__ import annotations

import json
from dataclasses import dataclass
from typing import Any, AsyncIterator

from .context_store import SessionContextStore
from .host_bridge import HostBridgeClient
from .logging_utils import get_logger, structured
from .schemas import AgentEvent, AgentInvokeRequest
from .task_store import TaskStore


@dataclass
class PlanResult:
    summary: str
    should_query_canvas: bool
    should_check_errors: bool
    suggested_tasks: list[dict[str, str]]


@dataclass
class VerificationResult:
    status: str
    summary: str
    details: dict[str, Any]


class MinimalPlanner:
    def build_plan(self, request: AgentInvokeRequest) -> PlanResult:
        text = (request.user_input or "").strip()
        lowered = text.lower()
        should_query_canvas = True
        should_check_errors = any(token in lowered for token in ("修复", "报错", "error", "fix", "check", "检查"))
        tasks = [
            {
                "title": "Inspect current Grasshopper canvas",
                "description": "Read current canvas summary before making decisions.",
            }
        ]
        if should_check_errors:
            tasks.append(
                {
                    "title": "Check runtime errors",
                    "description": "Verify whether current canvas has runtime errors or warnings.",
                }
            )
        return PlanResult(
            summary="Start with canvas inspection, then optionally run error verification.",
            should_query_canvas=should_query_canvas,
            should_check_errors=should_check_errors,
            suggested_tasks=tasks,
        )


class MinimalVerifier:
    def verify(
        self,
        *,
        canvas_summary: dict[str, Any] | None,
        error_result: dict[str, Any] | None,
    ) -> VerificationResult:
        details: dict[str, Any] = {}
        status = "ok"
        summary = "Basic verification passed."

        if canvas_summary is not None:
            result = canvas_summary.get("result")
            details["canvas_summary_present"] = result is not None
            if isinstance(result, dict):
                details["canvas_component_count"] = len(result.get("components", []) or [])
                details["canvas_group_count"] = len(result.get("groups", []) or [])

        if error_result is not None:
            details["error_result_present"] = True
            result = error_result.get("result")
            if isinstance(result, str) and result.strip():
                status = "needs_attention"
                summary = "Runtime error check returned non-empty diagnostics."
                details["runtime_diagnostics"] = result
            elif isinstance(result, dict) and result:
                status = "needs_attention"
                summary = "Runtime error check returned structured diagnostics."
                details["runtime_diagnostics"] = result
        else:
            details["error_result_present"] = False

        return VerificationResult(status=status, summary=summary, details=details)


class MinimalWorkflowService:
    def __init__(
        self,
        *,
        host_bridge: HostBridgeClient,
        context_store: SessionContextStore,
        task_store: TaskStore,
    ) -> None:
        self._host_bridge = host_bridge
        self._context_store = context_store
        self._task_store = task_store
        self._planner = MinimalPlanner()
        self._verifier = MinimalVerifier()

    async def run_stream(
        self,
        request: AgentInvokeRequest,
        collector: dict[str, Any] | None = None,
    ) -> AsyncIterator[AgentEvent]:
        logger = get_logger("magpie.agent_service.workflow")
        if request.user_goal:
            self._context_store.set_goal(request.session_id, request.user_goal)

        self._context_store.append_message(request.session_id, "user", request.user_input)
        plan = self._planner.build_plan(request)
        structured(
            logger,
            20,
            "workflow_started",
            session_id=request.session_id,
            should_query_canvas=plan.should_query_canvas,
            should_check_errors=plan.should_check_errors,
        )
        yield AgentEvent(
            type="plan",
            text=plan.summary,
            status="completed",
            data={
                "should_query_canvas": plan.should_query_canvas,
                "should_check_errors": plan.should_check_errors,
            },
        )

        created_tasks = []
        for task in plan.suggested_tasks:
            created = self._task_store.add(
                request.session_id,
                title=task["title"],
                description=task["description"],
            )
            created_tasks.append(created.model_dump(mode="json"))
            yield AgentEvent(
                type="task",
                text=task["title"],
                status="created",
                data=created.model_dump(mode="json"),
            )

        canvas_summary = None
        if plan.should_query_canvas:
            yield AgentEvent(
                type="tool_call",
                tool="get_canvas_summary",
                text="Inspecting current Grasshopper canvas.",
                status="running",
            )
            canvas_summary = await self._host_bridge.invoke(
                tool="get_canvas_summary",
                args={},
                request_id="workflow_canvas_summary",
            )
            yield AgentEvent(
                type="tool_result",
                tool="get_canvas_summary",
                text="Canvas inspection completed.",
                status=canvas_summary.get("status", "ok"),
                data=canvas_summary,
            )

        error_result = None
        if plan.should_check_errors:
            yield AgentEvent(
                type="tool_call",
                tool="check_gh_errors",
                text="Checking Grasshopper runtime errors.",
                status="running",
            )
            error_result = await self._host_bridge.invoke(
                tool="check_gh_errors",
                args={},
                request_id="workflow_check_errors",
            )
            yield AgentEvent(
                type="tool_result",
                tool="check_gh_errors",
                text="Runtime error check completed.",
                status=error_result.get("status", "ok"),
                data=error_result,
            )

        verification = self._verifier.verify(
            canvas_summary=canvas_summary,
            error_result=error_result,
        )

        output_lines = [
            "Workflow completed.",
            f"Plan: {plan.summary}",
            f"Verification: {verification.summary}",
        ]
        if verification.details.get("canvas_component_count") is not None:
            output_lines.append(f"Canvas components: {verification.details['canvas_component_count']}")
        if verification.status != "ok":
            output_lines.append("Further repair or inspection is required.")
        output_text = "\n".join(output_lines)
        yield AgentEvent(
            type="final",
            text=output_text,
            status=verification.status,
            data=verification.details,
        )

        self._context_store.set_fact(request.session_id, "last_workflow_status", verification.status)
        self._context_store.set_fact(request.session_id, "last_workflow_summary", verification.summary)
        self._context_store.append_message(request.session_id, "assistant", output_text)
        structured(
            logger,
            20,
            "workflow_completed",
            session_id=request.session_id,
            verification_status=verification.status,
        )

        if collector is not None:
            collector["created_tasks"] = created_tasks
            collector["canvas_summary"] = canvas_summary
            collector["error_result"] = error_result
            collector["output_text"] = output_text
            collector["verification"] = verification
            collector["plan"] = plan

    async def run(self, request: AgentInvokeRequest) -> dict[str, Any]:
        collector: dict[str, Any] = {}
        events: list[AgentEvent] = []
        async for event in self.run_stream(request, collector):
            events.append(event)

        plan = collector["plan"]
        verification = collector["verification"]
        output_text = collector["output_text"]
        created_tasks = collector["created_tasks"]
        canvas_summary = collector["canvas_summary"]
        error_result = collector["error_result"]

        return {
            "session_id": request.session_id,
            "mode": "workflow",
            "status": verification.status,
            "events": [item.model_dump(mode="json") for item in events],
            "final_text": output_text,
            "plan": {
                "summary": plan.summary,
                "should_query_canvas": plan.should_query_canvas,
                "should_check_errors": plan.should_check_errors,
            },
            "created_tasks": created_tasks,
            "tool_results": {
                "canvas_summary": canvas_summary,
                "error_result": error_result,
            },
            "verification": {
                "status": verification.status,
                "summary": verification.summary,
                "details": verification.details,
            },
            "output_text": output_text,
        }
