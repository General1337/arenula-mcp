using System;
using System.Text.Json;

namespace Arenula;

/// <summary>
/// continue tool: retrieve next pages of large paginated responses.
/// When any tool response includes a '_cursor' block with 'has_more: true',
/// use this tool with the cursor_id to get the next page.
/// </summary>
internal static class ContinueHandler
{
    internal static object Handle( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "next" => Next( args ),
                _ => HandlerBase.Error( $"Unknown action '{action}' for tool 'continue'.", action,
                    "Valid actions: next" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    private static object Next( JsonElement args )
    {
        var cursorId = HandlerBase.GetString( args, "cursor_id" );
        if ( string.IsNullOrEmpty( cursorId ) )
            return HandlerBase.Error( "Missing required 'cursor_id' parameter.", "next" );

        var result = CursorStore.GetNextPage( cursorId );
        if ( result == null )
            return HandlerBase.Error(
                $"Cursor '{cursorId}' not found or expired. Cursors expire after 60 seconds — re-run the original query.",
                "next" );

        return result;
    }
}
