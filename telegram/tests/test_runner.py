import asyncio

import pytest

from telegram_mcp import runner


class _FakeSession:
    def __init__(self, identity: str):
        self._identity = identity

    def save(self):
        return self._identity


class _FakeClient:
    def __init__(self, *, authorized: bool, identity: str = "test-identity"):
        self.authorized = authorized
        self.connected = False
        self.started = False
        self.session = _FakeSession(identity)

    async def connect(self):
        self.connected = True

    async def is_user_authorized(self):
        return self.authorized

    async def start(self):
        self.started = True


@pytest.fixture(autouse=True)
def _isolate_session_locks(tmp_path, monkeypatch):
    import telegram_mcp.singleton as singleton_module

    original_init = singleton_module.SessionLock.__init__

    def _init_with_tmp_dir(self, label, identity, *, lock_dir=tmp_path):
        original_init(self, label, identity, lock_dir=lock_dir)

    monkeypatch.setattr(singleton_module.SessionLock, "__init__", _init_with_tmp_dir)
    monkeypatch.setattr(runner, "_lock_grace_seconds", lambda: 0.01)
    yield
    for lock in runner._session_locks.values():
        lock.release()
    runner._session_locks.clear()


def test_main_uses_standard_asyncio_run_without_event_loop_patching(monkeypatch):
    baseline_loop = asyncio.new_event_loop()
    try:
        standard_run_once = type(baseline_loop)._run_once
    finally:
        baseline_loop.close()

    observed = {}

    async def fake_main():
        loop = asyncio.get_running_loop()
        observed["task"] = asyncio.current_task()
        observed["run_once"] = type(loop)._run_once

    monkeypatch.setattr(runner, "_configure_allowed_roots_from_cli", lambda _argv: None)
    monkeypatch.setattr(runner._runtime, "_apply_exposed_tools_mode", lambda: None)
    monkeypatch.setattr(runner, "_main", fake_main)

    runner.main()

    assert observed["task"] is not None
    assert observed["run_once"] is standard_run_once
    assert not hasattr(runner, "nest_asyncio")


@pytest.mark.asyncio
async def test_connect_authorized_client_uses_existing_session_without_interactive_start():
    client = _FakeClient(authorized=True)

    await runner._connect_authorized_client("default", client)

    assert client.connected is True
    assert client.started is False


@pytest.mark.asyncio
async def test_connect_authorized_client_rejects_unauthorized_session():
    client = _FakeClient(authorized=False)

    with pytest.raises(RuntimeError, match="Interactive phone login is disabled"):
        await runner._connect_authorized_client("default", client)

    assert client.connected is True
    assert client.started is False


@pytest.mark.asyncio
async def test_connect_authorized_client_refuses_concurrent_duplicate_session():
    first = _FakeClient(authorized=True, identity="shared-session")
    second = _FakeClient(authorized=True, identity="shared-session")

    await runner._connect_authorized_client("default", first)

    with pytest.raises(runner.SessionLockError, match="already connected"):
        await runner._connect_authorized_client("default", second)

    assert second.connected is False


@pytest.mark.asyncio
async def test_connect_authorized_client_refuses_duplicate_session_across_labels():
    first = _FakeClient(authorized=True, identity="shared-session")
    second = _FakeClient(authorized=True, identity="shared-session")

    await runner._connect_authorized_client("default", first)

    with pytest.raises(runner.SessionLockError, match="already connected"):
        await runner._connect_authorized_client("work", second)

    assert second.connected is False


@pytest.mark.asyncio
async def test_connect_authorized_client_allows_different_sessions():
    first = _FakeClient(authorized=True, identity="session-a")
    second = _FakeClient(authorized=True, identity="session-b")

    await runner._connect_authorized_client("default", first)
    await runner._connect_authorized_client("work", second)

    assert first.connected is True
    assert second.connected is True


class _FakeSettings:
    def __init__(self):
        self.host = None
        self.port = None


class _FakeMcp:
    def __init__(self):
        self.settings = _FakeSettings()
        self.ran = None

    async def run_stdio_async(self):
        self.ran = "stdio"

    async def run_sse_async(self):
        self.ran = "sse"

    async def run_streamable_http_async(self):
        self.ran = "http"


@pytest.mark.asyncio
@pytest.mark.parametrize("transport", ["stdio", "unknown"])
async def test_serve_defaults_to_stdio(monkeypatch, transport):
    fake = _FakeMcp()
    monkeypatch.setattr(runner, "mcp", fake)

    await runner._serve(transport)

    assert fake.ran == "stdio"


@pytest.mark.asyncio
@pytest.mark.parametrize("transport", ["http", "sse"])
async def test_serve_http_transports_bind_host_and_port(monkeypatch, transport):
    fake = _FakeMcp()
    monkeypatch.setattr(runner, "mcp", fake)
    monkeypatch.setenv("MCP_HOST", "0.0.0.0")
    monkeypatch.setenv("MCP_PORT", "9000")

    await runner._serve(transport)

    assert fake.ran == transport
    assert fake.settings.host == "0.0.0.0"
    assert fake.settings.port == 9000


@pytest.mark.asyncio
async def test_serve_http_uses_default_host_and_port(monkeypatch):
    fake = _FakeMcp()
    monkeypatch.setattr(runner, "mcp", fake)
    monkeypatch.delenv("MCP_HOST", raising=False)
    monkeypatch.delenv("MCP_PORT", raising=False)

    await runner._serve("http")

    assert fake.ran == "http"
    assert fake.settings.host == "127.0.0.1"
    assert fake.settings.port == 8765
