# Arenula

MCP server suite for the s&box game engine. Connects AI coding assistants to the s&box editor for scene inspection, editor-safe writes, compilation, assets, playtests, API lookup, and documentation.

## Servers

| Server | What it does | Runtime | Transport |
|--------|-------------|---------|-----------|
| **Editor** | Scene manipulation, compilation, assets, terrain, playtests, agent safety | C# plugin inside s&box | Streamable HTTP http://localhost:8098/mcp |
| **API** | Offline type/member reference and package/texture lookup | Node.js | stdio |
| **Docs** | Narrative docs from sbox.game | Node.js | stdio |

## Setup

Copy ditor/ into your s&box project's Libraries/arenula_mcp/ folder, then build the Node servers with 
pm run setup.

Configure the editor server with /mcp:

`json
{
  "mcpServers": {
    "editor": {
      "type": "http",
      "url": "http://localhost:8098/mcp"
    }
  }
}
`

Legacy /sse and /message remain for older clients for one compatibility window.

## Agent Quickstart

1. Start with mcp.capabilities for the live tool inventory and routing hints.
2. For code edits, use pipeline.compile_and_verify; for mixed changes, use erify_change_set.
3. For scene writes, use operation.preview/operation.apply or 	ransaction.preview/	ransaction.apply.
4. For runtime assertions, use playtest.
5. For unknown APIs or property shapes, query eflection and schema before writing.

## Editor Tools

Generated from ditor/Editor/Core/ToolRegistry.cs.

Tool count: **43**. Action count: **233**.

| Tool | Actions | Count |
|------|---------|-------|
| `mcp.health` | (no action parameter) | 0 |
| `mcp.capabilities` | (no action parameter) | 0 |
| `mcp.server_log` | (no action parameter) | 0 |
| `scene` | summary, hierarchy, statistics, find, find_in_radius, get_details, prefab_instances | 7 |
| `gameobject` | create, destroy, destroy_by_name, destroy_children, duplicate, reparent, rename, enable, set_tags, set_transform, batch_transform | 11 |
| `component` | add, remove, set_property, set_enabled, get_properties, get_types, copy, bulk_set, apply_properties | 9 |
| `prefab` | instantiate, get_structure, get_instances, break, update, create, save_overrides, revert, get_overrides | 9 |
| `asset_query` | browse, search, open, get_dependencies, get_model_info, get_material_properties, get_mesh_info, get_bounds, get_unsaved, get_status, get_json, get_references | 12 |
| `asset_manage` | create, delete, rename, move, save, reload, get_references | 7 |
| `editor` | select, get_selected, set_selected, clear_selection, frame_selection, get_play_state, start_play, stop_play, get_log, save_scene, save_scene_as, undo, redo, console_list, console_run, open_code_file, get_preferences, set_preference, restart_mcp, reload_scene | 20 |
| `compile` | trigger, status, errors, generate_solution, wait, smart_trigger, fix_context, snapshot_hotreload, verify_hotreload | 9 |
| `mesh` | create_block, create_clutter, create_plane, create_cylinder, create_wedge, create_arch, extrude_faces, remove_faces, add_face, clip_faces, scale_mesh, thicken_faces, bevel_edges, bevel_vertices, split_edges, quad_slice_faces, dissolve_edges, bridge_edges, connect_vertices, flip_faces, extend_edges, set_face_material, set_texture_params, set_vertex_position, set_vertex_color, set_vertex_blend, get_info | 27 |
| `lighting` | create, configure, create_skybox, set_skybox | 4 |
| `physics` | add_collider, configure_collider, add_rigidbody, create_model_physics, create_character_controller, create_joint | 6 |
| `audio` | create, configure | 2 |
| `effects` | create, configure_particle, configure_post_processing | 3 |
| `camera` | create, configure, capture_viewport, capture_tour, orbit_capture | 5 |
| `navmesh` | create_agent, create_area, create_link, generate, get_status, query_path | 6 |
| `session` | list, set_active, load_scene | 3 |
| `project` | get_collision, set_collision_rule, get_input, get_info | 4 |
| `cloud` | search, get_package, get_versions, mount | 4 |
| `terrain` | create, configure, get_info, get_height, get_height_region, set_height, noise, erode, stamp, add_material, remove_material, get_material_at, blend_materials, set_hole, paint_material, import_heightmap, export_heightmap, sync | 18 |
| `code` | read_file, write_file, list_files, search_code | 4 |
| `trace` | ray, box, ground | 3 |
| `batch` | execute | 1 |
| `reflection` | search, get_type, get_members, list_assemblies | 4 |
| `sbep_verify` | prefab_check, component_check, smoke_test, resource_health, state_snapshot, full_verify | 6 |
| `pipeline` | compile_and_verify, probe_typelibrary | 2 |
| `workflow` | list, create_prefab_pair, fix_compile_errors, verify_networked_component, implement_spawnable_entity, add_tool_mode | 6 |
| `verify_change_set` | plan, run | 2 |
| `playtest` | boot_scene, run_assertions | 2 |
| `schema` | components, component, property, resources, enum | 5 |
| `operation` | validate, preview, apply, rollback | 4 |
| `transaction` | preview, apply, rollback | 3 |
| `autofix` | prefab_fixups, create_sent_pair | 2 |
| `cross_index` | build, stats, query | 3 |
| `search` | by_pattern, by_property_value, get_neighborhood, get_related | 4 |
| `snapshot` | take, list, diff, restore, delete | 5 |
| `validate` | scene, component, prefab, references | 4 |
| `continue` | next | 1 |
| `blueprint` | create, validate | 2 |
| `animation` | list_parameters, set_parameters, get_model_animations | 3 |
| `cockpit_verify` | run | 1 |

## License

GPL-3.0 for the editor plugin; see package-level license files for Node servers.