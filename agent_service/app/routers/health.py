from __future__ import annotations

from fastapi import APIRouter, Depends, Request

from ..container import ServiceContainer
from ..config import load_model_config
from ..dependencies import get_container
from ..response_models import success_envelope


router = APIRouter(tags=["health"])


@router.get("/health")
async def health(request: Request, container: ServiceContainer = Depends(get_container)) -> dict:
    api_key, base_url, model_name = load_model_config(container.settings)
    host_error = None
    try:
        host = await container.host_bridge.health()
    except Exception as exc:
        host = {
            "status": "unavailable",
            "base_url": container.settings.host_bridge_base_url,
            "available": False,
        }
        host_error = str(exc)

    return success_envelope(
        request_id=request.state.request_id,
        data={
            "status": "ok",
            "runtime": "langgraph",
            "graph_configured": bool(api_key and model_name),
            "host_bridge": host,
            "host_bridge_available": host.get("available", True) if isinstance(host, dict) else True,
            "host_bridge_error": host_error,
            "model_configured": bool(api_key and model_name),
            "model_base_url": base_url,
            "model_name": model_name,
        },
    )


@router.get("/host/manifest")
async def get_host_manifest(request: Request, container: ServiceContainer = Depends(get_container)) -> dict:
    return success_envelope(
        request_id=request.state.request_id,
        data=await container.host_bridge.manifest(),
    )
