from __future__ import annotations

from typing import Any, Generic, TypeVar

from pydantic import BaseModel


T = TypeVar("T")


class ApiEnvelope(BaseModel, Generic[T]):
    success: bool
    request_id: str
    data: T | None = None
    error: str | None = None
    message: str = ""


def success_envelope(*, request_id: str, data: Any, message: str = "") -> dict[str, Any]:
    return ApiEnvelope[Any](
        success=True,
        request_id=request_id,
        data=data,
        error=None,
        message=message,
    ).model_dump(mode="json")


def error_envelope(*, request_id: str, error: str, message: str = "") -> dict[str, Any]:
    return ApiEnvelope[Any](
        success=False,
        request_id=request_id,
        data=None,
        error=error,
        message=message,
    ).model_dump(mode="json")

