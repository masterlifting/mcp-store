import telegram_mcp.tools  # noqa: F401 - importing registers FastMCP tools
from telegram_mcp.runtime import mcp


def _parameters(name: str) -> dict:
    tool = next(tool for tool in mcp._tool_manager.list_tools() if tool.name == name)
    return tool.parameters


def test_forum_topic_pagination_constraints_are_published():
    props = _parameters("list_topics")["properties"]

    assert props["limit"]["minimum"] == 1
    assert props["limit"]["maximum"] == 100
    assert props["offset_topic"]["minimum"] == 0


def test_topic_message_constraints_are_published():
    props = _parameters("get_topic_messages")["properties"]

    assert props["topic_id"]["exclusiveMinimum"] == 0
    assert props["page"]["exclusiveMinimum"] == 0
    assert props["page_size"]["minimum"] == 1
    assert props["page_size"]["maximum"] == 100


def test_frozen_range_scalar_constraints_are_published():
    props = _parameters("get_messages_in_range")["properties"]

    assert props["after_message_id"]["minimum"] == 0
    assert props["through_message_id"]["exclusiveMinimum"] == 0

    topic_props = _parameters("get_topic_messages_in_range")["properties"]
    assert topic_props["topic_id"]["exclusiveMinimum"] == 0
    assert topic_props["after_message_id"]["minimum"] == 0
    assert topic_props["through_message_id"]["exclusiveMinimum"] == 0
