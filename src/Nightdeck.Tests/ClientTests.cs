using System.Net;
using System.Text;
using Nightdeck.Core;

namespace Nightdeck.Tests;

static class ClientTests
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    static async Task RunAsync()
    {
        using var server = new Tiny();
        server.Start();
        await using var client = new BluOsClient("127.0.0.1", server.Port);

        var status = await client.GetStatusAsync();
        TestHarness.Equal(status.Title, "Hello", "client status title");
        TestHarness.Equal(status.Etag, "etag1", "client status etag");
        TestHarness.Check(server.LastPath == "/Status", "status path");

        server.Hold = true;
        using var cts = new CancellationTokenSource();
        var started = DateTime.UtcNow;
        var poll = client.PollStatusAsync("etag1", 30, cts.Token);
        var until = DateTime.UtcNow.AddSeconds(2);
        while (!server.LastPath.Contains("timeout=30", StringComparison.Ordinal) && DateTime.UtcNow < until)
            await Task.Delay(20);
        cts.Cancel();
        bool cancelled = false;
        try { await poll; }
        catch (OperationCanceledException) { cancelled = true; }
        TestHarness.Check(cancelled, "poll cancels");
        TestHarness.Check((DateTime.UtcNow - started).TotalSeconds < 2, "poll cancel is prompt");
        TestHarness.Check(server.LastPath.Contains("timeout=30", StringComparison.Ordinal), "poll timeout query");
        TestHarness.Check(server.LastPath.Contains("etag=etag1", StringComparison.Ordinal), "poll etag query");

        server.Hold = false;
        await client.SendPathAsync("/Add?file=a%20b", CancellationToken.None);
        TestHarness.Equal(server.LastPath, "/Add?file=a%20b", "path is not double-encoded");

        server.RawBody = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="ISO-8859-1"?>
            <status etag="e2"><state>play</state><title1>Skuggsjá</title1><song>0</song></status>
            """);
        server.ContentType = "text/xml; charset=iso-8859-1";
        var named = await client.GetStatusAsync();
        TestHarness.Equal(named.Title, "Skuggsjá", "http body is utf-8 despite charset");

        byte[] errBody = Encoding.UTF8.GetBytes("""<?xml version="1.0"?><error><detail>stopped badly</detail></error>""");
        server.RawBody = new byte[errBody.Length + 3];
        server.RawBody[0] = 0xEF; server.RawBody[1] = 0xBB; server.RawBody[2] = 0xBF;
        errBody.CopyTo(server.RawBody, 3);
        server.ContentType = "text/xml";
        bool bomError = false;
        try { await client.SendPathAsync("/Stop", CancellationToken.None); }
        catch (BluOsException ex) { bomError = ex.Message == "stopped badly"; }
        TestHarness.Check(bomError, "http bom error is not success");

        server.StatusCode = 500;
        server.RawBody = Encoding.UTF8.GetBytes("""<error><detail>nope</detail></error>""");
        bool httpDetail = false;
        try { await client.GetStatusAsync(); }
        catch (BluOsException ex) { httpDetail = ex.Message == "nope"; }
        TestHarness.Check(httpDetail, "http error prefers detail");

        server.RawBody = "not xml"u8.ToArray();
        bool httpPlain = false;
        try { await client.GetStatusAsync(); }
        catch (BluOsException ex) { httpPlain = ex.Message == "HTTP Error 500"; }
        TestHarness.Check(httpPlain, "http error without detail");
        server.StatusCode = 200;
        server.RawBody = null;
    }

    sealed class Tiny : IDisposable
    {
        readonly HttpListener _listener = new();
        public int Port { get; }
        public string LastPath { get; private set; } = "";
        public volatile bool Hold;
        public byte[]? RawBody;
        public int StatusCode = 200;
        public string ContentType = "text/xml";

        public Tiny()
        {
            var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            Port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();
            _listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
        }

        public void Start()
        {
            _listener.Start();
            _ = Task.Run(Loop);
        }

        void Reply(HttpListenerContext ctx, string path)
        {
            if (Hold)
            {
                try { Task.Delay(30000).Wait(); } catch { }
                try { ctx.Response.Abort(); } catch { }
                return;
            }
            byte[] body = RawBody ?? (path.StartsWith("/Add", StringComparison.Ordinal) || path.StartsWith("/Stop", StringComparison.Ordinal)
                ? """<ok/>"""u8.ToArray()
                : """<?xml version="1.0"?><status etag="etag1"><state>play</state><title1>Hello</title1><song>0</song></status>"""u8.ToArray());
            ctx.Response.StatusCode = StatusCode;
            ctx.Response.ContentType = ContentType;
            ctx.Response.ContentLength64 = body.Length;
            try
            {
                ctx.Response.OutputStream.Write(body, 0, body.Length);
                ctx.Response.Close();
            }
            catch { }
        }

        async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }
                string path = ctx.Request.Url?.PathAndQuery ?? "";
                LastPath = path;
                _ = Task.Run(() => Reply(ctx, path));
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            _listener.Close();
        }
    }
}
