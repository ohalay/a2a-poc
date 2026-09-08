using Shared;

var builder = DistributedApplication.CreateBuilder(args);

// --- Local LLM: externally managed Ollama ---------------------------------
// Ollama is NOT started by Aspire. Run it yourself (e.g. an external container)
// and point at it via the single OLLAMA_ENDPOINT env var on the AppHost, which
// is propagated to all three services below. OLLAMA_MODEL is likewise optional.
var ollamaEndpoint = builder.Configuration["OLLAMA_ENDPOINT"]
    ?? Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT")
    ?? "http://localhost:30212";
var ollamaModel = builder.Configuration["OLLAMA_MODEL"]
    ?? Environment.GetEnvironmentVariable("OLLAMA_MODEL")
    ?? ServiceNames.OllamaModel;

// --- Domain agents (A2A servers) ------------------------------------------
// Each agent's endpoint/port comes from its launchSettings.json (Properties/).
// Aspire allocates a free port, injects ASPNETCORE_URLS, and reverse-proxies to
// the app, so there are no port collisions between the three services.
// The A2A agent card is served at /.well-known/agent-card.json. We make that
// the default dashboard link for each agent and probe it as the health check,
// so "healthy" means the agent is actually advertising its capabilities.
const string AgentCardPath = "/.well-known/agent-card.json";

var assortment = builder.AddProject<Projects.Agent_Assortment>(ServiceNames.AssortmentAgent)
    .WithEnvironment("OLLAMA_ENDPOINT", ollamaEndpoint)
    .WithEnvironment("OLLAMA_MODEL", ollamaModel)
    .WithUrlForEndpoint("http", url =>
    {
        url.Url = AgentCardPath;
        url.DisplayText = "Agent card";
    });

var supplyChain = builder.AddProject<Projects.Agent_SupplyChain>(ServiceNames.SupplyChainAgent)
    .WithEnvironment("OLLAMA_ENDPOINT", ollamaEndpoint)
    .WithEnvironment("OLLAMA_MODEL", ollamaModel)
    .WithUrlForEndpoint("http", url =>
    {
        url.Url = AgentCardPath;
        url.DisplayText = "Agent card";
    });

// --- Orchestrator + aggregator + chat UI ----------------------------------
builder.AddProject<Projects.Orchestrator>(ServiceNames.Orchestrator)
    .WithEnvironment("OLLAMA_ENDPOINT", ollamaEndpoint)
    .WithEnvironment("OLLAMA_MODEL", ollamaModel)
    // Inject the resolved agent base URLs so the orchestrator can discover them.
    .WithEnvironment("AGENTS__0", assortment.GetEndpoint("http"))
    .WithEnvironment("AGENTS__1", supplyChain.GetEndpoint("http"))
    // Don't start the orchestrator until both agents are up and their card
    // health check passes, so first-contact discovery doesn't race a cold agent.
    .WaitFor(assortment)
    .WaitFor(supplyChain)
    .WithExternalHttpEndpoints();

builder.Build().Run();
