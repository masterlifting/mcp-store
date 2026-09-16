import json
from datetime import datetime, timezone
from types import SimpleNamespace

import pytest
from telethon.tl import functions

from telegram_mcp.tools import messages


class RecordingClient:
    def __init__(self, topic_messages=None, topic_metadata=None):
        self.topic_messages = topic_messages
        self.topic_metadata = topic_metadata
        self.get_messages_calls = []
        self.requests = []

    async def get_messages(self, entity, **kwargs):
        self.get_messages_calls.append((entity, kwargs))
        return self.topic_messages

    async def __call__(self, request):
        self.requests.append(request)
        if isinstance(request, messages.GetForumTopicsByIDRequest):
            return self.topic_metadata


class IteratingClient:
    def __init__(self, iterated_messages, messages_by_id=None):
        self.iterated_messages = iterated_messages
        self.messages_by_id = messages_by_id or {}
        self.iter_messages_calls = []

    async def get_messages(self, entity, **kwargs):
        return self.messages_by_id.get(kwargs.get("ids"))

    async def iter_messages(self, entity, **kwargs):
        self.iter_messages_calls.append((entity, kwargs))
        for message in self.iterated_messages:
            yield message


class ServerScopedTopicClient(IteratingClient):
    def __init__(self, messages_by_topic, messages_by_id=None):
        super().__init__([], messages_by_id)
        self.messages_by_topic = messages_by_topic

    async def iter_messages(self, entity, **kwargs):
        self.iter_messages_calls.append((entity, kwargs))
        for message in self.messages_by_topic.get(kwargs["reply_to"], []):
            yield message


class ReadAcknowledgingClient:
    def __init__(self, messages_by_entity, observed_watermark=None, verification_error=None):
        self.messages_by_entity = messages_by_entity
        self.observed_watermark = observed_watermark
        self.verification_error = verification_error
        self.get_messages_calls = []
        self.read_acknowledgements = []
        self.requests = []

    async def send_read_acknowledge(self, entity, **kwargs):
        self.read_acknowledgements.append((entity, kwargs))

    async def get_messages(self, entity, **kwargs):
        self.get_messages_calls.append((entity, kwargs))
        return self.messages_by_entity.get(entity, {}).get(kwargs.get("ids"))

    async def get_input_entity(self, entity):
        return entity

    async def __call__(self, request):
        self.requests.append(request)
        if self.verification_error:
            raise self.verification_error
        return SimpleNamespace(
            dialogs=[SimpleNamespace(read_inbox_max_id=self.observed_watermark)]
        )


class CanonicalTestEntity:
    def __init__(self, marked_id):
        self.id = marked_id

    def __eq__(self, other):
        return other == "forum-entity" or (
            isinstance(other, CanonicalTestEntity) and other.id == self.id
        )

    def __hash__(self):
        return hash("forum-entity")


async def _resolve(chat_id, client):
    return CanonicalTestEntity(chat_id)


def _message(message_id, text, reply_to=None, action=None):
    return SimpleNamespace(
        id=message_id,
        sender=SimpleNamespace(first_name="Author", last_name=None, username="author"),
        sender_id=1,
        date=datetime(2026, 7, 20, tzinfo=timezone.utc),
        message=text,
        reply_to=reply_to,
        media=None,
        web_preview=None,
        grouped_id=None,
        fwd_from=None,
        edit_date=None,
        via_bot_id=None,
        pinned=False,
        buttons=None,
        entities=None,
        action=action,
        ttl_period=None,
        replies=None,
        reactions=None,
        views=None,
        forwards=None,
        out=False,
    )


class TopicCreateAction:
    pass


def _topic_metadata(topic_id, read_inbox_max_id):
    return SimpleNamespace(
        topics=[SimpleNamespace(id=topic_id, read_inbox_max_id=read_inbox_max_id)]
    )


@pytest.mark.asyncio
async def test_get_topic_messages_filters_by_topic(monkeypatch):
    topic_id = 33422
    client = RecordingClient()

    async def get_messages(entity, **kwargs):
        client.get_messages_calls.append((entity, kwargs))
        if "ids" in kwargs:
            return _message(topic_id, "Topic root", action=TopicCreateAction())
        return [_message(101, "Topic message")]

    client.get_messages = get_messages
    monkeypatch.setattr(messages, "get_client", lambda account=None: client)
    monkeypatch.setattr(messages, "resolve_entity", _resolve)
    monkeypatch.setattr(messages.types, "MessageActionTopicCreate", TopicCreateAction)

    result = await messages.get_topic_messages(
        chat_id=-1001528034935, topic_id=topic_id, page=2, page_size=10
    )

    assert "Topic message" in result
    assert client.get_messages_calls == [
        ("forum-entity", {"ids": topic_id}),
        (
            "forum-entity",
            {"limit": 10, "add_offset": 10, "reply_to": topic_id},
        ),
    ]


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "kwargs, expected",
    [
        (
            {"chat_id": "12345", "topic_id": 33422},
            "chat_id must be a canonical marked negative integer.",
        ),
        (
            {"chat_id": True, "topic_id": 33422},
            "chat_id must be a canonical marked negative integer.",
        ),
        ({"chat_id": -1001528034935, "topic_id": "33422"}, "topic_id must be a positive integer."),
        ({"chat_id": -1001528034935, "topic_id": True}, "topic_id must be a positive integer."),
        (
            {"chat_id": -1001528034935, "topic_id": 33422, "page": 0},
            "page must be a positive integer.",
        ),
        (
            {"chat_id": -1001528034935, "topic_id": 33422, "page_size": 101},
            "page_size must be an integer between 1 and 100.",
        ),
    ],
)
async def test_get_topic_messages_rejects_invalid_pagination_and_ids(
    monkeypatch, kwargs, expected
):
    monkeypatch.setattr(messages, "get_client", lambda account=None: pytest.fail("client used"))

    result = await messages.get_topic_messages(**kwargs)

    assert result == expected


@pytest.mark.asyncio
async def test_get_topic_messages_rejects_unverified_topic_root(monkeypatch):
    client = RecordingClient()
    monkeypatch.setattr(messages, "get_client", lambda account=None: client)
    monkeypatch.setattr(messages, "resolve_entity", _resolve)

    result = await messages.get_topic_messages(chat_id=-1001528034935, topic_id=33422)

    assert result == "Topic root 33422 was not found in chat -1001528034935."
    assert client.get_messages_calls == [("forum-entity", {"ids": 33422})]


@pytest.mark.asyncio
async def test_get_messages_in_range_uses_exact_id_bounds_and_returns_terminal_metadata(
    monkeypatch,
):
    client = IteratingClient(
        [
            _message(100, "Lower boundary"),
            _message(101, "Included"),
            _message(105, "Upper boundary"),
            _message(106, "Outside range"),
        ]
    )
    monkeypatch.setattr(messages, "get_client", lambda account=None: client)
    monkeypatch.setattr(messages, "resolve_entity", _resolve)

    result = await messages.get_messages_in_range(
        chat_id=-1001528034935, after_message_id=100, through_message_id=105
    )

    payload = json.loads(result)
    assert client.iter_messages_calls == [("forum-entity", {"min_id": 100, "max_id": 106})]
    assert [record["id"] for record in payload["results"]] == [101, 105]
    assert payload | {"results": None} == {
        "results": None,
        "chat_id": -1001528034935,
        "after_message_id": 100,
        "through_message_id": 105,
        "retrieved_count": 2,
        "range_complete": True,
    }


@pytest.mark.asyncio
async def test_get_messages_in_range_rejects_boolean_chat_id(monkeypatch):
    monkeypatch.setattr(messages, "get_client", lambda account=None: pytest.fail("client used"))

    result = await messages.get_messages_in_range(
        chat_id=True, after_message_id=100, through_message_id=101
    )

    assert result == "chat_id must be a canonical marked negative integer."


@pytest.mark.asyncio
async def test_get_messages_in_range_rejects_username_chat_id(monkeypatch):
    monkeypatch.setattr(messages, "get_client", lambda account=None: pytest.fail("client used"))

    result = await messages.get_messages_in_range(
        chat_id="forum_username", after_message_id=100, through_message_id=101
    )

    assert result == "chat_id must be a canonical marked negative integer."


@pytest.mark.asyncio
async def test_bounded_read_rejects_resolved_canonical_id_mismatch(monkeypatch):
    monkeypatch.setattr(messages, "get_client", lambda account=None: object())

    async def mismatched_resolve(chat_id, client):
        return CanonicalTestEntity(-1001528034935)

    monkeypatch.setattr(messages, "resolve_entity", mismatched_resolve)

    result = await messages.get_messages_in_range(
        chat_id=-1009999999999, after_message_id=100, through_message_id=101
    )

    assert result == (
        "chat_id -1009999999999 does not match resolved canonical chat ID -1001528034935."
    )


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "tool, kwargs, expected",
    [
        (
            messages.get_messages_in_range,
            {"chat_id": -1001528034935, "after_message_id": -1, "through_message_id": 1},
            "after_message_id must be a non-negative integer.",
        ),
        (
            messages.get_messages_in_range,
            {"chat_id": -1001528034935, "after_message_id": 5, "through_message_id": 5},
            "through_message_id must be greater than after_message_id.",
        ),
        (
            messages.get_topic_messages_in_range,
            {
                "chat_id": -1001528034935,
                "topic_id": True,
                "after_message_id": 5,
                "through_message_id": 6,
            },
            "topic_id must be a positive integer.",
        ),
    ],
)
async def test_range_reads_validate_before_using_client(monkeypatch, tool, kwargs, expected):
    monkeypatch.setattr(messages, "get_client", lambda account=None: pytest.fail("client used"))

    result = await tool(**kwargs)

    assert result == expected


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "tool, kwargs",
    [
        (
            messages.get_messages_in_range,
            {"chat_id": -1001528034935, "after_message_id": 0, "through_message_id": 1001},
        ),
        (
            messages.get_topic_messages_in_range,
            {
                "chat_id": -1001528034935,
                "topic_id": 33429,
                "after_message_id": 0,
                "through_message_id": 1001,
            },
        ),
    ],
)
async def test_range_reads_reject_oversized_ranges_before_using_client(monkeypatch, tool, kwargs):
    monkeypatch.setattr(messages, "get_client", lambda account=None: pytest.fail("client used"))

    result = await tool(**kwargs)

    assert result == "Requested message range exceeds 1000 messages."


@pytest.mark.asyncio
async def test_get_topic_messages_in_range_scopes_iterator_and_results_to_topic(monkeypatch):
    topic_id = 33429
    through_message_id = 219833
    client = IteratingClient(
        [
            _message(219831, "Included"),
            _message(219832, "Included upper"),
        ],
        {topic_id: _message(topic_id, "Topic root", action=TopicCreateAction())},
    )
    monkeypatch.setattr(messages, "get_client", lambda account=None: client)
    monkeypatch.setattr(messages, "resolve_entity", _resolve)
    monkeypatch.setattr(messages.types, "MessageActionTopicCreate", TopicCreateAction)

    result = await messages.get_topic_messages_in_range(
        chat_id=-1001528034935,
        topic_id=topic_id,
        after_message_id=219830,
        through_message_id=through_message_id,
    )

    payload = json.loads(result)
    assert client.iter_messages_calls == [
        (
            "forum-entity",
            {"min_id": 219830, "max_id": 219834, "reply_to": topic_id},
        )
    ]
    assert [record["id"] for record in payload["results"]] == [219831, 219832]
    assert payload["topic_id"] == topic_id
    assert payload["retrieved_count"] == 2
    assert payload["range_complete"] is True


@pytest.mark.asyncio
async def test_get_topic_messages_in_range_cannot_admit_cross_topic_server_results(monkeypatch):
    topic_id = 33429
    client = ServerScopedTopicClient(
        {
            topic_id: [_message(219833, "Included")],
            99999: [_message(219833, "Foreign topic message")],
        },
        {topic_id: _message(topic_id, "Topic root", action=TopicCreateAction())},
    )
    monkeypatch.setattr(messages, "get_client", lambda account=None: client)
    monkeypatch.setattr(messages, "resolve_entity", _resolve)
    monkeypatch.setattr(messages.types, "MessageActionTopicCreate", TopicCreateAction)

    payload = json.loads(
        await messages.get_topic_messages_in_range(
            chat_id=-1001528034935,
            topic_id=topic_id,
            after_message_id=219832,
            through_message_id=219833,
        )
    )

    assert [record["text"] for record in payload["results"]] == ["Included"]
    assert client.iter_messages_calls == [
        ("forum-entity", {"min_id": 219832, "max_id": 219834, "reply_to": topic_id})
    ]


@pytest.mark.asyncio
async def test_mark_topic_as_read_uses_latest_topic_message(monkeypatch):
    topic_id = 33422
    latest_message_id = 218975
    client = RecordingClient(topic_metadata=_topic_metadata(topic_id, latest_message_id))

    async def get_messages(entity, **kwargs):
        client.get_messages_calls.append((entity, kwargs))
        if "ids" in kwargs:
            return _message(topic_id, "Topic root", action=TopicCreateAction())
        return [_message(latest_message_id, "Latest")]

    client.get_messages = get_messages
    monkeypatch.setattr(messages, "get_client", lambda account=None: client)
    monkeypatch.setattr(messages, "resolve_entity", _resolve)
    monkeypatch.setattr(messages.types, "MessageActionTopicCreate", TopicCreateAction)

    result = await messages.mark_topic_as_read(chat_id=-1001528034935, topic_id=topic_id)

    assert result == (
        "Marked topic 33422 in chat -1001528034935 through message 218975; "
        "observed read watermark 218975."
    )
    assert client.get_messages_calls == [
        ("forum-entity", {"ids": topic_id}),
        ("forum-entity", {"limit": 1, "reply_to": topic_id}),
    ]
    assert len(client.requests) == 2
    request = client.requests[0]
    assert isinstance(request, functions.messages.ReadDiscussionRequest)
    assert request.peer == "forum-entity"
    assert request.msg_id == topic_id
    assert request.read_max_id == latest_message_id
    assert isinstance(client.requests[1], messages.GetForumTopicsByIDRequest)


@pytest.mark.asyncio
async def test_mark_read_through_checks_exact_watermark_before_acknowledging(monkeypatch):
    client = ReadAcknowledgingClient(
        {"forum-entity": {218975: _message(218975, "Watermark")}}, observed_watermark=218975
    )
    monkeypatch.setattr(messages, "get_client", lambda account=None: client)
    monkeypatch.setattr(messages, "resolve_entity", _resolve)

    result = await messages.mark_read_through(chat_id=-1001528034935, through_message_id=218975)

    assert result == (
        "Marked chat -1001528034935 as read through message 218975; "
        "observed read watermark 218975."
    )
    assert client.get_messages_calls == [("forum-entity", {"ids": 218975})]
    assert client.read_acknowledgements == [("forum-entity", {"max_id": 218975})]
    assert isinstance(client.requests[0], functions.messages.GetPeerDialogsRequest)


@pytest.mark.asyncio
async def test_mark_read_through_rejects_boolean_chat_id(monkeypatch):
    monkeypatch.setattr(messages, "get_client", lambda account=None: pytest.fail("client used"))

    result = await messages.mark_read_through(chat_id=True, through_message_id=218975)

    assert result == "chat_id must be a canonical marked negative integer."


@pytest.mark.asyncio
async def test_mark_read_through_rejects_username_chat_id(monkeypatch):
    monkeypatch.setattr(messages, "get_client", lambda account=None: pytest.fail("client used"))

    result = await messages.mark_read_through(chat_id="forum_username", through_message_id=218975)

    assert result == "chat_id must be a canonical marked negative integer."


@pytest.mark.asyncio
async def test_mark_read_through_reports_indeterminate_when_chat_verification_is_unavailable(
    monkeypatch,
):
    client = ReadAcknowledgingClient(
        {"forum-entity": {218975: _message(218975, "Watermark")}},
        verification_error=RuntimeError("peer dialogs unavailable"),
    )
    monkeypatch.setattr(messages, "get_client", lambda account=None: client)
    monkeypatch.setattr(messages, "resolve_entity", _resolve)

    result = await messages.mark_read_through(chat_id=-1001528034935, through_message_id=218975)

    assert result == "Chat -1001528034935 read watermark verification is indeterminate."
    assert client.read_acknowledgements == [("forum-entity", {"max_id": 218975})]


@pytest.mark.asyncio
async def test_mark_read_through_reports_mismatched_chat_watermark(monkeypatch):
    client = ReadAcknowledgingClient(
        {"forum-entity": {218975: _message(218975, "Watermark")}}, observed_watermark=218976
    )
    monkeypatch.setattr(messages, "get_client", lambda account=None: client)
    monkeypatch.setattr(messages, "resolve_entity", _resolve)

    result = await messages.mark_read_through(chat_id=-1001528034935, through_message_id=218975)

    assert "concurrent advancement" in result
    assert "observed read watermark 218976" in result


@pytest.mark.asyncio
async def test_mark_read_through_rejects_lower_observed_watermark(monkeypatch):
    client = ReadAcknowledgingClient(
        {"forum-entity": {218975: _message(218975, "Watermark")}}, observed_watermark=218974
    )
    monkeypatch.setattr(messages, "get_client", lambda account=None: client)
    monkeypatch.setattr(messages, "resolve_entity", _resolve)

    result = await messages.mark_read_through(chat_id=-1001528034935, through_message_id=218975)

    assert "verification mismatch" in result
    assert "expected at least 218975" in result


@pytest.mark.asyncio
async def test_mark_as_read_requires_authoritative_read_back(monkeypatch):
    client = ReadAcknowledgingClient({}, observed_watermark=218975)

    async def get_messages(entity, **kwargs):
        return [_message(218975, "Latest")]

    client.get_messages = get_messages
    monkeypatch.setattr(messages, "get_client", lambda account=None: client)
    monkeypatch.setattr(messages, "resolve_entity", _resolve)

    result = await messages.mark_as_read(chat_id=-1001528034935)

    assert result == (
        "Marked chat -1001528034935 as read through frozen message 218975; "
        "observed read watermark 218975."
    )
    assert client.read_acknowledgements == [
        (CanonicalTestEntity(-1001528034935), {"max_id": 218975})
    ]
    assert isinstance(client.requests[0], functions.messages.GetPeerDialogsRequest)


@pytest.mark.asyncio
async def test_mark_as_read_reports_concurrent_advancement_without_expanding_ack(monkeypatch):
    client = ReadAcknowledgingClient({}, observed_watermark=218976)

    async def get_messages(entity, **kwargs):
        return [_message(218975, "Frozen latest")]

    client.get_messages = get_messages
    monkeypatch.setattr(messages, "get_client", lambda account=None: client)
    monkeypatch.setattr(messages, "resolve_entity", _resolve)

    result = await messages.mark_as_read(chat_id=-1001528034935)

    assert "concurrent advancement" in result
    assert client.read_acknowledgements[0][1] == {"max_id": 218975}


@pytest.mark.asyncio
async def test_mark_as_read_rejects_stale_read_back(monkeypatch):
    client = ReadAcknowledgingClient({}, observed_watermark=218974)

    async def get_messages(entity, **kwargs):
        return [_message(218975, "Frozen latest")]

    client.get_messages = get_messages
    monkeypatch.setattr(messages, "get_client", lambda account=None: client)
    monkeypatch.setattr(messages, "resolve_entity", _resolve)

    result = await messages.mark_as_read(chat_id=-1001528034935)

    assert "verification mismatch" in result
    assert "expected at least 218975" in result


@pytest.mark.asyncio
async def test_mark_as_read_is_indeterminate_without_read_back(monkeypatch):
    client = ReadAcknowledgingClient(
        {}, verification_error=RuntimeError("peer dialogs unavailable")
    )

    async def get_messages(entity, **kwargs):
        return [_message(218975, "Latest")]

    client.get_messages = get_messages
    monkeypatch.setattr(messages, "get_client", lambda account=None: client)
    monkeypatch.setattr(messages, "resolve_entity", _resolve)

    result = await messages.mark_as_read(chat_id=-1001528034935)

    assert result == "Chat -1001528034935 read verification is indeterminate."


@pytest.mark.asyncio
async def test_mark_topic_as_read_rejects_unverified_root(monkeypatch):
    client = RecordingClient()
    monkeypatch.setattr(messages, "get_client", lambda account=None: client)
    monkeypatch.setattr(messages, "resolve_entity", _resolve)

    result = await messages.mark_topic_as_read(chat_id=-1001528034935, topic_id=33422)

    assert result == "Topic root 33422 was not found in chat -1001528034935."
    assert client.requests == []


@pytest.mark.asyncio
async def test_mark_topic_as_read_rejects_topic_without_messages(monkeypatch):
    topic_id = 33422
    client = RecordingClient()

    async def get_messages(entity, **kwargs):
        if "ids" in kwargs:
            return _message(topic_id, "Topic root", action=TopicCreateAction())
        return []

    client.get_messages = get_messages
    monkeypatch.setattr(messages, "get_client", lambda account=None: client)
    monkeypatch.setattr(messages, "resolve_entity", _resolve)
    monkeypatch.setattr(messages.types, "MessageActionTopicCreate", TopicCreateAction)

    result = await messages.mark_topic_as_read(chat_id=-1001528034935, topic_id=topic_id)

    assert result == "Topic 33422 has no message watermark to acknowledge in chat -1001528034935."
    assert client.requests == []


@pytest.mark.asyncio
async def test_mark_topic_as_read_reports_unverified_watermark(monkeypatch):
    topic_id = 33422
    latest_message_id = 218975
    client = RecordingClient(topic_metadata=_topic_metadata(topic_id, latest_message_id - 1))

    async def get_messages(entity, **kwargs):
        if "ids" in kwargs:
            return _message(topic_id, "Topic root", action=TopicCreateAction())
        return [_message(latest_message_id, "Latest")]

    client.get_messages = get_messages
    monkeypatch.setattr(messages, "get_client", lambda account=None: client)
    monkeypatch.setattr(messages, "resolve_entity", _resolve)
    monkeypatch.setattr(messages.types, "MessageActionTopicCreate", TopicCreateAction)

    result = await messages.mark_topic_as_read(chat_id=-1001528034935, topic_id=topic_id)

    assert "verification mismatch" in result
    assert len(client.requests) == 2


@pytest.mark.asyncio
async def test_mark_topic_read_through_uses_exact_same_topic_marker(monkeypatch):
    topic_id = 33429
    through_message_id = 219833
    client = RecordingClient(topic_metadata=_topic_metadata(topic_id, through_message_id))
    client.messages_by_id = {
        topic_id: _message(topic_id, "Topic root", action=TopicCreateAction()),
        through_message_id: _message(through_message_id, "Topic message"),
    }

    async def get_messages(entity, **kwargs):
        client.get_messages_calls.append((entity, kwargs))
        return client.messages_by_id.get(kwargs.get("ids"))

    client.get_messages = get_messages

    async def iter_messages(entity, **kwargs):
        client.get_messages_calls.append((entity, {"iter": kwargs}))
        yield client.messages_by_id[through_message_id]

    client.iter_messages = iter_messages
    monkeypatch.setattr(messages, "get_client", lambda account=None: client)
    monkeypatch.setattr(messages, "resolve_entity", _resolve)
    monkeypatch.setattr(messages.types, "MessageActionTopicCreate", TopicCreateAction)

    result = await messages.mark_topic_read_through(
        chat_id=-1001528034935,
        topic_id=topic_id,
        through_message_id=through_message_id,
    )

    assert result == (
        "Marked topic 33429 in chat -1001528034935 as read through message 219833; "
        "observed read watermark 219833."
    )
    assert client.get_messages_calls == [
        ("forum-entity", {"ids": topic_id}),
        (
            "forum-entity",
            {"iter": {"min_id": 219832, "max_id": 219834, "reply_to": topic_id}},
        ),
    ]
    assert len(client.requests) == 2
    request = client.requests[0]
    assert isinstance(request, functions.messages.ReadDiscussionRequest)
    assert request.peer == "forum-entity"
    assert request.msg_id == topic_id
    assert request.read_max_id == through_message_id
    metadata_request = client.requests[1]
    assert isinstance(metadata_request, messages.GetForumTopicsByIDRequest)
    assert metadata_request.topics == [topic_id]


@pytest.mark.asyncio
async def test_mark_topic_read_through_rejects_unconfirmed_topic_root_watermark(monkeypatch):
    topic_id = 33422
    client = RecordingClient(None)
    client.messages_by_id = {topic_id: _message(topic_id, "Ordinary message")}

    async def get_messages(entity, **kwargs):
        client.get_messages_calls.append((entity, kwargs))
        return client.messages_by_id.get(kwargs.get("ids"))

    client.get_messages = get_messages
    monkeypatch.setattr(messages, "get_client", lambda account=None: client)
    monkeypatch.setattr(messages, "resolve_entity", _resolve)

    result = await messages.mark_topic_read_through(
        chat_id=-1001528034935,
        topic_id=topic_id,
        through_message_id=topic_id,
    )

    assert "Topic root" in result
    assert client.requests == []


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "messages_by_entity",
    [
        {},
        {"foreign-entity": {218975: _message(218975, "Foreign watermark")}},
    ],
    ids=["nonexistent", "cross-peer"],
)
async def test_mark_read_through_rejects_nonexistent_or_cross_peer_watermark(
    monkeypatch, messages_by_entity
):
    client = ReadAcknowledgingClient(messages_by_entity)
    monkeypatch.setattr(messages, "get_client", lambda account=None: client)
    monkeypatch.setattr(messages, "resolve_entity", _resolve)

    result = await messages.mark_read_through(chat_id=-1001528034935, through_message_id=218975)

    assert result == "Message 218975 was not found in chat -1001528034935."
    assert client.get_messages_calls == [("forum-entity", {"ids": 218975})]
    assert client.read_acknowledgements == []


@pytest.mark.asyncio
async def test_mark_topic_read_through_rejects_cross_topic_watermark(monkeypatch):
    client = RecordingClient(None)
    client.messages_by_id = {
        33422: _message(33422, "Topic root", action=TopicCreateAction()),
    }

    async def get_messages(entity, **kwargs):
        client.get_messages_calls.append((entity, kwargs))
        return client.messages_by_id.get(kwargs.get("ids"))

    client.get_messages = get_messages

    async def iter_messages(entity, **kwargs):
        if False:
            yield None

    client.iter_messages = iter_messages
    monkeypatch.setattr(messages, "get_client", lambda account=None: client)
    monkeypatch.setattr(messages, "resolve_entity", _resolve)
    monkeypatch.setattr(messages.types, "MessageActionTopicCreate", TopicCreateAction)

    result = await messages.mark_topic_read_through(
        chat_id=-1001528034935,
        topic_id=33422,
        through_message_id=218975,
    )

    assert "cannot be confirmed" in result
    assert client.requests == []


@pytest.mark.asyncio
async def test_mark_topic_read_through_fails_closed_when_exact_watermark_is_not_observed(
    monkeypatch,
):
    topic_id = 33429
    through_message_id = 219833
    client = RecordingClient(topic_metadata=_topic_metadata(topic_id, through_message_id + 1))
    client.messages_by_id = {
        topic_id: _message(topic_id, "Topic root", action=TopicCreateAction())
    }

    async def get_messages(entity, **kwargs):
        return client.messages_by_id.get(kwargs.get("ids"))

    async def iter_messages(entity, **kwargs):
        yield _message(through_message_id, "Topic message")

    client.get_messages = get_messages
    client.iter_messages = iter_messages
    monkeypatch.setattr(messages, "get_client", lambda account=None: client)
    monkeypatch.setattr(messages, "resolve_entity", _resolve)
    monkeypatch.setattr(messages.types, "MessageActionTopicCreate", TopicCreateAction)

    result = await messages.mark_topic_read_through(
        chat_id=-1001528034935, topic_id=topic_id, through_message_id=through_message_id
    )

    assert "concurrent advancement" in result
    assert "observed read watermark 219834" in result
    assert len(client.requests) == 2


@pytest.mark.asyncio
async def test_mark_topic_read_through_reports_indeterminate_when_verification_fetch_fails(
    monkeypatch,
):
    topic_id = 33429
    through_message_id = 219833

    class FailingMetadataClient(RecordingClient):
        async def __call__(self, request):
            self.requests.append(request)
            if isinstance(request, messages.GetForumTopicsByIDRequest):
                raise RuntimeError("metadata unavailable")

    client = FailingMetadataClient()
    client.messages_by_id = {
        topic_id: _message(topic_id, "Topic root", action=TopicCreateAction())
    }

    async def get_messages(entity, **kwargs):
        return client.messages_by_id.get(kwargs.get("ids"))

    async def iter_messages(entity, **kwargs):
        yield _message(through_message_id, "Topic message")

    client.get_messages = get_messages
    client.iter_messages = iter_messages
    monkeypatch.setattr(messages, "get_client", lambda account=None: client)
    monkeypatch.setattr(messages, "resolve_entity", _resolve)
    monkeypatch.setattr(messages.types, "MessageActionTopicCreate", TopicCreateAction)

    result = await messages.mark_topic_read_through(
        chat_id=-1001528034935, topic_id=topic_id, through_message_id=through_message_id
    )

    assert "verification is indeterminate" in result
    assert len(client.requests) == 2


@pytest.mark.asyncio
@pytest.mark.parametrize("chat_id", ["-1001528034935", True])
async def test_topic_tools_reject_non_numeric_chat_ids(monkeypatch, chat_id):
    monkeypatch.setattr(messages, "get_client", lambda account=None: pytest.fail("client used"))

    read_result = await messages.get_topic_messages_in_range(
        chat_id=chat_id, topic_id=33429, after_message_id=219832, through_message_id=219833
    )
    mark_result = await messages.mark_topic_read_through(
        chat_id=chat_id, topic_id=33429, through_message_id=219833
    )

    assert read_result == "chat_id must be a canonical marked negative integer."
    assert mark_result == "chat_id must be a canonical marked negative integer."


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "kwargs, expected",
    [
        (
            {"topic_id": "33429", "through_message_id": 219833},
            "topic_id must be a positive integer.",
        ),
        (
            {"topic_id": 33429, "through_message_id": "219833"},
            "through_message_id must be a positive integer.",
        ),
    ],
)
async def test_mark_topic_read_through_rejects_string_topic_and_message_ids(
    monkeypatch, kwargs, expected
):
    monkeypatch.setattr(messages, "get_client", lambda account=None: pytest.fail("client used"))

    result = await messages.mark_topic_read_through(chat_id=-1001528034935, **kwargs)

    assert result == expected


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "tool, kwargs",
    [
        (messages.mark_read_through, {"chat_id": -1001528034935, "through_message_id": 0}),
        (
            messages.mark_topic_read_through,
            {"chat_id": -1001528034935, "topic_id": 33422, "through_message_id": -1},
        ),
        (
            messages.mark_topic_read_through,
            {"chat_id": -1001528034935, "topic_id": 33422, "through_message_id": True},
        ),
        (
            messages.mark_topic_read_through,
            {"chat_id": -1001528034935, "topic_id": 0, "through_message_id": 1},
        ),
        (
            messages.mark_topic_read_through,
            {"chat_id": -1001528034935, "topic_id": True, "through_message_id": 1},
        ),
    ],
)
async def test_mark_read_through_tools_reject_invalid_ids(monkeypatch, tool, kwargs):
    monkeypatch.setattr(messages, "get_client", lambda account=None: pytest.fail("client used"))
    monkeypatch.setattr(messages, "resolve_entity", _resolve)

    result = await tool(**kwargs)

    if "topic_id" in kwargs and (kwargs["topic_id"] is True or kwargs["topic_id"] <= 0):
        assert result == "topic_id must be a positive integer."
    else:
        assert result == "through_message_id must be a positive integer."
