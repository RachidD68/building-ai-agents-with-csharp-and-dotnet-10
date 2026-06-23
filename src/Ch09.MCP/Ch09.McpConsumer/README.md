# Chapter 9 -- The Model Context Protocol (MCP Consumer)

Connects to the Ch09.McpServer via stdio transport, discovers its tools, and feeds them to a ChatClientAgent. The agent code is identical to Chapter 8 -- the only difference is where the tools come from.

## Key Concepts

| Concept | Description |
|---------|-------------|
| `StdioClientTransport` | Launches the MCP server as a child process |
| `McpClient.CreateAsync()` | Connects and negotiates MCP capabilities |
| `ListToolsAsync()` | Discovers available tools from the server |
| `McpClientTool` | Extends `AITool`, plugs directly into `ChatClientAgentOptions.Tools` |

## Prerequisites

Build the MCP server first:

```bash
dotnet build src/Ch09.MCP/Ch09.McpServer
```

## Running

```bash
dotnet run --project src/Ch09.MCP/Ch09.McpConsumer
```
