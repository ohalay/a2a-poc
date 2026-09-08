using Microsoft.Extensions.AI;
using OpenAI;
using Orchestrator;
using Orchestrator.Core;
using System.ClientModel;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var ollamaEndpoint = builder.Configuration["OLLAMA_ENDPOINT"]?? "http://localhost:11434";
var modelId = builder.Configuration["OLLAMA_MODEL"] ?? "llama3.2";

var openAiClient = new OpenAIClient(
    new ApiKeyCredential("ollama"),
    new OpenAIClientOptions { Endpoint = new Uri($"{ollamaEndpoint.TrimEnd('/')}/v1") });

builder.Services.AddChatClient(openAiClient.GetChatClient(modelId)
    .AsIChatClient())
    .UseFunctionInvocation()
    .UseOpenTelemetry(configure: o => o.EnableSensitiveData = true);

builder.Services.AddHttpClient();

// Dedicated client for talking to remote A2A agents at their concrete injected
// URLs. It deliberately does NOT use service discovery or the standard resilience
// handler (whose 10s attempt timeout was aborting agent-card probes). A longer
// timeout tolerates a cold agent whose first request also warms up the LLM.
builder.Services.AddHttpClient(AgentRegistry.HttpClientName, c =>
{
    c.Timeout = TimeSpan.FromMinutes(5);
})
// ConfigureHttpClientDefaults (in ServiceDefaults) applies the standard resilience
// handler to every client, including this one; its 10s attempt timeout aborts
// agent-card probes. Strip it so this client honors the timeout set above.
.RemoveAllResilienceHandlers();

builder.Services.AddSingleton<ChatStore>();
builder.Services.AddSingleton<OrchestrationService>();

builder.Services.AddSingleton<AgentRegistry>();

var app = builder.Build();

app.MapPost("/api/chat", async (ChatRequest request, ChatStore store, OrchestrationService orch, CancellationToken ct) =>
{
    var threadId = string.IsNullOrWhiteSpace(request.ThreadId)
        ? Guid.NewGuid().ToString("N")
        : request.ThreadId;

    var thread = store.GetOrCreate(threadId);

    // HandleAsync appends the user message to the thread and reads prior turns
    // as history, so the endpoint only needs to persist the assistant reply.
    var answer = await orch.HandleAsync(thread, request.Message, ct);
    thread.Turns.Add(new ChatTurn("assistant", answer, DateTimeOffset.UtcNow));

    return Results.Ok(new ChatResponse(threadId, answer));
});

app.MapGet("/api/agents", async (AgentRegistry registry) =>
{
    var agenst = await registry.GetAgents();
    return Results.Ok(agenst.Select(a => new
    {
        a.Card.DocumentationUrl,
        Name = a.Card.Name,
        Description = a.Card?.Description,
        Skills = a.Card?.Skills?.Select(s => s.Name),
    }));
});

app.MapGet("/", () => Results.Content(HtmlUi.Page, "text/html"));

app.Run();

public record ChatRequest(string? ThreadId, string Message);
public record ChatResponse(string ThreadId, string Answer);
