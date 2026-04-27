param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$SandboxRoot = "M:\SBoxProjects\sandbox-plus-plus"
)

$ErrorActionPreference = "Stop"

$registryPath = Join-Path $RepoRoot "editor\Editor\Core\ToolRegistry.cs"
$registry = Get-Content -Raw $registryPath

$toolBlocks = [regex]::Matches(
    $registry,
    'internal static readonly object\s+\w+\s*=\s*new\s*\{(?<body>.*?)(?=\r?\n\s*internal static readonly object|\r?\n\})',
    [System.Text.RegularExpressions.RegexOptions]::Singleline
)

$tools = foreach ($block in $toolBlocks) {
    $body = $block.Groups["body"].Value
    $nameMatch = [regex]::Match($body, 'name\s*=\s*"(?<name>[^"]+)"')
    if (-not $nameMatch.Success) { continue }

    $descriptionMatch = [regex]::Match($body, 'description\s*=\s*"(?<description>(?:\\"|[^"])*)"', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    $description = if ($descriptionMatch.Success) { $descriptionMatch.Groups["description"].Value -replace '\s+', ' ' } else { "" }

    $actionMatch = [regex]::Match($body, '\["action"\]\s*=\s*new\s*\{.*?@enum\s*=\s*new\[\]\s*\{(?<actions>.*?)\}', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    $actions = @()
    if ($actionMatch.Success) {
        $actions = [regex]::Matches($actionMatch.Groups["actions"].Value, '"([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
    }

    [pscustomobject]@{
        Name = $nameMatch.Groups["name"].Value
        Description = $description
        Actions = @($actions)
    }
}

$tools = @($tools)
$toolCount = $tools.Count
$actionCount = ($tools | ForEach-Object { $_.Actions.Count } | Measure-Object -Sum).Sum

function Format-Actions($actions) {
    if (-not $actions -or $actions.Count -eq 0) { return "(no action parameter)" }
    return ($actions -join ", ")
}

function Tool-Table {
    $lines = @(
        "| Tool | Actions | Count |",
        "|------|---------|-------|"
    )
    foreach ($tool in $tools) {
        $lines += "| ``$($tool.Name)`` | $(Format-Actions $tool.Actions) | $($tool.Actions.Count) |"
    }
    return ($lines -join "`n")
}

$table = Tool-Table

$readme = @"
# Arenula

MCP server suite for the s&box game engine. Connects AI coding assistants to the s&box editor for scene inspection, editor-safe writes, compilation, assets, playtests, API lookup, and documentation.

## Servers

| Server | What it does | Runtime | Transport |
|--------|-------------|---------|-----------|
| **Editor** | Scene manipulation, compilation, assets, terrain, playtests, agent safety | C# plugin inside s&box | Streamable HTTP `http://localhost:8098/mcp` |
| **API** | Offline type/member reference and package/texture lookup | Node.js | stdio |
| **Docs** | Narrative docs from sbox.game | Node.js | stdio |

## Setup

Copy `editor/` into your s&box project's `Libraries/arenula_mcp/` folder, then build the Node servers with `npm run setup`.

Configure the editor server with `/mcp`:

```json
{
  "mcpServers": {
    "editor": {
      "type": "http",
      "url": "http://localhost:8098/mcp"
    }
  }
}
```

Legacy `/sse` and `/message` remain for older clients for one compatibility window.

## Agent Quickstart

1. Start with `mcp.capabilities` for the live tool inventory and routing hints.
2. For code edits, use `pipeline.compile_and_verify`; for mixed changes, use `verify_change_set`.
3. For scene writes, use `operation.preview`/`operation.apply` or `transaction.preview`/`transaction.apply`.
4. For runtime assertions, use `playtest`.
5. For unknown APIs or property shapes, query `reflection` and `schema` before writing.

## Editor Tools

Generated from `editor/Editor/Core/ToolRegistry.cs`.

Tool count: **$toolCount**. Action count: **$actionCount**.

$table

## License

GPL-3.0 for the editor plugin; see package-level license files for Node servers.
"@

$editorReadme = @"
# Arenula Editor

C# plugin that runs inside the s&box editor and exposes editor capabilities over MCP Streamable HTTP at `http://localhost:8098/mcp`.

Legacy `/sse` plus `/message` remains for older clients, but `/mcp` is the documented transport.

## Architecture

Core transport, registry, schema, session, cursor, workspace, and coordination code lives under `Editor/Core/`. Tool handlers live under `Editor/Handlers/`.

## Agent Safety

Each tool action is classified in `ToolRegistry.GetOperationMetadata` as read-only, scene write, file write, or lifecycle. `McpOperationCoordinator` allows concurrent read-only calls and rejects conflicting writes/lifecycle operations with a structured busy error containing the active operation id and retry guidance.

Transactions preview target ids/paths and editor revision, apply under the exclusive write lock, stop on first failed operation by default, and rollback only when no unrelated write advanced the editor revision.

## Tools

Generated from `Editor/Core/ToolRegistry.cs`.

Tool count: **$toolCount**. Action count: **$actionCount**.

$table

## License

GPL-3.0 - see `LICENSE`.
"@

$sandboxGuide = @"
# Arenula MCP Integration Guide

## Overview

Sandbox++ uses `M:\SBoxProjects\Arenula-MCP` as the canonical Arenula MCP source. The deployed editor plugin copy lives at `Libraries/arenula_mcp`.

The preferred editor transport is Streamable HTTP at `http://localhost:8098/mcp`. Legacy `/sse` and `/message` remain available for older clients during the compatibility window.

## Configuration

```json
{
  "mcpServers": {
    "sbox-editor": { "type": "http", "url": "http://localhost:8098/mcp" },
    "sbox-api": { "command": "node", "args": ["M:/SBoxProjects/Arenula-MCP/api/dist/index.js"] },
    "sbox-docs": { "command": "node", "args": ["M:/SBoxProjects/Arenula-MCP/docs/dist/index.js"] }
  }
}
```

## Agent Quickstart

1. Call `mcp.capabilities` to confirm tool count, `/mcp`, coordination state, and routing hints.
2. Code edits route through `pipeline.compile_and_verify`; mixed changes route through `verify_change_set`.
3. Scene writes route through `operation.preview`/`operation.apply` or `transaction.preview`/`transaction.apply`.
4. Runtime assertions route through `playtest`.
5. API/property uncertainty routes through `reflection` and `schema`, then `sbox-api`/`sbox-docs` if needed.

## Current sbox-editor Tools

Generated from `ToolRegistry.All`.

Tool count: **$toolCount**. Action count: **$actionCount**.

$table

## Notes

- `mcp.server_log` is the preferred structured log reader; `editor.get_log` remains for compatibility.
- Responses over the MCP content channel can paginate through `continue.next`.
- Component property writes include structured schema support for enums, Color, Rotation/Angles, vectors, resources, and references where supported by handlers.
- `editor.undo` is global editor undo and is unsafe as a multi-agent rollback mechanism. Prefer transactions; rollback refuses if unrelated MCP writes advanced the editor revision after apply.
- MCP plugin code changes require `editor.restart_mcp` or an editor restart after compile/hotload.
"@

Set-Content -Path (Join-Path $RepoRoot "README.md") -Value $readme -NoNewline
Set-Content -Path (Join-Path $RepoRoot "editor\README.md") -Value $editorReadme -NoNewline
Set-Content -Path (Join-Path $SandboxRoot "docs\mcp-guide.md") -Value $sandboxGuide -NoNewline

Write-Host "Generated docs for $toolCount tools and $actionCount actions."
