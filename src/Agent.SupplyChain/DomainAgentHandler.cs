using A2A;
using Microsoft.Extensions.AI;
using System.Diagnostics;

namespace Agent.SupplyChain;

/// <summary>
/// A2A agent handler for the Supply Chain domain. Runs the orchestrator's task
/// objective through a local tool-calling LLM backed by the domain's private
/// warehouse/DWH tools, and replies through the A2A event queue.
/// </summary>
public sealed class DomainAgentHandler(
    IChatClient chatClient,
    SupplyChainTools tools,
    ILogger<DomainAgentHandler> logger) : IAgentHandler
{
    private const string SystemPrompt =
        "You are the Supply Chain domain specialist for a multi-shop commerce company. " +
        "You answer questions about warehouse stock levels, inbound shipments, delivery status, and stock velocity. " +
        "Always use the provided tools to look up real warehouse data before answering. " +
        "Be concise and factual. If data is unavailable for a product, say so plainly.";

    private ChatOptions BuildOptions() => new()
    {
        Tools =
        [
            AIFunctionFactory.Create(tools.GetStock),
            AIFunctionFactory.Create(tools.GetShipments),
        ],
        ToolMode = ChatToolMode.Auto,
    };

    public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        var userText = context.UserText ?? string.Empty;

        logger.LogInformation("Supply Chain agent received task: {Task}", userText);

        var responder = new MessageResponder(eventQueue, context.ContextId);

        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, SystemPrompt),
                new(ChatRole.User, userText),
            };

            var response = await chatClient.GetResponseAsync(messages, BuildOptions(), cancellationToken);
            var text = response.Text;

            if (string.IsNullOrWhiteSpace(text))
            {
                text = "The Supply Chain agent could not produce a result for this request.";
            }

            logger.LogInformation("Supply Chain agent produced result ({Length} chars)", text.Length);
            await responder.ReplyAsync(text, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Supply Chain agent execution failed");
            await responder.ReplyAsync($"Supply Chain agent error: {ex.Message}", cancellationToken);
        }
    }

    public Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        logger.LogInformation("Supply Chain agent task cancelled: {TaskId}", context.TaskId);
        return Task.CompletedTask;
    }
}
