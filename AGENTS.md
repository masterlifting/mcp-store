# Repository Guidance

## Layout

- `telegram/` is the Python Telegram MCP server; its entrypoint is `telegram/main.py` and package code is in `telegram/telegram_mcp/`.
- `github/` is a Windows PowerShell installer for the official GitHub MCP server. Work from the relevant package directory: the root has no shared build manifest.

## Telegram package

- Requires Python 3.10+ and uses `uv` (`telegram/pyproject.toml`, `telegram/uv.lock`).
- From `telegram/`, run `uv sync`, then use `uv run pytest` for tests. Coverage: `uv run pytest --cov --cov-report=term-missing --cov-report=xml`.
- Checks: `uv run black --check .`, `uv run flake8 .`, and `uv run pre-commit run --all-files`.
- Add or update deterministic tests for behavior changes; do not depend on live Telegram credentials.

## GitHub installer

- Run from `github/` with PowerShell. The entrypoint is `github/install.ps1`; its test harness is `github/tests/install.tests.ps1`.
- The installer may install Go through `winget`; do not run it without explicit confirmation for that external installation.

## Security

- Never read, print, commit, or transmit `.env`, Telegram session files, tokens, credentials, or private keys. Use `.env.example` and dummy values for local validation.
- The Telegram HTTP endpoint is unauthenticated; bind it to localhost unless an explicit security decision authorizes otherwise.
