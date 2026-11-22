using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Logging;

// The same scripted peer can intercept every destination (including a mistaken Foundry URI),
// or listen on a fresh loopback port to exercise the production redirect/proxy settings.
// Neither path forwards requests, opens credential stores, nor logs headers or payloads.
internal sealed class CopilotProxyFixture : HttpMessageHandler
{
    internal sealed record Call(string Method, Uri Uri, Dictionary<string, string> Headers, string Body);
    private readonly ConcurrentQueue<Call> calls = new();
    private readonly ConcurrentQueue<Func<CancellationToken, Task<HttpResponseMessage>>> replies = new();
    internal Call[] Calls => calls.ToArray();

    internal void Next(Func<CancellationToken, Task<HttpResponseMessage>> reply) => replies.Enqueue(reply);
    internal void Json(string json, HttpStatusCode status = HttpStatusCode.OK) => Next(_ => Task.FromResult(
        new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
    internal void Redirect(Uri location) => Next(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) {
        Headers = { Location = location }
    }));

    private Task<HttpResponseMessage> Dispatch(Call call, CancellationToken token)
    {
        calls.Enqueue(call);
        return replies.TryDequeue(out var reply) ? reply(token) :
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var headers = request.Headers.Concat(request.Content?.Headers.AsEnumerable() ?? [])
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        return await Dispatch(new(request.Method.Method, request.RequestUri!, headers,
            request.Content == null ? "" : await request.Content.ReadAsStringAsync(token)), token);
    }

    internal async Task<Listener> Listen()
    {
        // No appsettings, user secrets, environment configuration, DNS lookup or fixed port.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelCore().ConfigureKestrel(k => {
            k.Listen(IPAddress.Loopback, 0);
            k.Limits.MaxRequestBodySize = 1024 * 1024;
        });
        var app = builder.Build();
        app.Run(async context => {
            var request = context.Request;
            using var reader = new StreamReader(request.Body, leaveOpen: true);
            var call = new Call(request.Method, new Uri(request.GetEncodedUrl()),
                request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                await reader.ReadToEndAsync(context.RequestAborted));
            using var reply = await Dispatch(call, context.RequestAborted);
            context.Response.StatusCode = (int)reply.StatusCode;
            if (reply.Headers.Location is { } location) context.Response.Headers.Location = location.AbsoluteUri;
            context.Response.ContentType = reply.Content.Headers.ContentType?.ToString();
            await reply.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
        try { await app.StartAsync(); return new Listener(app); }
        catch { await app.DisposeAsync(); throw; }
    }

    internal sealed class Listener(WebApplication app) : IAsyncDisposable
    {
        internal string BaseUrl => app.Urls.Single().TrimEnd('/') + "/v1";
        public async ValueTask DisposeAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await app.StopAsync(deadline.Token); }
            finally { await app.DisposeAsync(); }
        }
    }
}
