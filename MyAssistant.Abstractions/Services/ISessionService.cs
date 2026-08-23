using System.Collections.Immutable;
using MyAssistant.Abstractions.Agent;

namespace MyAssistant.Abstractions.Services;

public interface ISessionService
{
    Task<Session?> GetAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<Session> CreateAsync(string title, CancellationToken cancellationToken = default);
    Task<Session> SaveAsync(Session session, CancellationToken cancellationToken = default);
    Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default);
    Task DeleteMessagesFromAsync(string sessionId, int fromIndex, CancellationToken cancellationToken = default);
    Task<ImmutableArray<Session>> ListAsync(CancellationToken cancellationToken = default);
    Task<ImmutableArray<ChatMessage>> GetMessagesAsync(string sessionId, CancellationToken cancellationToken = default);
    Task AddMessageAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default);
    Task<long> StoreKnowledgeAsync(string sessionId, string category, string content, string? metadataJson = null, CancellationToken cancellationToken = default);
    Task<ImmutableArray<KnowledgeEntry>> SearchKnowledgeAsync(string sessionId, string query, int limit = 10, string? category = null, CancellationToken cancellationToken = default);
    Task<ImmutableArray<KnowledgeEntry>> ListKnowledgeAsync(string sessionId, string? category = null, CancellationToken cancellationToken = default);
    Task<ImmutableArray<KnowledgeEntry>> ListAllKnowledgeAsync(string? category = null, int limit = 200, CancellationToken cancellationToken = default);
    Task<bool> DeleteKnowledgeAsync(long entryId, CancellationToken cancellationToken = default);
    Task<int> DeleteKnowledgeByCategoryAsync(string sessionId, string category, CancellationToken cancellationToken = default);
}

public record Session(
    string Id,
    string Title,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    long PromptTokens,
    long CompletionTokens);

public record KnowledgeEntry(
    long Id,
    string SessionId,
    string Category,
    string Content,
    string? MetadataJson,
    DateTime CreatedAt,
    DateTime UpdatedAt);
