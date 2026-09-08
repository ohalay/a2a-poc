using A2A;
using A2A.AspNetCore;
using Agent.SupplyChain;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// --- Local LLM (Ollama, OpenAI-compatible endpoint) -----------------------
var ollamaEndpoint = builder.Configuration["OLLAMA_ENDPOINT"]
    ?? Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT")
    ?? "http://localhost:11434";
var modelId = builder.Configuration["OLLAMA_MODEL"] ?? "llama3.2";

var openAiClient = new OpenAIClient(
    new ApiKeyCredential("ollama"),
    new OpenAIClientOptions { Endpoint = new Uri($"{ollamaEndpoint.TrimEnd('/')}/v1") });

builder.Services.AddChatClient(
        openAiClient.GetChatClient(modelId).AsIChatClient())
    .UseFunctionInvocation()
    .UseOpenTelemetry(configure: o => o.EnableSensitiveData = true);

// --- Domain tools ---------------------------------------------------------
builder.Services.AddSingleton<SupplyChainTools>();

// --- A2A agent handler ----------------------------------------------------
builder.Services.AddSingleton<ITaskStore, InMemoryTaskStore>();

// --- Agent card (public capability advertisement) -------------------------
var agentCard = new AgentCard
{
    Name = "SupplyChainAnalyst",
    Description = "Manages warehouse logistics, stock levels, inbound shipment delivery statuses, and DWH stock-velocity tracking.",
    Version = "1.0.0",
    DefaultInputModes = ["text"],
    DefaultOutputModes = ["text"],
    Capabilities = new AgentCapabilities { Streaming = true },
    Skills =
    [
        new AgentSkill
        {
            Id = "get-stock",
            Name = "GetStock",
            Description = "Get warehouse on-hand, in-transit, and weekly velocity for a product.",
            Tags = ["warehouse", "stock", "dwh"],
        },
        new AgentSkill
        {
            Id = "get-shipments",
            Name = "GetShipments",
            Description = "List inbound shipments and delivery status, optionally filtered by product.",
            Tags = ["logistics", "shipments"],
        },
    ],
};

builder.Services.AddA2AAgent<DomainAgentHandler>(agentCard, options => options.AutoAppendHistory = true);

var app = builder.Build();

app.MapWellKnownAgentCard(agentCard, "");
app.MapA2A("/");

app.Run();
