from __future__ import annotations

from datetime import datetime, timezone
import json
import shutil
from pathlib import Path
from uuid import uuid4

from .schemas import TaskItem, TaskStatus


class TaskStore:
    def __init__(self, root: Path) -> None:
        self._dir = root / "tasks"
        self._dir.mkdir(parents=True, exist_ok=True)

    def list(self, session_id: str) -> list[TaskItem]:
        path = self._path(session_id)
        if not path.exists():
            return []
        raw = path.read_text(encoding="utf-8-sig")
        payload = self._load_payload(path, raw)
        return [TaskItem.model_validate(item) for item in payload]

    def add(self, session_id: str, title: str, description: str = "") -> TaskItem:
        now = datetime.now(timezone.utc)
        tasks = self.list(session_id)
        item = TaskItem(
            id=f"task_{uuid4().hex[:10]}",
            title=title.strip(),
            description=description.strip(),
            status="todo",
            notes="",
            created_at=now,
            updated_at=now,
        )
        tasks.append(item)
        self._save(session_id, tasks)
        return item

    def update(
        self,
        session_id: str,
        task_id: str,
        *,
        status: TaskStatus | None = None,
        notes: str | None = None,
        title: str | None = None,
        description: str | None = None,
    ) -> TaskItem:
        tasks = self.list(session_id)
        for item in tasks:
            if item.id != task_id:
                continue
            if status is not None:
                item.status = status
            if notes is not None:
                item.notes = notes
            if title is not None:
                item.title = title
            if description is not None:
                item.description = description
            item.updated_at = datetime.now(timezone.utc)
            self._save(session_id, tasks)
            return item
        raise KeyError(f"Task not found: {task_id}")

    def _save(self, session_id: str, tasks: list[TaskItem]) -> None:
        payload = [item.model_dump(mode="json") for item in tasks]
        self._path(session_id).write_text(
            json.dumps(payload, indent=2, ensure_ascii=False),
            encoding="utf-8",
        )

    def _path(self, session_id: str) -> Path:
        safe = "".join(ch if ch.isalnum() or ch in ("-", "_") else "_" for ch in session_id)
        return self._dir / f"{safe}.json"

    def delete(self, session_id: str) -> bool:
        path = self._path(session_id)
        if not path.exists():
            return False
        path.unlink()
        return True

    def _load_payload(self, path: Path, raw: str) -> list[dict]:
        try:
            data = json.loads(raw)
            return data if isinstance(data, list) else []
        except json.JSONDecodeError:
            repaired = self._try_repair_json_array(raw)
            if repaired is None:
                raise

            backup = path.with_suffix(path.suffix + ".bak")
            shutil.copyfile(path, backup)
            path.write_text(json.dumps(repaired, indent=2, ensure_ascii=False), encoding="utf-8")
            return repaired

    @staticmethod
    def _try_repair_json_array(raw: str) -> list[dict] | None:
        start = raw.find("[")
        if start < 0:
            return None

        depth = 0
        in_string = False
        escaped = False
        end = -1
        for index in range(start, len(raw)):
            ch = raw[index]
            if in_string:
                if escaped:
                    escaped = False
                elif ch == "\\":
                    escaped = True
                elif ch == "\"":
                    in_string = False
                continue

            if ch == "\"":
                in_string = True
            elif ch == "[":
                depth += 1
            elif ch == "]":
                depth -= 1
                if depth == 0:
                    end = index
                    break

        if end < 0:
            return None

        candidate = raw[start:end + 1]
        try:
            data = json.loads(candidate)
            return data if isinstance(data, list) else None
        except json.JSONDecodeError:
            return None
