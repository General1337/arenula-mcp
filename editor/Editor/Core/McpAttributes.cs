using System;

namespace Arenula;

/// <summary>
/// Threading model for an MCP action handler. Determines where the dispatcher
/// awaits MainThread relative to handler invocation.
///
/// <list type="bullet">
///   <item><c>MainThread</c> — sync handler; dispatcher awaits MainThread BEFORE
///     invoking. Handler must be <c>static object Handle(JsonElement args)</c>.</item>
///   <item><c>AsyncBeforeMainThread</c> — async handler that internally awaits
///     MainThread when it needs scene access; dispatcher invokes WITHOUT awaiting
///     MainThread first. Handler must be
///     <c>static Task&lt;object&gt; HandleAsync(JsonElement args)</c>.</item>
///   <item><c>AnyThread</c> — async handler that never touches the main thread;
///     dispatcher invokes WITHOUT awaiting MainThread. Handler must be
///     <c>static Task&lt;object&gt; HandleAsync(JsonElement args)</c>.</item>
/// </list>
/// </summary>
public enum McpThreading
{
	MainThread,
	AsyncBeforeMainThread,
	AnyThread,
}

/// <summary>
/// Marks a class as an omnibus MCP tool. The class hosts one or more
/// <see cref="McpActionAttribute"/>-decorated static handler methods plus an
/// <c>internal static object InputSchema()</c> method that composes the tool's
/// JSON inputSchema fragment.
///
/// Discovered at plugin load via <see cref="McpToolScanner.Scan(System.Reflection.Assembly)"/>.
/// Replaces the legacy triple-touch pattern (ToolRegistry.All array + ActionMetadata
/// dictionary + RpcDispatcher.DispatchToolCall switch case) that <c>mcp-dynamic-discovery</c>
/// initiative is unwinding.
/// </summary>
[AttributeUsage( AttributeTargets.Class, AllowMultiple = false, Inherited = false )]
public sealed class McpToolAttribute : Attribute
{
	/// <summary>Tool name as it appears in <c>tools/list</c> (e.g. <c>"scene"</c>, <c>"mcp.health"</c>).</summary>
	public string Name { get; }

	/// <summary>
	/// Human-readable description for the LLM client. Per
	/// charter §"Description length contract" the migration must NOT regress any
	/// existing description below 60 words — paste verbatim from the legacy
	/// <c>ToolRegistry.cs</c> literal during Phase 3 migration.
	/// </summary>
	public string Description { get; }

	/// <summary>
	/// When true, the dispatcher advertises <c>annotations.readOnlyHint = true</c>
	/// in the <c>tools/list</c> payload. Per-tool flag — independent of per-action
	/// <see cref="McpActionAttribute.ReadOnly"/> which feeds operation metadata.
	/// </summary>
	public bool ReadOnlyHint { get; init; } = false;

	/// <summary>
	/// Emit <c>annotations.readOnlyHint</c> even when its value is false. MCP
	/// annotations are optional, so absence and an explicit false value are
	/// semantically close but not byte-identical. Migration adapters use this bit
	/// to preserve the exact pre-migration tools/list contract.
	/// </summary>
	public bool IncludeReadOnlyHint { get; init; } = false;

	public McpToolAttribute( string name, string description )
	{
		Name = name ?? throw new ArgumentNullException( nameof( name ) );
		Description = description ?? throw new ArgumentNullException( nameof( description ) );
	}
}

/// <summary>
/// Marks a static method as a handler for one MCP action on the enclosing
/// <see cref="McpToolAttribute"/>-decorated class. The dispatcher reflection-invokes
/// the method based on the <c>action</c> field of the incoming tool call.
///
/// Signature contract (validated at scan time, see
/// <see cref="McpToolScanner.Scan(System.Reflection.Assembly)"/>):
/// <list type="bullet">
///   <item><c>Threading == MainThread</c> requires
///     <c>static object Handle*(JsonElement args)</c> (sync return).</item>
///   <item><c>Threading == AsyncBeforeMainThread</c> or <c>AnyThread</c> requires
///     <c>static Task&lt;object&gt; Handle*Async(JsonElement args)</c>.</item>
///   <item>Either form may take an optional second parameter
///     <c>McpSession session</c> — the dispatcher threads the live session in
///     when present (for actions that dispatch sub-calls needing session
///     identity, e.g. <c>batch</c>). Added Wave 3.1 of mcp-dynamic-discovery.</item>
/// </list>
/// </summary>
[AttributeUsage( AttributeTargets.Method, AllowMultiple = false, Inherited = false )]
public sealed class McpActionAttribute : Attribute
{
	/// <summary>Action name as the client passes it via the <c>action</c> arg (e.g. <c>"summary"</c>, <c>"create"</c>).</summary>
	public string Action { get; }

	/// <summary>Operation-metadata flag; mirrors <c>ToolRegistry.ToolActionMetadata.ReadOnly</c>.</summary>
	public bool ReadOnly { get; init; } = false;

	/// <summary>
	/// Operation-metadata flag; mirrors <c>ToolRegistry.ToolActionMetadata.WriteKind</c>.
	/// Valid values: <c>"none"</c>, <c>"scene_write"</c>, <c>"file_write"</c>.
	/// </summary>
	public string WriteKind { get; init; } = "none";

	/// <summary>
	/// Operation-metadata flag; mirrors <c>ToolRegistry.ToolActionMetadata.LifecycleKind</c>.
	/// Valid values: <c>"none"</c>, <c>"lifecycle"</c>.
	/// </summary>
	public string LifecycleKind { get; init; } = "none";

	/// <summary>
	/// Optional per-call lock-key extraction strategy. Non-<c>None</c> values
	/// require <see cref="WriteKind"/> to be <c>"scene_write_per_go"</c> so a
	/// scanned action can express the complete <c>ToolActionMetadata</c> contract
	/// without a second hand-maintained registration.
	/// </summary>
	public LockKeyStrategy LockKey { get; init; } = LockKeyStrategy.None;

	/// <summary>
	/// Threading dispatch hint. Replaces the hand-curated switch in
	/// <c>RpcDispatcher.DispatchToolCall</c>.
	/// </summary>
	public McpThreading Threading { get; init; } = McpThreading.MainThread;

	/// <summary>
	/// Optional path to a JSON sidecar containing an inputSchema fragment for this
	/// action. Reserved for Phase 1.3 pattern (1) — JSON sidecar files. Phase 1 lands
	/// pattern (2) — <c>InputSchema()</c> static composition — so this is unused on
	/// the read path. Kept on the attribute for forward compatibility.
	/// </summary>
	public string ParamsSchemaSource { get; init; } = null;

	public McpActionAttribute( string action )
	{
		Action = action ?? throw new ArgumentNullException( nameof( action ) );
	}
}
