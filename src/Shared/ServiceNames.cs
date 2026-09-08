namespace Shared;

/// <summary>
/// Well-known Aspire service names. The AppHost injects the resolved base URLs
/// as environment variables so the orchestrator can discover each domain agent.
/// </summary>
public static class ServiceNames
{
    public const string AssortmentAgent = "assortment-agent";
    public const string SupplyChainAgent = "supplychain-agent";
    public const string Orchestrator = "orchestrator";
    public const string Ollama = "ollama";

    /// <summary>The local model pulled by Ollama. Must support tool calling.</summary>
    public const string OllamaModel = "llama3.2";
}
