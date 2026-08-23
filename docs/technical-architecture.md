# MyAssistant Technical Architecture

## Overview

MyAssistant is a sophisticated AI-powered development assistant designed to help developers navigate and understand complex codebases. It provides an intelligent interface for exploring code, reading files, executing commands, and leveraging AI to analyze source code.

## System Architecture

### High-Level Components

The system follows a layered architecture with the following key components:

1. **Core Logic Layer** - Contains core business logic
2. **Abstraction Layer** - Defines interfaces and contracts
3. **Provider Layer** - Integrates with different AI providers
4. **Tools Layer** - Implements functionality for code exploration and manipulation
5. **UI Layer** - Provides user interface for interaction

## Project Structure

```
MyAssistant/
├── MyAssistant.Abstractions/       # Core interfaces and contracts
│   ├── Agent/                       # Agent-related abstractions
│   ├── Configuration/               # Configuration models
│   └── Services/                    # Service interfaces
├── MyAssistant.Core/                # Core implementation logic
│   ├── Agent/                       # Chat agent implementation
│   ├── Services/                    # Session service implementations
│   └── Tools/                       # Tool implementations for code exploration
├── MyAssistant.Providers/           # AI provider integrations
│   ├── OpenAICompatibleProvider.cs  # OpenAI-compatible API integration
│   ├── OllamaProvider.cs            # Ollama integration
│   └── IStreamingProvider.cs        # Provider interface
└── MyAssistant.Main/                # UI and application startup
    ├── ViewModels/                  # WPF view models
    ├── Views/                       # WPF views
    └── App.xaml.cs                  # Application startup and DI configuration
```

## Detailed Architecture

### 1. Abstraction Layer

The abstraction layer defines interfaces that decouple the system from implementation details:

- **IAgent** - Defines agent behavior for running conversations
- **ITool** - Interface for tools that can be executed by the agent
- **ISessionService** - Manages chat sessions and conversation history
- **ProviderConfiguration** - Configuration models for AI providers

### 2. Core Logic Layer

#### Chat Agent (`ChatAgent.cs`)
The core AI agent that orchestrates conversations with the LLM:

- Implements streaming response handling
- Manages tool calling with automatic execution
- Handles conversation history management
- Includes safety mechanisms to prevent hallucination
- Supports cancellation and maximum iteration limits
- Integrates with different AI provider backends

#### Session Management (`MySqlSessionService.cs`)
Handles chat session persistence backed by a MySQL database (`mycoderdb`):

- Creates, retrieves, and manages chat sessions
- Stores conversation history
- Manages knowledge storage and retrieval in the `Knowledge` table (per-session rows plus a special `'global'` category shared across sessions)
- Hybrid long-term-memory recall: MySQL FULLTEXT keyword search merged with semantic ranking via Gemini embeddings (`gemini-embedding-001`, cosine similarity over stored vectors)
- Connection settings come from `MYCODER_DB_*` user-scope environment variables

### 3. Tool Layer

The system supports a rich set of tools for code exploration and manipulation:

- **File Operations**: `read_file`, `write_file`, `edit_file`
- **Directory Operations**: `list_directory`, `glob`, `grep`  
- **System Commands**: `bash` tool for executing shell commands
- **Web Operations**: `web_fetch`, `web_search` for internet research
- **Knowledge Management**: `store_knowledge`, `search_knowledge`, `manage_knowledge`
- **Task Management**: `task_agent` for breaking down complex tasks

### 4. Provider Layer

Supports integration with various AI providers:

- **OpenAI-compatible Providers** - Supports OpenAI, Azure OpenAI, and other compatible APIs
- **Ollama Provider** - Local LLM inference using Ollama
- **Gemini Provider** - Google's Gemini API integration
- **Groq Provider** - Groq API integration
- **Mistral Provider** - Mistral AI API integration

### 5. UI Layer

The WPF-based user interface:

- **MainWindow** - Main application window with chat interface
- **SettingsWindow** - Configuration management for providers and settings
- **ViewModels** - MVVM pattern implementation for UI state management
- **Themes** - Support for light/dark themes

## Key Features

### 1. Safety Mechanisms
- Prevents hallucination by ensuring files are actually read before being described
- Blocks access to system directories and dangerous commands
- Implements timeout protection for long-running operations

### 2. Code Exploration Tools
- Deep file reading with line numbers
- Directory listing and recursive globbing
- File content search with grep-like functionality
- Web research capabilities

### 3. Knowledge Management
- Automatic capture of trigger phrases ("remember that...", "my timezone is...") by UI middleware and the agent's `store_knowledge` tool, with duplicate suppression
- Store knowledge in session-specific repositories (plus a cross-session `'global'` category)
- Search through stored knowledge using hybrid keyword (FULLTEXT) + semantic (embedding cosine) ranking
- Manage knowledge categories and metadata; entries persist in MySQL across restarts

### 4. Task Decomposition
- `task_agent` tool that can break down complex tasks into subtasks
- Automatic execution of tools within task decomposition

### 5. Provider Flexibility
- Easy switching between different AI providers
- Configuration-based provider management
- Support for local LLMs (Ollama) and cloud APIs

## Data Flow

1. **User Input** → UI Layer → ViewModel → Agent Layer
2. **Agent Processing** → Tool Resolution → Tool Execution → Provider Integration
3. **Provider Response** → Streaming Handler → Agent Layer → UI Layer
4. **Session Management** → Persistence Layer → Data Storage

## Security Considerations

### Path Safety
- All file operations are restricted to the configured workspace root
- Blocked system paths prevent access to critical OS directories
- Path validation ensures only safe operations are performed

### Command Safety
- Shell command execution is limited to safe commands
- Dangerous commands are blocked (shutdown, delete, etc.)
- Execution timeouts prevent long-running processes

## Configuration

Configuration is managed through:
- Application settings in `appsettings.json`
- Dynamic settings stored in a repository
- Provider configurations for different AI services
- User preferences for UI and behavior
- Database credentials via `MYCODER_DB_HOST`, `MYCODER_DB_PORT`, `MYCODER_DB_NAME`, `MYCODER_DB_USER`, `MYCODER_DB_PASS`, `MYCODER_DB_SSL`, `MYCODER_DB_ALLOW_PUBKEY` environment variables (secrets are not stored in source or config files)

## Technology Stack

### Core Technologies
- **.NET 10** - Main framework
- **WPF** - UI framework
- **Microsoft.Extensions.DependencyInjection** - Dependency injection
- **System.Text.Json** - JSON serialization
- **HtmlAgilityPack** - HTML parsing

### AI Integration
- **OpenAI API Compatible** - Support for various OpenAI-compatible providers
- **Ollama** - Local LLM inference
- **Google Gemini** - Google's generative AI
- **Groq** - High-speed inference API
- **Mistral** - Open-source models

## Deployment

The application is designed to be:
- Cross-platform (Windows)
- Configurable for different AI providers
- Extensible with custom tools
- Secure against malicious file operations
- Easily deployable with proper configuration management

## Future Extensibility

The architecture supports:
- Additional AI provider integrations
- New tool implementations
- Custom session storage backends
- Extended knowledge management features
- Multi-user support through database persistence