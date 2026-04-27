using System;
using System.Collections.Generic;
using System.Linq;

namespace Arenula;

/// <summary>
/// All 21 Arenula tool schemas for the MCP tools/list response.
/// Each tool uses the omnibus pattern: required action enum + flat params.
/// Descriptions follow Anthropic best practices: 3-4 sentences, negative guidance.
/// </summary>
internal static class ToolRegistry
{
    internal static readonly string Version = "1.0.0";

    internal sealed class ToolActionMetadata
    {
        public bool ReadOnly { get; init; }
        public string WriteKind { get; init; } = "none";
        public string LifecycleKind { get; init; } = "none";
    }

    internal static object[] All => new object[]
    {
        McpHealth, McpCapabilities, McpServerLog,
        Scene, GameObject, Component, Prefab,
        AssetQuery, AssetManage, Editor, Compile,
        Mesh, Lighting, Physics, Audio,
        Effects, Camera, Navmesh, Session,
        Project, Cloud, Terrain, Code, Trace, Batch,
        Reflection, SbepVerify, Pipeline,
        Workflow, VerifyChangeSet, Playtest,
        Schema, Operation, Transaction, Autofix, CrossIndex,
        Search, Snapshot, Validate, Continue, Blueprint, Animation,
        CockpitVerify
    };

    private static readonly ToolActionMetadata ReadOnly = new() { ReadOnly = true };
    private static readonly ToolActionMetadata SceneWrite = new() { ReadOnly = false, WriteKind = "scene_write" };
    private static readonly ToolActionMetadata FileWrite = new() { ReadOnly = false, WriteKind = "file_write" };
    private static readonly ToolActionMetadata Lifecycle = new() { ReadOnly = false, LifecycleKind = "lifecycle" };

    private static readonly HashSet<string> ReadOnlyTools = new( StringComparer.OrdinalIgnoreCase )
    {
        "mcp.health", "mcp.capabilities", "mcp.server_log",
        "scene", "asset_query", "trace", "reflection", "schema",
        "cross_index", "search", "validate", "continue", "workflow"
    };

    private static readonly Dictionary<string, ToolActionMetadata> ActionMetadata = new( StringComparer.OrdinalIgnoreCase )
    {
        ["compile.trigger"] = Lifecycle,
        ["compile.generate_solution"] = Lifecycle,
        ["compile.wait"] = Lifecycle,
        ["compile.smart_trigger"] = Lifecycle,
        ["compile.status"] = ReadOnly,
        ["compile.errors"] = ReadOnly,
        ["compile.fix_context"] = ReadOnly,

        ["code.read_file"] = ReadOnly,
        ["code.list_files"] = ReadOnly,
        ["code.search_code"] = ReadOnly,
        ["code.write_file"] = FileWrite,

        ["editor.get_selected"] = ReadOnly,
        ["editor.get_play_state"] = ReadOnly,
        ["editor.get_log"] = ReadOnly,
        ["editor.console_list"] = ReadOnly,
        ["editor.get_preferences"] = ReadOnly,
        ["editor.open_code_file"] = ReadOnly,
        ["editor.select"] = SceneWrite,
        ["editor.set_selected"] = SceneWrite,
        ["editor.clear_selection"] = SceneWrite,
        ["editor.frame_selection"] = SceneWrite,
        ["editor.save_scene"] = SceneWrite,
        ["editor.save_scene_as"] = SceneWrite,
        ["editor.undo"] = SceneWrite,
        ["editor.redo"] = SceneWrite,
        ["editor.set_preference"] = FileWrite,
        ["editor.console_run"] = Lifecycle,
        ["editor.start_play"] = Lifecycle,
        ["editor.stop_play"] = Lifecycle,
        ["editor.restart_mcp"] = Lifecycle,
        ["editor.reload_scene"] = Lifecycle,

        ["session.list"] = ReadOnly,
        ["session.set_active"] = Lifecycle,
        ["session.load_scene"] = Lifecycle,

        ["project.get_collision"] = ReadOnly,
        ["project.get_input"] = ReadOnly,
        ["project.get_info"] = ReadOnly,
        ["project.set_collision_rule"] = FileWrite,

        ["asset_manage.create"] = FileWrite,
        ["asset_manage.delete"] = FileWrite,
        ["asset_manage.rename"] = FileWrite,
        ["asset_manage.move"] = FileWrite,
        ["asset_manage.save"] = FileWrite,
        ["asset_manage.reload"] = Lifecycle,
        ["asset_manage.get_references"] = ReadOnly,

        ["blueprint.validate"] = ReadOnly,
        ["blueprint.create"] = SceneWrite,
        ["snapshot.take"] = ReadOnly,
        ["snapshot.list"] = ReadOnly,
        ["snapshot.diff"] = ReadOnly,
        ["snapshot.restore"] = SceneWrite,
        ["snapshot.delete"] = SceneWrite,
        ["animation.list_parameters"] = ReadOnly,
        ["animation.get_model_animations"] = ReadOnly,
        ["animation.set_parameters"] = SceneWrite,

        ["pipeline.compile_and_verify"] = Lifecycle,
        ["pipeline.probe_typelibrary"] = ReadOnly,
        ["verify_change_set.run"] = Lifecycle,
        ["playtest.boot_scene"] = Lifecycle,
        ["playtest.start"] = Lifecycle,
        ["playtest.stop"] = Lifecycle,
        ["playtest.run_assertions"] = Lifecycle,
        ["cockpit_verify.run"] = Lifecycle,
        ["sbep_verify.full_verify"] = Lifecycle,

        ["operation.validate"] = ReadOnly,
        ["operation.preview"] = ReadOnly,
        ["operation.apply"] = SceneWrite,
        ["operation.rollback"] = SceneWrite,
        ["transaction.preview"] = ReadOnly,
        ["transaction.apply"] = SceneWrite,
        ["transaction.rollback"] = SceneWrite,

        ["autofix.prefab_fixups"] = FileWrite,
        ["autofix.create_sent_pair"] = FileWrite,
        ["cross_index.build"] = FileWrite,
        ["cross_index.stats"] = ReadOnly,
        ["cross_index.query"] = ReadOnly
    };

    internal static ToolActionMetadata GetOperationMetadata( string tool, string action )
    {
        var key = string.IsNullOrWhiteSpace( action ) ? tool : $"{tool}.{action}";
        if ( ActionMetadata.TryGetValue( key, out var metadata ) )
            return metadata;

        if ( !string.IsNullOrWhiteSpace( tool ) && ReadOnlyTools.Contains( tool ) )
            return ReadOnly;

        return SceneWrite;
    }

    internal static object[] DescribeActions( object tool )
    {
        var name = GetStringProperty( tool, "name" );
        var actions = GetActionNames( tool );
        if ( actions.Length == 0 )
        {
            var metadata = GetOperationMetadata( name, null );
            return new object[]
            {
                new { action = (string)null, read_only = metadata.ReadOnly, write_kind = metadata.WriteKind, lifecycle_kind = metadata.LifecycleKind }
            };
        }

        return actions.Select( action =>
        {
            var metadata = GetOperationMetadata( name, action );
            return (object)new
            {
                action,
                read_only = metadata.ReadOnly,
                write_kind = metadata.WriteKind,
                lifecycle_kind = metadata.LifecycleKind
            };
        } ).ToArray();
    }

    internal static string GetStringProperty( object value, string propertyName )
    {
        return value?.GetType().GetProperty( propertyName )?.GetValue( value )?.ToString();
    }

    internal static string[] GetActionNames( object tool )
    {
        var inputSchema = tool?.GetType().GetProperty( "inputSchema" )?.GetValue( tool );
        var properties = inputSchema?.GetType().GetProperty( "properties" )?.GetValue( inputSchema );
        if ( properties is not IDictionary<string, object> dict || !dict.TryGetValue( "action", out var actionSchema ) )
            return Array.Empty<string>();

        var enumValue = actionSchema.GetType().GetProperty( "enum" )?.GetValue( actionSchema );
        return enumValue is IEnumerable<string> actions ? actions.ToArray() : Array.Empty<string>();
    }

    internal static readonly object McpHealth = new
    {
        name = "mcp.health",
        description = "Return MCP server health and live editor context. Use this before a tool sequence to confirm transport, port, session count, play mode, active scene, and retained log health. This is read-only and has no required action parameter.",
        annotations = new { readOnlyHint = true },
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>(),
            additionalProperties = false
        }
    };

    internal static readonly object McpCapabilities = new
    {
        name = "mcp.capabilities",
        description = "Return the current Arenula tool inventory with advertised actions and basic server capabilities. Use this when a client needs a compact capability summary without downloading and inspecting the full tools/list schema. This is read-only and has no required action parameter.",
        annotations = new { readOnlyHint = true },
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>(),
            additionalProperties = false
        }
    };

    internal static readonly object McpServerLog = new
    {
        name = "mcp.server_log",
        description = "Read the MCP/editor structured log ring buffer. Use this to inspect recent server errors, tool calls, compile messages, and runtime log entries captured by the live editor. Supports count, filter, offset, and limit; this is read-only and has no required action parameter.",
        annotations = new { readOnlyHint = true },
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["count"] = new { type = "integer", description = "Trim to the last N retained entries before filtering. Default: 200." },
                ["filter"] = new { type = "string", description = "Case-insensitive substring filter over messages." },
                ["level"] = new { type = "string", description = "Comma-separated severity filter such as 'error' or 'warn,error'." },
                ["source"] = new { type = "string", description = "Comma-separated source filter such as 'engine' or 'mcp'." },
                ["offset"] = new { type = "integer", description = "Pagination offset. Default: 0." },
                ["limit"] = new { type = "integer", description = "Max entries to return. Default: 100." }
            },
            additionalProperties = false
        }
    };

    // ── Tool 1: scene ────────────────────────────────────────────────

    internal static readonly object Scene = new
    {
        name = "scene",
        description = "Read and query the s&box editor scene. Use this to understand what's in the scene before making changes. Actions: 'summary' returns a high-level overview with root object list, 'hierarchy' returns the full GameObject tree, 'statistics' returns object/component counts, 'find' searches by name/tag/component type with pagination, 'find_in_radius' searches spatially around a point, 'get_details' returns full info on a specific GameObject including all components, 'prefab_instances' lists all prefab instances. This tool only reads scene state — use 'gameobject' or 'component' to create or modify objects.",
        annotations = new { readOnlyHint = true },
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "summary", "hierarchy", "statistics", "find", "find_in_radius", "get_details", "prefab_instances" } },
                ["id"] = new { type = "string", description = "GameObject GUID. Required for: get_details." },
                ["query"] = new { type = "string", description = "Search term. Used by: find." },
                ["tag"] = new { type = "string", description = "Tag filter. Used by: find." },
                ["component_type"] = new { type = "string", description = "Component type filter. Used by: find." },
                ["position"] = new { type = "string", description = "Center point as 'x,y,z'. Required for: find_in_radius." },
                ["radius"] = new { type = "number", description = "Search radius in units. Required for: find_in_radius." },
                ["prefab_path"] = new { type = "string", description = "Filter by prefab source path. Used by: prefab_instances." },
                ["max_depth"] = new { type = "integer", description = "Max tree depth. Used by: hierarchy. Default: unlimited." },
                ["offset"] = new { type = "integer", description = "Pagination offset. Used by: find, find_in_radius, prefab_instances. Default: 0." },
                ["limit"] = new { type = "integer", description = "Max results. Used by: find, find_in_radius, prefab_instances. Default: 50." },
                ["format"] = new { type = "string", description = "Response detail level. Used by: find, get_details. Values: 'concise' (default), 'detailed'.", @enum = new[] { "concise", "detailed" } }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 2: gameobject ───────────────────────────────────────────

    internal static readonly object GameObject = new
    {
        name = "gameobject",
        description = "Create, modify, and destroy GameObjects in the scene. Use 'scene' to find objects first, then this tool to change them. For adding or configuring components, use 'component' instead. All write actions return the GUID and name of the affected object. Use 'batch_transform' to move multiple objects in a single call. Use 'destroy_by_name' to bulk-destroy objects by name (optionally under a parent), and 'destroy_children' to destroy all children of an object.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "create", "destroy", "destroy_by_name", "destroy_children", "duplicate", "reparent", "rename", "enable", "set_tags", "set_transform", "batch_transform" } },
                ["id"] = new { type = "string", description = "Target GameObject GUID. Required for: destroy, duplicate, reparent, rename, enable, set_tags, set_transform. Also used as parent GUID for: destroy_by_name, destroy_children." },
                ["ids"] = new { type = "string", description = "Comma-separated GUIDs. Required for: batch_transform." },
                ["name"] = new { type = "string", description = "Object name. Used by: create, rename, destroy_by_name (required — name filter for objects to destroy)." },
                ["parent_id"] = new { type = "string", description = "Parent GUID. Used by: create (optional), reparent (required)." },
                ["position"] = new { type = "string", description = "Position as 'x,y,z'. Used by: create, set_transform, batch_transform." },
                ["rotation"] = new { type = "string", description = "Rotation as 'pitch,yaw,roll'. Used by: create, set_transform, batch_transform." },
                ["scale"] = new { type = "string", description = "Scale as 'x,y,z'. Used by: create, set_transform, batch_transform." },
                ["space"] = new { type = "string", description = "Coordinate space. Used by: set_transform. Values: 'world' (default), 'local'.", @enum = new[] { "world", "local" } },
                ["enabled"] = new { type = "boolean", description = "Enable/disable state. Required for: enable." },
                ["tags"] = new { type = "string", description = "Comma-separated tags. Required for: set_tags." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 3: component ────────────────────────────────────────────

    internal static readonly object Component = new
    {
        name = "component",
        description = "Manage components on GameObjects. Use 'scene' to find objects, then this tool to add, remove, or configure their components. 'add' returns similar type suggestions if the exact name doesn't match. Use 'get_types' with a filter to discover available component types before adding. Use 'get_properties' to inspect a component's current values before setting them. Use 'bulk_set' or 'apply_properties' to set multiple properties on a single component in one call via a JSON object of {property: value} pairs.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "add", "remove", "set_property", "set_enabled", "get_properties", "get_types", "copy", "bulk_set", "apply_properties" } },
                ["id"] = new { type = "string", description = "Target GameObject GUID. Required for: add, remove, set_property, set_enabled, get_properties, bulk_set, apply_properties." },
                ["component_id"] = new { type = "string", description = "Component GUID on the target object. Required for: remove, set_property, set_enabled, get_properties, bulk_set, apply_properties." },
                ["type"] = new { type = "string", description = "Component type name (e.g. 'Rigidbody', 'ModelRenderer'). Required for: add." },
                ["property"] = new { type = "string", description = "Property name. Required for: set_property." },
                ["value"] = new { type = "string", description = "Property value (auto-converted to correct type). Required for: set_property." },
                ["properties"] = new { description = "Object or JSON string of {property: value} pairs. Required for: bulk_set, apply_properties. Example: {\"Model\": \"path.vmdl\", \"Tint\": \"1,0,0,1\"}", oneOf = new object[] { new { type = "object", additionalProperties = true }, new { type = "string" } } },
                ["enabled"] = new { type = "boolean", description = "Enable/disable state. Required for: set_enabled." },
                ["source_component_id"] = new { type = "string", description = "Source component GUID to copy from. Required for: copy." },
                ["target_id"] = new { type = "string", description = "Target GameObject GUID to copy to. Required for: copy." },
                ["filter"] = new { type = "string", description = "Type name filter. Used by: get_types." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 4: prefab ───────────────────────────────────────────────

    internal static readonly object Prefab = new
    {
        name = "prefab",
        description = "Manage prefabs: instantiate into scenes, create from GameObjects, and manage instance overrides. Use 'create' to convert a scene GameObject into a reusable .prefab file on disk. Use 'save_overrides' to push instance changes back to the prefab source file. Use 'revert' to discard instance changes and match the source prefab. Use 'get_overrides' to see which properties differ from the source.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "instantiate", "get_structure", "get_instances", "break", "update", "create", "save_overrides", "revert", "get_overrides" } },
                ["id"] = new { type = "string", description = "GameObject GUID. Required for: break, update, create, save_overrides, revert, get_overrides." },
                ["path"] = new { type = "string", description = "Prefab file path. Required for: instantiate, get_structure. Optional filter for: get_instances." },
                ["save_path"] = new { type = "string", description = "Where to save the new prefab file. Required for: create." },
                ["position"] = new { type = "string", description = "Spawn position as 'x,y,z'. Used by: instantiate." },
                ["rotation"] = new { type = "string", description = "Spawn rotation as 'pitch,yaw,roll'. Used by: instantiate." },
                ["offset"] = new { type = "integer", description = "Pagination offset. Used by: get_instances. Default: 0." },
                ["limit"] = new { type = "integer", description = "Max results. Used by: get_instances. Default: 50." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 5: asset_query ──────────────────────────────────────────

    internal static readonly object AssetQuery = new
    {
        name = "asset_query",
        description = "Browse, search, and inspect project assets. Use 'browse' to list a directory, 'search' to find assets by name or type, and the various 'get_*' actions to inspect specific asset details. This tool only reads — use 'asset_manage' to create, rename, move, or delete assets. For cloud/workshop assets, use 'cloud' instead.",
        annotations = new { readOnlyHint = true },
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "browse", "search", "open", "get_dependencies", "get_model_info", "get_material_properties", "get_mesh_info", "get_bounds", "get_unsaved", "get_status", "get_json", "get_references" } },
                ["path"] = new { type = "string", description = "Asset path. Required for: open, get_dependencies, get_model_info, get_material_properties, get_status, get_json, get_references." },
                ["id"] = new { type = "string", description = "GameObject GUID. Required for: get_mesh_info, get_bounds." },
                ["query"] = new { type = "string", description = "Search term. Required for: search." },
                ["type"] = new { type = "string", description = "Asset type filter. Used by: search." },
                ["directory"] = new { type = "string", description = "Directory to browse. Used by: browse." },
                ["deep"] = new { type = "boolean", description = "Include transitive references. Used by: get_references. Default: false." },
                ["format"] = new { type = "string", description = "Response detail level. Used by: search, get_model_info. Values: 'concise' (default), 'detailed'.", @enum = new[] { "concise", "detailed" } },
                ["offset"] = new { type = "integer", description = "Pagination offset. Used by: browse, search. Default: 0." },
                ["limit"] = new { type = "integer", description = "Max results. Used by: browse, search. Default: 50." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 6: asset_manage ─────────────────────────────────────────

    internal static readonly object AssetManage = new
    {
        name = "asset_manage",
        description = "Create, rename, move, delete, save, and reload project assets. Use 'create' to make a new GameResource of any type (material, sound event, etc.). Use 'reload' to force-recompile an asset from disk. Destructive actions (delete) send files to the recycle bin, not permanent deletion. For browsing or inspecting assets, use 'asset_query' instead.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "create", "delete", "rename", "move", "save", "reload", "get_references" } },
                ["path"] = new { type = "string", description = "Source asset path. Required for: delete, rename, move, save, reload, get_references." },
                ["type"] = new { type = "string", description = "GameResource type name. Required for: create." },
                ["new_name"] = new { type = "string", description = "New name for the asset. Required for: rename." },
                ["destination"] = new { type = "string", description = "Target directory path. Required for: move." },
                ["deep"] = new { type = "boolean", description = "Include transitive references. Used by: get_references. Default: false." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 7: editor ───────────────────────────────────────────────

    internal static readonly object Editor = new
    {
        name = "editor",
        description = "Control the s&box editor: manage object selection, play mode, scene saving, undo/redo, console commands, code editor, preferences, and MCP server lifecycle. For scene content operations use 'scene' and 'gameobject'. Use 'get_log' to read compiler output and runtime errors. Use 'undo' and 'redo' to step through edit history. Use 'console_run' to execute console commands — both ConVar (get/set) and ConCmd (execute with output capture). Use 'console_list' with filter to discover available commands. Use 'restart_mcp' after hot-reload to restart the SSE server with recompiled code. Use 'reload_scene' to safely reload the active scene (or a scene by path) from disk — recovery for the post-asset-recompile blank-scene bug; stops play mode automatically.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "select", "get_selected", "set_selected", "clear_selection", "frame_selection", "get_play_state", "start_play", "stop_play", "get_log", "save_scene", "save_scene_as", "undo", "redo", "console_list", "console_run", "open_code_file", "get_preferences", "set_preference", "restart_mcp", "reload_scene" } },
                ["id"] = new { type = "string", description = "GameObject GUID. Used by: select, frame_selection." },
                ["ids"] = new { type = "string", description = "Comma-separated GUIDs. Required for: set_selected." },
                ["path"] = new { type = "string", description = "File or scene path. Required for: save_scene_as, open_code_file. Optional for: reload_scene (defaults to active scene)." },
                ["command"] = new { type = "string", description = "Console command string. Required for: console_run. Supports both ConVar ('sbep.debug 1') and ConCmd ('sbep.dump_resources')." },
                ["filter"] = new { type = "string", description = "Filter text. Used by: get_log, console_list." },
                ["wait_ms"] = new { type = "integer", description = "Milliseconds to wait for ConCmd output capture. Used by: console_run. Default: 100. Max: 5000." },
                ["count"] = new { type = "integer", description = "Number of log entries. Used by: get_log." },
                ["line"] = new { type = "integer", description = "Line number. Used by: open_code_file." },
                ["column"] = new { type = "integer", description = "Column number. Used by: open_code_file." },
                ["key"] = new { type = "string", description = "Preference key. Required for: set_preference." },
                ["value"] = new { type = "string", description = "Preference value. Required for: set_preference." },
                ["offset"] = new { type = "integer", description = "Pagination offset. Used by: get_log. Default: 0." },
                ["limit"] = new { type = "integer", description = "Max results. Used by: get_log. Default: 50." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 8: compile ──────────────────────────────────────────────

    internal static readonly object Compile = new
    {
        name = "compile",
        description = "Trigger code compilation and read build results. Use 'smart_trigger' for a one-call compile+wait+errors-with-source-context (best for typical fix loops). Use 'trigger' to start a build, 'status' to check state, and 'errors' to read diagnostics. Use 'wait' to block until compilation finishes (with configurable timeout). Use 'fix_context' to retrieve the full source file and surrounding context for a specific error index to help fix it in one shot. Use 'snapshot_hotreload' before a compile and 'verify_hotreload' after to detect silent DLL swap failures (build succeeded but editor is still running the old assembly). Runs on background thread — does not block the editor.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "trigger", "status", "errors", "generate_solution", "wait", "smart_trigger", "fix_context", "snapshot_hotreload", "verify_hotreload" } },
                ["severity"] = new { type = "string", description = "Filter diagnostics by severity. Used by: errors. Values: 'error', 'warning', 'info'.", @enum = new[] { "error", "warning", "info" } },
                ["timeout_ms"] = new { type = "integer", description = "Max wait time in milliseconds. Used by: wait. Default: 60000." },
                ["context_lines"] = new { type = "integer", description = "Lines of source context around each error. Used by: smart_trigger (default 10, max 30), fix_context (default 15, max 50)." },
                ["max_errors"] = new { type = "integer", description = "Max errors to return. Used by: smart_trigger. Default: 5, max: 20." },
                ["error_index"] = new { type = "integer", description = "0-based index into the sorted error list. Used by: fix_context. Default: 0." },
                ["generation"] = new { type = "integer", description = "Pre-compile hotload generation counter from snapshot_hotreload. Used by: verify_hotreload." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 9: mesh ─────────────────────────────────────────────────

    internal static readonly object Mesh = new
    {
        name = "mesh",
        description = "Create and edit polygon meshes directly in the scene. Primitives: 'create_block' (box), 'create_plane' (flat quad), 'create_cylinder' (ring-based), 'create_wedge' (triangular prism/ramp), 'create_arch' (semicircular arc), 'create_clutter' (scattered objects). Construction: 'extrude_faces' (push faces outward), 'remove_faces' (cut holes), 'add_face' (polygon from vertices), 'clip_faces' (slice with plane), 'scale_mesh' (resize). Refinement: 'thicken_faces' (give faces depth), 'bevel_edges' (round edges), 'bevel_vertices' (round corners), 'split_edges' (subdivide edges), 'quad_slice_faces' (subdivide faces into grid), 'dissolve_edges' (simplify by merging faces). Topology: 'bridge_edges' (connect two open edges), 'connect_vertices' (split face by connecting two vertices), 'flip_faces' (reverse normals), 'extend_edges' (extrude boundary edges outward). Per-element: vertex position/color/blend, face material/texture. Note: after topology changes, face/vertex/edge indices are invalidated — use returned indices or 'get_info' for subsequent operations.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "create_block", "create_clutter", "create_plane", "create_cylinder", "create_wedge", "create_arch", "extrude_faces", "remove_faces", "add_face", "clip_faces", "scale_mesh", "thicken_faces", "bevel_edges", "bevel_vertices", "split_edges", "quad_slice_faces", "dissolve_edges", "bridge_edges", "connect_vertices", "flip_faces", "extend_edges", "set_face_material", "set_texture_params", "set_vertex_position", "set_vertex_color", "set_vertex_blend", "get_info" } },
                ["id"] = new { type = "string", description = "Target mesh GameObject GUID. Required for: extrude_faces, remove_faces, add_face, clip_faces, scale_mesh, thicken_faces, bevel_edges, bevel_vertices, split_edges, quad_slice_faces, dissolve_edges, bridge_edges, connect_vertices, flip_faces, extend_edges, set_face_material, set_texture_params, set_vertex_position, set_vertex_color, set_vertex_blend, get_info." },
                ["position"] = new { type = "string", description = "Position as 'x,y,z'. Used by: create_block, create_plane, create_cylinder, create_wedge, create_arch, create_clutter." },
                ["size"] = new { type = "string", description = "Size as 'x,y,z'. Used by: create_block, create_plane, create_cylinder, create_wedge, create_arch. For scale_mesh: scale factor as 'x,y,z'." },
                ["material"] = new { type = "string", description = "Material path. Used by: create_block, create_plane, create_cylinder, create_wedge, create_arch, set_face_material, add_face." },
                ["name"] = new { type = "string", description = "Object name. Used by: create_block, create_plane, create_cylinder, create_wedge, create_arch, create_clutter." },
                ["definition"] = new { type = "string", description = "Clutter definition asset path. Required for: create_clutter." },
                ["radius"] = new { type = "number", description = "Scatter radius. Used by: create_clutter." },
                ["segments"] = new { type = "integer", description = "Segment count (3-64). Used by: create_cylinder (default 16), create_arch (default 8)." },
                ["face_index"] = new { type = "integer", description = "Single face index. Required for: set_face_material, set_texture_params." },
                ["face_indices"] = new { type = "string", description = "Comma-separated face indices '0,1,2'. Required for: extrude_faces, remove_faces, clip_faces, thicken_faces, quad_slice_faces." },
                ["vertex_index"] = new { type = "integer", description = "Single vertex index. Required for: set_vertex_position, set_vertex_color, set_vertex_blend." },
                ["vertex_indices"] = new { type = "string", description = "Comma-separated vertex indices in winding order. Required for: add_face. Used by: bevel_vertices." },
                ["extrude_offset"] = new { type = "string", description = "Extrusion direction/distance as 'x,y,z'. Used by: extrude_faces. Default: face normal * 50." },
                ["plane_normal"] = new { type = "string", description = "Cutting plane normal as 'x,y,z'. Required for: clip_faces." },
                ["plane_point"] = new { type = "string", description = "Point on cutting plane as 'x,y,z'. Required for: clip_faces." },
                ["remove_behind"] = new { type = "boolean", description = "Remove faces behind cutting plane. Used by: clip_faces. Default: true." },
                ["cap"] = new { type = "boolean", description = "Cap the cut opening with a new face. Used by: clip_faces. Default: true." },
                ["edge_indices"] = new { type = "string", description = "Comma-separated half-edge indices. Required for: bevel_edges, split_edges, dissolve_edges, extend_edges." },
                ["edge_index_a"] = new { type = "integer", description = "First edge index. Required for: bridge_edges." },
                ["edge_index_b"] = new { type = "integer", description = "Second edge index. Required for: bridge_edges." },
                ["vertex_index_a"] = new { type = "integer", description = "First vertex index. Required for: connect_vertices." },
                ["vertex_index_b"] = new { type = "integer", description = "Second vertex index. Required for: connect_vertices." },
                ["amount"] = new { type = "number", description = "Thickness/distance amount. Required for: thicken_faces, extend_edges." },
                ["distance"] = new { type = "number", description = "Bevel distance. Used by: bevel_edges, bevel_vertices. Default: 5." },
                ["bevel_segments"] = new { type = "integer", description = "Bevel smoothness (segment count). Used by: bevel_edges. Default: 1." },
                ["shape"] = new { type = "number", description = "Bevel profile shape (-1 to 1). Used by: bevel_edges. Default: 0." },
                ["cuts_x"] = new { type = "integer", description = "X subdivisions. Used by: quad_slice_faces. Default: 1." },
                ["cuts_y"] = new { type = "integer", description = "Y subdivisions. Used by: quad_slice_faces. Default: 1." },
                ["must_be_planar"] = new { type = "boolean", description = "Require planar faces for dissolve. Used by: dissolve_edges. Default: false." },
                ["color"] = new { type = "string", description = "Color as 'r,g,b,a'. Required for: set_vertex_color." },
                ["blend"] = new { type = "number", description = "Blend weight 0-1. Required for: set_vertex_blend." },
                ["offset"] = new { type = "string", description = "Texture offset as 'u,v'. Used by: set_texture_params." },
                ["scale"] = new { type = "string", description = "Texture scale as 'u,v'. Used by: set_texture_params." },
                ["rotation"] = new { type = "number", description = "Texture rotation degrees. Used by: set_texture_params." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 10: lighting ────────────────────────────────────────────

    internal static readonly object Lighting = new
    {
        name = "lighting",
        description = "Create and configure all light types in the scene. Use 'create' with the 'type' parameter: point (omnidirectional), spot (cone), ambient (scene-wide flat), environment (directional sun/moon), or indirect_volume (GI bounce probe). Use 'configure' to modify color, intensity, range, and shadow settings on existing lights. Skybox actions control the scene's background environment and ambient lighting.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "create", "configure", "create_skybox", "set_skybox" } },
                ["id"] = new { type = "string", description = "Light GameObject GUID. Required for: configure. Optional for: set_skybox (auto-finds if omitted)." },
                ["type"] = new { type = "string", description = "Light type. Required for: create.", @enum = new[] { "point", "spot", "ambient", "environment", "indirect_volume" } },
                ["position"] = new { type = "string", description = "Position as 'x,y,z'. Used by: create." },
                ["color"] = new { type = "string", description = "Color as 'r,g,b' (0-255). Used by: create, configure." },
                ["intensity"] = new { type = "number", description = "Light intensity. Used by: create, configure." },
                ["range"] = new { type = "number", description = "Light range in units. Used by: create, configure." },
                ["shadows"] = new { type = "boolean", description = "Enable shadows. Used by: configure." },
                ["material"] = new { type = "string", description = "Skybox material path. Used by: create_skybox, set_skybox." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 11: physics ─────────────────────────────────────────────

    internal static readonly object Physics = new
    {
        name = "physics",
        description = "Add and configure physics components: colliders, rigidbodies, character controllers, and joints. Use 'add_collider' with a 'type' parameter for any shape: box, sphere, capsule, hull (convex), plane (finite quad — default 50x50, NOT infinite), or model (auto-generated from mesh). Use 'create_joint' for physics constraints between objects: fixed, spring, hinge, ball, or slider. Plane colliders are finite meshes — for large ground planes, use a BoxCollider with large dimensions instead.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "add_collider", "configure_collider", "add_rigidbody", "create_model_physics", "create_character_controller", "create_joint" } },
                ["id"] = new { type = "string", description = "Target GameObject GUID. Required for: add_collider, configure_collider, add_rigidbody, create_model_physics, create_character_controller." },
                ["type"] = new { type = "string", description = "Collider or joint type. Required for: add_collider, create_joint." },
                ["component_id"] = new { type = "string", description = "Collider component GUID. Required for: configure_collider." },
                ["size"] = new { type = "string", description = "Collider size as 'x,y,z'. Used by: add_collider." },
                ["center"] = new { type = "string", description = "Collider center offset as 'x,y,z'. Used by: add_collider." },
                ["is_trigger"] = new { type = "boolean", description = "Set as trigger (no physics response). Used by: configure_collider." },
                ["surface"] = new { type = "string", description = "Surface material path. Used by: configure_collider." },
                ["gravity"] = new { type = "boolean", description = "Enable gravity. Used by: add_rigidbody. Default: true." },
                ["mass"] = new { type = "number", description = "Mass in kg. Used by: add_rigidbody." },
                ["enhanced_ccd"] = new { type = "boolean", description = "Enable enhanced continuous collision detection for fast-moving objects (bullets, rockets). Used by: add_rigidbody." },
                ["body_a"] = new { type = "string", description = "First body GUID. Required for: create_joint." },
                ["body_b"] = new { type = "string", description = "Second body GUID. Used by: create_joint." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 12: audio ───────────────────────────────────────────────

    internal static readonly object Audio = new
    {
        name = "audio",
        description = "Create and configure audio components in the scene. Use 'create' with the 'type' parameter: point (directional emitter with falloff), box (rectangular sound zone), soundscape (ambient trigger volume), dsp_volume (audio effect zone), or listener (required for audio playback — every scene needs one). Use 'configure' to adjust volume, radius, falloff, and sound event. For playing sounds at runtime from C# code, use Sound.Play() — this tool is for scene-placed audio sources.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "create", "configure" } },
                ["id"] = new { type = "string", description = "Target GameObject GUID. Required for: configure." },
                ["component_id"] = new { type = "string", description = "Audio component GUID. Required for: configure." },
                ["type"] = new { type = "string", description = "Audio component type. Required for: create.", @enum = new[] { "point", "box", "soundscape", "dsp_volume", "listener" } },
                ["position"] = new { type = "string", description = "Position as 'x,y,z'. Used by: create." },
                ["sound_event"] = new { type = "string", description = "Sound event path. Used by: create." },
                ["volume"] = new { type = "number", description = "Volume 0-1. Used by: configure." },
                ["radius"] = new { type = "number", description = "Sound radius. Used by: configure." },
                ["falloff"] = new { type = "number", description = "Falloff distance. Used by: configure." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 13: effects ─────────────────────────────────────────────

    internal static readonly object Effects = new
    {
        name = "effects",
        description = "Create and configure visual effects in the scene. Use 'create' with the 'type' parameter: particle (particle system), fog (volumetric fog zone), beam (laser/beam between points), rope (verlet physics rope), radius_damage (area damage trigger), or render_entity (custom render object). Use 'configure_particle' or 'configure_post_processing' to modify effect-specific properties. This tool creates scene-placed effects — for triggering effects at runtime from code, use the particle/sound APIs directly.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "create", "configure_particle", "configure_post_processing" } },
                ["id"] = new { type = "string", description = "Target GameObject GUID. Required for: configure_particle, configure_post_processing." },
                ["component_id"] = new { type = "string", description = "Effect component GUID. Required for: configure_particle." },
                ["type"] = new { type = "string", description = "Effect type. Required for: create.", @enum = new[] { "particle", "fog", "beam", "rope", "radius_damage", "render_entity" } },
                ["position"] = new { type = "string", description = "Position as 'x,y,z'. Used by: create." },
                ["properties"] = new { type = "string", description = "JSON object of effect-specific properties. Used by: create, configure_particle, configure_post_processing." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 14: camera ──────────────────────────────────────────────

    internal static readonly object Camera = new
    {
        name = "camera",
        description = "Create and configure camera components, or capture screenshots of the scene. 'capture_viewport': single shot from any position/look_at. 'capture_tour': batch multiple named shots in one call — pass a 'shots' array, get all images back at once. 'orbit_capture': auto-orbit a target point at a given radius/elevation, evenly-spaced shots with compass labels — no coordinate math needed.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "create", "configure", "capture_viewport", "capture_tour", "orbit_capture" } },
                ["id"] = new { type = "string", description = "Camera GameObject GUID. Required for: configure." },
                ["position"] = new { type = "string", description = "Position as 'x,y,z'. Used by: create, capture_viewport." },
                ["rotation"] = new { type = "string", description = "Rotation as 'pitch,yaw,roll'. Used by: create, capture_viewport. Ignored if look_at is set." },
                ["look_at"] = new { type = "string", description = "Target position as 'x,y,z' to aim the camera at. Used by: capture_viewport. Takes priority over rotation." },
                ["fov"] = new { type = "number", description = "Field of view in degrees. Used by: configure, capture_viewport, capture_tour (default 90), orbit_capture (default 75)." },
                ["near_clip"] = new { type = "number", description = "Near clipping plane distance. Used by: configure." },
                ["far_clip"] = new { type = "number", description = "Far clipping plane distance. Used by: configure." },
                ["width"] = new { type = "integer", description = "Capture width in pixels (320-3840). Default 1280. Used by: capture_viewport, capture_tour, orbit_capture." },
                ["height"] = new { type = "integer", description = "Capture height in pixels (240-2160). Default 720. Used by: capture_viewport, capture_tour, orbit_capture." },
                ["quality"] = new { type = "integer", description = "JPEG quality 10-100. Default 75. Used by: capture_viewport, capture_tour, orbit_capture." },
                ["shots"] = new
                {
                    type = "array",
                    description = "Array of shots for capture_tour. Each element: {position: 'x,y,z', look_at: 'x,y,z', label?: 'string', fov?: number}. Shared width/height/quality/fov apply to all shots unless overridden per-shot.",
                    items = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            ["position"] = new { type = "string", description = "Camera world position as 'x,y,z'." },
                            ["look_at"]  = new { type = "string", description = "Look-at world position as 'x,y,z'." },
                            ["label"]    = new { type = "string", description = "Label shown in the image caption." },
                            ["fov"]      = new { type = "number", description = "Per-shot FOV override." }
                        }
                    }
                },
                ["target"] = new { type = "string", description = "World position as 'x,y,z' to orbit around. Required for: orbit_capture." },
                ["radius"] = new { type = "number", description = "Orbit radius in world units. Default 300. Used by: orbit_capture." },
                ["elevation"] = new { type = "number", description = "Camera world Z height during orbit. Default 200. Used by: orbit_capture." },
                ["count"] = new { type = "integer", description = "Number of evenly-spaced orbit shots (2-32). Default 8. Used by: orbit_capture." },
                ["start_angle"] = new { type = "number", description = "Starting angle in degrees (0 = east). Default 0. Used by: orbit_capture." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 15: navmesh ─────────────────────────────────────────────

    internal static readonly object Navmesh = new
    {
        name = "navmesh",
        description = "Create and manage AI navigation meshes. Place 'create_area' volumes to define walkable regions, 'create_link' for off-mesh connections (jumps, ladders), and 'create_agent' on GameObjects that need pathfinding. Use 'generate' to build the navmesh after placing areas — this can take several seconds on large scenes. Use 'query_path' to test pathfinding between two points. For physics constraints between objects (hinges, springs), use 'physics' with the 'create_joint' action instead.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "create_agent", "create_area", "create_link", "generate", "get_status", "query_path" } },
                ["id"] = new { type = "string", description = "Target GameObject GUID. Required for: create_agent." },
                ["position"] = new { type = "string", description = "Position as 'x,y,z'. Used by: create_area." },
                ["size"] = new { type = "string", description = "Area size as 'x,y,z'. Used by: create_area." },
                ["start_position"] = new { type = "string", description = "Link start as 'x,y,z'. Required for: create_link." },
                ["end_position"] = new { type = "string", description = "Link end as 'x,y,z'. Required for: create_link." },
                ["from"] = new { type = "string", description = "Path start as 'x,y,z'. Required for: query_path." },
                ["to"] = new { type = "string", description = "Path end as 'x,y,z'. Required for: query_path." },
                ["speed"] = new { type = "number", description = "Agent speed. Used by: create_agent." },
                ["radius"] = new { type = "number", description = "Agent radius. Used by: create_agent." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 16: session ─────────────────────────────────────────────

    internal static readonly object Session = new
    {
        name = "session",
        description = "Manage editor sessions: list all open editing sessions, switch between them, and load new scenes. Use this when working with multiple scenes simultaneously or when you need to switch editing context. For scene content operations within the active session, use 'scene' and 'gameobject'. For play mode and editor state, use 'editor'.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "list", "set_active", "load_scene" } },
                ["session_id"] = new { type = "string", description = "Session identifier. Required for: set_active." },
                ["path"] = new { type = "string", description = "Scene file path. Required for: load_scene." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 17: project ─────────────────────────────────────────────

    internal static readonly object Project = new
    {
        name = "project",
        description = "Read and modify project-level settings: collision layer configuration, input action bindings, and project metadata. Use 'set_collision_rule' to control which physics layers collide with each other. Use 'get_input' to see all configured input actions and their bindings. For runtime project info, use the 'get_info' action.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "get_collision", "set_collision_rule", "get_input", "get_info" } },
                ["layer_a"] = new { type = "string", description = "First collision layer name. Required for: set_collision_rule." },
                ["layer_b"] = new { type = "string", description = "Second collision layer name. Required for: set_collision_rule." },
                ["collides"] = new { type = "boolean", description = "Whether the layers should collide. Required for: set_collision_rule." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 18: cloud ───────────────────────────────────────────────

    internal static readonly object Cloud = new
    {
        name = "cloud",
        description = "Search the s&box cloud asset store, get package details, list versions, and mount packages into the current project. Returns package idents, titles, types, and thumbnail URLs. Use 'get_package' to get the 'primaryAsset' field — this contains the local vmdl path needed for Model.Load() and scene file references. Use 'get_versions' to list all available revisions of a package (useful for pinning map versions). Use 'mount' to download and register a package in the project's .sbproj file — optionally pin to a specific revision. Runs on background thread — does not block the editor. For local project assets, use 'asset_query' instead.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "search", "get_package", "get_versions", "mount" } },
                ["query"] = new { type = "string", description = "Search term. Required for: search." },
                ["ident"] = new { type = "string", description = "Package identifier in 'org.name' format. Required for: get_package, get_versions, mount." },
                ["type"] = new { type = "string", description = "Filter by asset type. Used by: search." },
                ["max_results"] = new { type = "integer", description = "Max search results (1-50). Used by: search. Default: 10." },
                ["revision"] = new { type = "integer", description = "Pin to a specific package revision number (from get_versions). Used by: mount." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 19: terrain ─────────────────────────────────────────────

    internal static readonly object Terrain = new
    {
        name = "terrain",
        description = "Full terrain editing: create, sculpt heightmaps, apply procedural noise, paint materials, punch holes, import/export heightmaps. Use 'set_height' to sculpt with modes: set, raise, lower, flatten, smooth. Use 'noise' for procedural generation (perlin, ridged, billow). Use 'get_height' / 'get_material_at' to query terrain state. Use 'set_hole' for caves/tunnels. Use 'import_heightmap' / 'export_heightmap' for grayscale PNG I/O. Always call 'sync' after edits.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "create", "configure", "get_info", "get_height", "get_height_region", "set_height", "noise", "erode", "stamp", "add_material", "remove_material", "get_material_at", "blend_materials", "set_hole", "paint_material", "import_heightmap", "export_heightmap", "sync" } },
                ["id"] = new { type = "string", description = "Terrain GameObject GUID. Optional for most actions (auto-finds if only one terrain exists)." },
                ["name"] = new { type = "string", description = "GameObject name for the terrain. IMPORTANT: determines the .terrain storage filename (terrain_data/{name}.terrain). Use unique names per scene to avoid overwriting other terrains. Used by: create. Default: scene name." },
                ["size"] = new { type = "number", description = "Total terrain size in inches (= World Scale × Heightmap Size). e.g. 19968 for ~500m with scale=39, res=512. Used by: create. Default: 1024." },
                ["height"] = new { type = "number", description = "Height value in inches. create: max terrain elevation (e.g. 10000 ~= 254m). set_height: target or delta height in world units." },
                ["resolution"] = new { type = "integer", description = "Heightmap resolution (512, 1024, 2048, 4096, or 8192). Higher = more detail but more VRAM: 512=~1.5MB, 2048=24MB, 4096=96MB. Used by: create. Default: 512." },
                ["position"] = new { type = "string", description = "s&box world position 'x,y,z'. Terrain occupies [terrainPos, terrainPos+size] in X and Y. Use get_info to find terrain position and size. Required for: get_height, set_height, get_material_at, set_hole, paint_material, stamp, get_height_region, blend_materials." },
                ["material"] = new { type = "string", description = "Terrain material name. Required for: paint_material." },
                ["material_path"] = new { type = "string", description = ".terrain_material asset path. Used by: add_material, remove_material." },
                ["material_index"] = new { type = "integer", description = "Material index. Used by: remove_material." },
                ["radius"] = new { type = "number", description = "Brush radius (world units). Used by: set_height, set_hole, paint_material. Default: 50." },
                ["strength"] = new { type = "number", description = "Brush strength 0-1. Used by: set_height, paint_material. Default: 1." },
                ["mode"] = new { type = "string", description = "set_height: 'set'|'raise'|'lower'|'flatten'|'smooth'. noise: 'set'|'add'|'multiply'. Default: 'set'." },
                ["falloff"] = new { type = "string", description = "Brush falloff: 'linear'|'smooth'|'none'. Used by: set_height. Default: 'linear'." },
                ["type"] = new { type = "string", description = "Noise type: 'perlin'|'ridged'|'billow'. Used by: noise. Default: 'perlin'." },
                ["scale"] = new { type = "number", description = "Noise frequency scale. Used by: noise. Default: 0.01." },
                ["amplitude"] = new { type = "number", description = "Noise height amplitude (world units). Used by: noise. Default: 30% of terrain height." },
                ["octaves"] = new { type = "integer", description = "Noise octave layers. Used by: noise. Default: 4." },
                ["persistence"] = new { type = "number", description = "Amplitude decay per octave. Used by: noise. Default: 0.5." },
                ["lacunarity"] = new { type = "number", description = "Frequency increase per octave. Used by: noise. Default: 2.0." },
                ["seed"] = new { type = "integer", description = "Noise random seed. Used by: noise. Default: 42." },
                ["offset_x"] = new { type = "number", description = "Noise X offset. Used by: noise." },
                ["offset_y"] = new { type = "number", description = "Noise Y offset. Used by: noise." },
                ["enabled"] = new { type = "boolean", description = "true = punch hole, false = fill. Used by: set_hole. Default: true." },
                ["path"] = new { type = "string", description = "File path. import_heightmap: source image. export_heightmap: output PNG." },
                ["iterations"] = new { type = "integer", description = "Number of erosion droplets. Used by: erode. Default: 50000." },
                ["erosion_rate"] = new { type = "number", description = "How fast droplets erode. Used by: erode. Default: 0.3." },
                ["deposition_rate"] = new { type = "number", description = "How fast sediment deposits. Used by: erode. Default: 0.3." },
                ["evaporation_rate"] = new { type = "number", description = "Water evaporation rate. Used by: erode. Default: 0.01." },
                ["gravity"] = new { type = "number", description = "Gravity affecting flow speed. Used by: erode. Default: 4." },
                ["inertia"] = new { type = "number", description = "Droplet direction inertia 0-1. Used by: erode. Default: 0.05." },
                ["capacity"] = new { type = "number", description = "Sediment capacity factor. Used by: erode. Default: 4." },
                ["lifetime"] = new { type = "integer", description = "Max droplet lifetime in steps. Used by: erode. Default: 30." },
                ["erosion_radius"] = new { type = "integer", description = "Erosion brush radius in texels. Used by: erode. Default: 3." },
                ["angle"] = new { type = "number", description = "Rotation angle in degrees. Used by: stamp (for ridge/valley). Default: 0." },
                ["region_size"] = new { type = "number", description = "Region size in world units. Used by: get_height_region. Default: 100." },
                ["samples"] = new { type = "integer", description = "Samples per axis. Used by: get_height_region. Default: 10." },
                ["base_index"] = new { type = "integer", description = "Base material index 0-31. Used by: blend_materials." },
                ["overlay_index"] = new { type = "integer", description = "Overlay material index 0-31. Used by: blend_materials." },
                ["blend"] = new { type = "number", description = "Blend factor 0-1 between base and overlay. Used by: blend_materials. Default: 0.5." },
                ["lod_levels"] = new { type = "integer", description = "LOD level count. Used by: configure." },
                ["subdivision"] = new { type = "integer", description = "Subdivision factor. Used by: configure." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 19b: code ────────────────────────────────────────────────

    internal static readonly object Code = new
    {
        name = "code",
        description = "Read, write, and search project source code files. Use 'read_file' to view file contents with line numbers (supports offset/limit for large files). Use 'write_file' to create or overwrite files. Use 'list_files' to discover files by glob pattern in a directory. Use 'search_code' for grep-like text search across .cs files with context lines. All paths are relative to the project root. Cannot access Libraries/ (except arenula_mcp). This tool does NOT modify scenes — use 'gameobject' and 'component' for that.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "read_file", "write_file", "list_files", "search_code" } },
                ["path"] = new { type = "string", description = "Relative file path within project. Required for: read_file, write_file." },
                ["content"] = new { type = "string", description = "File content to write. Required for: write_file." },
                ["query"] = new { type = "string", description = "Search text. Required for: search_code." },
                ["directory"] = new { type = "string", description = "Subdirectory to scope operations. Used by: list_files, search_code. Default: 'Code/'." },
                ["pattern"] = new { type = "string", description = "Glob pattern for file matching. Used by: list_files. Default: '*.cs'." },
                ["offset"] = new { type = "integer", description = "Line offset (0-based) to start reading from. Used by: read_file. Default: 0." },
                ["limit"] = new { type = "integer", description = "Max lines to return. Used by: read_file (default 200, max 1000), list_files (default 100, max 500)." },
                ["case_sensitive"] = new { type = "boolean", description = "Case-sensitive search. Used by: search_code. Default: false." },
                ["max_results"] = new { type = "integer", description = "Max search matches. Used by: search_code. Default: 50, max: 200." },
                ["context_lines"] = new { type = "integer", description = "Lines of context around matches. Used by: search_code. Default: 2, max: 5." },
                ["create_directories"] = new { type = "boolean", description = "Auto-create parent directories. Used by: write_file. Default: true." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 20: trace ────────────────────────────────────────────────

    internal static readonly object Trace = new
    {
        name = "trace",
        description = "Cast physics traces (raycasts) in the scene to find surfaces, check clearance, and locate ground positions. Use 'ray' to cast from point A to B and get hit info. Use 'box' to check if a volume at a position overlaps any geometry (clearance check). Use 'ground' to find the ground position below a point by casting straight down. All actions are read-only queries — they do not modify the scene. Use this before placing objects to ensure valid positions.",
        annotations = new { readOnlyHint = true },
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "ray", "box", "ground" } },
                ["from"] = new { type = "string", description = "Start position as 'x,y,z'. Required for: ray." },
                ["to"] = new { type = "string", description = "End position as 'x,y,z'. Required for: ray." },
                ["position"] = new { type = "string", description = "World position as 'x,y,z'. Required for: box, ground." },
                ["size"] = new { type = "string", description = "Box size as 'x,y,z'. Used by: box. Default: '32,32,72' (player-sized)." },
                ["max_distance"] = new { type = "number", description = "Maximum downward distance. Used by: ground. Default: 10000." },
                ["tags"] = new { type = "string", description = "Comma-separated tags to exclude from trace. Used by: ray, box, ground." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 21: batch ──────────────────────────────────────────────

    internal static readonly object Batch = new
    {
        name = "batch",
        description = "Execute multiple tool operations in a single request. Provide a JSON array of operations, each specifying a tool name, action, and parameters. Operations execute sequentially and results are collected into an array. Use this to reduce round-trips when performing many related changes (e.g. creating multiple objects, setting multiple properties). If any operation fails, subsequent operations still execute — check individual results for errors.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "execute" } },
                ["operations"] = new { type = "string", description = "JSON array of operations. Each element: {\"tool\": \"gameobject\", \"action\": \"destroy\", \"params\": {\"id\": \"...\"}}. Required for: execute." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 22: reflection ─────────────────────────────────────────

    internal static readonly object Reflection = new
    {
        name = "reflection",
        description = "Reflect over loaded S&Box engine assemblies for ground-truth API discovery. Use 'search' to find types by name with optional namespace and kind filters. Use 'get_type' to get full details of a specific type (constructors, methods, properties, fields, attributes, interfaces). Use 'get_members' to search for methods or properties across all types by member name. Use 'list_assemblies' to see all loaded assemblies. This tool reads live engine state — it always reflects the current engine version, unlike cached documentation. Read-only, does not modify anything.",
        annotations = new { readOnlyHint = true },
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "search", "get_type", "get_members", "list_assemblies" } },
                ["query"] = new { type = "string", description = "Search term. Required for: search, get_members." },
                ["name"] = new { type = "string", description = "Full or short type name. Required for: get_type." },
                ["namespace"] = new { type = "string", description = "Namespace filter. Used by: search." },
                ["kind"] = new { type = "string", description = "Type kind filter. Used by: search, get_members (member kind: method, property).", @enum = new[] { "class", "struct", "enum", "interface", "method", "property" } },
                ["section"] = new { type = "string", description = "Limit get_type output to a specific section. Used by: get_type.", @enum = new[] { "interfaces", "attributes", "constructors", "properties", "methods", "fields", "values" } },
                ["include_inherited"] = new { type = "boolean", description = "Include inherited members. Used by: get_type. Default: false." },
                ["filter"] = new { type = "string", description = "Name filter. Used by: list_assemblies." },
                ["offset"] = new { type = "integer", description = "Pagination offset. Used by: search, get_members. Default: 0." },
                ["limit"] = new { type = "integer", description = "Max results. Used by: search, get_members. Default: 25." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 23: sbep_verify ────────────────────────────────────────

    internal static readonly object SbepVerify = new
    {
        name = "sbep_verify",
        description = "Run SBEP system verification checks. Use 'prefab_check' to validate all SBEP prefab JSON files (GUIDs, types, models, NetworkMode). Use 'component_check' to validate SBEP component classes via reflection. Use 'smoke_test' to spawn and verify all SBEP entities (requires play mode). Use 'resource_health' to get resource network status. Use 'state_snapshot' for complete SBEP system state. Use 'full_verify' for comprehensive end-to-end validation. Static checks work in editor mode; runtime checks require play mode.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The verification to run.", @enum = new[] { "prefab_check", "component_check", "smoke_test", "resource_health", "state_snapshot", "full_verify" } },
                ["filter"] = new { type = "string", description = "Filter checks by name substring. Used by: prefab_check, component_check." },
                ["verbose"] = new { type = "boolean", description = "Include passing checks in output. Default: false (only failures/warnings)." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 24: pipeline ───────────────────────────────────────────

    internal static readonly object Pipeline = new
    {
        name = "pipeline",
        description = "Automated build-and-verify workflow. Use 'compile_and_verify' after writing code to save the scene, compile, check for errors, run a TypeLibrary probe to confirm new types are live, and optionally run SBEP smoke tests — all in a single call. Use 'probe_typelibrary' to cheaply verify that one or more types resolve in TypeLibrary after a compile — the authoritative 'is my new code live?' check that bypasses the unreliable [EditorEvent.Hotload] counter. Returns structured pass/fail with actionable error messages.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The pipeline action to run.", @enum = new[] { "compile_and_verify", "probe_typelibrary" } },
                ["timeout_ms"] = new { type = "integer", description = "Max compilation wait time in milliseconds. Used by: compile_and_verify. Default: 120000 (2 minutes)." },
                ["skip_verify"] = new { type = "boolean", description = "Skip SBEP smoke test even if in play mode. Used by: compile_and_verify. Default: false." },
                ["pre_reload"] = new { type = "boolean", description = "Reload the scene before compile if playing + hotload-risky .cs file touched in last 60s. Used by: compile_and_verify. Default: true." },
                ["type_names"] = new { type = "array", items = new { type = "string" }, description = "Type names to resolve via Sandbox.TypeLibrary.GetType(). Used by: probe_typelibrary. If omitted, auto-derives from type declarations in .cs files edited in the last 60s." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    internal static readonly object Workflow = new
    {
        name = "workflow",
        description = "High-level AI workflows built on top of the lower-level scene, component, prefab, compile, and verification tools. Use this when the task is bigger than one primitive operation and you want a one-shot helper. It returns structured plans, checklists, or orchestration results for common s&box tasks such as prefab pairing, compile triage, and networked component verification.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The workflow to run.", @enum = new[] { "list", "create_prefab_pair", "fix_compile_errors", "verify_networked_component", "implement_spawnable_entity", "add_tool_mode" } },
                ["prefab_path"] = new { type = "string", description = "Prefab path. Used by: create_prefab_pair, implement_spawnable_entity." },
                ["type_name"] = new { type = "string", description = "Type name. Required for: verify_networked_component." },
                ["title"] = new { type = "string", description = "Spawn menu title. Optional for: create_prefab_pair." },
                ["description"] = new { type = "string", description = "Spawn menu description. Optional for: create_prefab_pair." },
                ["name"] = new { type = "string", description = "Tool mode name. Optional for: add_tool_mode." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    internal static readonly object VerifyChangeSet = new
    {
        name = "verify_change_set",
        description = "Map a set of touched files to the right verification plan, and optionally run the matching checks. Use this after code or asset changes when you want the MCP server to pick compile, SBEP, asset, or playtest validation automatically. This reduces both under-verification and unnecessary checks.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The verification mode.", @enum = new[] { "plan", "run" } },
                ["paths"] = new { type = "string", description = "Comma-separated repo-relative file paths." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    internal static readonly object Playtest = new
    {
        name = "playtest",
        description = "Deterministic play-mode orchestration with assertions over play state, logs, scene queries, and console output. Use 'boot_scene' to load a scene and optionally start play mode. Use 'run_assertions' with a JSON array to smoke-test gameplay and runtime behavior in a structured way.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The playtest operation.", @enum = new[] { "boot_scene", "run_assertions" } },
                ["scene_path"] = new { type = "string", description = "Scene path. Used by: boot_scene." },
                ["start_play"] = new { type = "boolean", description = "Start play mode after booting scene. Used by: boot_scene. Default: true." },
                ["ensure_playing"] = new { type = "boolean", description = "Ensure play mode before assertions. Used by: run_assertions. Default: true." },
                ["wait_ms"] = new { type = "integer", description = "Delay before evaluating assertions. Used by: run_assertions. Default: 250." },
                ["assertions"] = new { type = "string", description = "JSON array of assertions. Used by: run_assertions." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    internal static readonly object Schema = new
    {
        name = "schema",
        description = "Inspect component, property, resource, and enum metadata from the live editor TypeLibrary. Use this before component writes to discover exact type names, writable properties, accepted value kinds, and enum values. This tool is read-only.",
        annotations = new { readOnlyHint = true },
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "Schema lookup operation.", @enum = new[] { "components", "component", "property", "resources", "enum" } },
                ["type"] = new { type = "string", description = "Component/resource/enum type name. Required for: component, property, enum." },
                ["property"] = new { type = "string", description = "Property name. Required for: property." },
                ["filter"] = new { type = "string", description = "Case-insensitive name or namespace filter. Used by: components, resources." },
                ["offset"] = new { type = "integer", description = "Pagination offset. Default: 0." },
                ["limit"] = new { type = "integer", description = "Max results. Default: 100." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    internal static readonly object Operation = new
    {
        name = "operation",
        description = "Compatibility wrapper for validate, preview, apply, and rollback write operations. Prefer this when a client wants a uniform validate/preview/apply/rollback flow; internally it reuses the live transaction and batch dispatch paths. For multi-operation flows, pass operations as the same JSON array string accepted by transaction.preview.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "Operation lifecycle action.", @enum = new[] { "validate", "preview", "apply", "rollback" } },
                ["operations"] = new { type = "string", description = "JSON array of batched write operations. Used by: validate, preview." },
                ["transaction_id"] = new { type = "string", description = "Transaction id returned by preview. Required for apply/rollback of a previewed multi-operation transaction." },
                ["tool"] = new { type = "string", description = "Single-operation tool name such as component or gameobject." },
                ["operation_action"] = new { type = "string", description = "Single-operation action name. Used instead of action to avoid conflicting with the lifecycle action." },
                ["arguments"] = new { type = "object", description = "Single-operation parameters as an object.", additionalProperties = true },
                ["continue_on_error"] = new { type = "boolean", description = "For transaction apply: continue after a failed operation. Default false." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    internal static readonly object Transaction = new
    {
        name = "transaction",
        description = "Preview, apply, and conditionally rollback a sequence of write operations. Use this when an AI agent wants to make bolder scene or asset edits under one exclusive editor operation. Preview records targets and editor revision, apply stops on first failed mutating operation unless continue_on_error=true, and rollback refuses if another write advanced the editor revision after apply. editor.undo remains available but is global and unsafe across agents.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "Transaction operation.", @enum = new[] { "preview", "apply", "rollback" } },
                ["operations"] = new { type = "string", description = "JSON array of batched write operations. Required for: preview." },
                ["transaction_id"] = new { type = "string", description = "Transaction id. Required for: apply, rollback." },
                ["continue_on_error"] = new { type = "boolean", description = "For apply: continue after a failed operation. Default false, so apply stops on first error." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    internal static readonly object Autofix = new
    {
        name = "autofix",
        description = "Apply safe, targeted repairs for common prefab and spawnable-entity issues. Use 'prefab_fixups' to normalize network mode, clear broken dev models, and add missing Rigidbody/Collider defaults. Use 'create_sent_pair' to generate a sibling .sent file for a spawnable prefab.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "Autofix operation.", @enum = new[] { "prefab_fixups", "create_sent_pair" } },
                ["path"] = new { type = "string", description = "Prefab path. Required for: prefab_fixups." },
                ["prefab_path"] = new { type = "string", description = "Prefab path. Required for: create_sent_pair." },
                ["title"] = new { type = "string", description = "Spawn menu title. Optional for: create_sent_pair." },
                ["description"] = new { type = "string", description = "Spawn menu description. Optional for: create_sent_pair." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    internal static readonly object CrossIndex = new
    {
        name = "cross_index",
        description = "Generate and query a repo-to-engine cross index that links code symbols, assets, .sent files, console commands, docs, examples, and live scene objects. Use 'build' to refresh the artifact on disk, 'stats' for a quick summary, and 'query' to search across the unified index. This gives AI agents a single searchable knowledge graph instead of split repo/runtime lookups.",
        annotations = new { readOnlyHint = true },
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "Cross-index operation.", @enum = new[] { "build", "stats", "query" } },
                ["query"] = new { type = "string", description = "Search text. Required for: query." },
                ["type"] = new { type = "string", description = "Optional index domain filter. Used by: query. Values: code, asset, sent, console, doc, example, scene." },
                ["include_scene"] = new { type = "boolean", description = "Include live scene objects while building. Used by: build. Default: true." },
                ["limit"] = new { type = "integer", description = "Max results. Used by: query. Default: 25." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 31: search ──────────────────────────────────────────────

    internal static readonly object Search = new
    {
        name = "search",
        description = "Advanced scene search beyond name/tag matching. Use 'by_pattern' for regex matching on object names or hierarchy paths. Use 'by_property_value' to find all objects where a specific component property equals, contains, or compares to a value (e.g. find all ModelRenderers where Model contains 'citizen'). Use 'get_neighborhood' to understand spatial context around an object (what's nearby with distances and directions). Use 'get_related' to discover reference relationships (which objects point to this one, and vice versa). All actions are read-only.",
        annotations = new { readOnlyHint = true },
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "by_pattern", "by_property_value", "get_neighborhood", "get_related" } },
                ["id"] = new { type = "string", description = "GameObject GUID. Required for: get_related. Used by: get_neighborhood (alternative to position)." },
                ["pattern"] = new { type = "string", description = "Regex pattern. Required for: by_pattern." },
                ["match_path"] = new { type = "boolean", description = "Match against full hierarchy path instead of just name. Used by: by_pattern. Default: false." },
                ["component_type"] = new { type = "string", description = "Component type name to search. Required for: by_property_value." },
                ["property"] = new { type = "string", description = "Property name to compare. Required for: by_property_value." },
                ["value"] = new { type = "string", description = "Value to compare against. Required for: by_property_value." },
                ["operator"] = new { type = "string", description = "Comparison operator.", @enum = new[] { "eq", "gt", "lt", "gte", "lte", "contains", "starts_with" } },
                ["position"] = new { type = "string", description = "Center position as 'x,y,z'. Used by: get_neighborhood (alternative to id)." },
                ["radius"] = new { type = "number", description = "Search radius. Used by: get_neighborhood. Default: 500." },
                ["offset"] = new { type = "integer", description = "Pagination offset. Default: 0." },
                ["limit"] = new { type = "integer", description = "Max results. Default: 50." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 32: snapshot ────────────────────────────────────────────

    internal static readonly object Snapshot = new
    {
        name = "snapshot",
        description = "Take, compare, and restore scene state snapshots. Use 'take' to serialize the current scene state to in-memory storage with a name. Use 'diff' to compare a snapshot against the current scene (or two snapshots) — shows added, removed, and modified objects with property-level changes. Use 'restore' to revert transforms, properties, and enabled state on existing objects (cannot recreate destroyed objects). Use 'list' to see stored snapshots and 'delete' to free memory. Max 10 snapshots stored.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "take", "list", "diff", "restore", "delete" } },
                ["name"] = new { type = "string", description = "Snapshot name. Required for: take, delete. Used by: diff (compare vs current), restore." },
                ["name_a"] = new { type = "string", description = "First snapshot name for two-snapshot comparison. Used by: diff." },
                ["name_b"] = new { type = "string", description = "Second snapshot name for two-snapshot comparison. Used by: diff." },
                ["scope_id"] = new { type = "string", description = "GameObject GUID to limit snapshot to a subtree. Used by: take." },
                ["include_disabled"] = new { type = "boolean", description = "Include disabled objects in snapshot. Used by: take. Default: true." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 33: validate ────────────────────────────────────────────

    internal static readonly object Validate = new
    {
        name = "validate",
        description = "Run diagnostics on scene objects, components, prefabs, and references to find problems before running the game. Use 'scene' for a full scan (null models, colliders without rigidbodies, duplicate components, multiple active cameras). Use 'component' to validate specific component properties (null references, broken asset paths). Use 'prefab' for prefab integrity checks (broken script references). Use 'references' to scan the entire scene for broken GameObject/Component/Model references. All actions are read-only. This is a general scene-health validator — for SBEP-specific validation use 'sbep_verify'.",
        annotations = new { readOnlyHint = true },
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "scene", "component", "prefab", "references" } },
                ["id"] = new { type = "string", description = "GameObject GUID. Required for: component." },
                ["component_id"] = new { type = "string", description = "Component GUID (validates all if omitted). Used by: component." },
                ["path"] = new { type = "string", description = "Prefab file path. Required for: prefab." },
                ["severity_filter"] = new { type = "string", description = "Filter results by severity.", @enum = new[] { "error", "warning", "info" } }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 34: continue ────────────────────────────────────────────

    internal static readonly object Continue = new
    {
        name = "continue",
        description = "Retrieve the next page of a large result that was automatically paginated. When any tool response includes a '_cursor' block with 'has_more: true', use this tool with the cursor_id to get the next page. Cursors expire after 60 seconds — if expired, re-run the original query. Do not call this tool unless you see a _cursor block in a previous response.",
        annotations = new { readOnlyHint = true },
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "next" } },
                ["cursor_id"] = new { type = "string", description = "Cursor ID from a previous response's _cursor block. Required." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 35: blueprint ───────────────────────────────────────────

    internal static readonly object Blueprint = new
    {
        name = "blueprint",
        description = "Create complex object hierarchies from a single declarative JSON spec. Instead of multiple sequential gameobject.create + component.add + component.set_property calls, describe the full object tree in one 'spec' parameter. Supports nested children, multiple components with properties, tags, and transforms. Use 'validate' to check a spec before creating. On failure, all partially-created objects are rolled back. The 'spec' format: {\"name\": \"...\", \"tags\": [\"...\"], \"position\": \"x,y,z\", \"components\": [{\"type\": \"ModelRenderer\", \"properties\": {\"Model\": \"path.vmdl\"}}], \"children\": [...]}",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "create", "validate" } },
                ["spec"] = new { type = "string", description = "JSON blueprint specification. Required for: create, validate. Describes a hierarchy of objects with components and properties." },
                ["parent_id"] = new { type = "string", description = "Parent GameObject GUID to create under. Used by: create. Default: scene root." },
                ["position"] = new { type = "string", description = "Override root position as 'x,y,z'. Used by: create." },
                ["rotation"] = new { type = "string", description = "Override root rotation as 'pitch,yaw,roll'. Used by: create." },
                ["dry_run"] = new { type = "boolean", description = "Validate spec without creating objects. Used by: create. Default: false." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 36: animation ───────────────────────────────────────────

    internal static readonly object Animation = new
    {
        name = "animation",
        description = "Configure animation parameters on SkinnedModelRenderer and CitizenAnimationHelper components. Use 'list_parameters' to discover available animation parameters on a model (movement speed, duck level, look direction, etc.). Use 'set_parameters' to configure animation values — tries CitizenAnimationHelper first, then SkinnedModelRenderer properties, then AnimGraph parameters via Set(). Use 'get_model_animations' to list animation clips/sequences on a model. Requires a SkinnedModelRenderer on the target GameObject.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "list_parameters", "set_parameters", "get_model_animations" } },
                ["id"] = new { type = "string", description = "GameObject GUID with SkinnedModelRenderer. Required for: list_parameters, set_parameters. Used by: get_model_animations." },
                ["parameters"] = new { type = "string", description = "JSON object of parameter name/value pairs. Required for: set_parameters. Example: '{\"MoveSpeed\": 100, \"DuckLevel\": 0.5}'" },
                ["model_path"] = new { type = "string", description = "Model asset path (.vmdl). Used by: get_model_animations (alternative to id)." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 37: cockpit_verify ──────────────────────────────────────

    internal static readonly object CockpitVerify = new
    {
        name = "cockpit_verify",
        description = "Run the cockpit beauty verifier as a single orchestrated MCP call. Use action='run' to optionally restart play mode, ask the game-side cockpit diagnostic to select the runtime scene, invoke the cockpit beauty runner, poll FileSystem.Data artifacts, and return packet/rubric/AAA summary fields. Responses include handler_version and contract_version so wrappers can detect stale hotloaded MCP code. This tool is purpose-built to avoid long manual play/stop/log/artifact loops; it does not rebuild Blender assets.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "run" } },
                ["label"] = new { type = "string", description = "Run label used for FileSystem.Data/cockpit-beauty/<label>. Default: baseline." },
                ["restart_play"] = new { type = "boolean", description = "Stop and start play before verifying. Default: true." },
                ["timeout_seconds"] = new { type = "integer", description = "Maximum time to wait for readiness and artifacts. Default: 75." },
                ["data_root"] = new { type = "string", description = "Absolute cockpit-beauty data root. Optional; default resolves sibling S&Box-Pub game data." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };
}
