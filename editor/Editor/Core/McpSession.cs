using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Arenula;

/// <summary>Per-connection SSE session state.</summary>
public class McpSession
{
    public string SessionId { get; set; }
    public HttpListenerResponse SseResponse { get; set; }
    public TaskCompletionSource<bool> Tcs { get; set; } = new();
    public bool Initialized { get; set; }

    /// <summary>
    /// Serializes all writes to <see cref="SseResponse"/>'s output stream. Multiple RPC
    /// completions (compile/cloud/schema finish off-thread) plus the keep-alive ping loop
    /// can otherwise interleave byte writes on the same stream and corrupt the SSE framing.
    /// </summary>
    public SemaphoreSlim WriteLock { get; } = new( 1, 1 );
}
