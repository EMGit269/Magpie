from __future__ import annotations

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse

from .config import settings
from .middleware import RequestContextMiddleware
from .response_models import error_envelope
from .routers.agent import router as agent_router
from .routers.health import router as health_router
from .routers.sessions import router as sessions_router


app = FastAPI(title="Magpie Agent Service", version="0.1.0")
app.add_middleware(RequestContextMiddleware)
app.include_router(health_router)
app.include_router(sessions_router)
app.include_router(agent_router)


@app.get("/")
async def root() -> dict:
    return {
        "service": "Magpie Agent Service",
        "version": "0.1.0",
        "host_bridge_base_url": settings.host_bridge_base_url,
    }


@app.exception_handler(Exception)
async def unhandled_exception_handler(request: Request, exc: Exception):
    request_id = getattr(request.state, "request_id", "-")
    return JSONResponse(
        status_code=500,
        content=error_envelope(
            request_id=request_id,
            error=str(exc),
            message="Unhandled server error.",
        ),
    )
