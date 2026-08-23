# MyAssistant

A local-first, ChatGPT-style desktop AI coding assistant for Windows — built with **WPF / .NET 10**.

It combines multi-provider LLM streaming, a tool-using agent loop, and a **MySQL-backed long-term memory** that persists facts across sessions and recalls them semantically.

## Features

### Agent
- Streaming responses from OpenAI-compatible endpoints (OpenRouter, OpenAI, Groq, Mistral), Ollama, and Google Gemini
- Automatic tool calling with safety guardrails: hallucination detection, premature-stop nudges, repeat breaker, empty-response breaker
- Context budget manager — token estimation, oversized tool-result truncation, atomic history grouping (never splits an assistant tool-call from its results)
- History sanitizer that repairs sessions containing orphaned tool calls

### Tools
`read_file` `write_file` `edit_file` `list_directory` `glob` `grep` `bash` `web_fetch` `web_search` `task_agent`
plus knowledge tools: `store_knowledge` `search_knowledge` `manage_knowledge`

File/shell tools run inside a sandbox (`ToolSandbox`) that blocks system paths and destructive commands.

### Long-term memory
- Facts are captured automatically from trigger phrases ("remember that…", "my timezone is…") or saved by the agent via `store_knowledge`, with duplicate suppression
- Stored per-session plus a shared `'global'` category in a MySQL `Knowledge` table
- Recall is **hybrid**: MySQL FULLTEXT keyword search merged with cosine-similarity ranking over Gemini embeddings (`gemini-embedding-001`, 3072 dims); current-session entries get a small relevance boost
- Cross-session by design — memories outlive the chat that created them

### UI
- Dark / light themes, markdown rendering, live tool-call status chips
- Session history, rename/delete, token usage and cost tracking
- Settings window for providers, models, and agent limits

## Getting started

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- MySQL 8+ running locally (schema is created automatically on first launch)

### Configure database credentials
Credentials are read from user-scope environment variables (never stored in source):

```powershell
[Environment]::SetEnvironmentVariable("MYCODER_DB_HOST", "localhost", "User")
[Environment]::SetEnvironmentVariable("MYCODER_DB_PORT", "3306", "User")
[Environment]::SetEnvironmentVariable("MYCODER_DB_NAME", "mycoderdb", "User")
[Environment]::SetEnvironmentVariable("MYCODER_DB_USER", "<your-user>", "User")
[Environment]::SetEnvironmentVariable("MYCODER_DB_PASS", "<your-password>", "User")
```

If a process starts before these were set, `DbConfig` falls back to reading them directly from `HKCU\Environment`.

### Configure a provider
Launch the app → gear icon → add a provider (e.g. OpenRouter with your API key) → pick a model.
For semantic recall, also configure a `gemini` provider entry — its API key powers embeddings.

### Build & run

```powershell
dotnet build MyAssistant.sln -c Release
MyAssistant.Main\bin\Release\net10.0-windows\MyAssistant.Main.exe
```

### Run tests

```powershell
dotnet test MyAssistant.Tests\MyAssistant.Tests.csproj
```

## Configuration reference

| Setting | Where | Default |
|---|---|---|
| `MYCODER_DB_HOST/PORT/NAME/USER/PASS/SSL/ALLOW_PUBKEY` | env vars | localhost:3306 |
| `Agent.MaxIterations` | appsettings.json | 20 |
| `Agent.MaxContextTokens` | appsettings.json | 131072 |

Logs are written to `%LOCALAPPDATA%\MyAssistant\logs\`.

## Architecture

| Project | Purpose |
|---|---|
| `MyAssistant.Abstractions` | Contracts: `IAgent`, `ITool`, `IStreamingProvider`, message/config records |
| `MyAssistant.Core` | `ChatAgent` loop, tool sandbox + tools, memory helpers (token math, sanitizers) |
| `MyAssistant.Providers` | `OpenAICompatibleProvider`, `OllamaProvider`, `GeminiEmbeddingClient` |
| `MyAssistant.Main` | WPF UI, ViewModels, MySQL services (`SettingsRepository`, `MySqlSessionService`) |
| `MyAssistant.Tests` | xUnit suite (65 tests): context trimming, sanitizers, tools, scripted-provider agent behavior |

More detail in [`docs/technical-architecture.md`](docs/technical-architecture.md).

## License

All rights reserved. Private project.
