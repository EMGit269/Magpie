from __future__ import annotations

import os
import json
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Settings:
    host_bridge_base_url: str
    state_dir: Path
    model_config_path: Path
    openai_api_key: str
    openai_base_url: str | None
    openai_model: str | None
    model_run_limit: int


def load_settings() -> Settings:
    root = Path(__file__).resolve().parents[1]
    state_dir = Path(os.getenv("MAGPIE_AGENT_STATE_DIR", root / ".state"))
    state_dir.mkdir(parents=True, exist_ok=True)
    local_app_data = Path(os.getenv("LOCALAPPDATA", str(root)))
    return Settings(
        host_bridge_base_url=os.getenv("MAGPIE_HOST_BRIDGE_BASE_URL", "http://127.0.0.1:8765").rstrip("/"),
        state_dir=state_dir,
        model_config_path=Path(os.getenv("MAGPIE_AGENT_MODEL_CONFIG", local_app_data / "Magpie" / "agent-service-model.json")),
        openai_api_key=os.getenv("OPENAI_API_KEY", "").strip(),
        openai_base_url=os.getenv("OPENAI_BASE_URL", "").strip() or None,
        openai_model=os.getenv("OPENAI_MODEL", "").strip() or None,
        model_run_limit=int(os.getenv("MAGPIE_AGENT_MODEL_RUN_LIMIT", "8")),
    )


def load_model_config(settings: Settings) -> tuple[str, str | None, str | None]:
    api_key = settings.openai_api_key
    base_url = settings.openai_base_url
    model = settings.openai_model

    path = settings.model_config_path
    if path.exists():
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
            api_key = str(data.get("apiKey", "") or "").strip()
            base_url = str(data.get("baseUrl", "") or "").strip() or None
            model = str(data.get("model", "") or "").strip() or None
        except Exception:
            pass

    return api_key, base_url, model


settings = load_settings()
