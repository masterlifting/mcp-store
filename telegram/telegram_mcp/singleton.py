"""Per-session file lock guarding against concurrent Telegram connections.

The lock is keyed by the canonical session identity rather than the account
label, so separate server processes cannot race the same Telegram auth key.
The operating system releases the advisory lock when the process exits.
"""

from __future__ import annotations

import hashlib
import os
import tempfile
import time
from pathlib import Path
from typing import IO, Optional

if os.name == "nt":
    import msvcrt

    def _try_lock(fh: IO) -> bool:
        try:
            fh.seek(0)
            msvcrt.locking(fh.fileno(), msvcrt.LK_NBLCK, 1)
            return True
        except OSError:
            return False

    def _unlock(fh: IO) -> None:
        try:
            fh.seek(0)
            msvcrt.locking(fh.fileno(), msvcrt.LK_UNLCK, 1)
        except OSError:
            pass

else:
    import fcntl

    def _try_lock(fh: IO) -> bool:
        try:
            fcntl.flock(fh.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
            return True
        except OSError:
            return False

    def _unlock(fh: IO) -> None:
        try:
            fcntl.flock(fh.fileno(), fcntl.LOCK_UN)
        except OSError:
            pass


DEFAULT_LOCK_DIR = Path(tempfile.gettempdir()) / "telegram-mcp-locks"
DEFAULT_GRACE_SECONDS = 20.0
DEFAULT_POLL_INTERVAL = 0.5


class SessionLockError(RuntimeError):
    """Raised when a session lock is not acquired before its grace period."""


class SessionLock:
    """Exclusive, OS-released lock for one Telegram session."""

    def __init__(
        self,
        label: str,
        session_identity: str,
        *,
        lock_dir: Path = DEFAULT_LOCK_DIR,
    ):
        digest = hashlib.sha256(session_identity.encode("utf-8")).hexdigest()[:16]
        lock_dir.mkdir(parents=True, exist_ok=True)
        # The session digest is the lock identity; labels are only local aliases.
        self.path = lock_dir / f"{digest}.lock"
        self._fh: Optional[IO] = None

    def acquire(
        self,
        *,
        grace_seconds: float = DEFAULT_GRACE_SECONDS,
        poll_interval: float = DEFAULT_POLL_INTERVAL,
    ) -> None:
        """Wait briefly for a replacement process, then acquire exclusively."""
        fh = open(self.path, "a+")
        deadline = time.monotonic() + grace_seconds
        while True:
            if _try_lock(fh):
                self._fh = fh
                return
            if time.monotonic() >= deadline:
                fh.close()
                raise SessionLockError(
                    "Another telegram-mcp process is already connected with this "
                    "session. Refusing to connect a second time; retry once the "
                    "other process is gone."
                )
            time.sleep(poll_interval)

    def release(self) -> None:
        """Release the lock and close its handle; repeated calls are harmless."""
        if self._fh is not None:
            _unlock(self._fh)
            self._fh.close()
            self._fh = None


def session_identity(client: object) -> str:
    """Derive a stable identity value used only to calculate the lock filename."""
    session = getattr(client, "session", None)
    if session is not None:
        filename = getattr(session, "filename", None)
        if filename:
            canonical_path = os.path.normcase(os.path.realpath(os.path.abspath(filename)))
            return f"file:{canonical_path}"
        save = getattr(session, "save", None)
        if callable(save):
            try:
                saved = save()
            except Exception:
                saved = None
            if saved:
                return f"string:{saved}"
    return f"anon:{id(client)}"
