using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sandbox;

namespace Arenula;

/// <summary>
/// Shared response helpers used by all tool handlers.
/// Provides canonical response formatting per Arenula MCP spec.
/// </summary>
internal static class HandlerBase
{
    internal const int MaxResponseChars = 16384;

    // ── Success responses ──────────────────────────────────────────────

    /// <summary>Wrap a plain text message as an MCP success response.</summary>
    internal static object Text( string text ) => new
    {
        content = new object[] { new { type = "text", text } }
    };

    /// <summary>Serialize an object to JSON and wrap as MCP success response.
    /// Large responses are automatically paginated via CursorStore.</summary>
    internal static object Success( object data )
    {
        var result = McpResult.Structured( data, out var json );
        if ( json.Length > MaxResponseChars )
            return CursorStore.StoreAndPage( json );
        return result;
    }

    // ── Image responses ─────────────────────────────────────────────────

    /// <summary>Return a base64-encoded image as an MCP image content block, with optional caption.</summary>
    internal static object Image( byte[] imageData, string mimeType = "image/jpeg", string caption = null )
    {
        var content = new List<object>
        {
            new { type = "image", data = Convert.ToBase64String( imageData ), mimeType }
        };
        if ( caption != null )
            content.Add( new { type = "text", text = caption } );
        return new { content };
    }

    // ── Error responses ────────────────────────────────────────────────

    /// <summary>Return a tool execution error with action context and suggestion.</summary>
    internal static object Error( string message, string action = null, string suggestion = null )
        => Error( message, action, suggestion, null, null );

    /// <summary>Return a tool execution error with structured diagnostics for agents.</summary>
    internal static object Error( string message, string action, string suggestion, object details )
        => Error( message, action, suggestion, details, null );

    /// <summary>
    /// Return a tool execution error carrying a machine-branchable <c>error_code</c> from the
    /// <see cref="McpErrorCode"/> taxonomy. Additive and backward compatible: <c>error</c>
    /// (the prose message), <c>action</c>, <c>suggestion</c> and <c>details</c> are unchanged.
    /// When a code is supplied the payload also carries <c>retryable</c> so agents can branch
    /// retry-vs-fatal without regexing the message text.
    /// </summary>
    internal static object Error( string message, string action, string suggestion, object details, string errorCode )
    {
        var error = new Dictionary<string, object> { ["error"] = message };
        if ( errorCode != null )
        {
            error["error_code"] = errorCode;
            error["retryable"] = McpErrorCode.IsRetryable( errorCode );
        }
        if ( action != null ) error["action"] = action;
        if ( suggestion != null ) error["suggestion"] = suggestion;
        if ( details != null ) error["details"] = details;
        return McpResult.Structured( error, out _, isError: true );
    }

    /// <summary>Return a protocol-level error (invalid action, missing param).</summary>
    internal static object ProtocolError( int code, string message ) => new
    {
        code, message
    };

    // ── Identifier tuples ──────────────────────────────────────────────

    /// <summary>Canonical { id, name } tuple for a GameObject.</summary>
    internal static object IdTuple( GameObject go ) => new
    {
        id = go.Id.ToString(),
        name = go.Name
    };

    /// <summary>Canonical { id, name } tuple for a Component.</summary>
    internal static object IdTuple( Component c ) => new
    {
        id = c.Id.ToString(),
        name = c.GetType().Name
    };

    // ── Pagination ─────────────────────────────────────────────────────

    /// <summary>Apply offset/limit pagination to a list and wrap with metadata.</summary>
    internal static object Paginate<T>( IReadOnlyList<T> items, int offset, int limit,
        Func<T, object> transform )
    {
        var total = items.Count;
        var page = items.Skip( offset ).Take( limit ).Select( transform ).ToList();
        return Success( new
        {
            results = page,
            total,
            offset,
            limit,
            has_more = offset + limit < total
        } );
    }

    // ── Param extraction helpers ───────────────────────────────────────

    /// <summary>Extract a string parameter from JSON args, or return default.</summary>
    internal static string GetString( JsonElement args, string name, string defaultValue = null )
    {
        if ( args.ValueKind == JsonValueKind.Undefined ) return defaultValue;
        if ( args.TryGetProperty( name, out var el ) && el.ValueKind == JsonValueKind.String )
            return el.GetString();
        return defaultValue;
    }

    /// <summary>
    /// Extract an int parameter from JSON args, or return default.
    /// A numeric STRING is accepted too ("4" as well as 4): plenty of MCP clients
    /// stringify scalars, and before this a stringified number silently fell back
    /// to the default — the caller then got a quietly wrong answer that still
    /// reported success. Found 2026-08-05 when sample_grid returned a 10x10 grid
    /// for a requested 4x4 and said samples_x=10.
    /// </summary>
    internal static int GetInt( JsonElement args, string name, int defaultValue = 0 )
    {
        if ( args.ValueKind == JsonValueKind.Undefined ) return defaultValue;
        if ( !args.TryGetProperty( name, out var el ) ) return defaultValue;
        if ( el.ValueKind == JsonValueKind.Number ) return el.GetInt32();
        if ( el.ValueKind == JsonValueKind.String
            && int.TryParse( el.GetString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed ) )
            return parsed;
        return defaultValue;
    }

    /// <summary>Extract a bool parameter from JSON args, or return default.</summary>
    internal static bool GetBool( JsonElement args, string name, bool defaultValue = false )
    {
        if ( args.ValueKind == JsonValueKind.Undefined ) return defaultValue;
        if ( args.TryGetProperty( name, out var el ) )
        {
            if ( el.ValueKind == JsonValueKind.True ) return true;
            if ( el.ValueKind == JsonValueKind.False ) return false;
        }
        return defaultValue;
    }

    /// <summary>
    /// Extract a float parameter from JSON args, or return default.
    /// Accepts a numeric string as well as a JSON number — see <see cref="GetInt"/>
    /// for why.
    /// </summary>
    internal static float GetFloat( JsonElement args, string name, float defaultValue = 0f )
    {
        if ( args.ValueKind == JsonValueKind.Undefined ) return defaultValue;
        if ( !args.TryGetProperty( name, out var el ) ) return defaultValue;
        if ( el.ValueKind == JsonValueKind.Number ) return el.GetSingle();
        if ( el.ValueKind == JsonValueKind.String
            && float.TryParse( el.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed ) )
            return parsed;
        return defaultValue;
    }

    // ── Vector/rotation helpers ────────────────────────────────────────

    /// <summary>Format a Vector3 as a compact object.</summary>
    internal static object V3( Vector3 v ) => new { x = MathF.Round( v.x, 2 ), y = MathF.Round( v.y, 2 ), z = MathF.Round( v.z, 2 ) };

    /// <summary>Format a Rotation as Euler angles.</summary>
    internal static object Rot( Rotation r )
    {
        var e = r.Angles();
        return new { pitch = MathF.Round( e.pitch, 2 ), yaw = MathF.Round( e.yaw, 2 ), roll = MathF.Round( e.roll, 2 ) };
    }

    /// <summary>Parse a "x,y,z" string into a Vector3.</summary>
    internal static Vector3 ParseVector3( string s )
    {
        if ( string.IsNullOrWhiteSpace( s ) )
            throw new ArgumentException( "Expected 'x,y,z' format, got empty string" );
        var parts = s.Split( ',' );
        if ( parts.Length != 3 ) throw new ArgumentException( $"Expected 'x,y,z' format, got '{s}'" );
        return new Vector3( float.Parse( parts[0].Trim() ), float.Parse( parts[1].Trim() ), float.Parse( parts[2].Trim() ) );
    }

    internal static Vector2 ParseVector2( string s )
    {
        if ( string.IsNullOrWhiteSpace( s ) )
            throw new ArgumentException( "Expected 'u,v' format, got empty string" );
        var parts = s.Split( ',' );
        if ( parts.Length != 2 ) throw new ArgumentException( $"Expected 'u,v' format, got '{s}'" );
        return new Vector2( float.Parse( parts[0].Trim() ), float.Parse( parts[1].Trim() ) );
    }

    /// <summary>Parse a comma-separated string of integers "0,1,2" into an int array.</summary>
    internal static int[] ParseIntArray( string s )
    {
        if ( string.IsNullOrWhiteSpace( s ) )
            throw new ArgumentException( "Expected comma-separated integers, got empty string" );
        var parts = s.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
        var result = new int[parts.Length];
        for ( int i = 0; i < parts.Length; i++ )
            result[i] = int.Parse( parts[i] );
        return result;
    }

    // ── Write confirmation ─────────────────────────────────────────────

    /// <summary>Standard write confirmation with ID tuple and message.</summary>
    internal static object WriteConfirm( GameObject go, string message ) => Success( new
    {
        id = go.Id.ToString(),
        name = go.Name,
        message
    } );

    internal static object Confirm( string message ) => Text( message );

    // ── Project root resolution ───────────────────────────────────────

    /// <summary>
    /// Resolve the project root directory via the current project API.
    /// Returns null if it cannot be determined.
    /// </summary>
    internal static string GetProjectRoot()
    {
        try
        {
            return Project.Current?.GetRootPath();
        }
        catch { return null; }
    }

    /// <summary>
    /// Resolve a relative path to an absolute path under the project root.
    /// Returns null (with error message) if the project root cannot be determined.
    /// </summary>
    internal static string ResolveProjectPath( string relativePath )
    {
        if ( System.IO.Path.IsPathRooted( relativePath ) )
            return relativePath;

        var root = GetProjectRoot();
        if ( string.IsNullOrEmpty( root ) )
            return null;

        return System.IO.Path.Combine( root, relativePath.Replace( '/', System.IO.Path.DirectorySeparatorChar ) );
    }

    // ── Partial-success warning ───────────────────────────────────────

    /// <summary>
    /// Structured warning for an operation that partially succeeded. Use when the
    /// caller needs to know what was skipped — a bare success response hides it.
    /// </summary>
    internal static object Warning( string message, string field = null, string suggestion = null )
    {
        var w = new Dictionary<string, string> { ["message"] = message };
        if ( field != null ) w["field"] = field;
        if ( suggestion != null ) w["suggestion"] = suggestion;
        return w;
    }

    // ── Require / Resolve validation family ───────────────────────────
    //
    // Ported from upstream Arenula (2026-07 hardening batch). These exist to kill
    // the silent-no-op class: a handler that receives a bad model path, a bad enum
    // name or a missing id, skips the assignment, and still returns success. The
    // caller then believes a write landed that never did.
    //
    // Convention throughout:
    //   Require* — throws if the input is missing OR unresolvable.
    //   Resolve* — returns null for a missing input (the caller meant "leave it"),
    //              throws only when a value was supplied and could not be resolved.
    //
    // All of these throw InvalidOperationException, which the per-handler try/catch
    // already converts into an Error response, so no call site needs new plumbing.

    internal static T RequireResource<T>( string path, string action, string hint = null ) where T : Resource
    {
        if ( string.IsNullOrEmpty( path ) )
            throw new InvalidOperationException( $"Missing required resource path for '{action}'." );
        var res = ResourceLibrary.Get<T>( path );
        if ( res == null )
        {
            var extra = hint != null ? $" {hint}" : "";
            throw new InvalidOperationException(
                $"{typeof( T ).Name} not found at '{path}'.{extra} " +
                "User-authored assets must live under 'Assets/'. Path is relative to Assets/ " +
                "(e.g. 'rifle.weapon' for Assets/rifle.weapon, no 'Assets/' prefix)." );
        }
        return res;
    }

    internal static T ResolveResource<T>( string path, string action, string hint = null ) where T : Resource
    {
        if ( string.IsNullOrEmpty( path ) ) return null;
        return RequireResource<T>( path, action, hint );
    }

    // Model and Material use their own static factories, not ResourceLibrary.

    internal static Model RequireModel( string path, string action )
    {
        if ( string.IsNullOrEmpty( path ) )
            throw new InvalidOperationException( $"Missing required model path for '{action}'." );
        var m = Model.Load( path );
        if ( m == null )
            throw new InvalidOperationException(
                $"Model not found at '{path}'. Check the .vmdl path is valid and the model is mounted or under Assets/." );
        return m;
    }

    internal static Model ResolveModel( string path, string action )
    {
        if ( string.IsNullOrEmpty( path ) ) return null;
        return RequireModel( path, action );
    }

    internal static Material RequireMaterial( string path, string action )
    {
        if ( string.IsNullOrEmpty( path ) )
            throw new InvalidOperationException( $"Missing required material path for '{action}'." );
        var m = Material.Load( path );
        if ( m == null )
            throw new InvalidOperationException(
                $"Material not found at '{path}'. Check the .vmat path is valid and the material is mounted or under Assets/." );
        return m;
    }

    internal static Material ResolveMaterial( string path, string action )
    {
        if ( string.IsNullOrEmpty( path ) ) return null;
        return RequireMaterial( path, action );
    }

    /// <summary>Parse an enum, listing the valid names in the error when it fails.</summary>
    internal static TEnum RequireEnum<TEnum>( string value, string paramName, string action )
        where TEnum : struct, Enum
    {
        if ( string.IsNullOrEmpty( value ) )
            throw new InvalidOperationException( $"Missing required '{paramName}' for '{action}'." );
        if ( !Enum.TryParse<TEnum>( value, ignoreCase: true, out var result ) )
        {
            var valid = string.Join( ", ", Enum.GetNames<TEnum>() );
            throw new InvalidOperationException(
                $"Invalid {paramName} '{value}' for '{action}'. Valid values: {valid}." );
        }
        return result;
    }

    internal static TEnum? ResolveEnum<TEnum>( string value, string paramName, string action )
        where TEnum : struct, Enum
    {
        if ( string.IsNullOrEmpty( value ) ) return null;
        return RequireEnum<TEnum>( value, paramName, action );
    }

    /// <summary>
    /// Enforce that a user-authored asset path is under 'Assets/'. Anything written
    /// elsewhere is not indexed by ResourceLibrary, so every later load returns null.
    /// </summary>
    internal static void RequireAssetsPath( string path, string action )
    {
        if ( string.IsNullOrEmpty( path ) )
            throw new InvalidOperationException( $"Missing required path for '{action}'." );
        var normalized = path.Replace( '\\', '/' ).TrimStart( '/' );
        if ( !normalized.StartsWith( "Assets/", StringComparison.OrdinalIgnoreCase ) )
            throw new InvalidOperationException(
                $"Path '{path}' is not under 'Assets/'. User-created assets outside 'Assets/' are not indexed " +
                "by ResourceLibrary, so tools that load the resource will silently get null. " +
                "Use a path like 'Assets/name.ext' or 'Assets/subfolder/name.ext'." );
    }

    internal static GameObject RequireGameObjectById( Scene scene, string id, string action )
    {
        if ( string.IsNullOrEmpty( id ) )
            throw new InvalidOperationException( $"Missing required GameObject id for '{action}'." );
        var go = SceneHelpers.FindById( scene, id );
        if ( go == null )
            throw new InvalidOperationException(
                $"GameObject '{id}' not found in the active scene. " +
                "GUIDs can change on scene reload — call 'scene.summary' to refresh IDs." );
        return go;
    }

    internal static GameObject ResolveGameObjectById( Scene scene, string id, string action )
    {
        if ( string.IsNullOrEmpty( id ) ) return null;
        return RequireGameObjectById( scene, id, action );
    }

    /// <summary>Resolve the active scene, throwing a usable message when there isn't one.</summary>
    internal static Scene RequireScene( string action )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            throw new InvalidOperationException(
                $"No active scene found for '{action}'. Open a scene in the editor first." );
        return scene;
    }

    // ── Presence-aware argument extraction ────────────────────────────
    //
    // The Get* accessors above cannot distinguish "caller passed 0" from "caller
    // omitted the field", so a handler using them silently overwrites a property
    // with a default. These return false when the field was not supplied.

    /// <summary>Extract a float. Returns true only if the property was actually provided.</summary>
    internal static bool TryGetFloat( JsonElement args, string name, out float value )
    {
        if ( args.ValueKind != JsonValueKind.Undefined &&
             args.TryGetProperty( name, out var el ) && el.ValueKind == JsonValueKind.Number )
        {
            value = el.GetSingle();
            return true;
        }
        value = 0f;
        return false;
    }

    /// <summary>Extract an int. Returns true only if the property was actually provided.</summary>
    internal static bool TryGetInt( JsonElement args, string name, out int value )
    {
        if ( args.ValueKind != JsonValueKind.Undefined &&
             args.TryGetProperty( name, out var el ) && el.ValueKind == JsonValueKind.Number )
        {
            value = el.GetInt32();
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>Extract a bool. Returns true only if the property was actually provided.</summary>
    internal static bool TryGetBool( JsonElement args, string name, out bool value )
    {
        if ( args.ValueKind != JsonValueKind.Undefined &&
             args.TryGetProperty( name, out var el ) )
        {
            if ( el.ValueKind == JsonValueKind.True ) { value = true; return true; }
            if ( el.ValueKind == JsonValueKind.False ) { value = false; return true; }
        }
        value = false;
        return false;
    }

    /// <summary>
    /// Parse a colour string from args and invoke the setter only when one was given.
    /// Deliberately silent on an unparseable value — this replaces an inline pattern
    /// with the same behaviour, and tightening it is a separate decision.
    /// </summary>
    internal static void ApplyColor( JsonElement args, string name, Action<Color> apply )
    {
        var s = GetString( args, name );
        if ( string.IsNullOrEmpty( s ) ) return;
        try
        {
            var c = Color.Parse( s );
            if ( c.HasValue ) apply( c.Value );
        }
        catch { }
    }

    /// <summary>
    /// Extract an asset path from args with the leading "Assets/" prefix stripped.
    /// AssetSystem.FindByPath returns null rather than erroring when the prefix is
    /// left on, which reads as "asset missing" instead of "path wrong".
    /// </summary>
    internal static string GetAssetPath( JsonElement args, string name = "path" )
        => SceneHelpers.NormalizePath( GetString( args, name ) );
}

/// <summary>
/// Machine-branchable error-code taxonomy for <see cref="HandlerBase.Error(string,string,string,object,string)"/>.
/// Agents branch on <c>error_code</c> + <c>retryable</c> instead of regexing prose
/// (even BatchHandler resorts to <c>resultJson.Contains("\"isError\":true")</c> today).
/// Codes are stable string constants — never renumber/rename once shipped; add new ones instead.
/// </summary>
internal static class McpErrorCode
{
    // Environment / addressing — fatal until the caller changes something.
    internal const string NoProjects        = "NO_PROJECTS";          // no project loaded / project root unresolvable
    internal const string NoActiveSession   = "NO_ACTIVE_SESSION";    // SceneEditorSession.Active is null
    internal const string NoActiveScene     = "NO_ACTIVE_SCENE";      // no scene open / resolvable
    internal const string NotFound          = "NOT_FOUND";            // GameObject / component / asset / path not found
    internal const string InvalidArgs       = "INVALID_ARGS";         // missing or malformed required parameter
    internal const string UnknownAction     = "UNKNOWN_ACTION";       // action string not in the tool's switch

    // Compile / hotload pipeline.
    internal const string CompileTimeout    = "COMPILE_TIMEOUT";      // compile wait exceeded timeout_ms — retry (longer timeout) is sane
    internal const string CompileFailed     = "COMPILE_FAILED";       // diagnostics with severity=error — fix code, don't blind-retry
    internal const string HotloadStalled    = "HOTLOAD_STALLED";      // DLL swap did not take; new types unresolved
    internal const string TypeLibraryCorrupted = "TYPELIBRARY_CORRUPTED"; // InstanceUpgrader 'couldn't upgrade' — cold restart territory
    internal const string VerifyFailed      = "VERIFY_FAILED";        // a verification stage reported failures

    // Runtime pawn readiness / ownership.
    internal const string PawnNotReady      = "PAWN_NOT_READY";       // play began, but no active-scene pawn exists yet
    internal const string PawnOwnerNotLocal = "PAWN_OWNER_NOT_LOCAL"; // active-scene controllers exist, none is locally controlled

    // Contention — transient by definition; re-issue the same call after a pause.
    internal const string LockedBy          = "LOCKED_BY";            // GameObject/file lock held by another agent
    internal const string OperationInProgress = "OPERATION_IN_PROGRESS"; // operation coordinator busy / mutex contention

    // Integrity guards (findings #5) — needs a state change, not a retry.
    internal const string SaveRefusedIntegrityHold = "SAVE_REFUSED_INTEGRITY_HOLD"; // reload hold active on this scene
    internal const string SaveRefusedCountDeficit  = "SAVE_REFUSED_COUNT_DEFICIT";  // memory<disk deficit over threshold
    internal const string ReloadRefusedSceneReferenced = "RELOAD_REFUSED_SCENE_REFERENCED"; // asset referenced by a loaded scene

    // Catch-all.
    internal const string CursorExpired     = "CURSOR_EXPIRED";       // continue-tool cursor gone (60s TTL) — re-run the original call
    internal const string InternalException = "INTERNAL_EXCEPTION";   // unhandled exception inside the handler

    private static readonly HashSet<string> Retryable = new( StringComparer.Ordinal )
    {
        LockedBy, OperationInProgress, CompileTimeout, PawnNotReady
    };

    /// <summary>True when blind re-issue of the same call is a sane next step (contention/timeout class).</summary>
    internal static bool IsRetryable( string code ) => code != null && Retryable.Contains( code );
}
