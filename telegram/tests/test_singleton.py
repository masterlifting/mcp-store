import pytest

from telegram_mcp.singleton import (
    SessionLock,
    SessionLockError,
    session_identity,
)


class _FileSession:
    def __init__(self, filename):
        self.filename = filename


class _FileClient:
    def __init__(self, filename):
        self.session = _FileSession(filename)


def test_file_session_symlink_aliases_contend_for_one_lock(tmp_path):
    session_path = tmp_path / "sessions" / "account.session"
    session_path.parent.mkdir()
    session_path.touch()
    alias_path = tmp_path / "account-alias.session"
    try:
        alias_path.symlink_to(session_path)
    except (OSError, NotImplementedError) as error:
        pytest.skip(f"symlink creation unavailable: {error}")

    assert session_identity(_FileClient(session_path)) == session_identity(_FileClient(alias_path))

    first = SessionLock("first", session_identity(_FileClient(session_path)), lock_dir=tmp_path)
    second = SessionLock("second", session_identity(_FileClient(alias_path)), lock_dir=tmp_path)
    first.acquire(grace_seconds=0)
    try:
        with pytest.raises(SessionLockError, match="already connected"):
            second.acquire(grace_seconds=0)
    finally:
        first.release()
        second.release()


def test_distinct_file_sessions_can_be_locked_concurrently(tmp_path):
    first_path = tmp_path / "first.session"
    second_path = tmp_path / "second.session"
    first = SessionLock(
        "first", session_identity(_FileClient(first_path)), lock_dir=tmp_path / "locks"
    )
    second = SessionLock(
        "second", session_identity(_FileClient(second_path)), lock_dir=tmp_path / "locks"
    )

    assert first.path != second.path
    first.acquire(grace_seconds=0)
    try:
        second.acquire(grace_seconds=0)
    finally:
        first.release()
        second.release()
