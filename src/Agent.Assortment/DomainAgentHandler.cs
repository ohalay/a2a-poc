using A2A;
using Microsoft.Extensions.AI;
using System.Diagnostics;

namespace Agent.Assortment;

/// <summary>
/// A2A agent handler. Receives a task objective from the orchestrator, runs it
/// through a local tool-calling LLM (backed by the domain's private tools),
/// and streams the result back through the A2A event queue.
/// Constructed by DI via <c>AddA2AAgent&lt;DomainAgentHandler&gt;</c>.
/// </summary>
public sealed class DomainAgentHandler(
    IChatClient chatClient,
    AssortmentTools tools,
    ILogger<DomainAgentHandler> logger) : IAgentHandler
{
    private const string SystemPrompt =
        "You are the Assortment domain specialist for a multi-shop commerce company. " +
        "You answer questions about the product catalog, categories, and which stores carry which products. " +
        "Always use the provided tools to look up real catalog data before answering. " +
        "Be concise and factual. If a product is not in the catalog, say so plainly.";

    private ChatOptions BuildOptions() => new()
    {
        Tools =
        [
            AIFunctionFactory.Create(tools.GetProduct),
            AIFunctionFactory.Create(tools.GetActiveCatalog),
        ],
        ToolMode = ChatToolMode.Auto,
    };

    public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        var userText = context.UserText ?? string.Empty;

        logger.LogInformation("Assortment agent received task: {Task}", userText);

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
                text = "The Assortment agent could not produce a result for this request.";
            }

            logger.LogInformation("Assortment agent produced result ({Length} chars)", text.Length);
            await responder.ReplyAsync(text, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Assortment agent execution failed");
            await responder.ReplyAsync($"Assortment agent error: {ex.Message}", cancellationToken);
        }
    }

    public Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        logger.LogInformation("Assortment agent task cancelled: {TaskId}", context.TaskId);
        return Task.CompletedTask;
    }
}
