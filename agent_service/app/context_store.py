from __future__ import annotations

import json
from datetime import datetime, timezone
from pathlib import Path

from .schemas import SessionContext


class SessionContextStore:
    def __init__(self, root: Path) -> None:
        self._dir = root / "contexts"
        self._dir.mkdir(parents=True, exist_ok=True)

    def get(self, session_id: str) -> SessionContext:
        path = self._path(session_id)
        if not path.exists():
            return SessionContext(
                session_id=session_id,
                updated_at=datetime.now(timezone.utc),
            )
        return SessionContext.model_validate_json(path.read_text(encoding="utf-8"))

    def save(self, context: SessionContext) -> SessionContext:
        context.updated_at = datetime.now(timezone.utc)
        self._path(context.session_id).write_text(
            context.model_dump_json(indent=2),
            encoding="utf-8",
        )
        return context

    def set_goal(self, session_id: str, goal: str) -> SessionContext:
        context = self.get(session_id)
        context.user_goal = goal.strip()
        return self.save(context)

    def set_fact(self, session_id: str, key: str, value: object) -> SessionContext:
        context = self.get(session_id)
        context.facts[key] = value
        return self.save(context)

    def set_host_capabilities(self, session_id: str, capabilities: list[dict]) -> SessionContext:
        context = self.get(session_id)
        context.host_capabilities = capabilities
        return self.save(context)

    def list_all(self) -> list[SessionContext]:
        contexts: list[SessionContext] = []
        for path in self._dir.glob("*.json"):
            try:
                contexts.append(SessionContext.model_validate_json(path.read_text(encoding="utf-8")))
            except Exception:
                continue
        return contexts

    def delete(self, session_id: str) -> bool:
        path = self._path(session_id)
        if not path.exists():
            return False
        path.unlink()
        return True

    def set_title(self, session_id: str, title: str) -> SessionContext:
        context = self.get(session_id)
        context.title = title.strip()
        return self.save(context)

    def append_message(self, session_id: str, role: str, content: str, limit: int = 20) -> SessionContext:
        context = self.get(session_id)
        context.recent_messages.append({"role": role, "content": content})
        if len(context.recent_messages) > limit:
            context.recent_messages = context.recent_messages[-limit:]
        return self.save(context)

    def _path(self, session_id: str) -> Path:
        safe = "".join(ch if ch.isalnum() or ch in ("-", "_") else "_" for ch in session_id)
        return self._dir / f"{safe}.json"

