using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sandbox;

namespace Arenula;

/// <summary>
/// Canonical MCP tool result. Structured values carry the same JSON twice:
/// <c>structuredContent</c> for schema-aware clients and a text content block
/// for existing Arenula clients that parse <c>content[0].text</c>.
/// </summary>
/// <remarks>
/// Result shaping and compact scene-object references are adapted from the
/// Facepunch sbox-public MCP implementation. See Libraries/arenula_mcp/LICENSE.
/// </remarks>
internal sealed class McpResult
{
    [JsonPropertyName( "content" )]
    public IReadOnlyList<object> Content { get; }

    [JsonPropertyName( "structuredContent" )]
    public JsonElement? StructuredContent { get; }

    [JsonPropertyName( "isError" )]
    public bool? IsError { get; }

    private McpResult( IReadOnlyList<object> content, JsonElement? structuredContent, bool? isError )
    {
        Content = content;
        StructuredContent = structuredContent;
        IsError = isError;
    }

    internal static McpResult Structured( object value, out string json, bool isError = false )
    {
        json = JsonSerializer.Serialize( value, ResultJsonOptions );
        using var document = JsonDocument.Parse( json );
        var structured = document.RootElement.Clone();
        return new McpResult(
            new object[] { new { type = "text", text = json } },
            structured,
            isError ? true : null );
    }

    internal static bool IsErrorResult( object result )
    {
        if ( result is McpResult typed )
            return typed.IsError == true;
        if ( result == null )
            return false;

        try
        {
            var json = JsonSerializer.Serialize( result, ArenulaMcpServer.JsonOptions );
            using var document = JsonDocument.Parse( json );
            return document.RootElement.TryGetProperty( "isError", out var flag )
                && flag.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions ResultJsonOptions = CreateResultJsonOptions();

    private static JsonSerializerOptions CreateResultJsonOptions()
    {
        var options = new JsonSerializerOptions( ArenulaMcpServer.JsonOptions );
        options.Converters.Insert( 0, new GameObjectReferenceConverter() );
        options.Converters.Insert( 0, new ComponentReferenceConverter() );
        return options;
    }

    private sealed class GameObjectReferenceConverter : JsonConverter<GameObject>
    {
        public override bool CanConvert( Type typeToConvert )
            => typeof( GameObject ).IsAssignableFrom( typeToConvert );

        public override GameObject Read( ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options )
            => throw new NotSupportedException();

        public override void Write( Utf8JsonWriter writer, GameObject gameObject, JsonSerializerOptions options )
        {
            if ( gameObject == null || !gameObject.IsValid() )
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStartObject();
            writer.WriteString( "type", gameObject.GetType().Name );
            writer.WriteString( "id", gameObject.Id.ToString() );
            writer.WriteString( "name", gameObject.Name );
            writer.WriteEndObject();
        }
    }

    private sealed class ComponentReferenceConverter : JsonConverter<Component>
    {
        public override bool CanConvert( Type typeToConvert )
            => typeof( Component ).IsAssignableFrom( typeToConvert );

        public override Component Read( ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options )
            => throw new NotSupportedException();

        public override void Write( Utf8JsonWriter writer, Component component, JsonSerializerOptions options )
        {
            if ( component == null || !component.IsValid() )
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStartObject();
            writer.WriteString( "type", component.GetType().Name );
            writer.WriteString( "id", component.Id.ToString() );
            writer.WriteString( "gameObject", component.GameObject.Id.ToString() );
            writer.WriteEndObject();
        }
    }
}
