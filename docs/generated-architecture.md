# MyAssistant Technical Architecture Document

## Overview
MyAssistant is a WPF‑based AI coding assistant that combines multiple LLM providers with a rich set of **tools** for filesystem, web, and knowledge‑base interactions. The application follows a **layered architecture**:

1. **Presentation Layer** – WPF UI (`MainWindow`, `SettingsWindow`) with MVVM (`MainWindowViewModel`).
2. **Application Layer** – Core orchestration (`ChatAgent`) and tool execution (`ITool` implementations).
3. **Infrastructure Layer** – Provider adapters (`OllamaProvider`, `OpenAICompatibleProvider`) exposing a unified streaming interface.
4. **Data Layer** – Session and knowledge persistence (`ISessionService` implementations, e.g., `MySqlSessionService` with MySQL + hybrid semantic recall).
5. **Abstraction Layer** – Contracts that decouple the above layers (`IAgent`, `ITool`, `IStreamingProvider`, configuration models).

The architecture enables **plug‑and‑play** of new providers or tools without touching the UI or core logic.

---

## 1. Abstraction Layer
### Interfaces & Records
| Interface / Record | Location | Purpose |
|--------------------|----------|---------|
| `IAgent` | `MyAssistant.Abstractions\Agent\IAgent.cs:9-16` | Defines the chat agent contract: `RunAsync`, `IsBusy`, `Cancel`, `UpdateSystemPrompt`, and `SystemPrompt` property. |
| `ITool` | `MyAssistant.Abstractions\Agent\ITool.cs:6-12` | Represents a tool callable by the agent. Exposes `Name`, `Description`, `Definition` and `ExecuteAsync`. |
| `IStreamingProvider` | `MyAssistant.Providers\IStreamingProvider.cs:5-12` | Unified streaming API for LLM providers. Returns `IAsyncEnumerable<StreamDelta>`. |
| `ProviderConfig` | `MyAssistant.Abstractions\Agent\IAgent.cs:30-35` | Holds provider connection details (Id, BaseUrl, ApiKey, Kind). |
| `AgentCall` | `MyAssistant.Abstractions\Agent\IAgent.cs:19-28` | Request payload for a single agent turn (prompt, model, attachments, etc.). |
| `ChatMessage` / `ChatToolCall` / `ChatToolResult` | `MyAssistant.Abstractions\Agent\IAgent.cs:58-71` | Data structures used for the conversation history and tool calls. |
| `ProviderKind` enum | `MyAssistant.Abstractions\Agent\IAgent.cs:37-44` | Enumerates supported provider families (OpenAICompatible, Ollama, Gemini, Groq, Mistral). |

These abstractions allow the core to remain provider‑agnostic and testable.

---

## 2. Core Logic Layer
### 2.1 `ChatAgent`
Implemented in `MyAssistant.Core\Agent\ChatAgent.cs` (see lines **12‑208**). Key responsibilities:
- **Message orchestration** – builds the initial system prompt, merges user history, and sends a `ProviderRequest` to the selected `IStreamingProvider`.
- **Streaming handling** – consumes `StreamDelta` from the provider, yielding `AgentEvent` objects (`TextDelta`, `ThinkingDelta`, `ToolCallStart`, `ToolCallResult`, `Usage`, `Done`).
- **Tool call resolution** – maps tool names to registered `ITool` instances, executes them asynchronously (`ExecuteToolCalls`), and injects results back into the conversation.
- **Safety mechanisms** – detects hallucination (`LooksLikeHallucination`) and forces the agent to call `list_directory`/`read_file` before describing code (lines **162‑170**).
- **Cancellation & iteration limits** – respects a per‑call `CancellationTokenSource` and a configurable `maxIterations` (default 30).

### 2.2 Session Service
`MySqlSessionService` (`MyAssistant.Main\Services\MySqlSessionService.cs`) is the production implementation of `ISessionService`, backed by a MySQL database (`mycoderdb`). It stores:
- `Session` metadata (title, timestamps, token usage) in the `Sessions` table.
- `ChatMessage` history per session in the `Messages` table.
- `KnowledgeEntry` items for the long‑term‑memory feature in the `Knowledge` table (per-session scope plus a shared `'global'` category), including an `Embedding` column with Gemini embedding vectors (`gemini-embedding-001`).

Knowledge recall is **hybrid**: a MySQL FULLTEXT keyword query merged with cosine-similarity ranking over stored embeddings. Connection settings are read from `MYCODER_DB_*` environment variables via `DbConfig`. A lightweight in-memory fake (`FakeSessionService`) exists in the `MyAssistant.Tests` project for unit-testing tools.

### 2.3 Tool Sandbox
`ToolSandbox` (`MyAssistant.Core\Tools\ToolSandbox.cs:5‑70`) centralises security:
- Maintains a whitelist of **blocked paths** (Windows system directories, Program Files, etc.) and validates any file path before a tool accesses the filesystem (`IsPathAllowed`).
- Provides `ValidateAndResolve` to resolve relative paths against the workspace root while enforcing the path policy.
- Restricts shell commands via `IsCommandAllowed` (blocks destructive commands like `format`, `del /s`, `shutdown`).

### 2.4 Tools
All tools implement `ITool`. Representative examples:
- **`ReadFileTool`** – reads a file with line numbers.
- **`WriteFileTool`** – writes content to a file.
- **`GlobTool`** – performs recursive file pattern matching.
- **`BashTool`** – executes a shell command with sandbox checks (`ToolSandbox.IsCommandAllowed`).
- **`WebFetchTool` / `WebSearchTool`** – perform HTTP fetches and web searches.
- **`TaskAgentTool`** – spawns a sub‑agent to solve complex subtasks.
- **Knowledge tools** (`StoreKnowledgeTool`, `SearchKnowledgeTool`, `ManageKnowledgeTool`) interact with `ISessionService` to persist and query knowledge entries.

---

## 3. Provider Layer
### 3.1 `IStreamingProvider`
Defines the contract for streaming LLM responses (`StreamAsync`) and model enumeration (`ListModelsAsync`).

### 3.2 `OllamaProvider`
Implemented in `MyAssistant.Providers\OllamaProvider.cs` (lines **12‑210**). It:
- Sends a POST to `{baseUrl}/api/chat` with a JSON body built by `BuildRequest`.
- Parses the Ollama streaming format, emitting `StreamDelta` events for text, thinking, tool calls, and usage.
- Handles time‑outs (900 s) and error translation to `StreamDelta.Error`.

### 3.3 `OpenAICompatibleProvider`
Implemented in `MyAssistant.Providers\OpenAICompatibleProvider.cs` (lines **12‑271**). It:
- Normalises Gemini URLs to an OpenAI‑compatible endpoint.
- Sends a POST to `{baseUrl}/chat/completions` with `stream:true`.
- Parses the OpenAI SSE format (`data:` lines) and extracts tool calls, text, and usage tokens.
- Supports model listing via the `/models` endpoint.

Both providers share the same `ProviderRequest` shape, allowing `ChatAgent` to remain agnostic.

---

## 4. Presentation Layer (WPF UI)
### 4.1 Application Bootstrap (`App.xaml.cs`)
- Configures DI container (`ServiceCollection`) with **session service**, **all tools**, and the **agent** (lines **64‑110**).
- Loads configuration from a MySQL‑backed `SettingsRepository` and falls back to a JSON file (`MigrateFromJsonAsync`).
- Sets the workspace root (`ToolSandbox.WorkingDirectory = GetWorkspaceRoot()`).
- Instantiates `MainWindow` and shows it.

### 4.2 `MainWindowViewModel`
Located in `MyAssistant.Main\ViewModels\MainWindowViewModel.cs` (lines **20‑938**). Responsibilities:
- Exposes observable collections for **messages**, **sessions**, **providers**, and **models**.
- Handles user input (`SendCommand`) by constructing an `AgentCall` and delegating to `IAgent.RunAsync`.
- Implements **middleware** before sending the prompt:
  - Automatic knowledge injection (lines **430‑470**).
  - File‑mention detection and content injection (lines **472‑496**).
  - Git context injection (lines **521‑539**).
  - Project‑structure injection on first turn (lines **543‑555**).
- Updates UI state (status bar, token usage, cost calculation) based on `AgentEvent` streams.
- Provides session management (new, switch, rename, delete) and markdown export.

### 4.3 Views
`MainWindow.xaml` defines the chat UI; `SettingsWindow.xaml` handles provider configuration. Interaction is driven via data bindings to the view‑model.

---

## 5. Data Flow
1. **User input** → `MainWindowViewModel.SendCommand` → builds `AgentCall`.
2. **Middleware** enriches the prompt with knowledge, file contents, git diff, and project tree.
3. `ChatAgent.RunAsync` receives the call, creates a `ProviderRequest`, selects the active `IStreamingProvider` (`CreateProviderFromConfig`).
4. Provider streams `StreamDelta` objects back to `ChatAgent`.
5. `ChatAgent` converts each delta to an `AgentEvent` and yields it to the view‑model.
6. The view‑model updates the UI (messages, thinking, tool call status) and persists messages/knowledge via `ISessionService`.
7. If a tool call is encountered, `ChatAgent` executes the corresponding `ITool` (sandboxed) and feeds the result back as a `ToolCallResult`.
8. After completion, session metadata (token usage, cost) is stored.

---

## 6. Security & Safety
- **Path sandbox** (`ToolSandbox.IsPathAllowed`) prevents access to OS directories (lines **7‑13** in `ToolSandbox.cs`).
- **Command sandbox** (`ToolSandbox.IsCommandAllowed`) blocks dangerous shell commands (lines **52‑68**).
- **Hallucination guard** in `ChatAgent` forces the agent to read files before describing them (`LooksLikeHallucination`, lines **349‑373**).
- **Cancellation tokens** with generous time‑outs protect against runaway processes.

---

## 7. Extensibility Points
| Extension | How to add |
|-----------|------------|
| New LLM provider | Implement `IStreamingProvider` and register in DI (see `CreateProviderFromConfig` in `App.xaml.cs`). |
| New tool | Create a class implementing `ITool`, ensure arguments are defined via `ToolDefinition`, and register in the service collection. |
| Persistent session store | Implement `ISessionService` (e.g., a SQL or NoSQL backend) and replace the registration in `App.xaml.cs`. |
| Additional UI components | Follow the MVVM pattern: add a ViewModel, bind to XAML, and inject required services via DI. |

---

## 8. Known Issues & Limitations
- **Tool sandbox bypass**: Path validation is simple string‑based; symbolic links or UNC paths could circumvent restrictions (**ToolSandbox.cs:7‑13**).
- **Embedding latency**: Knowledge stores and searches make a Gemini embedding API call (~200–800 ms); failures degrade gracefully to keyword-only recall.
- **Provider configuration complexity**: Multiple provider families require manual URL and key management; errors surface as generic `StreamDelta.Error` messages.
- **UI responsiveness**: Large file reads or extensive git diffs run on the UI thread via async/await but may still cause temporary freezes.

---

## 9. Glossary
- **Agent** – The orchestrator that talks to an LLM and decides when to invoke tools.
- **Tool** – A small, sandboxed capability (file I/O, shell, web, knowledge) callable by the agent.
- **Provider** – Backend service (Ollama, OpenAI, Gemini, etc.) that generates LLM responses.
- **Session** – A persisted conversation with its own message history and knowledge base.
- **Knowledge entry** – User‑provided facts stored for later retrieval (`store_knowledge`).

---

*Document generated automatically by MyAssistant analysis on $(date).*