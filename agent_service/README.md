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
3. With Python 3.11 or newer, create and activate a virtual environment,
   then install dependencies from the repository root:

```bash
pip install -e ./agent_service
```

4. Configure environment variables in the shell that starts the service.
   For example, in PowerShell:

```powershell
$env:OPENAI_BASE_URL = "https://api.deepseek.com"
$env:OPENAI_MODEL = "deepseek-chat"
$env:OPENAI_API_KEY = "your_new_deepseek_api_key"
```

`.env.example` lists the supported variables; copying it to `.env` alone
does not load them. The plugin can also supply model configuration through
its settings panel. Never commit real credentials or `.state/` data.

5. Start the service:

```bash
python agent_service/run_service.py
```

The service binds to `127.0.0.1:8000` by default. Use `--port 8123` to
select a different port, or `--reload` for development. The plugin's
auto-start process supplies `--port` automatically; set its start command
to `python agent_service/run_service.py` with the repository root as the
working directory. If the working directory is `agent_service/`, use
`python run_service.py` instead.

## Regression checks

After installing dependencies, run from the repository root:

```bash
python -m unittest discover -s agent_service/tests -v
```

The tests use temporary session storage and do not require Rhino or a model API key.

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
