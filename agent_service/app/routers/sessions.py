from __future__ import annotations

from fastapi import APIRouter, Depends, HTTPException, Request

from ..container import ServiceContainer
from ..dependencies import get_container
from ..response_models import success_envelope
from ..schemas import AddTaskRequest, RenameSessionRequest, SetFactRequest, UpdateTaskRequest


router = APIRouter(prefix="/sessions", tags=["sessions"])


def _derive_session_summary(context) -> dict:
    title = (context.title or "").strip()
    if not title:
        for msg in context.recent_messages:
            if msg.get("role") == "user":
                text = (msg.get("content") or "").strip()
                if text:
                    title = text[:60] + ("..." if len(text) > 60 else "")
                    break
    if not title:
        title = (context.user_goal or "").strip()[:60]
    if not title:
        title = "New chat"

    count = len(context.recent_messages)
    subtitle = f"{count} 条消息" if count else ""

    return {
        "id": context.session_id,
        "title": title,
        "subtitle": subtitle,
        "updated_at": context.updated_at.isoformat(),
    }


@router.get("")
async def list_sessions(request: Request, container: ServiceContainer = Depends(get_container)) -> dict:
    contexts = container.context_store.list_all()
    contexts.sort(key=lambda c: c.updated_at, reverse=True)
    return success_envelope(
        request_id=request.state.request_id,
        data=[_derive_session_summary(c) for c in contexts],
    )


@router.get("/{session_id}")
async def get_session(session_id: str, request: Request, container: ServiceContainer = Depends(get_container)) -> dict:
    return success_envelope(
        request_id=request.state.request_id,
        data=container.context_store.get(session_id).model_dump(mode="json"),
    )


@router.patch("/{session_id}")
async def rename_session(
    session_id: str,
    request: RenameSessionRequest,
    http_request: Request,
    container: ServiceContainer = Depends(get_container),
) -> dict:
    context = container.context_store.set_title(session_id, request.title)
    return success_envelope(
        request_id=http_request.state.request_id,
        data=context.model_dump(mode="json"),
    )


@router.delete("/{session_id}")
async def delete_session(session_id: str, request: Request, container: ServiceContainer = Depends(get_container)) -> dict:
    deleted_context = container.context_store.delete(session_id)
    container.task_store.delete(session_id)
    if not deleted_context:
        raise HTTPException(status_code=404, detail=f"Session not found: {session_id}")
    return success_envelope(
        request_id=request.state.request_id,
        data={"deleted": True},
    )


@router.post("/{session_id}/facts")
async def set_session_fact(
    session_id: str,
    request: SetFactRequest,
    container: ServiceContainer = Depends(get_container),
) -> dict:
    return success_envelope(
        request_id=request.state.request_id,
        data=container.context_store.set_fact(session_id, request.key, request.value).model_dump(mode="json"),
    )


@router.get("/{session_id}/tasks")
async def list_tasks(session_id: str, request: Request, container: ServiceContainer = Depends(get_container)) -> dict:
    return success_envelope(
        request_id=request.state.request_id,
        data=[item.model_dump(mode="json") for item in container.task_store.list(session_id)],
    )


@router.post("/{session_id}/tasks")
async def add_task(
    session_id: str,
    request: AddTaskRequest,
    container: ServiceContainer = Depends(get_container),
) -> dict:
    return success_envelope(
        request_id=request.state.request_id,
        data=container.task_store.add(session_id, request.title, request.description).model_dump(mode="json"),
    )


@router.patch("/{session_id}/tasks/{task_id}")
async def update_task(
    session_id: str,
    task_id: str,
    request: UpdateTaskRequest,
    container: ServiceContainer = Depends(get_container),
) -> dict:
    try:
        item = container.task_store.update(
            session_id,
            task_id,
            status=request.status,
            notes=request.notes,
            title=request.title,
            description=request.description,
        )
        return success_envelope(
            request_id=request.state.request_id,
            data=item.model_dump(mode="json"),
        )
    except KeyError as exc:
        raise HTTPException(status_code=404, detail=str(exc)) from exc


@router.get("/{session_id}/tools")
async def session_tools(session_id: str, request: Request, container: ServiceContainer = Depends(get_container)) -> dict:
    return success_envelope(
        request_id=request.state.request_id,
        data=await container.tool_registry.describe_tools(session_id),
    )
