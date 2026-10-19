from __future__ import annotations

from typing import Any

import httpx

from .logging_utils import get_logger, structured


logger = get_logger("magpie.agent_service.host_bridge")


class HostBridgeClient:
    def __init__(self, base_url: str) -> None:
        self._base_url = base_url.rstrip("/")

    async def health(self) -> dict[str, Any]:
        async with httpx.AsyncClient(timeout=20.0) as client:
            structured(logger, 20, "host_bridge_health_request", base_url=self._base_url)
            response = await client.get(f"{self._base_url}/health")
            response.raise_for_status()
            return response.json()

    async def manifest(self) -> dict[str, Any]:
        async with httpx.AsyncClient(timeout=20.0) as client:
            structured(logger, 20, "host_bridge_manifest_request", base_url=self._base_url)
            response = await client.get(f"{self._base_url}/tools/manifest")
            response.raise_for_status()
            return response.json()

    async def invoke(self, tool: str, args: dict[str, Any], request_id: str, document_id: str | None = None) -> dict[str, Any]:
        payload = {
            "request_id": request_id,
            "tool": tool,
            "args": args,
        }
        if document_id is not None:
            payload["document_id"] = document_id
        async with httpx.AsyncClient(timeout=90.0) as client:
            structured(logger, 20, "host_bridge_invoke_request", tool=tool, request_id=request_id)
            response = await client.post(f"{self._base_url}/tools/invoke", json=payload)
            response.raise_for_status()
            result = response.json()
            structured(
                logger,
                20,
                "host_bridge_invoke_response",
                tool=tool,
                request_id=request_id,
                status=result.get("status"),
            )
            return result
