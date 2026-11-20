using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Ch09.McpConsumer;

/// <summary>The agent the live run and the self-test both build: tools come from MCP, nothing else changes.</summary>
public static class CatalogAgent
{
    public const string Instructions =
        "You are a product assistant. Use the available tools to look up products and check inventory. Be helpful and concise.";

    // McpClientTool derives from AIFunction, so the discovered list plugs straight
    // into the tools parameter, exactly like the inline tools of the tools chapter.
    public static ChatClientAgent Create(IChatClient chatClient, IList<McpClientTool> mcpTools) =>
        new(chatClient,
            instructions: Instructions,
            name: "McpProductAssistant",
            description: "A product assistant powered by tools from an MCP server",
            tools: [.. mcpTools]);
}
