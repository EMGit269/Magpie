# Magpie Agent Service

This directory contains the external agent foundation for Magpie.

## What is included

- session context store
- task list store
- host bridge client for the local Rhino/Grasshopper plugin
- tool registry that combines local tools and plugin host tools
- FastAPI service shell
- LangChain runtime scaffold

## Quick start

1. Start the Magpie plugin inside Rhino/Grasshopper and open the plugin window once.
2. Confirm the local plugin bridge is up at `http://127.0.0.1:8765/health`.
3. Create a Python environment and install dependencies:

```bash
pip install -e ./agent_service
```

4. Configure environment variables:

```bash
copy agent_service\.env.example agent_service\.env
```

Recommended default for the main agent:

- `OPENAI_BASE_URL=https://api.deepseek.com`
- `OPENAI_MODEL=deepseek-chat`
- `OPENAI_API_KEY=your_deepseek_api_key`

5. Start the service:

```bash
python agent_service/run_service.py
```

## Recommended first checks

- `GET /health`
- `GET /host/manifest`
- `GET /sessions/default/tools`
- `POST /sessions/default/tasks`
- `POST /graph/invoke`
- `POST /workflow/run`

## Notes

- The service is designed so the plugin remains the only process that mutates Grasshopper state.
- The external agent only plans, selects tools, and calls the plugin bridge.
- Task list management is implemented as both API endpoints and agent tools.
- The project now uses a more formal router/container layout so later auth, logging, and websocket support can be added without rewiring the whole app.
- Request IDs are returned in `x-request-id` response headers and API envelopes for cross-process tracing.
