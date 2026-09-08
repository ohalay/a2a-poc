using A2A;
using A2A.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Orchestrator.Tests;

/// <summary>
/// Hosts a real in-process A2A agent on a loopback port so the orchestrator's
/// A2AClient exercises the actual HTTP/JSON-RPC transport.
/// </summary>
public sealed class TestAgentHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    public Uri BaseUrl { get; }
    public string CardName { get; }

    private TestAgentHost(WebApplication app, Uri baseUrl, string cardName)
    {
        _app = app;
        BaseUrl = baseUrl;
        CardName = cardName;
    }

    public static async Task<TestAgentHost> StartAsync(string cardName, string description, Func<string, string> reply)
    {
        var port = GetFreePort();
        var url = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(url);
        builder.Services.AddSingleton<ITaskStore, InMemoryTaskStore>();
        builder.Services.AddSingleton(reply);

        var card = new AgentCard
        {
            Name = cardName,
            Description = description,
            Version = "1.0.0",
            DefaultInputModes = ["text"],
            DefaultOutputModes = ["text"],
            Capabilities = new AgentCapabilities { Streaming = true },
            Skills = [new AgentSkill { Id = "do", Name = "Do", Description = description }],
        };
        builder.Services.AddA2AAgent<ReplyHandler>(card, o => o.AutoAppendHistory = true);

        var app = builder.Build();
        app.MapWellKnownAgentCard(card, "");
        app.MapA2A("/");
        await app.StartAsync();

        return new TestAgentHost(app, new Uri(url), cardName);
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private sealed class ReplyHandler(Func<string, string> reply) : IAgentHandler
    {
        public async Task ExecuteAsync(RequestContext ctx, AgentEventQueue q, CancellationToken ct)
        {
            var responder = new MessageResponder(q, ctx.ContextId);
            await responder.ReplyAsync(reply(ctx.UserText ?? string.Empty), ct);
        }

        public Task CancelAsync(RequestContext ctx, AgentEventQueue q, CancellationToken ct)
            => Task.CompletedTask;
    }
}
