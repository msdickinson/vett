using System.Net;
using System.Text;

namespace Vett.Live;

/// <summary>
/// Minimal Server-Sent-Events endpoint for live VETT events. Started only when
/// `vett run --live-port N` is passed. Uses HttpListener so we don't drag in
/// the ASP.NET Core stack — vett stays a small console app.
///
/// Endpoints:
///   GET /events     — text/event-stream of every Event the run emits, one JSON
///                     object per "data:" frame. Browser-native via EventSource;
///                     curl-friendly via `curl -N`.
///   GET /healthz    — plain "ok\n", for liveness probes.
///   GET /           — small HTML page that connects to /events and dumps lines
///                     to the console (handy if you don't want to wire AI-
///                     Timeline yet — just open http://runner:5151/ in a browser).
///
/// One server per `vett run`. Lifetime is bounded by RunAsync.
/// </summary>
public sealed class LiveServer : IDisposable
{
    private readonly LiveSink _sink;
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public LiveServer(LiveSink sink, int port)
    {
        _sink = sink;
        _listener = new HttpListener();
        // Bind to all interfaces. NOTE: HttpListener on Linux requires no sudo
        // for ports >= 1024.
        _listener.Prefixes.Add($"http://+:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (HttpListenerException) { break; }   // listener stopped
            catch (ObjectDisposedException) { break; }

            // Fan out per request — never block the accept loop.
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            switch (ctx.Request.Url?.AbsolutePath)
            {
                case "/events":   await HandleEventsAsync(ctx); break;
                case "/healthz":  await WritePlainAsync(ctx, "ok\n"); break;
                case "/":         await WriteHtmlAsync(ctx, IndexHtml); break;
                default:          ctx.Response.StatusCode = 404; ctx.Response.Close(); break;
            }
        }
        catch (Exception ex)
        {
            // Best-effort 500 — request may already be torn down. Surface
            // the error to stderr so a 500 in the wild isn't a black box.
            Console.Error.WriteLine($"[live] request handler crashed: {ex.GetType().Name}: {ex.Message}");
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); }
            catch (Exception closeEx) { Console.Error.WriteLine($"[live] also failed to send 500: {closeEx.Message}"); }
        }
    }

    private async Task HandleEventsAsync(HttpListenerContext ctx)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";  // disable nginx buffering if proxied
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.SendChunked = true;

        var reader = _sink.Subscribe();
        var clientGone = ctx.Response.OutputStream;

        // SSE comment as a keepalive so flaky middleboxes don't drop us.
        var keepAlive = new Timer(async _ =>
        {
            try { await clientGone.WriteAsync(Encoding.UTF8.GetBytes(": ka\n\n")); await clientGone.FlushAsync(); }
            catch { /* will be caught by the read loop */ }
        }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

        try
        {
            await foreach (var line in reader.ReadAllAsync(_cts.Token))
            {
                var bytes = Encoding.UTF8.GetBytes("data: " + line + "\n\n");
                await clientGone.WriteAsync(bytes);
                await clientGone.FlushAsync();
            }
        }
        catch { /* client disconnected; that's fine */ }
        finally
        {
            keepAlive.Dispose();
            _sink.Unsubscribe(reader);
            try { ctx.Response.Close(); } catch { }
        }
    }

    private static async Task WritePlainAsync(HttpListenerContext ctx, string body)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/plain";
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private static async Task WriteHtmlAsync(HttpListenerContext ctx, string body)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/html";
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        // Listener teardown may throw if it's already stopped (HttpListener
        // double-close on Linux) — fine, we're disposing. Log to stderr if
        // the failure isn't the expected "already stopped" type so a real
        // crash during shutdown isn't silently lost.
        try { _listener.Stop(); }
        catch (HttpListenerException) { /* expected on shutdown race */ }
        catch (ObjectDisposedException) { /* expected on shutdown race */ }
        catch (Exception ex) { Console.Error.WriteLine($"[live] listener.Stop() failed: {ex.Message}"); }

        try { _listener.Close(); }
        catch (HttpListenerException) { /* expected */ }
        catch (ObjectDisposedException) { /* expected */ }
        catch (Exception ex) { Console.Error.WriteLine($"[live] listener.Close() failed: {ex.Message}"); }

        try { _loop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { /* loop's own exceptions; not actionable here */ }
        catch (Exception ex) { Console.Error.WriteLine($"[live] accept loop wait failed: {ex.Message}"); }

        _cts.Dispose();
    }

    // Tiny default page so a human can sanity-check without writing client code.
    // Lives here as a single string so we don't add static-assets infrastructure.
    private const string IndexHtml = """
<!doctype html>
<html><head><meta charset=utf-8><title>vett live</title>
<style>body{font-family:ui-monospace,monospace;background:#111;color:#ddd;margin:0;padding:1rem}
.evt{margin:.15rem 0;padding:.2rem .4rem;border-left:3px solid #555;background:#1a1a1a;font-size:12px}
.t{color:#7af}.iid{color:#fc7}.ts{color:#666}</style></head>
<body><h2 style="color:#7af">vett live · <span id=count>0</span> events</h2>
<div id=log></div>
<script>
const log=document.getElementById('log'),count=document.getElementById('count');
let n=0;const es=new EventSource('/events');
es.onmessage=e=>{
  const o=JSON.parse(e.data);
  const div=document.createElement('div');div.className='evt';
  div.innerHTML=`<span class=ts>${o.ts.slice(11,19)}</span> <span class=t>${o.type}</span>`+
    (o.instance_id?` <span class=iid>${o.instance_id}</span>`:'')+
    ` <span style=color:#777>${JSON.stringify(o.data).slice(0,200)}</span>`;
  log.prepend(div);count.textContent=++n;
  while(log.children.length>500)log.removeChild(log.lastChild);
};
es.onerror=()=>{count.textContent+=' (disconnected)';};
</script></body></html>
""";
}
