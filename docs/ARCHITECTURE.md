# MyAssistant Technical Architecture Document

## 1. Overview
MyAssistant is a modular, extensible **WPF desktop application** that provides an AI‑driven chat experience.  The system is built around a **single agent** (`ChatAgent`) that communicates with a **streaming LLM provider** (Ollama or OpenAI‑compatible) and a **tool sandbox** that exposes a set of `ITool` implementations (file system, shell, web, knowledge‑base, etc.).  The UI layer (`MainWindowViewModel`) drives the conversation, while a MySQL‑backed `ISessionService` persists sessions and knowledge entries.

---

## 2. High‑Level Architecture
```
+-------------------+      +-------------------+      +-------------------+
|   UI (WPF)        |<---->|   Core (Agent)    |<---->|   Providers       |
|   MainWindowVM    |      |   ChatAgent       |      |   OllamaProvider  |
|   App.xaml.cs     |      |   ToolSandbox     |      |   OpenAICompat... |
+-------------------+      +-------------------+      +-------------------+
        ^                         ^                         ^
        |                         |                         |
        |                         |                         |
        |                         |                         |
        |   +---------------------+---------------------+   |
        +---|   Tools (ITool implementations)            |
            |   - ReadFileTool, WriteFileTool, EditFileTool |
            |   - BashTool, WebFetchTool, WebSearchTool    |
            |   - StoreKnowledgeTool, SearchKnowledgeTool |
            +---------------------------------------------+
```

* **UI Layer** – WPF windows (`MainWindow`, `SettingsWindow`) and view‑models in `MyAssistant.Main`.  Dependency injection (DI) is configured in `App.xaml.cs`.
* **Core Layer** – Contains the agent (`ChatAgent`), tool sandbox (`ToolSandbox`), and the generic `ITool` contract.
* **Providers Layer** – Implements `IStreamingProvider` for various LLM back‑ends (`OllamaProvider`, `OpenAICompatibleProvider`).
* **Abstractions Layer** – Defines contracts (`IAgent`, `ITool`, `ISessionService`) and shared data models (`AgentCall`, `ChatMessage`, `ProviderConfig`).
* **Persistence Layer** – `MySqlSessionService` implements `ISessionService` and stores sessions, messages, and knowledge entries in MySQL.

---

## 3. Key Components
### 3.1. Abstractions
| Interface / Record | File | Responsibility |
|--------------------|------|-----------------|
| `IAgent` | `MyAssistant.Abstractions/Agent/IAgent.cs` (lines 9‑16) | Runs a chat session, exposes `RunAsync`, cancellation, system‑prompt handling. |
| `ITool` | `MyAssistant.Abstractions/Agent/ITool.cs` (lines 6‑12) | Defines a tool (`Name`, `Description`, `Definition`) and async `ExecuteAsync`. |
| `ISessionService` | `MyAssistant.Abstractions/Services/ISessionService.cs` (lines 6‑21) | CRUD for sessions, messages and knowledge entries. |
| Data models (`AgentCall`, `ChatMessage`, `ProviderConfig`, `ToolDefinition`, etc.) | `IAgent.cs` (lines 19‑88) | Standardised payloads exchanged between UI, agent and providers. |

### 3.2. Core
* **`ChatAgent`** – Implements `IAgent` (see `ChatAgent.cs` lines 12‑38, 57‑210).  It builds the message history, invokes the selected `IStreamingProvider`, parses streaming deltas, detects embedded tool calls, and orchestrates tool execution via `ExecuteToolCalls`.
* **`ToolSandbox`** – Central security gate (see `ToolSandbox.cs` lines 3‑45).  Validates file paths, resolves relative paths, and blocks dangerous system directories/commands.
* **`ToolJsonOptions` & `PathHelper`** – Helper utilities for case‑insensitive JSON deserialization and path resolution (`PathHelper.cs` lines 15‑49).
* **Tools** – Each tool implements `ITool`.  Examples:
  * `ReadFileTool` (`ReadFileTool.cs` lines 6‑48) – reads a file with line numbers.
  * `BashTool` (`BashTool.cs` lines 8‑106) – executes shell commands with sandbox checks.
  * `WebFetchTool`, `WebSearchTool` – perform HTTP fetch / web search.
  * Knowledge tools (`StoreKnowledgeTool`, `SearchKnowledgeTool`, `ManageKnowledgeTool`) – interact with `ISessionService`.

### 3.3. Providers
* **`IStreamingProvider`** – Contract for streaming LLM responses (`IStreamingProvider.cs` lines 5‑30).
* **`OllamaProvider`** – HTTP client for Ollama API, streams JSON lines, translates them to `StreamDelta` objects (`OllamaProvider.cs` lines 12‑142). Handles time‑outs, error handling, and model listing.
* **`OpenAICompatibleProvider`** – Similar implementation for OpenAI‑style APIs (not shown but follows same pattern).

### 3.4. UI / Application Startup
* **`App.xaml.cs`** – Entry point (lines 23‑108). Sets up environment variables, loads configuration, configures DI, registers all `ITool` implementations, creates the `ChatAgent` with the selected provider, and launches `MainWindow`.
* **`MainWindowViewModel`** – Orchestrates user input, calls `IAgent.RunAsync`, updates UI state, and refreshes provider/model information (lines 20‑173). Uses `AsyncRelayCommand` for async commands.
* **`SettingsRepository`** – Reads/writes configuration and tool state to a MySQL database (used during startup).

---

## 4. Data Flow
1. **Startup** – `App.OnStartup` loads `AppConfig`, resolves the workspace root (`ToolSandbox.WorkingDirectory`), registers tools, creates `ChatAgent` via DI.
2. **User sends a prompt** – `MainWindowViewModel.SendCommand` invokes `IAgent.RunAsync` with an `AgentCall` containing session id, prompt, provider config, model id, etc.
3. **Agent builds message list** – Adds system prompt (including workspace hint) and optional history.
4. **Provider streaming** – `ChatAgent` calls `provider.StreamAsync`.  The provider yields `StreamDelta` objects (text, thinking, tool‑call start/args, usage, error, done).
5. **Tool call detection** – Agent collects pending tool calls, emits `ToolCallStart` events, then executes each tool via `ExecuteToolCalls`.
6. **Tool execution** – Each `ITool` receives a `ToolCallContext` with the arguments JSON and the working directory.  The sandbox validates paths/commands before performing the action.
7. **Tool results** – Returned as `ChatToolResult` and added to the message list as a `tool` role message.  Agent then loops for the next iteration (up to `_maxIterations`).
8. **Session persistence** – `MainWindowViewModel` uses `ISessionService` to store messages and knowledge entries; the UI reflects token usage and status.

---

## 5. Configuration & Extensibility
* **Configuration** – `AppConfig` (JSON in `appsettings.json`) contains provider definitions, UI settings, and active provider.  `App.CreateProviderConfig` builds a `ProviderConfig` for the agent.
* **Provider detection** – `App.DetectProviderKind` inspects the base URL or explicit `kind` string (lines 130‑151).
* **Tool registration** – All tools are registered as `Singleton<ITool>` in DI.  The `TaskAgentTool` creates sub‑agents for nested tool calls, enabling recursion.
* **Adding a new tool** – Implement `ITool`, register it in `App.xaml.cs`, and optionally expose a UI entry.

---

## 6. Security Model
* **File‑system sandbox** – `ToolSandbox.IsPathAllowed` blocks access to Windows system directories (lines 5‑13).  `ValidateAndResolve` throws if a path is disallowed (lines 36‑46).
* **Command sandbox** – `ToolSandbox.IsCommandAllowed` blocks destructive shell commands (lines 48‑68).
* **Provider isolation** – Each provider runs in its own `HttpClient` with optional API key handling.

---

## 7. Known Issues & Limitations
1. **Hard‑coded environment variables** for MySQL credentials in `App.xaml.cs` (lines 47‑51) – should be externalised.
2. **Tool sandbox path list** may need updates for non‑Windows platforms.
3. **`LooksLikeHallucination`** heuristics in `ChatAgent` (lines 326‑347) may produce false positives for legitimate summarisation.
4. **No graceful shutdown of long‑running provider streams** beyond cancellation token.
5. **UI thread safety** – `MainWindowViewModel` updates observable collections from async contexts; may require `Dispatcher` for cross‑thread updates.

---

## 8. Build & Run
```bash
# From the repository root
dotnet build MyAssistant.sln
# Run the WPF app (Windows only)
dotnet run --project MyAssistant.Main/MyAssistant.Main.csproj
```
The application expects a running Ollama instance at `http://localhost:11434` or a reachable OpenAI‑compatible endpoint as defined in `appsettings.json`.

---

*Document generated by MyAssistant AI coding assistant.*