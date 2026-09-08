using A2A;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Orchestrator.Core;

public class RemoteAgent(AgentCard Card, A2AClient Client);

/// <summary>
/// Holds the set of remote domain agents. Resolves each agent's public
/// AgentCard (capabilities) lazily so a temporarily-down agent doesn't crash
/// the orchestrator at startup.
/// </summary>
public sealed class AgentRegistry(IHttpClientFactory httpClientFactory, ILogger<AgentRegistry> logger, IConfiguration configuration)
{
    // Named client that bypasses the global service-discovery + resilience
    // defaults (those are meant for logical service names and impose a 10s
    // attempt timeout that kills first-contact card probes). Registered in
    // Program.cs / tests as "agent-direct".
    public const string HttpClientName = "agent-direct";

    private readonly List<RemoteAgent> agents = [];

    /// <summary>Resolves any not-yet-resolved agent cards. Safe to call repeatedly.</summary>
    public async Task<IReadOnlyCollection<RemoteAgent> Register(CancellationToken ct = default)
    {
        foreach (var agentUrl in configuration.GetValue<string[]>("AGENTS") ?? [])
        {
            try
            {
                var http = httpClientFactory.CreateClient(HttpClientName);
                var resolver = new A2ACardResolver(new Uri(agentUrl), http);
                var card = await resolver.GetAgentCardAsync(ct);
                var client = new A2AClient(new Uri(agentUrl), httpClientFactory.CreateClient(HttpClientName));

                agents.Add(new RemoteAgent(card, client));

                logger.LogInformation("Resolved agent {Name} at {Url}", card.Name, agentUrl);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not resolve agent at {Url} yet", agentUrl);
            }
        }

        return agents;
    }

    public async Task<IReadOnlyCollection<RemoteAgent>> GetAgents(CancellationToken ct = default)
    {
        if(agents is not []) return agents;

        return await Register(ct);
    }
}
