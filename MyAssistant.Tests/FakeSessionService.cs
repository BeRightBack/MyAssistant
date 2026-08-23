using System.Collections.Immutable;
using MyAssistant.Abstractions.Agent;
using MyAssistant.Abstractions.Services;

namespace MyAssistant.Tests;

/// <summary>In-memory ISessionService stub for tool unit tests.</summary>
public class FakeSessionService : ISessionService
{
    public List<KnowledgeEntry> Knowledge { get; } = new();
    public List<string> Calls { get; } = new();
    private long _nextId = 100;

    public Task<Session?> GetAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<Session> CreateAsync(string title, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<Session> SaveAsync(Session session, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task DeleteMessagesFromAsync(string sessionId, int fromIndex, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<ImmutableArray<Session>> ListAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<ImmutableArray<ChatMessage>> GetMessagesAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task AddMessageAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task<long> StoreKnowledgeAsync(string sessionId, string category, string content, string? metadataJson = null, CancellationToken cancellationToken = default)
    {
        Calls.Add($"store:{category}:{content}");
        var id = _nextId++;
        Knowledge.Add(new KnowledgeEntry(id, sessionId, category, content, metadataJson, DateTime.UtcNow, DateTime.UtcNow));
        return Task.FromResult(id);
    }

    public Task<ImmutableArray<KnowledgeEntry>> SearchKnowledgeAsync(string sessionId, string query, int limit = 10, string? category = null, CancellationToken cancellationToken = default)
    {
        Calls.Add($"search:{query}");
        var hits = Knowledge
            .Where(e => (category == null || e.Category == category) && e.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToImmutableArray();
        return Task.FromResult(hits);
    }

    public Task<ImmutableArray<KnowledgeEntry>> ListKnowledgeAsync(string sessionId, string? category = null, CancellationToken cancellationToken = default)
    {
        Calls.Add($"list:{sessionId}:{category}");
        return Task.FromResult(Knowledge
            .Where(e => e.SessionId == sessionId && (category == null || e.Category == category))
            .ToImmutableArray());
    }

    public Task<ImmutableArray<KnowledgeEntry>> ListAllKnowledgeAsync(string? category = null, int limit = 200, CancellationToken cancellationToken = default)
    {
        Calls.Add("listAll");
        return Task.FromResult(Knowledge
            .Where(e => category == null || e.Category == category)
            .Take(limit)
            .ToImmutableArray());
    }

    public Task<bool> DeleteKnowledgeAsync(long entryId, CancellationToken cancellationToken = default)
    {
        Calls.Add($"delete:{entryId}");
        var removed = Knowledge.RemoveAll(e => e.Id == entryId) > 0;
        return Task.FromResult(removed);
    }

    public Task<int> DeleteKnowledgeByCategoryAsync(string sessionId, string category, CancellationToken cancellationToken = default)
    {
        Calls.Add($"deleteCategory:{sessionId}:{category}");
        var n = Knowledge.RemoveAll(e => e.SessionId == sessionId && e.Category == category);
        return Task.FromResult(n);
    }
}
