import pytest

import telegram_mcp.tools  # noqa: F401  (registers the package's MCP tools)
from telegram_mcp import runtime


def _schema_by_name(tools):
    return {tool.name: tool.inputSchema for tool in tools}


def _assert_nullable(schema, name):
    property_schema = schema["properties"][name]
    assert any(option.get("type") == "null" for option in property_schema["anyOf"])
    assert property_schema["default"] is None


@pytest.mark.asyncio
async def test_optional_account_arguments_accept_explicit_null():
    schemas = _schema_by_name(await runtime.mcp.list_tools())

    account_tools = [schema for schema in schemas.values() if "account" in schema["properties"]]

    assert account_tools
    for schema in account_tools:
        _assert_nullable(schema, "account")


@pytest.mark.asyncio
async def test_nullable_optional_tool_arguments_match_runtime_defaults():
    schemas = _schema_by_name(await runtime.mcp.list_tools())

    expected_nullable = {
        "create_forum_topic": ("icon_color", "icon_emoji_id"),
        "download_media": ("file_path",),
        "list_chats": ("chat_type", "archived"),
        "list_messages": ("search_query", "from_date", "to_date"),
        "list_topics": ("search_query",),
        "send_album": ("caption",),
        "send_file": ("caption",),
        "update_profile": ("first_name", "last_name", "about"),
        "create_poll": ("close_date",),
        "promote_admin": ("rights",),
    }

    for tool_name, names in expected_nullable.items():
        for name in names:
            _assert_nullable(schemas[tool_name], name)


@pytest.mark.asyncio
async def test_collection_arguments_advertise_their_item_types():
    schemas = _schema_by_name(await runtime.mcp.list_tools())

    assert schemas["create_poll"]["properties"]["options"]["items"] == {"type": "string"}
    assert schemas["import_contacts"]["properties"]["contacts"]["items"]["type"] == "object"
    assert schemas["set_bot_commands"]["properties"]["commands"]["items"]["type"] == "object"
