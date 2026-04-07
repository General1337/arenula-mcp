# Arenula MCP

MCP server suite for the s&box game engine. Three servers: editor (C#), api (TypeScript), docs (TypeScript).

## Build

```bash
npm run setup          # install deps + build both TS servers
npm run build          # build only (no install)
```

Editor plugin compiles automatically inside s&box — copy `editor/` into your project's `Libraries/arenula_mcp/`.

## Project Layout

```
editor/Editor/Core/       C# infrastructure (transport, dispatch, helpers)
editor/Editor/Handlers/   C# tool handlers (one file per tool, plus ConsoleHandler)
api/src/                  TypeScript API reference server
docs/src/                 TypeScript docs fetcher server
test/                     MCP test harness (not in CI — requires running s&box editor)
```

## Architecture

- **ToolRegistry.cs** — all 19 tool schemas (name, description, inputSchema)
- **RpcDispatcher.cs** — routes `tools/call` to handler by tool name
- **HandlerBase.cs** — shared response helpers (Text, Success, Error, Image, param extraction)
- **SceneHelpers.cs** — scene traversal, object lookup, selection management
- Each handler is a `static class` with `Handle(string action, JsonElement args)` returning an object

### Threading

- Most tools dispatch on main thread via `GameTask.MainThread()`
- `compile` and `cloud` are async (bypass main thread)
- `editor.console_run/console_list` dispatch on a separate path for exception isolation

## Code Style

### C# (editor/)
- Allman braces, spaces around operators
- s&box conventions: `Log.Info`, `GameTask`, `ResourceLibrary`
- Static handler classes, private static action methods
- Response objects via `HandlerBase.Success()` / `HandlerBase.Error(message, action, suggestion)`
- Param extraction via `HandlerBase.GetString/GetInt/GetFloat/GetBool`
- Positions as `"x,y,z"` strings parsed by `HandlerBase.ParseVector3`

### TypeScript (api/, docs/)
- Strict mode, ESM modules
- No semicolons
- Zod for input validation

## Adding a New Editor Tool

1. Create `Editor/Handlers/FooHandler.cs` with `static object Handle(string action, JsonElement args)`
2. Add the tool schema to `ToolRegistry.cs` (follow the omnibus pattern: required action enum + flat params)
3. Add routing in `RpcDispatcher.cs` switch statement
4. Update the action count in `README.md`

## Common Patterns

- Terrain coordinate conversion: world pos -> local UV (0-1) -> texel index
- All list endpoints support offset/limit pagination via `HandlerBase.Paginate`
- Object lookup: `SceneHelpers.FindByIdOrThrow(scene, id, action)`
- Scene resolution: `SceneHelpers.ResolveScene()` tries editor sessions first, then Game.ActiveScene
