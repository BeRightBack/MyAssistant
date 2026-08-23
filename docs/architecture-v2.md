# MyAssistant Project Architecture

This is a sophisticated AI coding assistant application that combines multiple AI providers with extensive file system tools to analyze and understand codebases.

## Core Components

### 1. **Abstractions Layer**
The Abstractions layer defines core interfaces for agents, sessions, and tools.

- `IAgent` interface in `MyAssistant.Abstractions\Agent\IAgent.cs` defines the main chat agent functionality
- `ISessionService` in `MyAssistant.Abstractions\Services\ISessionService.cs` handles session management and knowledge storage
- `ITool` interface in `MyAssistant.Abstractions\Tools\ITool.cs` defines tool execution contracts
- Data models like `AgentCall`, `ProviderConfig`, `ChatMessage`, etc., in `MyAssistant.Abstractions\Models`

### 2. **Core Layer**
The Core layer implements the main application logic.

- `ChatAgent` in `MyAssistant.Core\Agent\ChatAgent.cs`: The main agent implementation that orchestrates interactions with AI providers and tools
- Tools are implemented as separate classes in `MyAssistant.Core\Tools`
- `ToolSandbox` in `MyAssistant.Core\Tools\ToolSandbox.cs` manages the execution environment for tools

### 3. **Providers Layer**
The Providers layer contains implementations for different AI services.

- `OllamaProvider` in `MyAssistant.Providers\OllamaProvider.cs`: Implements connection to Ollama API for local LLMs
- `OpenAICompatibleProvider` in `MyAssistant.Providers\OpenAICompatibleProvider.cs`: Generic provider for OpenAI-compatible APIs (including Gemini, Groq, Mistral)
- Both providers support streaming responses and tool calling

### 4. **Main Application**
The Main application contains the WPF UI and session management.

- WPF-based UI with `MainWindowViewModel` in `MyAssistant.Main\ViewModels\MainWindowViewModel.cs` managing the chat interface
- Session management system with MySQL backend
- Tool configuration and activation system
- Middleware for knowledge injection and context enhancement

## Key Features

1. **Multi-Provider Support**: Supports Ollama, OpenAI, Gemini, Groq, and Mistral providers (lines 23-40 in `MyAssistant.Providers\OpenAICompatibleProvider.cs`)
2. **Intelligent Tool Calling**: Tools can be automatically called based on user input patterns
3. **Knowledge Management**: Automatic storage of information when users say "remember that..." (lines 412-426 in `MyAssistant.Main\ViewModels\MainWindowViewModel.cs`)
4. **Context Enhancement**: 
   - Auto-injects relevant knowledge from memory (lines 68-71 in `MyAssistant.Core\Agent\ChatAgent.cs`)
   - Detects file mentions and injects their contents (lines 145-160 in `MyAssistant.Core\Agent\ChatAgent.cs`)
   - Shows recent git changes and project structure
5. **File System Integration**: Extensive tools for reading files, directories, searching code with grep, etc.
6. **Session Management**: Complete chat history management with ability to switch between sessions

## Architecture Pattern

The application follows a layered architecture:
- **Presentation Layer**: WPF UI with ViewModel pattern (`MyAssistant.Main\ViewModels\MainWindowViewModel.cs`)
- **Application Layer**: Core logic in `ChatAgent` and tools (`MyAssistant.Core\Agent\ChatAgent.cs`, `MyAssistant.Core\Tools`)
- **Infrastructure Layer**: Providers for different AI services (`MyAssistant.Providers\OllamaProvider.cs`, `MyAssistant.Providers\OpenAICompatibleProvider.cs`)
- **Data Layer**: Session and knowledge management through `ISessionService` (`MyAssistant.Abstractions\Services\ISessionService.cs`)

## Key Technical Details

1. **Tool Execution**: Tools are called through the `ITool` interface with JSON argument parsing (lines 25-35 in `MyAssistant.Core\Agent\ChatAgent.cs`)
2. **Streaming Responses**: Uses async/await pattern to stream responses from providers
3. **CancellationToken Management**: Proper cancellation handling for long-running operations (lines 60-70 in `MyAssistant.Core\Agent\ChatAgent.cs`)
4. **Security**: Sandboxed execution environment (`ToolSandbox`) prevents unauthorized file access (lines 15-25 in `MyAssistant.Core\Tools\ToolSandbox.cs`)
5. **Cost Tracking**: Pricing per token calculation for cloud services (lines 30-40 in `MyAssistant.Providers\OpenAICompatibleProvider.cs`)

## Known Issues and Limitations

1. **[HIGH] Tool Sandbox Security Vulnerability** - The `ToolSandbox` implementation may not fully prevent file system access bypasses, as it relies on simple path validation that could be circumvented with symbolic links or other techniques (MyAssistant.Core\Tools\ToolSandbox.cs:20-30)

2. **[MEDIUM] Memory Management Issues** - The chat history and knowledge storage can grow indefinitely without cleanup mechanisms, potentially causing memory leaks in long-running sessions (MyAssistant.Core\Agent\ChatAgent.cs:68-71)

3. **[MEDIUM] Provider Configuration Complexity** - The multiple provider implementations create complexity in configuration management and switching between providers (MyAssistant.Providers\OpenAICompatibleProvider.cs:23-40)

4. **[LOW] UI Responsiveness with Large File Handling** - When large files are read or analyzed, the UI may become unresponsive during tool execution (MyAssistant.Core\Tools\ReadFileTool.cs:30-40)

5. **[LOW] Limited Error Recovery** - The application has limited recovery mechanisms when AI provider connections fail, potentially causing session interruptions (MyAssistant.Core\Agent\ChatAgent.cs:65-75)