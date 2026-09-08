using Microsoft.Extensions.Logging.Abstractions;
using Orchestrator.Core;
using Xunit;

namespace Orchestrator.Tests;

public class OrchestrationServiceTests
{
    private static AgentRegistry BuildRegistry(params TestAgentHost[] hosts)
    {
        var httpFactory = new SimpleHttpClientFactory();
        var registry = new AgentRegistry(httpFactory, NullLogger<AgentRegistry>.Instance);
        foreach (var h in hosts)
        {
            registry.Register(h.CardName, h.BaseUrl);
        }
        return registry;
    }

    [Fact]
    public async Task Routes_To_Single_Agent_And_Returns_Its_Output()
    {
        await using var assortment = await TestAgentHost.StartAsync(
            "AssortmentSpecialist", "product catalog",
            userText => $"CATALOG: {userText}");

        var registry = BuildRegistry(assortment);

        // LLM calls the AssortmentSpecialist tool, then aggregates its output.
        var chat = new StubChatClient(["AssortmentSpecialist"]);

        var orch = new OrchestrationService(registry, chat, NullLogger<OrchestrationService>.Instance);
        var thread = new ChatThread();

        var answer = await orch.HandleAsync(thread, "list winter coats", CancellationToken.None);

        Assert.Contains("CATALOG:", answer);
        Assert.Contains("list winter coats", answer);
    }

    [Fact]
    public async Task Routes_To_Multiple_Agents_And_Aggregates_Both()
    {
        await using var assortment = await TestAgentHost.StartAsync(
            "AssortmentSpecialist", "product catalog",
            _ => "ASSORTMENT_RESULT");
        await using var supply = await TestAgentHost.StartAsync(
            "SupplyChainAnalyst", "warehouse stock",
            _ => "SUPPLY_RESULT");

        var registry = BuildRegistry(assortment, supply);

        var chat = new StubChatClient(["AssortmentSpecialist", "SupplyChainAnalyst"]);

        var orch = new OrchestrationService(registry, chat, NullLogger<OrchestrationService>.Instance);
        var thread = new ChatThread();

        var answer = await orch.HandleAsync(thread, "is stock aligned with catalog?", CancellationToken.None);

        Assert.Contains("ASSORTMENT_RESULT", answer);
        Assert.Contains("SUPPLY_RESULT", answer);
    }

    [Fact]
    public async Task No_Match_Answers_Directly()
    {
        await using var assortment = await TestAgentHost.StartAsync(
            "AssortmentSpecialist", "product catalog",
            _ => "SHOULD_NOT_BE_CALLED");

        var registry = BuildRegistry(assortment);

        var chat = new StubChatClient([]); // no tools called -> direct answer

        var orch = new OrchestrationService(registry, chat, NullLogger<OrchestrationService>.Instance);
        var thread = new ChatThread();

        var answer = await orch.HandleAsync(thread, "what is the weather?", CancellationToken.None);

        Assert.Equal("DIRECT_ANSWER", answer);
    }

    private sealed class SimpleHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
