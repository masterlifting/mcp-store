import json
from datetime import datetime, timezone
from types import SimpleNamespace

import pytest

from telegram_mcp.tools import messages


class _ContextClient:
    def __init__(self, reply, replied_message):
        self._responses = [[], reply, [], replied_message]

    async def get_messages(self, _entity, **_kwargs):
        return self._responses.pop(0)


def _message(text, *, reply_to=None, url=None):
    entity = SimpleNamespace(url=url) if url else None
    return SimpleNamespace(
        id=1 if reply_to else 2,
        sender=SimpleNamespace(first_name="Sender", last_name=None),
        sender_id=7,
        date=datetime(2026, 1, 1, tzinfo=timezone.utc),
        message=text,
        entities=[entity] if entity else [],
        reply_to=reply_to,
    )


@pytest.mark.asyncio
async def test_message_context_exposes_hidden_links_for_message_and_reply(monkeypatch):
    link = "https://t.me/c/1565619651/424991"
    dirty_link = f"{link}\x00"
    replied = _message("replied\x00", url=dirty_link)
    reply = _message(
        "reply\x00",
        reply_to=SimpleNamespace(reply_to_msg_id=2),
        url=dirty_link,
    )
    client = _ContextClient(reply, replied)
    monkeypatch.setattr(messages, "get_client", lambda account: client)

    async def resolve_entity(_chat_id, _client):
        return "chat"

    monkeypatch.setattr(messages, "resolve_entity", resolve_entity)

    result = json.loads(await messages.get_message_context(42, 1, account="test"))

    record = result["results"][0]
    assert record["link_urls"] == [link]
    assert record["text"] == "reply"
    assert record["replied_message"]["link_urls"] == [link]
    assert record["replied_message"]["text"] == "replied"


def test_link_urls_strip_controls_but_preserve_valid_url():
    link = "https://t.me/c/1565619651/424991"
    msg = _message("text", url=f"{link}\x00\n\t")

    assert messages._link_urls(msg) == [link]
