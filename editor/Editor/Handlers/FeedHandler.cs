// Editor/Handlers/FeedHandler.cs
//
// [HANDOFF-TRUST: greenfield Wave 3.1 of mcp-agent-capability-system — sibling to Wave 1 + Wave 2 SHIPPED 2026-05-15 (6/6 features live). NOT a continuation of any thruster-vfx wave3 or cloud-wave3 handoff; zero file overlap with Code/SBEP/Vfx/** or Assets/shaders/**. Bare 'wave3' substring collision does NOT inherit any hypothesis.]
// [HANDOFF-TRUST: closeout reflection-bridge refactor is independent of predecessor H1 raise-site hypothesis; this edit moves direct-type calls to reflection helper (HotloadResultBridge), engine fork commits ed7b424d / 764a599b unchanged. H1 already CONFIRMED in predecessor session.]
//
// MCP handler for the 'feed' tool namespace. v1 ships four channels:
//   feed.log              — engine log stream (Log.Info/Warning/Error etc.)
//   feed.hotload          — [EditorEvent.Hotload] events
//   feed.concmd_registry  — ConCmd IL-hash divergence detection (the stale-IL self-detect signal)
//   feed.async_job        — plugin-side emit helper for long-running jobs
//
// v1 STUB CHANNELS (registered but inactive — return {available: false, reason}):
//   feed.compile / play_state / convar_changed / scene_dirty / entity_lifecycle
//   These ship in a v1.1 follow-up once the v1 architecture proves out.
//
// Actions:
//   feed.subscribe channel filter? — idempotent; subscribes if not already; returns seq baseline
//   feed.poll      channel since_seq=null — drains pending events since seq; returns next_seq + dropped_count
//   feed.unsubscribe channel — tears down subscription; subsequent poll returns error
//   feed.channels — describe all channels + per-channel state
//
// Design notes:
// - State is process-scoped in a ConcurrentDictionary<string, ChannelState>.
//   Per-channel ring buffer (default 1024, configurable via ConVar
//   mcp_feed_ring_size). Drop-oldest on overflow; dropped_count tracked.
// - Hot-reload behaviour: FeedHandler's static state resets when
//   arenula_mcp.editor.dll hot-reloads. Subscribers see channels reset
//   to unsubscribed. This matches EditorHandler.cs:35-130 behaviour for
//   the existing log ring. Documented v1 limitation in mcp-guide.md.
// - Log channel: defensively RemoveLogger first, then AddLogger — same
//   idempotent pattern EditorHandler.cs:63-64 and AssetQueryHandler.cs:103-104
//   use. Multiple AddLogger subscriptions to the same source are supported
//   (existing two subscriptions prove it).
// - [EditorEvent.Hotload] handlers in this file MUST swallow all exceptions
//   per ArenulaMcpServer.cs:158-162 contract — an unhandled throw breaks
//   the dispatcher for every other [EditorEvent.Hotload] subscriber on
//   subsequent hotloads (silent editor-wide breakage, extremely hard to diagnose).

// [HANDOFF-TRUST: closeout reflection-bridge refactor — orthogonal to predecessor H1]
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Arenula.Reflection;
using Editor;
using Sandbox;

namespace Arenula;

internal static class FeedHandler
{
	// [HANDOFF-TRUST: Phase 2.3 wrapper on engine-fork-elite-leverage; no live H1 hypothesis from any inherited handoff applies — this session is a verbatim copy of the Phase 2.1 consumer/producer split shipped today, with the four phase2-1 iteration bugs inherited as BANNED APPROACHES, not falsifiable hypotheses]
	// Channel name constants — keep in one place so subscribe/poll/channels share lookups.
	private const string ChannelLog = "log";
	private const string ChannelHotload = "hotload";
	private const string ChannelConcmdRegistry = "concmd_registry";
	private const string ChannelAsyncJob = "async_job";
	private const string ChannelCompile = "compile";
	private const string ChannelScene = "scene";
	// [HANDOFF-TRUST: Phase 2.4 wrapper additive constant — closes charter Phase 2 exit criterion; no live hypothesis from any inherited handoff applies, this mirrors ChannelCompile (Phase 2.1) + ChannelScene (Phase 2.3) byte-for-byte]
	private const string ChannelAssets = "assets";
	// v1 stubs
	private const string ChannelPlayState = "play_state";
	private const string ChannelConvarChanged = "convar_changed";
	private const string ChannelSceneDirty = "scene_dirty";
	private const string ChannelEntityLifecycle = "entity_lifecycle";

	private static readonly string[] AllChannels = new[]
	{
		ChannelLog, ChannelHotload, ChannelConcmdRegistry, ChannelAsyncJob,
		ChannelCompile, ChannelScene, ChannelAssets, ChannelPlayState, ChannelConvarChanged,
		ChannelSceneDirty, ChannelEntityLifecycle,
	};

	// [HANDOFF-TRUST: greenfield Phase 2.1 wrapper on engine-fork-elite-leverage initiative — no inherited handoff hypothesis applies; this flips an already-designed stub (deferred-Phase-2-hook comment at CompileHandler.cs:1030-1032) into a consumer/producer split mirroring EmitAsyncJob byte-for-byte.]
	// [HANDOFF-TRUST: Phase 2.3 ActiveChannels promotion — verbatim parallel of the Phase 2.1 ChannelCompile entry directly above; mechanical wrapper landing, no live hypothesis from any inherited handoff applies]
	private static readonly HashSet<string> ActiveChannels = new( StringComparer.OrdinalIgnoreCase )
	{
		ChannelLog, ChannelHotload, ChannelConcmdRegistry, ChannelAsyncJob,
		// Phase 2.1 (engine-fork-elite-leverage initiative): feed.compile is
		// the wire-format consumer for CompileHandler's fork-only
		// CompileGroup.OnBuildCompleted (Phase 1.1) + OnCompileFailed (Phase 1.2)
		// subscriptions. Producer = CompileHandler.cs (owns the engine reflection
		// seam). Consumer = FeedHandler.EmitCompile (this file, below). Same
		// consumer/producer split as ChannelAsyncJob.
		ChannelCompile,
		// Phase 2.3 (engine-fork-elite-leverage initiative): feed.scene is the
		// wire-format consumer for SceneMutationHandler's fork-only Scene event
		// subscriptions (OnGameObjectAdded / OnGameObjectRemoved /
		// OnComponentRegistered / OnComponentUnregistered — all bound at
		// Phase 1.5). Producer = SceneMutationHandler.cs (owns the engine
		// reflection seam). Consumer = FeedHandler.EmitScene (this file, below).
		// Same consumer/producer split as ChannelAsyncJob + ChannelCompile.
		// NOTE: distinct from ChannelSceneDirty (a v1.1-deferred stub for
		// dirty-scene notifications); ChannelScene carries the 4 mutation events.
		ChannelScene,
		// [HANDOFF-TRUST: Phase 2.4 ActiveChannels promotion — verbatim parallel of the ChannelScene entry directly above; closes the Phase 2 exit criterion (four hand-rolled log-scrape paths replaced); no live hypothesis from any inherited handoff applies]
		// Phase 2.4 (engine-fork-elite-leverage initiative): feed.assets is the
		// wire-format consumer for AssetSystemHandler's fork-only AssetSystem event
		// subscription (OnAssetCompileFailed — bound at Phase 2.4 / 1.6).
		// Producer = AssetSystemHandler.cs (owns the engine reflection seam).
		// Consumer = FeedHandler.EmitAssets (this file, below). Same
		// consumer/producer split as ChannelAsyncJob + ChannelCompile + ChannelScene.
		// Replaces the editor.get_log filter:"compile" log-scrape workaround.
		ChannelAssets,
	};

	[ConVar( "mcp_feed_ring_size", ConVarFlags.Saved, Help = "Max events retained per feed channel ring buffer (default 1024)." )]
	public static int RingSize { get; set; } = 1024;

	// ── State ─────────────────────────────────────────────────────────────

	internal sealed class FeedEvent
	{
		public long Seq { get; init; }
		public DateTime Time { get; init; }
		public string Channel { get; init; }
		public string Type { get; init; }
		public object Payload { get; init; }
		/// <summary>
		/// Best-effort attribution: the agent whose action caused this event, or
		/// null when unknown/ambiguous (global events). Null ALWAYS passes the
		/// feed.poll agent_id filter (mcp-multiagent-verdict-integrity #4).
		/// </summary>
		public string AgentId { get; init; }
	}

	private sealed class ChannelState
	{
		public string Name { get; init; }
		public bool Subscribed;
		public long NextSeq;
		public int DroppedCount;
		public string Filter;
		public DateTime SubscribedAt;
		// Ring buffer — Queue with explicit trim on enqueue.
		public readonly Queue<FeedEvent> Buffer = new();
		// Optional teardown action set by channel-specific subscribe path.
		public Action OnUnsubscribe;
		public readonly object Gate = new();
	}

	private static readonly ConcurrentDictionary<string, ChannelState> _channels = new( StringComparer.OrdinalIgnoreCase );

	// ConCmd IL-hash cache for concmd_registry channel. Populated on first
	// subscribe; recomputed on [EditorEvent.Hotload] firings + on every poll.
	private static readonly ConcurrentDictionary<string, string> _concmdIlHash = new( StringComparer.Ordinal );

	// Defensive log-callback handle so we can RemoveLogger on unsubscribe.
	private static Action<LogEvent> _logCallback;

	// ── Public entrypoint ────────────────────────────────────────────────

	internal static object Handle( string action, JsonElement args, McpSession session = null )
	{
		try
		{
			return action switch
			{
				"subscribe"   => Subscribe( args ),
				"poll"        => Poll( args, session ),
				"unsubscribe" => Unsubscribe( args ),
				"channels"    => Channels( args ),
				_ => HandlerBase.Error( $"Unknown action '{action}'", action,
					"Valid actions: subscribe, poll, unsubscribe, channels. v1 active channels: log, hotload, concmd_registry, async_job. Stubs (registered but inactive): compile, play_state, convar_changed, scene_dirty, entity_lifecycle." )
			};
		}
		catch ( Exception ex )
		{
			return HandlerBase.Error( ex.Message, action );
		}
	}

	// ── subscribe ─────────────────────────────────────────────────────────

	private static object Subscribe( JsonElement args )
	{
		var channel = HandlerBase.GetString( args, "channel" );
		if ( string.IsNullOrEmpty( channel ) )
			return HandlerBase.Error( "Missing required 'channel' parameter.", "subscribe",
				$"Available channels: {string.Join( ", ", AllChannels )}" );

		if ( !AllChannels.Contains( channel, StringComparer.OrdinalIgnoreCase ) )
			return HandlerBase.Error( $"Unknown channel '{channel}'.", "subscribe",
				$"Available channels: {string.Join( ", ", AllChannels )}" );

		if ( !ActiveChannels.Contains( channel ) )
		{
			return HandlerBase.Success( new
			{
				ok = false,
				channel,
				available = false,
				reason = "v1 stub — channel is registered but inactive; ships in v1.1 follow-up to Wave 3.1"
			} );
		}

		var filter = HandlerBase.GetString( args, "filter" );
		var state = _channels.GetOrAdd( channel, name => new ChannelState { Name = name } );

		bool wasSubscribed;
		lock ( state.Gate )
		{
			wasSubscribed = state.Subscribed;
			state.Subscribed = true;
			state.Filter = filter;
			if ( !wasSubscribed ) state.SubscribedAt = DateTime.UtcNow;
		}

		// Idempotent: re-subscribing returns ok=true without re-attaching the
		// engine source (avoids double-delivery on log channel).
		if ( !wasSubscribed )
		{
			try { AttachChannelSource( state ); }
			catch ( Exception ex )
			{
				return HandlerBase.Error( $"Failed to attach source for '{channel}': {ex.Message}", "subscribe" );
			}
		}

		return HandlerBase.Success( new
		{
			ok = true,
			channel,
			subscribed = true,
			already_subscribed = wasSubscribed,
			next_seq = state.NextSeq,
			subscribed_at = state.SubscribedAt.ToString( "yyyy-MM-ddTHH:mm:ss.fffZ" ),
			filter
		} );
	}

	// ── poll ──────────────────────────────────────────────────────────────

	private static object Poll( JsonElement args, McpSession session = null )
	{
		var channel = HandlerBase.GetString( args, "channel" );
		if ( string.IsNullOrEmpty( channel ) )
			return HandlerBase.Error( "Missing required 'channel' parameter.", "poll" );

		if ( !_channels.TryGetValue( channel, out var state ) || !state.Subscribed )
		{
			return HandlerBase.Error( $"Not subscribed to '{channel}'.", "poll",
				$"Call feed.subscribe channel={channel} first." );
		}

		// For concmd_registry: recompute on every poll as a backstop in case
		// [EditorEvent.Hotload] missed a compile (Sandbox.Hotload counter is
		// unreliable for MCP-triggered compiles per CompileHandler.cs:643/686 +
		// PipelineHandler.cs:235).
		if ( string.Equals( channel, ChannelConcmdRegistry, StringComparison.OrdinalIgnoreCase ) )
		{
			TryRecomputeConcmdRegistry();
		}

		long sinceSeq = HandlerBase.GetInt( args, "since_seq", -1 );
		var limit = HandlerBase.GetInt( args, "limit", 100 );
		if ( limit <= 0 ) limit = 100;
		if ( limit > 1000 ) limit = 1000;

		// Per-agent filter (mcp-multiagent-verdict-integrity, findings.md #4):
		// when the caller names (or is identified as) an agent, drop events
		// attributed to OTHER agents server-side. Unattributed events (AgentId
		// null — global engine activity, ambiguous attribution) always pass, so
		// nothing that might be the caller's is ever hidden.
		var (filterAgentId, _) = AgentIdentityRegistry.ResolveCaller( args, session );

		FeedEvent[] page;
		long nextSeq;
		int dropped;
		int bufferCount;
		int foreignFiltered = 0;
		lock ( state.Gate )
		{
			nextSeq = state.NextSeq;
			dropped = state.DroppedCount;
			bufferCount = state.Buffer.Count;

			var selected = new List<FeedEvent>( Math.Min( limit, state.Buffer.Count ) );
			foreach ( var e in state.Buffer )
			{
				if ( sinceSeq >= 0 && e.Seq <= sinceSeq ) continue;
				if ( filterAgentId != null && e.AgentId != null
					&& !string.Equals( e.AgentId, filterAgentId, StringComparison.Ordinal ) )
				{
					foreignFiltered++;
					continue;
				}
				if ( selected.Count < limit ) selected.Add( e );
			}
			page = selected.ToArray();
		}

		return HandlerBase.Success( new
		{
			ok = true,
			channel,
			events = page.Select( SerializeEvent ).ToList(),
			count = page.Length,
			next_seq = nextSeq,
			dropped_count = dropped,
			buffer_size = bufferCount,
			buffer_cap = RingSize,
			agent_filter = filterAgentId,
			foreign_filtered_count = filterAgentId == null ? (int?)null : foreignFiltered
		} );
	}

	private static object SerializeEvent( FeedEvent e ) => new
	{
		seq = e.Seq,
		time = e.Time.ToString( "yyyy-MM-ddTHH:mm:ss.fffZ" ),
		channel = e.Channel,
		type = e.Type,
		agent_id = e.AgentId,
		payload = e.Payload
	};

	// ── unsubscribe ──────────────────────────────────────────────────────

	private static object Unsubscribe( JsonElement args )
	{
		var channel = HandlerBase.GetString( args, "channel" );
		if ( string.IsNullOrEmpty( channel ) )
			return HandlerBase.Error( "Missing required 'channel' parameter.", "unsubscribe" );

		if ( !_channels.TryGetValue( channel, out var state ) )
		{
			return HandlerBase.Success( new { ok = true, channel, was_subscribed = false } );
		}

		Action teardown = null;
		bool wasSubscribed;
		lock ( state.Gate )
		{
			wasSubscribed = state.Subscribed;
			state.Subscribed = false;
			teardown = state.OnUnsubscribe;
			state.OnUnsubscribe = null;
			state.Buffer.Clear();
			state.DroppedCount = 0;
		}

		try { teardown?.Invoke(); }
		catch ( Exception ex )
		{
			return HandlerBase.Error( $"Teardown for '{channel}' threw: {ex.Message}", "unsubscribe" );
		}

		return HandlerBase.Success( new { ok = true, channel, was_subscribed = wasSubscribed } );
	}

	// ── channels ─────────────────────────────────────────────────────────

	private static object Channels( JsonElement _args )
	{
		var rows = AllChannels.Select( name =>
		{
			_channels.TryGetValue( name, out var st );
			bool active = ActiveChannels.Contains( name );
			return new
			{
				name,
				available = active,
				reason = active ? null : "v1 stub — ships in v1.1",
				subscribed = st?.Subscribed ?? false,
				buffer_size = st == null ? 0 : st.Buffer.Count,
				dropped_count = st?.DroppedCount ?? 0,
				next_seq = st?.NextSeq ?? 0,
				description = DescribeChannel( name )
			};
		} ).ToList();

		return HandlerBase.Success( new
		{
			ok = true,
			ring_size = RingSize,
			channels = rows
		} );
	}

	private static string DescribeChannel( string name ) => name switch
	{
		// [HANDOFF-TRUST: Phase 2.1 wrapper, no live hypothesis inherited]
		ChannelLog => "Engine log stream — every Log.Info/Warning/Error, asset-import message, runtime exception, engine diagnostic. Wraps Editor.EditorUtility.AddLogger (same source the editor Console widget consumes).",
		ChannelHotload => "[EditorEvent.Hotload] event emit — fires once per hotload cycle. Note: Sandbox.Hotload counter is unreliable for MCP-triggered compiles (per CompileHandler/PipelineHandler comments) — combine with concmd_registry polling for stale-IL detection.",
		ChannelConcmdRegistry => "ConCmd IL-hash divergence detection — emits concmd_orphaned events when a registered ConCmd's MethodBody IL byte sequence changes between hotloads. The stale-DLL self-detection signal that kills the AP-069 manual diagnostic loop.",
		ChannelAsyncJob => "Plugin-side async-job lifecycle emit — long-running operations (compile, sky-view LUT bake, etc.) call FeedHandler.EmitAsyncJob(jobId, status, payload). Statuses: pending, running, complete, failed.",
		ChannelCompile => "Push-driven compile state — wraps the fork-only CompileGroup.OnBuildCompleted (event_type=build_completed) + OnCompileFailed (event_type=compile_failed) events that CompileHandler reflection-binds in its static ctor. Payload mirrors the compile.errors MCP wire format (per-output: compiler/successful/error_count/warning_count/diagnostic_count + truncated diagnostics list, first 100 per output). Replaces the log-scrape polling path. Under stable Facepunch engine the producer-side reflection probe returns null and this channel never emits.",
		// [HANDOFF-TRUST: Phase 2.3 description completes the wire surface for feed.scene; no live hypothesis from any inherited handoff applies]
		ChannelScene => "Push-driven scene mutation event stream — wraps the fork-only Scene.OnGameObjectAdded / OnGameObjectRemoved / OnComponentRegistered / OnComponentUnregistered events that SceneMutationHandler reflection-binds at Phase 1.5. Event types: gameobject_added, gameobject_removed, component_registered, component_unregistered. Payload shape: kind / scene_name / scene_guid / target_name / target_guid / captured (snake_case literals — note JsonNamingPolicy.CamelCase only lowercases first char). Replaces the snapshot.take + snapshot.diff polling workaround. Under stable Facepunch engine the producer-side reflection probe returns null and this channel never emits.",
		// [HANDOFF-TRUST: Phase 2.4 description for feed.assets; closes Phase 2 exit criterion; no live hypothesis from any inherited handoff applies]
		ChannelAssets => "Push-driven asset-pipeline error stream — wraps the fork-only AssetSystem.OnAssetCompileFailed event that AssetSystemHandler reflection-binds at Phase 2.4 / 1.6. Event type: asset_compile_failed. Payload shape: kind / asset_path / asset_guid / asset_type / error_message / exception_type / exception_message / captured (snake_case literals — note JsonNamingPolicy.CamelCase only lowercases first char). Replaces the editor.get_log filter:\"compile\" log-scrape workaround (the warn-level Log.Warning stream is ring-buffered and severity-filter-misses warn-level Error| entries). Under stable Facepunch engine the producer-side reflection probe returns null and this channel never emits.",
		_ => "v1 stub — registered for API surface stability; ships in v1.1."
	};

	// ── Channel sources ──────────────────────────────────────────────────

	private static void AttachChannelSource( ChannelState state )
	{
		switch ( state.Name.ToLowerInvariant() )
		{
			case ChannelLog:
				AttachLogChannel( state );
				break;
			case ChannelHotload:
				// [EditorEvent.Hotload] is wired via the static OnHotload
				// handler below — always-on as soon as the assembly loads.
				// No per-subscribe attach needed; just record the baseline seq.
				break;
			case ChannelConcmdRegistry:
				// Same: hotload handler does the diff. First subscribe also
				// captures the baseline ConCmd hash set so subsequent diffs
				// have something to compare against.
				TryRecomputeConcmdRegistry( emitOnDiff: false );
				break;
			case ChannelAsyncJob:
				// Push channel; emit-side helper EmitAsyncJob writes here.
				break;
			// [HANDOFF-TRUST: Phase 2.1 wrapper, no live hypothesis inherited]
			case ChannelCompile:
				// Phase 2.1 push channel; producer = CompileHandler reflection-bound
				// CompileGroup.OnBuildCompleted (1.1) + OnCompileFailed (1.2);
				// consumer = FeedHandler.EmitCompile (below). No per-subscribe attach
				// needed — CompileHandler is always-on across the editor lifetime
				// (subscription bound in its static ctor + RebindForkEngineEventsOnHotload).
				break;
		}
	}

	private static void AttachLogChannel( ChannelState state )
	{
		// Defensive RemoveLogger first — idempotent across hot-reload (same
		// pattern EditorHandler.cs:63-64 + AssetQueryHandler.cs:103-104 use).
		try { if ( _logCallback != null ) EditorUtility.RemoveLogger( _logCallback ); } catch { }

		_logCallback = ev =>
		{
			try
			{
				if ( !state.Subscribed ) return;
				if ( !string.IsNullOrEmpty( state.Filter )
					&& ( ev.Message ?? "" ).IndexOf( state.Filter, StringComparison.OrdinalIgnoreCase ) < 0 )
					return;

				var payload = new
				{
					level = ev.Level.ToString().ToLowerInvariant(),
					logger = ev.Logger,
					message = ev.Message,
					stack = string.IsNullOrEmpty( ev.Stack ) ? null : ev.Stack
				};
				PushEvent( state, "log", payload );
			}
			catch { /* never throw from a logger callback — would re-enter logger */ }
		};

		EditorUtility.AddLogger( _logCallback );
		state.OnUnsubscribe = () =>
		{
			try { if ( _logCallback != null ) EditorUtility.RemoveLogger( _logCallback ); } catch { }
			_logCallback = null;
		};
	}

	// ── PushEvent ────────────────────────────────────────────────────────

	private static void PushEvent( ChannelState state, string type, object payload, string agentId = null )
	{
		lock ( state.Gate )
		{
			var ev = new FeedEvent
			{
				Seq = state.NextSeq++,
				Time = DateTime.UtcNow,
				Channel = state.Name,
				Type = type,
				Payload = payload,
				AgentId = agentId
			};
			state.Buffer.Enqueue( ev );
			var cap = RingSize > 0 ? RingSize : 1024;
			while ( state.Buffer.Count > cap )
			{
				state.Buffer.Dequeue();
				state.DroppedCount++;
			}
		}
	}

	// ── Hotload handler — fires for hotload + concmd_registry channels ──

	// Monotonic hotload-generation counter since editor boot (2026-07-12
	// agent-friction-audit F1). The editor decays predictably with generation
	// count (ConCmd dispatch ~gen 6+, TypeLibrary ~gen 12, component type
	// identity ~gen 13); EditorHeartbeat writes this into the disk heartbeat so
	// tools/hotload-gen.py can classify decay with zero MCP round-trips.
	// Lives in the editor assembly, so it survives game-assembly hotloads.
	private static int _hotloadGeneration;
	internal static int HotloadGeneration => System.Threading.Volatile.Read( ref _hotloadGeneration );

	[EditorEvent.Hotload]
	public static void OnHotload()
	{
		// MANDATORY try/catch swallowing all exceptions per
		// ArenulaMcpServer.cs:158-162 contract.
		try
		{
			System.Threading.Interlocked.Increment( ref _hotloadGeneration );

			// hotload channel
			if ( _channels.TryGetValue( ChannelHotload, out var hl ) && hl.Subscribed )
			{
				PushEvent( hl, "hotload", new
				{
					event_type = "hotload",
					utc = DateTime.UtcNow.ToString( "o" )
				} );
			}

			// concmd_registry: diff IL hashes + emit per-orphaned event
			TryRecomputeConcmdRegistry( emitOnDiff: true );
		}
		catch ( Exception ex )
		{
			try { Log.Error( $"FeedHandler.OnHotload failed: {ex.Message}" ); } catch { }
		}
	}

	// ── concmd_registry helper ───────────────────────────────────────────
	//
	// Reflection-based scan of [ConCmd]-marked methods. Uses
	// System.Reflection.MethodBody.GetILAsByteArray() — permitted inside
	// arenula_mcp.editor.dll (the SB1000 whitelist applies to game-csproj
	// only). Hash via SHA-1; compare to last-seen.

	private static void TryRecomputeConcmdRegistry( bool emitOnDiff = true )
	{
		try
		{
			if ( !_channels.TryGetValue( ChannelConcmdRegistry, out var state ) || !state.Subscribed )
				return;

			var sha = SHA1.Create();
			int scanned = 0;
			int orphanedEmitted = 0;
			foreach ( var asm in AppDomain.CurrentDomain.GetAssemblies() )
			{
				Type[] types;
				try { types = asm.GetTypes(); }
				catch ( ReflectionTypeLoadException tle ) { types = tle.Types.Where( t => t != null ).ToArray(); }
				catch { continue; }

				foreach ( var t in types )
				{
					MethodInfo[] methods;
					try { methods = t.GetMethods( BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly ); }
					catch { continue; }

					foreach ( var m in methods )
					{
						ConCmdAttribute attr;
						try { attr = m.GetCustomAttribute<ConCmdAttribute>(); }
						catch { continue; }
						if ( attr == null ) continue;

						scanned++;
						string name = attr.Name ?? m.Name;
						string newHash = HashMethodBody( m, sha );
						if ( newHash == null ) continue;

						if ( _concmdIlHash.TryGetValue( name, out var prior ) && prior != newHash )
						{
							if ( emitOnDiff )
							{
								PushEvent( state, "concmd_orphaned", new
								{
									name,
									prior_hash = prior,
									new_hash = newHash,
									declaring_type = t.FullName,
									assembly = asm.GetName().Name
								} );
								orphanedEmitted++;
							}
						}
						_concmdIlHash[name] = newHash;
					}
				}
			}

			if ( emitOnDiff && orphanedEmitted == 0 )
			{
				// Always emit a baseline "scanned" event so the agent can
				// confirm the channel is alive even when nothing changed.
				PushEvent( state, "concmd_scan", new
				{
					scanned_count = scanned,
					orphaned_count = 0
				} );
			}
		}
		catch ( Exception ex )
		{
			try { Log.Warning( $"FeedHandler.TryRecomputeConcmdRegistry: {ex.Message}" ); } catch { }
		}
	}

	private static string HashMethodBody( MethodInfo m, SHA1 sha )
	{
		try
		{
			var body = m.GetMethodBody();
			if ( body == null ) return null;
			var bytes = body.GetILAsByteArray();
			if ( bytes == null || bytes.Length == 0 ) return null;
			var hash = sha.ComputeHash( bytes );
			return Convert.ToHexString( hash );
		}
		catch
		{
			return null;
		}
	}

	// ── async_job emit helper (public, called by other handlers) ────────
	//
	// Other handlers (CompileHandler, PipelineHandler, bake-* helpers) call
	// this to publish async-job state transitions. v1 wires it as a public
	// static method; callers are added incrementally — see mcp-guide.md
	// v1 limitations.

	/// <summary>
	/// Publish an async-job lifecycle event to feed.async_job subscribers.
	/// No-op if no one is subscribed. Statuses: pending, running, complete, failed.
	/// Pass <paramref name="agentId"/> when the job belongs to a specific agent so
	/// feed.poll's agent_id filter can drop it for everyone else (findings.md #4).
	/// </summary>
	public static void EmitAsyncJob( string jobId, string status, object payload = null, string agentId = null )
	{
		try
		{
			if ( !_channels.TryGetValue( ChannelAsyncJob, out var state ) || !state.Subscribed )
				return;
			PushEvent( state, "async_job", new
			{
				job_id = jobId,
				status,
				payload
			}, agentId );
		}
		catch { /* never throw from emit */ }
	}

	// ── compile emit helper (Phase 2.1, charter engine-fork-elite-leverage row 2.1) ──
	//
	// [HANDOFF-TRUST: Phase 2.1 wrapper, no live hypothesis inherited]
	//
	// Consumer side of the consumer/producer split with CompileHandler's
	// reflection-bound CompileGroup.OnBuildCompleted (1.1) + OnCompileFailed
	// (1.2) subscriptions. CompileHandler owns the engine reflection seam
	// (CompileHandler.cs:1026 OnEngineBuildCompleted + :1050 OnEngineCompileFailed);
	// FeedHandler owns the wire-format adapter. Same shape as EmitAsyncJob
	// above (push channel; no-op when no subscribers; defensive try/catch
	// to honor the never-throw-from-emit contract).
	//
	// Payload shape intentionally mirrors the existing compile.errors MCP
	// wire format (per-DiagnosticEntry: id/severity/message/file/line/column)
	// so subscribers can reuse the same renderer without remapping. Per-output
	// diagnostics array is TRUNCATED to first 100 entries to keep ring-buffer
	// memory bounded (the full count is preserved in diagnostic_count for
	// pagination/diff use). Total per-payload error_count + warning_count are
	// also summed at the top level for cheap subscriber-side triage.

	/// <summary>
	/// Publish a compile-event to feed.compile subscribers. Called by
	/// CompileHandler.OnEngineBuildCompleted (eventType="build_completed")
	/// and CompileHandler.OnEngineCompileFailed (eventType="compile_failed")
	/// after the existing _lastOutputs cache refresh. No-op when no subscribers
	/// OR when outputs is null/empty. Never throws into the engine event-dispatch
	/// path (defensive try/catch identical to EmitAsyncJob's contract).
	/// </summary>
	public static void EmitCompile( string eventType, CompilerOutput[] outputs )
	{
		try
		{
			if ( !_channels.TryGetValue( ChannelCompile, out var state ) || !state.Subscribed )
				return;
			if ( outputs == null || outputs.Length == 0 ) return;
			if ( string.IsNullOrEmpty( eventType ) ) eventType = "compile";

			// [HANDOFF-TRUST: fix payload-cap overflow, no live hypothesis]
			// Cap diagnostics to 5 per output (was 100). Real game-side compiles
			// produce 150+ warnings; the previous cap pushed the serialized
			// envelope past HandlerBase.MaxResponseChars=16384 (forcing
			// CursorStore.StoreAndPage which inserts a raw '\n' at the page
			// boundary — see CursorStore.cs:69 — making the response invalid
			// JSON for downstream parse). Subscribers that need the full
			// diagnostic list call compile.errors on demand; this channel is
			// for push notification of build state, not full diagnostic transport.
			const int MaxDiagnosticsPerOutput = 5;
			int totalErrors = 0;
			int totalWarnings = 0;
			var outputList = new List<object>( outputs.Length );

			foreach ( var output in outputs )
			{
				if ( output == null ) continue;
				var compiler = output.Compiler;
				var diags = compiler?.Diagnostics;
				int diagnosticCount = diags?.Length ?? 0;
				int outErrors = 0;
				int outWarnings = 0;

				var truncated = new List<object>( Math.Min( diagnosticCount, MaxDiagnosticsPerOutput ) );
				if ( diags != null )
				{
					for ( int i = 0; i < diags.Length; i++ )
					{
						var diag = diags[i];
						if ( diag == null ) continue;
						var sev = diag.Severity.ToString().ToLowerInvariant();
						if ( sev == "error" ) outErrors++;
						else if ( sev == "warning" ) outWarnings++;

						if ( truncated.Count >= MaxDiagnosticsPerOutput ) continue;

						string filePath = null;
						int? line = null;
						int? column = null;
						try
						{
							var lineSpan = diag.Location?.GetMappedLineSpan();
							if ( lineSpan.HasValue && lineSpan.Value.IsValid )
							{
								filePath = lineSpan.Value.Path;
								line = lineSpan.Value.StartLinePosition.Line + 1;
								column = lineSpan.Value.StartLinePosition.Character + 1;
							}
						}
						catch { /* Roslyn location APIs are best-effort */ }

						truncated.Add( new
						{
							id = diag.Id,
							severity = sev,
							message = diag.GetMessage(),
							file = filePath,
							line,
							column
						} );
					}
				}

				totalErrors += outErrors;
				totalWarnings += outWarnings;

				outputList.Add( new
				{
					compiler = compiler?.Name ?? "unknown",
					successful = output.Successful,
					error_count = outErrors,
					warning_count = outWarnings,
					diagnostic_count = diagnosticCount,
					diagnostics_truncated = diagnosticCount > MaxDiagnosticsPerOutput,
					diagnostics = truncated
				} );
			}

			// Best-effort attribution (findings.md #4): the single in-flight MCP
			// compile trigger when unambiguous, else null (passes every filter).
			var (attributedAgentId, _) = CompileTriggerAttribution.Current;

			PushEvent( state, eventType, new
			{
				event_type = eventType,
				utc = DateTime.UtcNow.ToString( "o" ),
				output_count = outputList.Count,
				total_error_count = totalErrors,
				total_warning_count = totalWarnings,
				outputs = outputList
			}, attributedAgentId );
		}
		catch { /* never throw from emit — would re-enter engine event dispatcher */ }
	}

	// ── scene emit helper (Phase 2.3, charter engine-fork-elite-leverage row 2.3) ──
	//
	// [HANDOFF-TRUST: Phase 2.3 wrapper, byte-for-byte parallel of EmitAsyncJob+EmitCompile; no live hypothesis inherited from any handoff applies]
	//
	// Consumer side of the consumer/producer split with SceneMutationHandler's
	// reflection-bound Scene.OnGameObjectAdded / OnGameObjectRemoved /
	// OnComponentRegistered / OnComponentUnregistered subscriptions (all bound
	// at Phase 1.5). SceneMutationHandler.cs owns the engine reflection seam
	// (lines 94/106/118/130: OnEngineGameObjectAdded etc); FeedHandler owns
	// the wire-format adapter. Same shape as EmitAsyncJob above (push channel;
	// no-op when no subscribers; defensive try/catch to honor the
	// never-throw-from-emit contract).
	//
	// Payload shape matches SceneMutationHandler.MutationEvent (record at
	// SceneMutationHandler.cs:60-68): kind / scene_name / scene_guid /
	// target_name / target_guid / captured. Caller builds the anonymous-object
	// payload (rather than passing the record) because the record's PascalCase
	// fields would survive JsonNamingPolicy.CamelCase verbatim only on the
	// first char — and the channel subscriber wire-format expects snake_case
	// per the Phase 2.1 / feed.compile pattern (see
	// feedback_jsonnamingpolicy_camelcase_does_not_convert_snake_case).
	//
	// Size note: each event payload is ~6 small string fields, well under the
	// HandlerBase.MaxResponseChars=16384 cap (per-event ~200B). 32-event ring
	// (RollingCap in SceneMutationHandler) × ~200B = 6.4KB worst case if every
	// event is in one poll response — comfortably under cap. No per-event
	// truncation logic needed (unlike EmitCompile's diagnostics array).

	/// <summary>
	/// Publish a scene-mutation event to feed.scene subscribers. Called by
	/// SceneMutationHandler.OnEngineGameObjectAdded (eventType="gameobject_added"),
	/// OnEngineGameObjectRemoved (eventType="gameobject_removed"),
	/// OnEngineComponentRegistered (eventType="component_registered"), and
	/// OnEngineComponentUnregistered (eventType="component_unregistered")
	/// AFTER the existing AppendEvent rolling-buffer write. No-op when no
	/// subscribers OR when payload is null. Never throws into the engine
	/// event-dispatch path (defensive try/catch identical to EmitAsyncJob's
	/// contract).
	/// </summary>
	public static void EmitScene( string eventType, object payload )
	{
		try
		{
			if ( !_channels.TryGetValue( ChannelScene, out var state ) || !state.Subscribed )
				return;
			if ( payload == null ) return;
			if ( string.IsNullOrEmpty( eventType ) ) eventType = "scene";
			PushEvent( state, eventType, payload );
		}
		catch { /* never throw from emit — would re-enter engine event dispatcher */ }
		// ── assets emit helper (Phase 2.4, charter engine-fork-elite-leverage row 2.4) ──
		//
		// [HANDOFF-TRUST: Phase 2.4 wrapper, byte-for-byte parallel of EmitScene+EmitCompile; closes the four-wrapper charter exit criterion; no live hypothesis inherited from any handoff applies]
		//
		// Consumer side of the consumer/producer split with AssetSystemHandler's
		// reflection-bound AssetSystem.OnAssetCompileFailed subscription (Phase 2.4
		// / 1.6 fork patch). AssetSystemHandler.cs owns the engine reflection seam
		// (line OnEngineAssetCompileFailed); FeedHandler owns the wire-format
		// adapter. Same shape as EmitScene above (push channel; no-op when no
		// subscribers; defensive try/catch to honor the never-throw-from-emit
		// contract).
		//
		// Payload shape matches AssetSystemHandler.AssetErrorEvent: kind /
		// asset_path / asset_guid / asset_type / error_message / exception_type /
		// exception_message / captured. Caller builds the anonymous-object
		// payload (rather than passing the record) because the record's PascalCase
		// fields would survive JsonNamingPolicy.CamelCase verbatim only on the
		// first char — and the channel subscriber wire-format expects snake_case
		// per the Phase 2.1 / 2.3 patterns (see
		// feedback_jsonnamingpolicy_camelcase_does_not_convert_snake_case).
		//
		// Size note: each event payload is ~8 small string fields, well under the
		// HandlerBase.MaxResponseChars=16384 cap (per-event ~400B including the
		// error_message + exception_message text). 32-event ring (RollingCap in
		// AssetSystemHandler) × ~400B = 12.8KB worst case if every event is in
		// one poll response — under cap with some headroom. No per-event
		// truncation logic needed (unlike EmitCompile's diagnostics array).
	}

	/// <summary>
	/// Publish an asset-pipeline error event to feed.assets subscribers. Called
	/// by AssetSystemHandler.OnEngineAssetCompileFailed (eventType="asset_compile_failed")
	/// AFTER the existing AppendEvent rolling-buffer write. No-op when no
	/// subscribers OR when payload is null. Never throws into the engine
	/// event-dispatch path (defensive try/catch identical to EmitAsyncJob's
	/// contract).
	/// </summary>
	public static void EmitAssets( string eventType, object payload )
	{
		try
		{
			if ( !_channels.TryGetValue( ChannelAssets, out var state ) || !state.Subscribed )
				return;
			if ( payload == null ) return;
			if ( string.IsNullOrEmpty( eventType ) ) eventType = "assets";
			PushEvent( state, eventType, payload );
		}
		catch { /* never throw from emit — would re-enter engine event dispatcher */ }
	}

	// ── Fork-engine event subscription (Phase 1.3, AP-069 cure) ──────────
	//
	// [HANDOFF-TRUST: closeout refactor moves to reflection-bridge; orthogonal to H1]
	//
	// Cures AP-069 by replacing the existing [EditorEvent.Hotload] polling
	// handler (OnHotload above at the top of this file) with a push-driven
	// signal carrying the full HotloadResult (Success, HasErrors, HasWarnings,
	// Entries, TypeTimings, NoAction, ProcessingTime, etc.). All access to
	// Sandbox.Hotload types goes through HotloadResultBridge (see
	// Libraries/arenula_mcp/Editor/Reflection/HotloadResultBridge.cs for the
	// rationale + the engine-source root cause). The library binary is
	// byte-identical between fork and stable builds — under stable engine
	// the bridge.IsAvailable returns false, this code silently no-ops, and
	// the existing OnHotload polling path stays primary.
	//
	// Architectural rationale (mirror of Phase 1.1's CompileHandler pattern,
	// adapted to FeedHandler since the hotload channel already lives here):
	// the existing OnHotload [EditorEvent.Hotload] handler (line 419) stays
	// in place as the polling-fallback for stable engine — under fork BOTH
	// fire (one polling event_type=hotload, one push event_type=hotload_complete).
	// Subscribers can prefer the push variant and use the polling one as a
	// backstop, OR consume both if they want timing information about the
	// observation latency between Hotload.UpdateReferences completion and
	// the editor's [EditorEvent.Hotload] dispatch.
	//
	// Why reflection instead of `#if FORK_ENGINE`: the S&Box editor's project
	// compiler is a custom Roslyn pipeline (engine/Sandbox.Compiling/) that
	// reads DefineConstants from the package's .sbproj `Metadata.Compiler`
	// block, NOT from MSBuild .csproj/.targets. Hardcoding FORK_ENGINE in
	// sbox_mcp.sbproj would always-define it, breaking the resilience seam.
	// AP-113 captures the failed-pattern.
	//
	// Why reflection bridge (not direct typeof + Action<HotloadResult>):
	// the editor's default library compile reference set is hardcoded at
	// engine/Sandbox.Compiling/Compiler/Compiler.References.cs:8-16 and
	// DOES NOT include Sandbox.Hotload (the hot-reload subsystem is
	// internal-by-convention to user libraries). Direct typeof(Hotload),
	// HotloadResult parameter types, or HotloadEntryType.Error literals
	// fail to compile under BOTH fork and stable engines. Bridge centralizes
	// the reflection in one place, caches metadata, fails loudly on engine
	// API drift, and uses Expression.Lambda.Compile() for hot-path delegate
	// invocation.
	//
	// VERIFIED via engine source read 2026-05-18 (codex/hotload-full-fallback,
	// engine/Sandbox.Hotload/Hotload.OnComplete.cs commit ed7b424d +
	// engine/Sandbox.Hotload/UpdateReferences.cs):
	//   * OnComplete declared `public static event Action<HotloadResult>` on
	//     Sandbox.Hotload partial class.
	//   * Raised once per UpdateReferences() completion at UpdateReferences.cs:65
	//     (NoAction early-out) and :172 (normal completion).
	//   * Per-subscriber try/catch in RaiseOnComplete shields the chain.
	//   * Sandbox.Hotload assembly carries default IgnoredAssemblies entry for
	//     itself, so static event field persists across downstream library
	//     hotloads. The -=/+= pattern in EnsureHotloadOnCompleteBound is
	//     idempotent across reload cycles.
	//
	// Engine source: M:/SBoxProjects/SBox-Pub-hotload-full-fallback/engine/
	//                Sandbox.Hotload/Hotload.OnComplete.cs (commit ed7b424d)

	// The bridge-issued Delegate (engine-typed Action<HotloadResult> built
	// via Expression.Lambda.Compile). Null on stable engine and before the
	// first bind. Caller MUST retain this reference to pass to Unsubscribe.
	private static Delegate _hotloadOnCompleteDelegate;

	/// <summary>
	/// Handler bound to the fork-only Sandbox.Hotload.OnComplete event via
	/// the reflection bridge. Pushes an enriched payload into the existing
	/// feed.hotload channel under event_type "hotload_complete" (distinguished
	/// from the existing polling [EditorEvent.Hotload] handler's "hotload"
	/// event_type at line 419). Subscribers receive both event types under
	/// fork; the push variant carries the full HotloadResult fields needed
	/// to replace CompileHandler.DescribeHotloadState's polling-based
	/// classifier. The 'result' parameter is the HotloadResult instance
	/// passed by the engine, boxed as object — use HotloadResultBridge
	/// accessor methods to extract typed values.
	/// </summary>
	private static void OnEngineHotloadComplete( object result )
	{
		try
		{
			if ( !_channels.TryGetValue( ChannelHotload, out var state ) || !state.Subscribed )
				return;

			PushEvent( state, "hotload_complete", new
			{
				event_type = "hotload_complete",
				utc = DateTime.UtcNow.ToString( "o" ),
				result = result == null ? null : new
				{
					success = HotloadResultBridge.Success( result ),
					no_action = HotloadResultBridge.NoAction( result ),
					has_errors = HotloadResultBridge.HasErrors( result ),
					has_warnings = HotloadResultBridge.HasWarnings( result ),
					instances_processed = HotloadResultBridge.InstancesProcessed( result ),
					processing_time_ms = HotloadResultBridge.ProcessingTime( result ),
					instance_queue_time_ms = HotloadResultBridge.InstanceQueueTime( result ),
					static_field_time_ms = HotloadResultBridge.StaticFieldTime( result ),
					watched_instance_time_ms = HotloadResultBridge.WatchedInstanceTime( result ),
					diagnostics_time_ms = HotloadResultBridge.DiagnosticsTime( result ),
					entry_count = HotloadResultBridge.EntryCount( result ),
					error_count = HotloadResultBridge.ErrorCount( result ),
					warning_count = HotloadResultBridge.WarningCount( result ),
					type_timing_count = HotloadResultBridge.TypeTimingCount( result ),
					auto_skipped_type_count = HotloadResultBridge.AutoSkippedTypeCount( result ),
					created = HotloadResultBridge.Created( result ).ToString( "o" )
				}
			} );
		}
		catch ( Exception ex )
		{
			// Never throw from an engine event handler — would break the
			// per-subscriber try/catch contract upstream and could destabilize
			// the engine's hotload thread.
			try { Log.Warning( $"FeedHandler.OnEngineHotloadComplete failed: {ex.Message}" ); } catch { }
		}
	}

	// Hotload re-bind. Sandbox.Hotload carries [SkipHotload] so the static
	// event field persists across our library reloads; we may end up with a
	// stale delegate in the invocation list pointing at the prior
	// generation's method target. The Unsubscribe/Subscribe pair in
	// EnsureHotloadOnCompleteBound is idempotent in either case.
	[EditorEvent.Hotload]
	public static void RebindHotloadOnCompleteOnHotload()
	{
		try { EnsureHotloadOnCompleteBound(); }
		catch ( Exception ex )
		{
			try { Log.Warning( $"FeedHandler.RebindHotloadOnCompleteOnHotload failed: {ex.Message}" ); } catch { }
		}
	}

	static FeedHandler()
	{
		try { EnsureHotloadOnCompleteBound(); }
		catch ( Exception ex )
		{
			try { Log.Warning( $"FeedHandler static ctor (fork-engine reflection probe) failed: {ex.Message}" ); } catch { }
		}
	}

	private static void EnsureHotloadOnCompleteBound()
	{
		if ( !HotloadResultBridge.IsAvailable )
		{
			// Stable engine OR pre-1.3 fork. No-op silently. The existing
			// [EditorEvent.Hotload] OnHotload path (line 419) stays primary
			// as the polling fallback.
			_hotloadOnCompleteDelegate = null;
			return;
		}

		// Idempotently remove the prior delegate (which may have a dead
		// method target after a library hotload) then re-add a fresh one.
		// HotloadResultBridge.Unsubscribe is defensive against stale targets.
		if ( _hotloadOnCompleteDelegate != null )
			HotloadResultBridge.Unsubscribe( _hotloadOnCompleteDelegate );

		_hotloadOnCompleteDelegate = HotloadResultBridge.Subscribe( OnEngineHotloadComplete );
	}
}
