import json
import struct
from types import SimpleNamespace

import pytest
from telethon.tl import functions
from telethon.tl.types import Channel, InputChannel

from telegram_mcp.tools import chats


def _supergroup(*, forum=False):
    return Channel(
        id=12345,
        title="Hermes Topics",
        photo=None,
        date=None,
        creator=True,
        left=False,
        broadcast=False,
        verified=False,
        megagroup=True,
        restricted=False,
        signatures=False,
        min=False,
        scam=False,
        has_link=False,
        has_geo=False,
        slowmode_enabled=False,
        call_active=False,
        call_not_empty=False,
        fake=False,
        gigagroup=False,
        noforwards=False,
        join_to_send=False,
        join_request=False,
        forum=forum,
        stories_hidden=False,
        stories_hidden_min=False,
        stories_unavailable=False,
        access_hash=67890,
    )


class RecordingClient:
    def __init__(self, result=None):
        self.requests = []
        self.result = result or SimpleNamespace(updates=[])

    async def __call__(self, request):
        self.requests.append(request)
        return self.result


class DialogClient:
    def __init__(self, dialogs):
        self.dialogs = dialogs

    async def get_dialogs(self, **kwargs):
        return self.dialogs


def test_get_forum_topics_by_id_request_serializes_exact_topic_vector():
    request = chats.GetForumTopicsByIDRequest(
        channel=InputChannel(channel_id=12345, access_hash=67890), topics=[33429]
    )

    serialized = request._bytes()

    assert struct.unpack("<I", serialized[:4])[0] == 0xB0831EB9
    assert request.to_dict()["topics"] == [33429]
    assert struct.unpack("<I", serialized[-12:-8])[0] == 0x1CB5C415
    assert struct.unpack("<i", serialized[-8:-4])[0] == 1
    assert struct.unpack("<i", serialized[-4:])[0] == 33429


@pytest.mark.asyncio
async def test_enable_forum_topics_sends_toggle_forum_request(monkeypatch):
    entity = _supergroup(forum=False)
    client = RecordingClient()

    async def fake_resolve(chat_id, cl):
        return entity

    monkeypatch.setattr(chats, "get_client", lambda account=None: client)
    monkeypatch.setattr(chats, "resolve_entity", fake_resolve)

    result = await chats.enable_forum_topics(chat_id=12345)

    assert result == "Forum topics enabled for Hermes Topics."
    assert len(client.requests) == 1
    request = client.requests[0]
    assert isinstance(request, functions.channels.ToggleForumRequest)
    assert request.channel is entity
    assert request.enabled is True
    assert request.tabs is True
    assert entity.forum is True


@pytest.mark.asyncio
async def test_create_forum_topic_sends_raw_create_forum_topic_request(monkeypatch):
    entity = _supergroup(forum=True)
    client = RecordingClient(SimpleNamespace(updates=[SimpleNamespace(id=777)]))

    async def fake_resolve(chat_id, cl):
        return entity

    monkeypatch.setattr(chats, "get_client", lambda account=None: client)
    monkeypatch.setattr(chats, "resolve_entity", fake_resolve)

    result = await chats.create_forum_topic(chat_id=12345, title="Dev", icon_color=0x6FB9F0)

    payload = json.loads(result)
    assert payload["results"] == [{"chat_id": -1000000012345, "topic_id": 777, "title": "Dev"}]
    assert len(client.requests) == 1
    request = client.requests[0]
    assert isinstance(request, chats.CreateForumTopicRequest)
    assert request.peer is entity
    assert request.title == "Dev"
    assert request.icon_color == 0x6FB9F0
    assert isinstance(request.random_id, int)


@pytest.mark.asyncio
async def test_create_forum_topic_requires_forum_enabled(monkeypatch):
    entity = _supergroup(forum=False)
    client = RecordingClient()

    async def fake_resolve(chat_id, cl):
        return entity

    monkeypatch.setattr(chats, "get_client", lambda account=None: client)
    monkeypatch.setattr(chats, "resolve_entity", fake_resolve)

    result = await chats.create_forum_topic(chat_id=12345, title="Dev")

    assert (
        result
        == "The specified supergroup does not have forum topics enabled. Use enable_forum_topics first."
    )
    assert client.requests == []


@pytest.mark.asyncio
async def test_list_topics_includes_read_watermarks_and_zero_when_unavailable(monkeypatch):
    entity = _supergroup(forum=True)
    result = SimpleNamespace(
        topics=[
            SimpleNamespace(
                id=101,
                title="Observed",
                read_inbox_max_id=97,
                top_message=105,
                closed=False,
                hidden=False,
            ),
            SimpleNamespace(
                id=201,
                title="Unavailable",
                read_inbox_max_id=None,
                top_message=None,
                closed=False,
                hidden=False,
            ),
        ],
        messages=[],
    )
    client = RecordingClient(result)

    async def fake_resolve(chat_id, cl):
        return entity

    monkeypatch.setattr(chats, "get_client", lambda account=None: client)
    monkeypatch.setattr(chats, "resolve_entity", fake_resolve)

    payload = json.loads(await chats.list_topics(chat_id=12345))

    assert payload["results"] == [
        {
            "id": 101,
            "title": "Observed",
            "read_through_message_id": 97,
            "latest_message_id": 105,
            "closed": False,
            "hidden": False,
        },
        {
            "id": 201,
            "title": "Unavailable",
            "read_through_message_id": 0,
            "latest_message_id": 0,
            "closed": False,
            "hidden": False,
        },
    ]


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "kwargs, expected",
    [
        ({"chat_id": "12345"}, "chat_id must be a non-zero integer."),
        ({"chat_id": True}, "chat_id must be a non-zero integer."),
        ({"chat_id": 12345, "offset_topic": True}, "offset_topic must be a non-negative integer."),
        ({"chat_id": 12345, "offset_topic": -1}, "offset_topic must be a non-negative integer."),
        ({"chat_id": 12345, "limit": 101}, "limit must be an integer between 1 and 100."),
    ],
)
async def test_list_topics_rejects_malformed_or_oversized_inputs(monkeypatch, kwargs, expected):
    monkeypatch.setattr(chats, "get_client", lambda account=None: pytest.fail("client used"))

    result = await chats.list_topics(**kwargs)

    assert result == expected


@pytest.mark.asyncio
async def test_list_chats_includes_dialog_read_watermarks_without_changing_filtering(monkeypatch):
    entity = SimpleNamespace(id=12345, title="Observed", username=None)
    dialog = SimpleNamespace(
        entity=entity,
        dialog=SimpleNamespace(
            read_inbox_max_id=97,
            top_message=105,
            unread_mark=False,
            notify_settings=None,
        ),
        unread_count=1,
        archived=False,
        unread_mentions_count=0,
    )
    unavailable_entity = SimpleNamespace(id=54321, title="Unavailable", username=None)
    unavailable_dialog = SimpleNamespace(
        entity=unavailable_entity,
        dialog=SimpleNamespace(
            read_inbox_max_id=None,
            top_message=None,
            unread_mark=False,
            notify_settings=None,
        ),
        unread_count=0,
        archived=False,
        unread_mentions_count=0,
    )
    client = DialogClient([dialog, unavailable_dialog])

    async def connected(cl):
        return None

    monkeypatch.setattr(chats, "get_client", lambda account=None: client)
    monkeypatch.setattr(chats, "ensure_connected", connected)
    monkeypatch.setattr(chats, "get_marked_id", lambda item: item.id)
    monkeypatch.setattr(chats, "get_entity_type", lambda item: "Group")
    monkeypatch.setattr(chats, "get_entity_filter_type", lambda item: "group")

    payload = json.loads(await chats.list_chats(chat_type="group", unread_only=True))

    assert payload["results"] == [
        {
            "chat_id": 12345,
            "title": "Observed",
            "type": "Group",
            "read_through_message_id": 97,
            "latest_message_id": 105,
            "unread": 1,
            "muted": False,
            "archived": False,
        }
    ]
