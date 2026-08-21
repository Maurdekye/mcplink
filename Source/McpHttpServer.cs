using System.Net;
using System.Text;

namespace McpLink;

/// <summary>
/// Minimal MCP streamable-HTTP endpoint on HttpListener. Zero external dependencies by design:
/// pulling the official MCP SDK (and its ASP.NET Core hosting) into the game process would
/// invite dependency conflicts with the engine. The protocol surface a tools-only server needs
/// (initialize / tools/list / tools/call / ping) is small enough to speak directly.
/// Responses are plain JSON (no SSE stream); GET returns 405, which the spec permits.
/// </summary>
internal sealed class McpHttpServer
{
    private readonly HttpListener _listener = new();
    private readonly McpDispatcher _dispatcher = new();

    public McpHttpServer(int port)
    {
        // localhost prefix requires no URL ACL / admin rights and is unreachable from the network
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        Task.Run(AcceptLoop);
    }

    public void Stop()
    {
        try { _listener.Stop(); } catch { /* shutting down */ }
    }

    private async Task AcceptLoop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch when (!_listener.IsListening)
            {
                break;
            }
            catch (Exception e)
            {
                McpLinkMod.LogError($"[McpLink] accept error: {e.Message}");
                continue;
            }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var request = ctx.Request;
        var response = ctx.Response;
        try
        {
            string path = (request.Url?.AbsolutePath ?? "").TrimEnd('/');
            if (path != "/mcp" && path != "")
            {
                response.StatusCode = 404;
                return;
            }

            switch (request.HttpMethod)
            {
                case "POST":
                {
                    string body;
                    // Always UTF-8: JSON is UTF-8 by spec (RFC 8259), and HttpListener's
                    // ContentEncoding is NEVER null — with no charset in Content-Type it returns
                    // the ANSI codepage, which mojibake'd every non-ASCII request body.
                    using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                        body = await reader.ReadToEndAsync().ConfigureAwait(false);

                    var (json, isInitialize) = _dispatcher.HandlePost(body);
                    if (json == null)
                    {
                        response.StatusCode = 202; // notification(s) only — no body
                        return;
                    }
                    if (isInitialize)
                        response.Headers["Mcp-Session-Id"] = _dispatcher.SessionId;

                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    response.ContentType = "application/json";
                    response.ContentLength64 = bytes.Length;
                    await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                    return;
                }
                case "GET": // no server-initiated stream offered
                    response.StatusCode = 405;
                    response.Headers["Allow"] = "POST, DELETE";
                    return;
                case "DELETE": // client ended the session; stateless server, nothing to tear down
                    response.StatusCode = 200;
                    return;
                case "OPTIONS":
                    response.StatusCode = 204;
                    return;
                default:
                    response.StatusCode = 405;
                    return;
            }
        }
        catch (Exception e)
        {
            McpLinkMod.LogError($"[McpLink] request error: {e}");
            try
            {
                response.StatusCode = 500;
                byte[] bytes = Encoding.UTF8.GetBytes(e.Message);
                await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            }
            catch { /* response already gone */ }
        }
        finally
        {
            try { response.Close(); } catch { /* already closed */ }
        }
    }
}
