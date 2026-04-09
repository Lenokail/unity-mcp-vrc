# VRChat Udon Tooling Guide

This guide describes the `manage_vrchat_udon` MCP tool for VRChat Worlds workflows.

## Scope

The tool is focused on UdonSharp authoring helpers:

- Environment checks for VRChat/UdonSharp readiness.
- UdonSharp script generation in `Assets/`.
- Attaching Udon behaviours to scene GameObjects.
- Networking-oriented snippet generation.
- Static validation for common networking anti-patterns.

## Actions

`manage_vrchat_udon` supports:

- `check_environment`
- `create_udonsharp_script`
- `attach_udon_behaviour`
- `configure_sync`
- `generate_network_pattern`
- `validate_networking`

## Recommended workflow

1. Run `check_environment`.
2. Create script with `create_udonsharp_script`.
3. Wait for compilation to complete.
4. Attach with `attach_udon_behaviour`.
5. Use `validate_networking` before upload/build.

## Platform constraints

- Recommended Unity version for VRChat: `2022.3.22f1`.
- Prefer ownership checks over master checks.
- Avoid high-frequency `RequestSerialization()` calls.
- Keep synced payloads small.

