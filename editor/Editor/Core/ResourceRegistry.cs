namespace Arenula;

/// <summary>
/// MCP resource definitions for project context.
/// Resources provide automatic context injection into AI conversations.
/// </summary>
internal static class ResourceRegistry
{
    internal static object[] All => new object[]
    {
        ProjectContext,
        ProjectConventions
    };

    internal static readonly object ProjectContext = new
    {
        uri = "arenula://project/context",
        name = "Project Context",
        description = "Project name, custom components, available prefabs, compilation state, and active scene summary.",
        mimeType = "application/json"
    };

    internal static readonly object ProjectConventions = new
    {
        uri = "arenula://project/conventions",
        name = "Project Conventions (CLAUDE.md)",
        description = "Project-specific AI instructions and conventions from CLAUDE.md.",
        mimeType = "text/markdown"
    };
}
