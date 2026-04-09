from typing import Annotated, Any, Literal, Optional, get_args

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry

VRChatUdonAction = Literal[
    "check_environment",
    "create_udonsharp_script",
    "attach_udon_behaviour",
    "configure_sync",
    "generate_network_pattern",
    "validate_networking",
]

ALL_ACTIONS: list[str] = list(get_args(VRChatUdonAction))


@mcp_for_unity_tool(
    group="vrchat",
    description=(
        "Create and wire VRChat UdonSharp scripts, then validate networking setup.\n\n"
        "Use this tool when building VRChat Worlds with Udon# (Unity 2022.3.22f1).\n"
        "Typical workflow: check_environment -> create_udonsharp_script -> attach_udon_behaviour -> validate_networking.\n\n"
        "Actions:\n"
        "- check_environment: Validate Unity version + VRChat/UdonSharp availability.\n"
        "- create_udonsharp_script: Create UdonSharpBehaviour C# script and ensure linked UdonSharpProgramAsset.\n"
        "- attach_udon_behaviour: Add VRC.Udon.UdonBehaviour to target and set Program Source to matching UdonSharpProgramAsset.\n"
        "- configure_sync: Generate sync snippets for manual/continuous modes and [UdonSynced] fields.\n"
        "- generate_network_pattern: Generate safe ownership/network event patterns.\n"
        "- validate_networking: Detect common anti-patterns (master-gating, sync misuse, serialization spam).\n\n"
        "Important:\n"
        "- `path` must be under Assets/ and point to .cs for script actions.\n"
        "- `target` should be exact name/path/id for attach action.\n"
        "- `search_method` can disambiguate target lookup."
    ),
    annotations=ToolAnnotations(
        title="Manage VRChat Udon",
        destructiveHint=True,
    ),
)
async def manage_vrchat_udon(
    ctx: Context,
    action: Annotated[
        VRChatUdonAction,
        "Action name. Use check_environment, create_udonsharp_script, attach_udon_behaviour, configure_sync, generate_network_pattern, or validate_networking.",
    ],
    path: Annotated[
        Optional[str],
        "Assets-relative .cs path for create/validate/attach context (example: Assets/Scripts/Udon/DoorToggle.cs).",
    ] = None,
    class_name: Annotated[
        Optional[str],
        "UdonSharp class name (used by create/attach). If omitted for create, inferred from filename.",
    ] = None,
    namespace: Annotated[Optional[str], "Optional C# namespace for generated Udon script."] = None,
    target: Annotated[
        Optional[str],
        "Target GameObject for attach. Accepts name, hierarchy path, or instance ID.",
    ] = None,
    search_method: Annotated[
        Optional[str], "Optional search method for target resolution (by_name, by_path, by_id_or_name_or_path, etc.)."
    ] = None,
    sync_mode: Annotated[
        Optional[Literal["manual", "continuous"]],
        "Udon sync mode for generation/snippets. manual = explicit RequestSerialization, continuous = automatic network updates.",
    ] = None,
    network_pattern: Annotated[
        Optional[Literal["ownership_gate", "manual_sync_toggle", "event_after_sync", "late_joiner_state"]],
        "Networking template key: ownership_gate | manual_sync_toggle | event_after_sync | late_joiner_state.",
    ] = None,
    variables: Annotated[
        Optional[list[str]],
        "Optional variable names to include in generated sync snippets and checks.",
    ] = None,
    include_example: Annotated[
        Optional[bool],
        "If true, include a full code example in output for configure_sync/generate_network_pattern.",
    ] = None,
) -> dict[str, Any]:
    action_lower = action.lower()
    if action_lower not in ALL_ACTIONS:
        return {
            "success": False,
            "message": f"Unknown action '{action}'. Valid: {', '.join(ALL_ACTIONS)}",
        }

    unity_instance = await get_unity_instance_from_context(ctx)

    params_dict: dict[str, Any] = {
        "action": action_lower,
    }
    if path is not None:
        params_dict["path"] = path
    if class_name is not None:
        params_dict["class_name"] = class_name
    if namespace is not None:
        params_dict["namespace"] = namespace
    if target is not None:
        params_dict["target"] = target
    if search_method is not None:
        params_dict["search_method"] = search_method
    if sync_mode is not None:
        params_dict["sync_mode"] = sync_mode
    if network_pattern is not None:
        params_dict["network_pattern"] = network_pattern
    if variables is not None:
        params_dict["variables"] = variables
    if include_example is not None:
        params_dict["include_example"] = include_example

    result = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "manage_vrchat_udon",
        params_dict,
    )
    return result if isinstance(result, dict) else {"success": False, "message": str(result)}
