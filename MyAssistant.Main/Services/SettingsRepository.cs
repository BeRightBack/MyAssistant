using MySqlConnector;
using MyAssistant.Main.Services;

namespace MyAssistant.Main;

public class SettingsRepository
{
    private readonly string _connStr;

    public SettingsRepository()
    {
        _connStr = DbConfig.ConnectionString;
    }

    public async Task InitializeAsync()
    {
        await using var conn = await OpenAsync();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Settings (
                `Key` VARCHAR(255) NOT NULL PRIMARY KEY,
                `Value` TEXT NOT NULL,
                `UpdatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS Providers (
                `Id` VARCHAR(255) NOT NULL PRIMARY KEY,
                `Name` VARCHAR(255) NOT NULL DEFAULT '',
                `BaseUrl` VARCHAR(1024) NOT NULL DEFAULT '',
                `ApiKey` TEXT NULL,
                `DefaultModel` VARCHAR(512) NOT NULL DEFAULT '',
                `Kind` VARCHAR(64) NOT NULL DEFAULT 'OpenAICompatible',
                `Temperature` DOUBLE NOT NULL DEFAULT 0.7,
                `TopP` DOUBLE NOT NULL DEFAULT 0.95,
                `MaxTokens` BIGINT NOT NULL DEFAULT 8192,
                `UpdatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS Sessions (
                `Id` VARCHAR(255) NOT NULL PRIMARY KEY,
                `Title` VARCHAR(512) NOT NULL DEFAULT 'New Chat',
                `SystemPrompt` LONGTEXT NULL,
                `CreatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                `UpdatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                `PromptTokens` BIGINT NOT NULL DEFAULT 0,
                `CompletionTokens` BIGINT NOT NULL DEFAULT 0
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS Messages (
                `Id` BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                `SessionId` VARCHAR(255) NOT NULL,
                `Role` VARCHAR(32) NOT NULL,
                `Content` LONGTEXT,
                `ToolCallsJson` TEXT NULL,
                `ToolCallId` VARCHAR(255) NULL,
                `CreatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                INDEX `idx_messages_session` (`SessionId`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS Knowledge (
                `Id` BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                `SessionId` VARCHAR(255) NOT NULL,
                `Category` VARCHAR(64) NOT NULL,
                `Content` TEXT NOT NULL,
                `Metadata` TEXT NULL,
                `CreatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                `UpdatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                INDEX `idx_knowledge_session` (`SessionId`),
                FULLTEXT KEY `ft_knowledge_content` (`Content`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS Tools (
                `Id` VARCHAR(255) NOT NULL PRIMARY KEY,
                `Name` VARCHAR(255) NOT NULL,
                `Enabled` BOOLEAN NOT NULL DEFAULT TRUE,
                `Category` VARCHAR(64) NOT NULL DEFAULT 'core',
                `UpdatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS Roles (
                `Id` VARCHAR(255) NOT NULL PRIMARY KEY,
                `Name` VARCHAR(255) NOT NULL,
                `Description` TEXT NOT NULL,
                `SystemPrompt` LONGTEXT NOT NULL,
                `IsActive` BOOLEAN NOT NULL DEFAULT FALSE,
                `CreatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                `UpdatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        ";
        await cmd.ExecuteNonQueryAsync();

        // Migration: embedding column for semantic (vector) knowledge recall
        var hasEmbedding = Convert.ToInt64(await ScalarAsync(conn,
            "SELECT COUNT(*) FROM information_schema.COLUMNS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Knowledge' AND COLUMN_NAME = 'Embedding'"));
        if (hasEmbedding == 0)
            await ExecAsync(conn, "ALTER TABLE Knowledge ADD COLUMN `Embedding` LONGTEXT NULL");
    }

    private async Task<MySqlConnection> OpenAsync()
    {
        var conn = new MySqlConnection(_connStr);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecAsync(MySqlConnection conn, string sql, Dictionary<string, object>? parameters = null)
    {
        using var cmd = new MySqlCommand(sql, conn);
        if (parameters != null)
            foreach (var kv in parameters)
                cmd.Parameters.AddWithValue(kv.Key, kv.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(MySqlConnection conn, string sql, Dictionary<string, object>? parameters = null)
    {
        using var cmd = new MySqlCommand(sql, conn);
        if (parameters != null)
            foreach (var kv in parameters)
                cmd.Parameters.AddWithValue(kv.Key, kv.Value);
        return await cmd.ExecuteScalarAsync();
    }

    public async Task<string> GetSettingAsync(string key, string defaultValue = "")
    {
        await using var conn = await OpenAsync();
        var result = await ScalarAsync(conn, "SELECT `Value` FROM Settings WHERE `Key` = @key",
            new Dictionary<string, object> { ["@key"] = key });
        return result?.ToString() ?? defaultValue;
    }

    public async Task SetSettingAsync(string key, string value)
    {
        await using var conn = await OpenAsync();
        await ExecAsync(conn, @"
            INSERT INTO Settings (`Key`, `Value`, `UpdatedAt`) VALUES (@key, @val, NOW())
            ON DUPLICATE KEY UPDATE `Value` = @val, `UpdatedAt` = NOW()
        ", new Dictionary<string, object> { ["@key"] = key, ["@val"] = value });
    }

    public async Task<ProviderSettings?> GetProviderAsync(string id)
    {
        await using var conn = await OpenAsync();
        using var cmd = new MySqlCommand(
            "SELECT `Name`,`BaseUrl`,`ApiKey`,`DefaultModel`,`Kind`,`Temperature`,`TopP`,`MaxTokens` FROM Providers WHERE `Id` = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new ProviderSettings
        {
            Name = reader.GetString(0),
            BaseUrl = reader.GetString(1),
            ApiKey = reader.IsDBNull(2) ? null : reader.GetString(2),
            DefaultModel = reader.GetString(3),
            Kind = reader.GetString(4),
            Temperature = reader.GetDouble(5),
            TopP = reader.GetDouble(6),
            MaxTokens = reader.GetInt64(7)
        };
    }

    public async Task SaveProviderAsync(string id, ProviderSettings ps)
    {
        await using var conn = await OpenAsync();
        using var cmd = new MySqlCommand(@"
            INSERT INTO Providers (`Id`,`Name`,`BaseUrl`,`ApiKey`,`DefaultModel`,`Kind`,`Temperature`,`TopP`,`MaxTokens`,`UpdatedAt`)
            VALUES (@id,@name,@url,@key,@model,@kind,@temp,@top,@max,NOW())
            ON DUPLICATE KEY UPDATE
                `Name`=@name, `BaseUrl`=@url, `ApiKey`=@key, `DefaultModel`=@model, `Kind`=@kind,
                `Temperature`=@temp, `TopP`=@top, `MaxTokens`=@max, `UpdatedAt`=NOW()
        ", conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@name", ps.Name ?? "");
        cmd.Parameters.AddWithValue("@url", ps.BaseUrl ?? "");
        cmd.Parameters.AddWithValue("@key", (object?)ps.ApiKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@model", ps.DefaultModel ?? "");
        cmd.Parameters.AddWithValue("@kind", ps.Kind ?? "OpenAICompatible");
        cmd.Parameters.AddWithValue("@temp", ps.Temperature);
        cmd.Parameters.AddWithValue("@top", ps.TopP);
        cmd.Parameters.AddWithValue("@max", ps.MaxTokens);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<string>> GetAllProviderIdsAsync()
    {
        await using var conn = await OpenAsync();
        using var cmd = new MySqlCommand("SELECT `Id` FROM Providers ORDER BY `Id`", conn);
        using var reader = await cmd.ExecuteReaderAsync();
        var ids = new List<string>();
        while (await reader.ReadAsync())
            ids.Add(reader.GetString(0));
        return ids;
    }

    public async Task DeleteProviderAsync(string id)
    {
        await using var conn = await OpenAsync();
        using var cmd = new MySqlCommand("DELETE FROM Providers WHERE `Id` = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<Dictionary<string, bool>> GetToolStatesAsync()
    {
        await using var conn = await OpenAsync();
        using var cmd = new MySqlCommand("SELECT `Id`, `Enabled` FROM Tools", conn);
        using var reader = await cmd.ExecuteReaderAsync();
        var states = new Dictionary<string, bool>();
        while (await reader.ReadAsync())
            states[reader.GetString(0)] = reader.GetBoolean(1);
        return states;
    }

    public async Task SetToolEnabledAsync(string toolId, bool enabled)
    {
        await using var conn = await OpenAsync();
        using var cmd = new MySqlCommand(@"
            INSERT INTO Tools (`Id`, `Name`, `Enabled`, `Category`, `UpdatedAt`)
            VALUES (@id, @name, @enabled, @cat, NOW())
            ON DUPLICATE KEY UPDATE `Enabled` = @enabled, `UpdatedAt` = NOW()
        ", conn);
        cmd.Parameters.AddWithValue("@id", toolId);
        cmd.Parameters.AddWithValue("@name", toolId);
        cmd.Parameters.AddWithValue("@enabled", enabled);
        cmd.Parameters.AddWithValue("@cat", "core");
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task InitializeDefaultToolsAsync(List<string> toolNames)
    {
        await using var conn = await OpenAsync();
        foreach (var name in toolNames)
        {
            using var cmd = new MySqlCommand(@"
                INSERT IGNORE INTO Tools (`Id`, `Name`, `Enabled`, `Category`, `UpdatedAt`)
                VALUES (@id, @name, TRUE, 'core', NOW())
            ", conn);
            cmd.Parameters.AddWithValue("@id", name);
            cmd.Parameters.AddWithValue("@name", name);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            await using var conn = await OpenAsync();
            using var cmd = new MySqlCommand("SELECT 1", conn);
            await cmd.ExecuteScalarAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<(bool Success, string Error)> TestConnectionDetailedAsync()
    {
        try
        {
            await using var conn = await OpenAsync();
            using var cmd = new MySqlCommand("SELECT 1", conn);
            await cmd.ExecuteScalarAsync();
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<List<RoleEntry>> GetAllRolesAsync()
    {
        await using var conn = await OpenAsync();
        using var cmd = new MySqlCommand("SELECT `Id`,`Name`,`Description`,`SystemPrompt`,`IsActive` FROM Roles ORDER BY `Name`", conn);
        using var reader = await cmd.ExecuteReaderAsync();
        var roles = new List<RoleEntry>();
        while (await reader.ReadAsync())
            roles.Add(new RoleEntry
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Description = reader.GetString(2),
                SystemPrompt = reader.GetString(3),
                IsActive = reader.GetBoolean(4)
            });
        return roles;
    }

    public async Task<RoleEntry?> GetRoleAsync(string id)
    {
        await using var conn = await OpenAsync();
        using var cmd = new MySqlCommand("SELECT `Id`,`Name`,`Description`,`SystemPrompt`,`IsActive` FROM Roles WHERE `Id` = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new RoleEntry
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            Description = reader.GetString(2),
            SystemPrompt = reader.GetString(3),
            IsActive = reader.GetBoolean(4)
        };
    }

    public async Task SaveRoleAsync(RoleEntry role)
    {
        await using var conn = await OpenAsync();
        using var cmd = new MySqlCommand(@"
            INSERT INTO Roles (`Id`,`Name`,`Description`,`SystemPrompt`,`IsActive`,`UpdatedAt`)
            VALUES (@id,@name,@desc,@prompt,@active,NOW())
            ON DUPLICATE KEY UPDATE
                `Name`=@name, `Description`=@desc, `SystemPrompt`=@prompt, `IsActive`=@active, `UpdatedAt`=NOW()
        ", conn);
        cmd.Parameters.AddWithValue("@id", role.Id);
        cmd.Parameters.AddWithValue("@name", role.Name);
        cmd.Parameters.AddWithValue("@desc", role.Description);
        cmd.Parameters.AddWithValue("@prompt", role.SystemPrompt);
        cmd.Parameters.AddWithValue("@active", role.IsActive);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SetActiveRoleAsync(string roleId)
    {
        await using var conn = await OpenAsync();
        await ExecAsync(conn, "UPDATE Roles SET `IsActive` = FALSE");
        await ExecAsync(conn, "UPDATE Roles SET `IsActive` = TRUE WHERE `Id` = @id",
            new Dictionary<string, object> { ["@id"] = roleId });
    }

    public async Task DeleteRoleAsync(string roleId)
    {
        await using var conn = await OpenAsync();
        await ExecAsync(conn, "DELETE FROM Roles WHERE `Id` = @id",
            new Dictionary<string, object> { ["@id"] = roleId });
    }

    public async Task<string> GetActiveSystemPromptAsync(string fallback)
    {
        await using var conn = await OpenAsync();
        var result = await ScalarAsync(conn, "SELECT `SystemPrompt` FROM Roles WHERE `IsActive` = TRUE LIMIT 1");
        return result?.ToString() ?? fallback;
    }

    public async Task InitializeDefaultRolesAsync()
    {
        var defaults = new List<RoleEntry>
        {
            new() { Id = "coder", Name = "Coder", Description = "AI coding assistant — writes, debugs, refactors, and explains code.", IsActive = true,
                SystemPrompt = GetCoderPrompt() },
            new() { Id = "cook", Name = "Culinary Cook", Description = "Kitchen assistant — recipes, substitutions, meal planning, dietary needs.", IsActive = false,
                SystemPrompt = GetCookPrompt() },
            new() { Id = "author", Name = "Author", Description = "Writing assistant — books, blogs, editing, tone, structure.", IsActive = false,
                SystemPrompt = GetAuthorPrompt() },
            new() { Id = "researcher", Name = "Research Analyst", Description = "Deep analysis, fact synthesis, summaries, research.", IsActive = false,
                SystemPrompt = GetResearcherPrompt() },
            new() { Id = "tutor", Name = "Tutor", Description = "Explains concepts, creates quizzes, tracks learning progress.", IsActive = false,
                SystemPrompt = GetTutorPrompt() },
            new() { Id = "fitness", Name = "Fitness Coach", Description = "Workouts, nutrition plans, progress tracking.", IsActive = false,
                SystemPrompt = GetFitnessPrompt() },
            new() { Id = "business", Name = "Business Strategist", Description = "Business plans, marketing, competitive analysis.", IsActive = false,
                SystemPrompt = GetBusinessPrompt() },
        };

        await using var conn = await OpenAsync();
        foreach (var role in defaults)
        {
            using var cmd = new MySqlCommand(@"
                INSERT IGNORE INTO Roles (`Id`,`Name`,`Description`,`SystemPrompt`,`IsActive`,`CreatedAt`,`UpdatedAt`)
                VALUES (@id,@name,@desc,@prompt,@active,NOW(),NOW())
            ", conn);
            cmd.Parameters.AddWithValue("@id", role.Id);
            cmd.Parameters.AddWithValue("@name", role.Name);
            cmd.Parameters.AddWithValue("@desc", role.Description);
            cmd.Parameters.AddWithValue("@prompt", role.SystemPrompt);
            cmd.Parameters.AddWithValue("@active", role.IsActive);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static string GetCoderPrompt() =>
        "You are MyAssistant, an AI coding assistant. You write, debug, refactor, and explain code. You are direct, accurate, and never guess when you can check.\n\n" +
        "## CRITICAL RULES\n" +
        "- NEVER create files or directories to satisfy a search. If a file/directory is not found, REPORT it — do NOT create it.\n" +
        "- Never modify code you haven't read first. Always read_file before edit_file.\n" +
        "- Never assume file contents, directory structure, or installed packages. Use tools to verify.\n" +
        "- Prefer minimal, targeted changes over broad rewrites.\n" +
        "- ALWAYS use absolute paths (e.g. C:\\Users\\Administrator\\Documents\\ProjectName) in all tools. Never use relative paths.\n" +
        "- When describing code, quote EXACTLY what you read. Do not invent method names, properties, or signatures. If you read a file, reference the exact line numbers and content. If unsure, say 'I need to read more files'.\n\n" +
        "## TOOL USAGE\n" +
        "Use tools proactively. Do not describe what you would do — do it.\n\n" +
        "FILE TOOLS:\n" +
        "- list_directory: List files/folders in a directory. REQUIRED: absolute path. Use this FIRST when exploring a project.\n" +
        "- read_file: Read file contents. REQUIRED: absolute path. Use offset/limit for large files.\n" +
        "- write_file: Create new files or overwrite existing ones completely.\n" +
        "- edit_file: Modify existing files using exact string replacement. Match oldString precisely.\n" +
        "- glob: Find files by name pattern (e.g. **/*.cs). REQUIRED: pattern + absolute path.\n" +
        "- grep: Search file contents with regex. REQUIRED: pattern + absolute path. Use include to filter by extension.\n\n" +
        "EXECUTION:\n" +
        "- bash: Run shell commands. Use for building, testing, git, package management.\n" +
        "  - On Windows, use PowerShell commands.\n" +
        "  - Chain dependent commands with ; not &&.\n\n" +
        "WEB:\n" +
        "- web_search: Search the web. Call ONCE, analyze results, proceed.\n" +
        "- web_fetch: Retrieve content from a URL. Always use when the user provides a URL.\n" +
        "  - If web_search returns nothing useful, try web_fetch directly on the domain.\n" +
        "  - Do NOT say 'no results' without trying both web_search and web_fetch.\n\n" +
        "## WORKFLOW\n" +
        "1. DISCOVER: When given a project path, call list_directory on it FIRST with the absolute path. Never skip this. The path structure is often nested (e.g., ProjectName/ProjectName/SubDir). You cannot guess — you must list to see.\n" +
        "2. EXPLORE: Call list_directory on EVERY subdirectory to map the full structure. Do not stop at the root.\n" +
        "3. READ: Read ALL source files (.cs, .xaml, .csproj, .sln) in every project. For large files, use offset/limit. Do not skip files — read interfaces, implementations, models, services, everything.\n" +
        "4. ANALYZY: Only after reading all files, describe what you actually found. Quote exact method names and signatures from the code.\n" +
        "5. EXECUTE: Make changes or provide answer based on what you read.\n" +
        "6. VERIFY: Build or test if applicable. Report results briefly.\n\n" +
        "IMPORTANT: When asked to 'analyze a codebase' or 'generate architecture document', you MUST read every source file. Listing directories is NOT enough. You must read the actual code to understand the architecture.\n\n" +
        "## CODE STYLES\n" +
        "- Follow existing code conventions. Match naming, formatting, patterns.\n" +
        "- Keep functions focused — one responsibility per function.\n" +
        "- Prefer async/await for I/O operations in C#.\n" +
        "- Use file-scoped namespaces in C#.\n\n" +
        "## COMMUNICATION\n" +
        "- Be concise. No preamble like 'Sure!' or 'I'd be happy to!'.\n" +
        "- State what you're doing, then do it.\n" +
        "- When done, confirm completion with a brief summary of changes.";

    private static string GetCookPrompt() =>
        "You are MyAssistant, a skilled culinary cook and kitchen assistant. You help with recipes, cooking techniques, ingredient substitutions, meal planning, and dietary accommodations.\n\n" +
        "## EXPERTISE\n" +
        "- cuisines worldwide (Italian, French, Japanese, Mexican, Indian, Thai, etc.)\n" +
        "- Baking and pastry\n" +
        "- Meal prep and planning\n" +
        "- Ingredient substitutions and dietary restrictions (vegan, gluten-free, keto, etc.)\n" +
        "- Kitchen equipment and techniques\n" +
        "- Food safety and storage\n" +
        "- Wine and beverage pairings\n\n" +
        "## RULES\n" +
        "- Be specific with measurements (metric and imperial).\n" +
        "- Include prep time and cook time for recipes.\n" +
        "- Warn about common mistakes for each technique.\n" +
        "- Suggest variations and alternatives.\n" +
        "- Consider nutritional balance when planning meals.\n" +
        "- When writing a recipe to a file, use write_file tool to save it as a text file.\n\n" +
        "## WORKFLOW\n" +
        "1. Listen to what the user wants to cook or learn.\n" +
        "2. Provide clear, step-by-step instructions.\n" +
        "3. Offer tips, variations, and substitution options.\n" +
        "4. If asked to save a recipe, use write_file to save it.\n\n" +
        "## COMMUNICATION\n" +
        "- Be warm and encouraging, like a friendly kitchen mentor.\n" +
        "- Use sensory language (golden brown, fragrant, sizzling).\n" +
        "- Don't overwhelm with too much at once. Keep it practical.";

    private static string GetAuthorPrompt() =>
        "You are MyAssistant, a published author and writing coach. You help with writing books, blogs, articles, essays, and creative fiction. You understand narrative structure, voice, pacing, and revision.\n\n" +
        "## EXPERTISE\n" +
        "- Fiction writing (novels, short stories, flash fiction)\n" +
        "- Non-fiction (essays, articles, blog posts, memoirs)\n" +
        "- Copywriting and marketing copy\n" +
        "- Editing and proofreading\n" +
        "- Story structure (three-act, hero's journey, save the cat)\n" +
        "- Character development and dialogue\n" +
        "- World-building\n" +
        "- Style and voice development\n\n" +
        "## RULES\n" +
        "- Match the user's voice and style unless asked to adjust.\n" +
        "- Show, don't tell. Use concrete details over abstract statements.\n" +
        "- When editing, explain WHY you're making changes.\n" +
        "- Preserve the author's intent while improving clarity.\n" +
        "- For long-form work, maintain consistency across chapters.\n" +
        "- When saving drafts, use write_file to save to a text file.\n\n" +
        "## WORKFLOW\n" +
        "1. Understand the genre, audience, and purpose.\n" +
        "2. Ask about tone, length, and any specific requirements.\n" +
        "3. Write or edit with clear, actionable improvements.\n" +
        "4. Offer feedback on structure, pacing, and engagement.\n\n" +
        "## COMMUNICATION\n" +
        "- Be direct and constructive. Praise what works, fix what doesn't.\n" +
        "- Use examples to illustrate writing techniques.\n" +
        "- Encourage the writer's unique voice.";

    private static string GetResearcherPrompt() =>
        "You are MyAssistant, a research analyst. You synthesize information, analyze data, create summaries, and provide well-reasoned insights on any topic.\n\n" +
        "## EXPERTISE\n" +
        "- Literature reviews and meta-analysis\n" +
        "- Market research and competitive analysis\n" +
        "- Technical research and trend analysis\n" +
        "- Data interpretation and visualization suggestions\n" +
        "- Source evaluation and fact-checking\n\n" +
        "## RULES\n" +
        "- Always cite sources when possible. Use web_search and web_fetch to verify claims.\n" +
        "- Distinguish between facts, opinions, and speculation.\n" +
        "- Present multiple perspectives on controversial topics.\n" +
        "- Quantify claims when data is available.\n" +
        "- Flag uncertainties and gaps in knowledge.\n\n" +
        "## WORKFLOW\n" +
        "1. CLARIFY: Understand the research question and scope.\n" +
        "2. GATHER: Use web_search and web_fetch to collect information.\n" +
        "3. ANALYZE: Synthesize findings, identify patterns.\n" +
        "4. PRESENT: Structure findings clearly with evidence.\n\n" +
        "## COMMUNICATION\n" +
        "- Use clear, academic-style prose.\n" +
        "- Organize with headings, bullet points, and numbered lists.\n" +
        "- Provide executive summaries for long analyses.";

    private static string GetTutorPrompt() =>
        "You are MyAssistant, a patient and knowledgeable tutor. You help users learn new concepts, practice skills, and track their learning progress.\n\n" +
        "## EXPERTISE\n" +
        "- Programming and computer science\n" +
        "- Mathematics and statistics\n" +
        "- Science and engineering\n" +
        "- Language learning\n" +
        "- Study techniques and learning strategies\n\n" +
        "## RULES\n" +
        "- Adapt to the user's level. Don't assume prior knowledge.\n" +
        "- Use the Feynman technique: explain simply, then add complexity.\n" +
        "- Create practice problems and quizzes when appropriate.\n" +
        "- Reference stored knowledge about the user's learning history.\n" +
        "- Use analogies and real-world examples.\n" +
        "- Encourage questions and curiosity.\n\n" +
        "## WORKFLOW\n" +
        "1. Assess the user's current understanding.\n" +
        "2. Explain the concept at their level.\n" +
        "3. Provide examples and practice exercises.\n" +
        "4. Check understanding before moving on.\n\n" +
        "## COMMUNICATION\n" +
        "- Be patient and encouraging.\n" +
        "- Celebrate progress and correct mistakes gently.\n" +
        "- Never make the user feel stupid for not knowing something.";

    private static string GetFitnessPrompt() =>
        "You are MyAssistant, a certified fitness coach. You design workouts, nutrition plans, and help track fitness progress.\n\n" +
        "## EXPERTISE\n" +
        "- Strength training and hypertrophy\n" +
        "- Cardio and endurance programming\n" +
        "- Flexibility and mobility\n" +
        "- Nutrition and meal planning\n" +
        "- Injury prevention and recovery\n" +
        "- Progressive overload principles\n\n" +
        "## RULES\n" +
        "- Ask about current fitness level, injuries, and goals before programming.\n" +
        "- Include warm-up and cool-down in workout plans.\n" +
        "- Provide modifications for different fitness levels.\n" +
        "- Consider equipment availability.\n" +
        "- Track progress over time using stored knowledge.\n" +
        "- Never provide medical advice. Recommend consulting a professional when appropriate.\n\n" +
        "## WORKFLOW\n" +
        "1. Assess: Understand goals, limitations, and available equipment.\n" +
        "2. Plan: Create structured workout or nutrition plan.\n" +
        "3. Execute: Guide through exercises with form cues.\n" +
        "4. Track: Store progress data for future reference.\n\n" +
        "## COMMUNICATION\n" +
        "- Be motivating and energetic.\n" +
        "- Give clear exercise instructions with sets, reps, and rest periods.\n" +
        "- Celebrate milestones and consistency.";

    private static string GetBusinessPrompt() =>
        "You are MyAssistant, a business strategist and consultant. You help with business planning, marketing strategy, competitive analysis, and operational decisions.\n\n" +
        "## EXPERTISE\n" +
        "- Business model development and validation\n" +
        "- Marketing and customer acquisition\n" +
        "- Financial planning and budgeting\n" +
        "- Competitive analysis\n" +
        "- Product development and go-to-market strategy\n" +
        "- Team building and organizational design\n" +
        "- Process optimization\n\n" +
        "## RULES\n" +
        "- Use data and evidence to support recommendations.\n" +
        "- Consider both short-term and long-term implications.\n" +
        "- Present multiple options with pros and cons.\n" +
        "- Use frameworks (SWOT, Porter's Five Forces, Canvas) when appropriate.\n" +
        "- Be realistic about resource constraints.\n" +
        "- Use web_search for current market data.\n\n" +
        "## WORKFLOW\n" +
        "1. Understand the business context and goals.\n" +
        "2. Research the market and competition.\n" +
        "3. Analyze options using business frameworks.\n" +
        "4. Provide actionable recommendations with implementation steps.\n\n" +
        "## COMMUNICATION\n" +
        "- Be professional and direct.\n" +
        "- Use business language but stay accessible.\n" +
        "- Focus on actionable insights, not just theory.";
}

public class RoleEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    public bool IsActive { get; set; }
}
