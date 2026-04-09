from __future__ import annotations

import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from services.tools.manage_vrchat_udon import ALL_ACTIONS, manage_vrchat_udon


@pytest.fixture
def mock_unity(monkeypatch):
    captured: dict[str, object] = {}

    async def fake_send(send_fn, unity_instance, tool_name, params):
        captured["unity_instance"] = unity_instance
        captured["tool_name"] = tool_name
        captured["params"] = params
        return {"success": True, "message": "ok"}

    monkeypatch.setattr(
        "services.tools.manage_vrchat_udon.get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )
    monkeypatch.setattr(
        "services.tools.manage_vrchat_udon.send_with_unity_instance",
        fake_send,
    )
    return captured


def test_action_list():
    assert set(ALL_ACTIONS) == {
        "check_environment",
        "create_udonsharp_script",
        "attach_udon_behaviour",
        "configure_sync",
        "generate_network_pattern",
        "validate_networking",
    }


def test_unknown_action_returns_error(mock_unity):
    result = asyncio.run(manage_vrchat_udon(SimpleNamespace(), action="bad_action"))
    assert result["success"] is False
    assert "Unknown action" in result["message"]
    assert "tool_name" not in mock_unity


def test_check_environment_forwards_action(mock_unity):
    result = asyncio.run(manage_vrchat_udon(SimpleNamespace(), action="check_environment"))
    assert result["success"] is True
    assert mock_unity["tool_name"] == "manage_vrchat_udon"
    assert mock_unity["params"]["action"] == "check_environment"


def test_create_script_includes_params(mock_unity):
    result = asyncio.run(
        manage_vrchat_udon(
            SimpleNamespace(),
            action="create_udonsharp_script",
            path="Assets/Scripts/Udon/DoorToggle.cs",
            class_name="DoorToggle",
            namespace="World.Logic",
            sync_mode="manual",
        )
    )
    assert result["success"] is True
    assert mock_unity["params"]["path"] == "Assets/Scripts/Udon/DoorToggle.cs"
    assert mock_unity["params"]["class_name"] == "DoorToggle"
    assert mock_unity["params"]["namespace"] == "World.Logic"
    assert mock_unity["params"]["sync_mode"] == "manual"


def test_attach_behaviour_includes_target(mock_unity):
    result = asyncio.run(
        manage_vrchat_udon(
            SimpleNamespace(),
            action="attach_udon_behaviour",
            class_name="DoorToggle",
            target="Door",
            search_method="by_name",
        )
    )
    assert result["success"] is True
    assert mock_unity["params"]["target"] == "Door"
    assert mock_unity["params"]["search_method"] == "by_name"


def test_network_pattern_args(mock_unity):
    result = asyncio.run(
        manage_vrchat_udon(
            SimpleNamespace(),
            action="generate_network_pattern",
            network_pattern="ownership_gate",
            include_example=True,
        )
    )
    assert result["success"] is True
    assert mock_unity["params"]["network_pattern"] == "ownership_gate"
    assert mock_unity["params"]["include_example"] is True


def test_validate_networking_args(mock_unity):
    result = asyncio.run(
        manage_vrchat_udon(
            SimpleNamespace(),
            action="validate_networking",
            path="Assets/Scripts/Udon/DoorToggle.cs",
            variables=["doorState", "ownerId"],
        )
    )
    assert result["success"] is True
    assert mock_unity["params"]["path"] == "Assets/Scripts/Udon/DoorToggle.cs"
    assert mock_unity["params"]["variables"] == ["doorState", "ownerId"]


def test_non_dict_response_wrapped(monkeypatch):
    monkeypatch.setattr(
        "services.tools.manage_vrchat_udon.get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )

    async def fake_send(send_fn, unity_instance, tool_name, params):
        return "unexpected response"

    monkeypatch.setattr(
        "services.tools.manage_vrchat_udon.send_with_unity_instance",
        fake_send,
    )

    result = asyncio.run(manage_vrchat_udon(SimpleNamespace(), action="check_environment"))
    assert result["success"] is False
    assert "unexpected response" in result["message"]
