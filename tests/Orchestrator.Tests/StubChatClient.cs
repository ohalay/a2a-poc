using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Orchestrator.Tests;

/// <summary>
/// Deterministic stub IChatClient that drives the function-calling loop.
///
/// First call (no tool results yet): emits a FunctionCallContent for each tool
/// name in <paramref name="toolsToCall"/>, passing the user's text as the
/// "request" argument. FunctionInvokingChatClient executes those tools and
/// calls back with the results; on that second call the stub returns a final
/// message that prefixes and concatenates all tool outputs so aggregation is
/// observable without a real LLM. If <paramref name="toolsToCall"/> is empty it
/// answers directly with <paramref name="directAnswer"/>.
/// </summary>
public sealed class StubChatClient(IReadOnlyList<string> toolsToCall, string directAnswer = "DIRECT_ANSWER")
    : IChatClient
{
    private int _callCount;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var msgs = messages.ToList();
        _callCount++;

        // Second (and later) turn: tool results are present -> synthesize final answer.
        var results = msgs
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Select(r => r.Result?.ToString() ?? string.Empty)
            .ToList();

        if (results.Count > 0)
        {
            var merged = "AGG: " + string.Join(" | ", results);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, merged)));
        }

        // First turn.
        if (toolsToCall.Count == 0)
        {
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, directAnswer)));
        }

        var userText = msgs.LastOrDefault(m => m.Role == ChatRole.User)?.Text
            ?? msgs.Where(m => m.Role != ChatRole.System)
                   .Select(m => m.Text)
                   .LastOrDefault(t => !string.IsNullOrEmpty(t))
            ?? string.Empty;
        var calls = toolsToCall
            .Select((tool, i) => (AIContent)new FunctionCallContent(
                callId: $"call-{i}",
                name: tool,
                arguments: new Dictionary<string, object?> { ["request"] = userText }))
            .ToList();

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, calls)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
