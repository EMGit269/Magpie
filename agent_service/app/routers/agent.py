from __future__ import annotations

import json

import httpx
from fastapi import APIRouter, Depends, HTTPException, Request
from fastapi.responses import StreamingResponse

from ..container import ServiceContainer
from ..dependencies import get_container
from ..response_models import success_envelope
from ..schemas import AgentInvokeRequest, AgentInvokeResponse


router = APIRouter(tags=["agent"])


async def _run_agent_invoke(
    request: AgentInvokeRequest,
    http_request: Request,
    container: ServiceContainer,
) -> dict:
    try:
        result: AgentInvokeResponse = await container.runtime.invoke(request)
        return success_envelope(
            request_id=http_request.state.request_id,
            data=result.model_dump(mode="json"),
        )
    except httpx.ConnectError as exc:
        raise HTTPException(
            status_code=503,
            detail=(
                "Magpie host bridge is not reachable at "
                f"{container.settings.host_bridge_base_url}. "
                "Keep the Grasshopper Magpie window open and wait for the local host bridge to finish starting."
            ),
        ) from exc
    except RuntimeError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc


@router.post("/agent/invoke")
async def agent_invoke(
    request: AgentInvokeRequest,
    http_request: Request,
    container: ServiceContainer = Depends(get_container),
) -> dict:
    return await _run_agent_invoke(request, http_request, container)


@router.post("/graph/invoke")
@router.post("/langgraph/invoke")
async def graph_invoke(
    request: AgentInvokeRequest,
    http_request: Request,
    container: ServiceContainer = Depends(get_container),
) -> dict:
    """LangGraph-compatible invoke entry points.

    The current runtime is a LangChain agent scaffold; these endpoints alias
    to the same agent invocation path so the Magpie plugin can use its
    LangGraph-first endpoint resolution without 404 fallbacks.
    """
    return await _run_agent_invoke(request, http_request, container)


@router.post("/workflow/run")
async def workflow_run(
    request: AgentInvokeRequest,
    http_request: Request,
    container: ServiceContainer = Depends(get_container),
) -> dict:
    return success_envelope(
        request_id=http_request.state.request_id,
        data=await container.workflow_service.run(request),
    )


async def _sse_agent_event_stream(request: AgentInvokeRequest, container: ServiceContainer):
    async for event in container.runtime.ainvoke_stream(request):
        yield f"data: {json.dumps({'type': 'agent_event', 'payload': event.model_dump(mode='json')}, ensure_ascii=False)}\n\n"
    yield f"data: {json.dumps({'type': 'done', 'payload': {}}, ensure_ascii=False)}\n\n"


@router.post("/graph/invoke/stream")
@router.post("/langgraph/invoke/stream")
async def graph_invoke_stream(
    request: AgentInvokeRequest,
    http_request: Request,
    container: ServiceContainer = Depends(get_container),
) -> StreamingResponse:
    """Streaming LangGraph-compatible entry points; alias to agent stream."""
    return await agent_invoke_stream(request, http_request, container)


@router.post("/agent/invoke/stream")
async def agent_invoke_stream(
    request: AgentInvokeRequest,
    http_request: Request,
    container: ServiceContainer = Depends(get_container),
) -> StreamingResponse:
    async def generator():
        try:
            async for chunk in _sse_agent_event_stream(request, container):
                yield chunk
        except Exception as exc:
            yield f"data: {json.dumps({'type': 'error', 'payload': {'message': str(exc)}}, ensure_ascii=False)}\n\n"
            yield f"data: {json.dumps({'type': 'done', 'payload': {}}, ensure_ascii=False)}\n\n"

    return StreamingResponse(generator(), media_type="text/event-stream")


@router.post("/workflow/run/stream")
async def workflow_run_stream(
    request: AgentInvokeRequest,
    http_request: Request,
    container: ServiceContainer = Depends(get_container),
) -> StreamingResponse:
    async def generator():
        try:
            async for event in container.workflow_service.run_stream(request):
                yield f"data: {json.dumps({'type': 'agent_event', 'payload': event.model_dump(mode='json')}, ensure_ascii=False)}\n\n"
            yield f"data: {json.dumps({'type': 'done', 'payload': {}}, ensure_ascii=False)}\n\n"
        except Exception as exc:
            yield f"data: {json.dumps({'type': 'error', 'payload': {'message': str(exc)}}, ensure_ascii=False)}\n\n"
            yield f"data: {json.dumps({'type': 'done', 'payload': {}}, ensure_ascii=False)}\n\n"

    return StreamingResponse(generator(), media_type="text/event-stream")
