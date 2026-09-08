using A2A;
using A2A.AspNetCore;
using Agent.Assortment;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// --- Local LLM (Ollama, OpenAI-compatible endpoint) -----------------------
// Aspire injects the Ollama connection string / endpoint. Fall back to the
// default local Ollama port for standalone runs.
var ollamaEndpoint = builder.Configuration["OLLAMA_ENDPOINT"]
    ?? Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT")
    ?? "http://localhost:11434";
var modelId = builder.Configuration["OLLAMA_MODEL"] ?? "llama3.2";

var openAiClient = new OpenAIClient(
    new ApiKeyCredential("ollama"), // Ollama ignores the key
    new OpenAIClientOptions { Endpoint = new Uri($"{ollamaEndpoint.TrimEnd('/')}/v1") });

builder.Services.AddChatClient(
        openAiClient.GetChatClient(modelId).AsIChatClient())
    .UseFunctionInvocation() // enables automatic tool-call loop
    .UseOpenTelemetry(configure: o => o.EnableSensitiveData = true);

// --- Domain tools ---------------------------------------------------------
builder.Services.AddSingleton<AssortmentTools>();

// --- A2A agent handler ----------------------------------------------------
builder.Services.AddSingleton<ITaskStore, InMemoryTaskStore>();

// --- Agent card (public capability advertisement) -------------------------
var agentCard = new AgentCard
{
    Name = "AssortmentSpecialist",
    Description = "Handles shop inventories, product categorizations, catalogs, and store assortments across all shops.",
    Version = "1.0.0",
    DefaultInputModes = ["text"],
    DefaultOutputModes = ["text"],
    Capabilities = new AgentCapabilities { Streaming = true },
    Skills =
    [
        new AgentSkill
        {
            Id = "get-product",
            Name = "GetProduct",
            Description = "Look up a product's SKU, category, active status, and store coverage by name.",
            Tags = ["catalog", "assortment", "product"],
        },
        new AgentSkill
        {
            Id = "get-active-catalog",
            Name = "GetActiveCatalog",
            Description = "List all products currently active in the store assortment.",
            Tags = ["catalog", "assortment"],
        },
    ],
};

builder.Services.AddA2AAgent<DomainAgentHandler>(agentCard, options => options.AutoAppendHistory = true);

var app = builder.Build();

app.MapWellKnownAgentCard(agentCard, "");
app.MapA2A("/");

app.Run();
