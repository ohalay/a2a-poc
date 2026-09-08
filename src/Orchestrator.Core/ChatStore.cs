using System.Collections.Concurrent;

namespace Orchestrator.Core;

public record ChatTurn(string Role, string Content, DateTimeOffset Timestamp);

public sealed class ChatThread
{
    public string ThreadId { get; init; } = Guid.NewGuid().ToString("N");
    public List<ChatTurn> Turns { get; } = [];
}

/// <summary>In-memory chat history store keyed by thread id.</summary>
public sealed class ChatStore
{
    private readonly ConcurrentDictionary<string, ChatThread> _threads = new();

    public ChatThread GetOrCreate(string threadId)
        => _threads.GetOrAdd(threadId, id => new ChatThread { ThreadId = id });

    public bool TryGet(string threadId, out ChatThread thread)
        => _threads.TryGetValue(threadId, out thread!);
}
