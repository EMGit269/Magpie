from __future__ import annotations

import time
from uuid import uuid4

from fastapi import Request
from starlette.middleware.base import BaseHTTPMiddleware

from .logging_utils import get_logger, request_id_var, structured


logger = get_logger("magpie.agent_service.http")


class RequestContextMiddleware(BaseHTTPMiddleware):
    async def dispatch(self, request: Request, call_next):
        request_id = request.headers.get("x-request-id", "").strip() or f"req_{uuid4().hex[:12]}"
        token = request_id_var.set(request_id)
        request.state.request_id = request_id
        started = time.perf_counter()

        structured(
            logger,
            20,
            "request_started",
            method=request.method,
            path=request.url.path,
            query=str(request.url.query or ""),
        )

        try:
            response = await call_next(request)
        except Exception:
            elapsed_ms = round((time.perf_counter() - started) * 1000, 2)
            structured(
                logger,
                40,
                "request_failed",
                method=request.method,
                path=request.url.path,
                elapsed_ms=elapsed_ms,
            )
            request_id_var.reset(token)
            raise

        elapsed_ms = round((time.perf_counter() - started) * 1000, 2)
        response.headers["x-request-id"] = request_id
        structured(
            logger,
            20,
            "request_completed",
            method=request.method,
            path=request.url.path,
            status_code=response.status_code,
            elapsed_ms=elapsed_ms,
        )
        request_id_var.reset(token)
        return response

