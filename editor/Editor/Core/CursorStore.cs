using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Arenula;

/// <summary>
/// Server-side store for paginated result continuations.
/// When a response exceeds MaxResponseChars, the full JSON is stored here
/// and delivered in pages via the 'continue' tool.
/// Entries auto-expire after 60 seconds.
/// </summary>
internal static class CursorStore
{
    private static readonly ConcurrentDictionary<string, CursorEntry> _store = new();

    private const int ExpirySeconds = 60;
    private const int MaxEntries = 50;

    private sealed class CursorEntry
    {
        public string FullJson;
        public int Offset;
        public int PageSize;
        public DateTime CreatedAt;
    }

    /// <summary>
    /// Store a large JSON response and return the first page with cursor metadata.
    /// Called by HandlerBase.Success when response exceeds MaxResponseChars.
    /// </summary>
    internal static object StoreAndPage( string fullJson, int pageSize = 0 )
    {
        PurgeExpired();

        if ( pageSize <= 0 )
            pageSize = HandlerBase.MaxResponseChars - 200; // leave room for cursor metadata

        var cursorId = Guid.NewGuid().ToString( "N" ).Substring( 0, 12 );
        var totalPages = (int)Math.Ceiling( (double)fullJson.Length / pageSize );

        var entry = new CursorEntry
        {
            FullJson = fullJson,
            Offset = pageSize,
            PageSize = pageSize,
            CreatedAt = DateTime.UtcNow
        };

        _store[cursorId] = entry;

        // Return first page with cursor metadata
        var firstPage = fullJson.Substring( 0, Math.Min( pageSize, fullJson.Length ) );
        var hasMore = fullJson.Length > pageSize;

        var cursorMeta = new
        {
            _cursor = new
            {
                cursor_id = cursorId,
                page = 1,
                total_pages = totalPages,
                total_chars = fullJson.Length,
                has_more = hasMore
            }
        };

        var metaJson = System.Text.Json.JsonSerializer.Serialize( cursorMeta );
        var combined = firstPage + "\n" + metaJson;

        return new
        {
            content = new object[] { new { type = "text", text = combined } }
        };
    }

    /// <summary>
    /// Get the next page for a cursor. Returns null if cursor not found or expired.
    /// </summary>
    internal static object GetNextPage( string cursorId )
    {
        PurgeExpired();

        if ( !_store.TryGetValue( cursorId, out var entry ) )
            return null;

        if ( entry.Offset >= entry.FullJson.Length )
        {
            _store.TryRemove( cursorId, out _ );
            return HandlerBase.Text( "No more pages. Cursor complete." );
        }

        var remaining = entry.FullJson.Length - entry.Offset;
        var chunk = entry.FullJson.Substring( entry.Offset, Math.Min( entry.PageSize, remaining ) );
        entry.Offset += entry.PageSize;

        var currentPage = (int)Math.Ceiling( (double)entry.Offset / entry.PageSize );
        var totalPages = (int)Math.Ceiling( (double)entry.FullJson.Length / entry.PageSize );
        var hasMore = entry.Offset < entry.FullJson.Length;

        if ( !hasMore )
            _store.TryRemove( cursorId, out _ );

        var cursorMeta = new
        {
            _cursor = new
            {
                cursor_id = cursorId,
                page = currentPage,
                total_pages = totalPages,
                total_chars = entry.FullJson.Length,
                has_more = hasMore
            }
        };

        var metaJson = System.Text.Json.JsonSerializer.Serialize( cursorMeta );
        var combined = chunk + "\n" + metaJson;

        return new
        {
            content = new object[] { new { type = "text", text = combined } }
        };
    }

    /// <summary>
    /// Remove expired entries (older than 60 seconds).
    /// </summary>
    private static void PurgeExpired()
    {
        var cutoff = DateTime.UtcNow.AddSeconds( -ExpirySeconds );
        var toRemove = new List<string>();

        foreach ( var kvp in _store )
        {
            if ( kvp.Value.CreatedAt < cutoff )
                toRemove.Add( kvp.Key );
        }

        // Also enforce max entries limit
        if ( _store.Count > MaxEntries )
        {
            var oldest = new List<KeyValuePair<string, CursorEntry>>();
            foreach ( var kvp in _store )
                oldest.Add( kvp );

            oldest.Sort( ( a, b ) => a.Value.CreatedAt.CompareTo( b.Value.CreatedAt ) );

            for ( int i = 0; i < oldest.Count - MaxEntries; i++ )
                toRemove.Add( oldest[i].Key );
        }

        foreach ( var key in toRemove )
            _store.TryRemove( key, out _ );
    }
}
