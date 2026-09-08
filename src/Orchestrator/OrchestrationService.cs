using System.ComponentModel;
using System.Diagnostics;
using A2A;
using Microsoft.Extensions.AI;

namespace Orchestrator;

/// <summary>
/// Orchestrates a request by exposing every available remote A2A agent as a
/// tool on one chat client. A single function-calling LLM loop then decides
/// which agents to invoke, calls them (concurrently, on demand), and synthesizes
/// the results into one answer — no separate route / dispatch / aggregate phases.
/// </summary>
public sealed class OrchestrationService(
    AgentRegistry registry,
    IChatClient chatClient,
    ILogger<OrchestrationService> logger)
{
    // ActivitySource name is picked up by ServiceDefaults (AddSource("A2A*")),
    // so these spans surface as traces in the Aspire dashboard.
    public static readonly ActivitySource ActivitySource = new("A2A.Orchestrator");

    private const string SystemPrompt =
        "You are the master commerce orchestrator coordinating specialist agents. " +
        "Each agent tool answers questions for one domain. To answer the user, identify EVERY " +
        "domain that is relevant and call each of those agent tools — if the question spans stock " +
        "AND catalog, you MUST call both the supply-chain agent and the assortment agent. " +
        "When calling a tool, pass the full natural-language question or sub-task as the 'request' " +
        "argument (e.g. 'What is the warehouse stock for winter coat?'). Never pass tool or skill " +
        "names as the request. " +
        "After the tools return, base your answer ONLY on their returned data — never invent prices, " +
        "quantities, or facts. Combine the outputs into one cohesive plain-text answer. " +
        "Do not include HTML or markup. If no tool is relevant, answer directly. " +
        "Never say 'agent X said' — just give the merged answer.";

    public async Task<string> HandleAsync(ChatThread thread, string userMessage, CancellationToken ct)
    {
        var agents = await registry.GetAgents(ct);

        var tools = agents
            .Select(ToTool)
            .Cast<AITool>()
            .ToList();

        var messages = new List<ChatMessage> { new(ChatRole.System, SystemPrompt) };
        // Make "the orchestrator works with history" visible as its own span.
        using (var historyActivity = ActivitySource.StartActivity("orchestrate.history", ActivityKind.Internal))
        {
            var priorTurns = thread.Turns.TakeLast(10).ToList();
            foreach (var turn in priorTurns)
            {
                var role = turn.Role == "assistant" ? ChatRole.Assistant : ChatRole.User;
                messages.Add(new ChatMessage(role, turn.Content));
            }
            historyActivity?.SetTag("a2a.history.total_turns", thread.Turns.Count);
            historyActivity?.SetTag("a2a.history.replayed_turns", priorTurns.Count);
        }
        messages.Add(new ChatMessage(ChatRole.User, userMessage));
        thread.Turns.Add(new ChatTurn("user", userMessage, DateTimeOffset.UtcNow));

        // Drive the tool-call loop with FunctionInvokingChatClient, then wrap the
        // WHOLE loop with OpenTelemetry. Order matters: the first Use* in the
        // builder chain is the OUTERMOST decorator, so putting UseOpenTelemetry
        // first means it observes each tool invocation (the "tool call" spans) as
        // well as the LLM "chat" spans. Wrapping a bare FunctionInvokingChatClient
        // WITHOUT this OTel layer (the previous behavior) is exactly why the
        // tool-call spans never showed up in the dashboard.
        //
        // Allow enough iterations for the model to call several agents in
        // sequence, and allow concurrent invocation so multiple agent calls in
        // one turn run in parallel.
        using var client = new FunctionInvokingChatClient(chatClient)
        {
            MaximumIterationsPerRequest = 10,
            AllowConcurrentInvocation = true,
        }
        .AsBuilder()
        .UseOpenTelemetry(configure: o => o.EnableSensitiveData = true)
        .Build();

        var response = await client.GetResponseAsync(
            messages,
            new ChatOptions { Tools = tools, AllowMultipleToolCalls = true },
            ct);

        return response.Text;
    }

    /// <summary>Wraps a remote A2A agent as a callable tool for the orchestrator LLM.</summary>
    private AIFunction ToTool(RemoteAgent agent)
    {
        var skills = string.Join(", ", agent.Card!.Skills?.Select(s => s.Name) ?? []);

        // The 'request' parameter is described so the model passes a full
        // natural-language sub-task to the agent, not a list of tool/skill names.
        var dispatch = async (
            [Description("The full natural-language question or task to send to this agent")]string request,
            CancellationToken ct) =>
        {
            logger.LogInformation("Dispatching A2A task to {Agent}: {Request}", agent.Card.Name, request);

            try
            {
                var response = await agent.Client!.SendMessageAsync(request, Role.User, cancellationToken: ct);
                var text = ExtractText(response);
                return text;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Dispatch to {Agent} failed", agent.Card.Name);
                return $"(agent unavailable: {ex.Message})";
            }
        };

        return AIFunctionFactory.Create(
            dispatch,
            agent.Card.Name,
            $"Ask the {agent.Card.Name} specialist. {agent.Card.Description} Skills: {skills}.");
    }

    private static string ExtractText(SendMessageResponse response)
    {
        var message = response.PayloadCase switch
        {
            SendMessageResponseCase.Message => response.Message,
            SendMessageResponseCase.Task => response.Task?.Status.Message,
            _ => null,
        };

        return message?.Parts is null
            ? string.Empty
            : string.Concat(message.Parts.Select(p => p.Text)).Trim();
    }
}
