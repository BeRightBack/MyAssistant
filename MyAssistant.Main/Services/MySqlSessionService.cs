using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using MySqlConnector;
using MyAssistant.Abstractions.Agent;
using MyAssistant.Abstractions.Services;

namespace MyAssistant.Main;

public class MySqlSessionService : ISessionService
{
    private readonly string _connStr;
    private readonly Func<string, CancellationToken, Task<float[]?>>? _embedder;

    public MySqlSessionService(Func<string, CancellationToken, Task<float[]?>>? embedder = null)
    {
        _connStr = DbConfig.ConnectionString;
        _embedder = embedder;
    }

    private async Task<MySqlConnection> OpenAsync()
    {
        return await RetryHelper.ExecuteAsync<MySqlConnection>(async () =>
        {
            var conn = new MySqlConnection(_connStr);
            await conn.OpenAsync();
            return conn;
        }, maxRetries: 2, initialDelay: TimeSpan.FromSeconds(0.5),
        shouldRetry: ex => ex is MySqlException || ex is IOException || ex is TimeoutException);
    }

    public async Task<Session?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync();
        using var cmd = new MySqlCommand("SELECT Id, Title, CreatedAt, UpdatedAt, PromptTokens, CompletionTokens FROM Sessions WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", sessionId);
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new Session(
                Id: reader.GetString(0),
                Title: reader.GetString(1),
                CreatedAt: reader.GetDateTime(2),
                UpdatedAt: reader.GetDateTime(3),
                PromptTokens: reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                CompletionTokens: reader.IsDBNull(5) ? 0 : reader.GetInt64(5));
        }
        return null;
    }

    public async Task<Session> CreateAsync(string title, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow;
        await using var conn = await OpenAsync();
        using var cmd = new MySqlCommand(
            "INSERT INTO Sessions (Id, Title, CreatedAt, UpdatedAt) VALUES (@id, @title, @created, @updated)", conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@created", now);
        cmd.Parameters.AddWithValue("@updated", now);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return new Session(id, title, now, now, 0, 0);
    }

    public async Task<Session> SaveAsync(Session session, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var conn = await OpenAsync();
        using var cmd = new MySqlCommand(
            "UPDATE Sessions SET Title = @title, UpdatedAt = @updated, PromptTokens = @pt, CompletionTokens = @ct WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", session.Id);
        cmd.Parameters.AddWithValue("@title", session.Title);
        cmd.Parameters.AddWithValue("@updated", now);
        cmd.Parameters.AddWithValue("@pt", session.PromptTokens);
        cmd.Parameters.AddWithValue("@ct", session.CompletionTokens);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return session with { UpdatedAt = now };
    }

    public async Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync();
        using var cmd1 = new MySqlCommand("DELETE FROM Messages WHERE SessionId = @sid", conn);
        cmd1.Parameters.AddWithValue("@sid", sessionId);
        await cmd1.ExecuteNonQueryAsync(cancellationToken);
        // Clean up session knowledge, but keep 'global' entries — they are meant to survive the session
        using var cmd2 = new MySqlCommand("DELETE FROM Knowledge WHERE SessionId = @sid AND Category <> 'global'", conn);
        cmd2.Parameters.AddWithValue("@sid", sessionId);
        await cmd2.ExecuteNonQueryAsync(cancellationToken);
        using var cmd3 = new MySqlCommand("DELETE FROM Sessions WHERE Id = @sid", conn);
        cmd3.Parameters.AddWithValue("@sid", sessionId);
        await cmd3.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ImmutableArray<Session>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync();
        using var cmd = new MySqlCommand("SELECT Id, Title, CreatedAt, UpdatedAt, PromptTokens, CompletionTokens FROM Sessions ORDER BY UpdatedAt DESC", conn);
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<Session>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new Session(
                Id: reader.GetString(0),
                Title: reader.GetString(1),
                CreatedAt: reader.GetDateTime(2),
                UpdatedAt: reader.GetDateTime(3),
                PromptTokens: reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                CompletionTokens: reader.IsDBNull(5) ? 0 : reader.GetInt64(5)));
        }
        return list.ToImmutableArray();
    }

    public async Task<ImmutableArray<ChatMessage>> GetMessagesAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync();
        using var cmd = new MySqlCommand(
            "SELECT Role, Content, ToolCallsJson, ToolCallId FROM Messages WHERE SessionId = @sid ORDER BY Id ASC", conn);
        cmd.Parameters.AddWithValue("@sid", sessionId);
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<ChatMessage>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var role = reader.GetString(0);
            var content = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var toolCallsJson = reader.IsDBNull(2) ? null : reader.GetString(2);
            var toolCallId = reader.IsDBNull(3) ? null : reader.GetString(3);

            IReadOnlyList<ChatToolCall>? toolCalls = null;
            if (!string.IsNullOrEmpty(toolCallsJson))
            {
                try
                {
                    toolCalls = JsonSerializer.Deserialize<List<ChatToolCall>>(toolCallsJson);
                }
                catch (Exception ex) { MyAssistant.Main.AppLog.Warn($"Failed to deserialize tool calls: {ex.Message}"); }
            }

            list.Add(new ChatMessage(
                Role: role,
                Content: content,
                ToolCalls: toolCalls,
                ToolCallId: toolCallId));
        }
        return list.ToImmutableArray();
    }

    public async Task DeleteMessagesFromAsync(string sessionId, int fromIndex, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync();
        using var cmd = new MySqlCommand(
            "DELETE FROM Messages WHERE SessionId = @sid AND Id >= (SELECT Id FROM (SELECT Id FROM Messages WHERE SessionId = @sid ORDER BY Id ASC LIMIT 1 OFFSET @offset) AS sub)", conn);
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@offset", fromIndex);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddMessageAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync();
        var toolCallsJson = message.ToolCalls is { Count: > 0 }
            ? JsonSerializer.Serialize(message.ToolCalls)
            : (string?)null;

        using var cmd = new MySqlCommand(
            "INSERT INTO Messages (SessionId, Role, Content, ToolCallsJson, ToolCallId) VALUES (@sid, @role, @content, @tcj, @tcid)", conn);
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@role", message.Role);
        cmd.Parameters.AddWithValue("@content", message.Content ?? "");
        cmd.Parameters.AddWithValue("@tcj", (object?)toolCallsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tcid", (object?)message.ToolCallId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        using var cmd2 = new MySqlCommand(
            "UPDATE Sessions SET UpdatedAt = NOW() WHERE Id = @sid", conn);
        cmd2.Parameters.AddWithValue("@sid", sessionId);
        await cmd2.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> StoreKnowledgeAsync(string sessionId, string category, string content, string? metadataJson = null, CancellationToken cancellationToken = default)
    {
        string? embeddingJson = null;

        // Best-effort semantic embedding; keyword search remains the fallback
        if (_embedder != null && !string.IsNullOrWhiteSpace(content))
        {
            try
            {
                var vector = await _embedder(content, cancellationToken);
                embeddingJson = MyAssistant.Core.Memory.VectorMath.ToJson(vector);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Embedding generation skipped: {ex.Message}");
            }
        }

        await using var conn = await OpenAsync();
        using var cmd = new MySqlCommand(
            "INSERT INTO Knowledge (SessionId, Category, Content, Metadata, Embedding) VALUES (@sid, @cat, @content, @meta, @emb)", conn);
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@cat", category);
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@meta", (object?)metadataJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@emb", (object?)embeddingJson ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        using var idCmd = new MySqlCommand("SELECT LAST_INSERT_ID()", conn);
        return Convert.ToInt64(await idCmd.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<ImmutableArray<KnowledgeEntry>> SearchKnowledgeAsync(string sessionId, string query, int limit = 10, string? category = null, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync();
        string sql;
        using var cmd = new MySqlCommand();
        cmd.Connection = conn;
        // Strip FULLTEXT boolean-mode operators so raw user text cannot alter query semantics
        var sanitizedQuery = MyAssistant.Core.Memory.FullTextSanitizer.Sanitize(query);
        if (sanitizedQuery is null)
            return ImmutableArray<KnowledgeEntry>.Empty;
        cmd.Parameters.AddWithValue("@query", sanitizedQuery);
        cmd.Parameters.AddWithValue("@limit", limit);

        if (category == "global")
        {
            sql = "SELECT Id, SessionId, Category, Content, Metadata, CreatedAt, UpdatedAt FROM Knowledge WHERE MATCH(Content) AGAINST(@query IN BOOLEAN MODE) ORDER BY MATCH(Content) AGAINST(@query IN BOOLEAN MODE) DESC LIMIT @limit";
        }
        else if (category != null)
        {
            sql = "SELECT Id, SessionId, Category, Content, Metadata, CreatedAt, UpdatedAt FROM Knowledge WHERE Category = @cat AND MATCH(Content) AGAINST(@query IN BOOLEAN MODE) ORDER BY MATCH(Content) AGAINST(@query IN BOOLEAN MODE) DESC LIMIT @limit";
            cmd.Parameters.AddWithValue("@cat", category);
        }
        else
        {
            // Cross-session keyword recall: memory must outlive the session that created it
            sql = "SELECT Id, SessionId, Category, Content, Metadata, CreatedAt, UpdatedAt FROM Knowledge WHERE MATCH(Content) AGAINST(@query IN BOOLEAN MODE) ORDER BY MATCH(Content) AGAINST(@query IN BOOLEAN MODE) DESC LIMIT @limit";
        }

        cmd.CommandText = sql;
        var keywordEntries = new List<KnowledgeEntry>();
        using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                keywordEntries.Add(ReadKnowledgeEntry(reader));
        }

        // Hybrid semantic recall: rank stored entries (all sessions) by cosine
        // similarity against the query and merge with keyword hits. Current-session
        // entries get a small boost so fresh context outranks older look-alikes.
        // Falls back to pure keyword order on any failure.
        if (_embedder != null)
        {
            try
            {
                var queryVector = await _embedder(query, cancellationToken);
                if (queryVector is { Length: > 0 })
                {
                    var candidates = await LoadVectorCandidatesAsync(sessionId, 200, cancellationToken);
                    var ranked = candidates
                        .Select(c => (
                            c.Entry,
                            Sim: MyAssistant.Core.Memory.VectorMath.CosineSimilarity(queryVector, c.Vector)
                                + (c.Entry.SessionId == sessionId ? 0.05f : 0f)))
                        .Where(t => t.Sim > 0f)
                        .OrderByDescending(t => t.Sim)
                        .Select(t => t.Entry)
                        .ToList();

                    var seen = new HashSet<long>();
                    var merged = new List<KnowledgeEntry>(ranked.Count + keywordEntries.Count);
                    foreach (var entry in ranked.Concat(keywordEntries))
                        if (seen.Add(entry.Id))
                            merged.Add(entry);

                    return merged.Take(limit).ToImmutableArray();
                }

                AppLog.Warn("Semantic recall skipped: embedding returned no vector");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Warn($"Semantic recall skipped: {ex.Message}");
            }
        }

        return keywordEntries.Take(limit).ToImmutableArray();
    }

    private static KnowledgeEntry ReadKnowledgeEntry(System.Data.Common.DbDataReader r) => new(
        Id: r.GetInt64(0),
        SessionId: r.GetString(1),
        Category: r.GetString(2),
        Content: r.GetString(3),
        MetadataJson: r.IsDBNull(4) ? null : r.GetString(4),
        CreatedAt: r.GetDateTime(5),
        UpdatedAt: r.GetDateTime(6));

    private async Task<List<(KnowledgeEntry Entry, float[]? Vector)>> LoadVectorCandidatesAsync(string sessionId, int limit, CancellationToken cancellationToken)
    {
        var results = new List<(KnowledgeEntry, float[]?)>();
        await using var conn = await OpenAsync();
        // Long-term memory is cross-session by design: rank every embedded entry.
        using var cmd = new MySqlCommand(
            "SELECT Id, SessionId, Category, Content, Metadata, CreatedAt, UpdatedAt, Embedding " +
            "FROM Knowledge WHERE Embedding IS NOT NULL ORDER BY UpdatedAt DESC LIMIT @lim", conn);
        cmd.Parameters.AddWithValue("@lim", limit);
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var entry = new KnowledgeEntry(
                Id: reader.GetInt64(0),
                SessionId: reader.GetString(1),
                Category: reader.GetString(2),
                Content: reader.GetString(3),
                MetadataJson: reader.IsDBNull(4) ? null : reader.GetString(4),
                CreatedAt: reader.GetDateTime(5),
                UpdatedAt: reader.GetDateTime(6));
            var vector = reader.IsDBNull(7) ? null : MyAssistant.Core.Memory.VectorMath.FromJson(reader.GetString(7));
            results.Add((entry, vector));
        }
        return results;
    }

    public async Task<ImmutableArray<KnowledgeEntry>> ListKnowledgeAsync(string sessionId, string? category = null, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync();
        var sql = category != null
            ? "SELECT Id, SessionId, Category, Content, Metadata, CreatedAt, UpdatedAt FROM Knowledge WHERE SessionId = @sid AND Category = @cat ORDER BY UpdatedAt DESC"
            : "SELECT Id, SessionId, Category, Content, Metadata, CreatedAt, UpdatedAt FROM Knowledge WHERE SessionId = @sid ORDER BY UpdatedAt DESC";
        using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@sid", sessionId);
        if (category != null) cmd.Parameters.AddWithValue("@cat", category);
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<KnowledgeEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new KnowledgeEntry(
                Id: reader.GetInt64(0),
                SessionId: reader.GetString(1),
                Category: reader.GetString(2),
                Content: reader.GetString(3),
                MetadataJson: reader.IsDBNull(4) ? null : reader.GetString(4),
                CreatedAt: reader.GetDateTime(5),
                UpdatedAt: reader.GetDateTime(6)));
        }
        return list.ToImmutableArray();
    }

    public async Task<ImmutableArray<KnowledgeEntry>> ListAllKnowledgeAsync(string? category = null, int limit = 200, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync();
        var sql = category != null
            ? "SELECT Id, SessionId, Category, Content, Metadata, CreatedAt, UpdatedAt FROM Knowledge WHERE Category = @cat ORDER BY UpdatedAt DESC LIMIT @limit"
            : "SELECT Id, SessionId, Category, Content, Metadata, CreatedAt, UpdatedAt FROM Knowledge ORDER BY UpdatedAt DESC LIMIT @limit";
        using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@limit", limit);
        if (category != null) cmd.Parameters.AddWithValue("@cat", category);
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<KnowledgeEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new KnowledgeEntry(
                Id: reader.GetInt64(0),
                SessionId: reader.GetString(1),
                Category: reader.GetString(2),
                Content: reader.GetString(3),
                MetadataJson: reader.IsDBNull(4) ? null : reader.GetString(4),
                CreatedAt: reader.GetDateTime(5),
                UpdatedAt: reader.GetDateTime(6)));
        }
        return list.ToImmutableArray();
    }

    public async Task<bool> DeleteKnowledgeAsync(long entryId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync();
        using var cmd = new MySqlCommand("DELETE FROM Knowledge WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", entryId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<int> DeleteKnowledgeByCategoryAsync(string sessionId, string category, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync();
        using var cmd = new MySqlCommand("DELETE FROM Knowledge WHERE SessionId = @sid AND Category = @cat", conn);
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@cat", category);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
