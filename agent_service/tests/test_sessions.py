from __future__ import annotations

import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

from fastapi.testclient import TestClient

from app.context_store import SessionContextStore
from app.dependencies import get_container
from app.main import app
from app.task_store import TaskStore


class SessionMutationTests(unittest.TestCase):
    def setUp(self) -> None:
        self.storage = tempfile.TemporaryDirectory()
        self.addCleanup(self.storage.cleanup)
        root = Path(self.storage.name)
        self.container = SimpleNamespace(
            context_store=SessionContextStore(root),
            task_store=TaskStore(root),
        )
        app.dependency_overrides[get_container] = lambda: self.container
        self.addCleanup(app.dependency_overrides.pop, get_container)
        self.client = self.enterContext(TestClient(app))

    def assert_success(self, response) -> dict:
        self.assertEqual(response.status_code, 200, response.text)
        body = response.json()
        self.assertTrue(body["success"])
        self.assertEqual(body["request_id"], "review-request")
        self.assertEqual(response.headers["x-request-id"], "review-request")
        return body["data"]

    def test_fact_is_saved_and_response_has_request_id(self) -> None:
        response = self.client.post(
            "/sessions/review/facts",
            json={"key": "units", "value": "mm"},
            headers={"x-request-id": "review-request"},
        )
        data = self.assert_success(response)
        self.assertEqual(data["facts"]["units"], "mm")
        self.assertEqual(self.container.context_store.get("review").facts["units"], "mm")

    def test_task_can_be_created_and_updated(self) -> None:
        headers = {"x-request-id": "review-request"}
        created = self.assert_success(self.client.post(
            "/sessions/review/tasks", json={"title": "Inspect canvas"}, headers=headers,
        ))
        self.assertEqual(created["title"], "Inspect canvas")
        updated = self.assert_success(self.client.patch(
            f"/sessions/review/tasks/{created['id']}",
            json={"status": "done", "notes": "Verified"}, headers=headers,
        ))
        self.assertEqual(updated["status"], "done")
        saved = self.container.task_store.list("review")
        self.assertEqual(len(saved), 1)
        self.assertEqual(saved[0].status, "done")
        self.assertEqual(saved[0].notes, "Verified")

    def test_missing_task_returns_404(self) -> None:
        response = self.client.patch("/sessions/review/tasks/missing", json={"status": "done"})
        self.assertEqual(response.status_code, 404)

    def test_invalid_status_does_not_change_task(self) -> None:
        task = self.container.task_store.add("review", "Inspect canvas")
        response = self.client.patch(
            f"/sessions/review/tasks/{task.id}", json={"status": "invalid"},
        )
        self.assertEqual(response.status_code, 422)
        self.assertEqual(self.container.task_store.list("review")[0].status, "todo")
