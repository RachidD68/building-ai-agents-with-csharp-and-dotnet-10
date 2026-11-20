using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ch24.KillSwitch;

public static class CapabilityGuards
{
    /// <summary>
    /// The tool-side switch: function-invocation middleware asks the registry
    /// before every tool call, so the model's request for a withdrawn tool is
    /// answered with a refusal and the function never runs. The operator's
    /// reason stays in the audit; the model and the caller get a fixed message.
    /// </summary>
    public static AIAgent WithCapabilityGuard(this AIAgent agent, CapabilityRegistry registry) =>
        agent.AsBuilder()
            .Use(async (innerAgent, context, next, cancellationToken) =>
            {
                string tool = context.Function.Name;
                if (registry.IsWithdrawn(tool, out _))
                {
                    registry.RecordDenial(tool, "tool");
                    return $"capability_withdrawn: '{tool}' is paused. Nothing was submitted.";
                }
                return await next(context, cancellationToken);
            })
            .Build();

    /// <summary>The service-side switch: the endpoint answers 503 while its capability is withdrawn.</summary>
    public static TBuilder RequireCapability<TBuilder>(this TBuilder builder, CapabilityRegistry registry, string capability)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            if (!registry.IsWithdrawn(capability, out _)) return await next(context);
            registry.RecordDenial(capability, "endpoint");
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "capability_withdrawn", detail: $"'{capability}' is paused.");
        });
}

/// <summary>
/// For the reproduction only: hides withdrawn tools from the list the model is
/// shown, and does nothing else. It sits below the function-invoking layer
/// that ChatClientAgent adds, so that layer still holds the tool and runs it
/// when the model asks for it by name anyway.
/// </summary>
public sealed class HideWithdrawnToolsChatClient(IChatClient inner, CapabilityRegistry registry) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, Hide(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, Hide(options), cancellationToken);

    private ChatOptions? Hide(ChatOptions? options)
    {
        if (options?.Tools is not { Count: > 0 } tools) return options;
        ChatOptions copy = options.Clone();
        copy.Tools = [.. tools.Where(t => !registry.IsWithdrawn(t.Name, out _))];
        return copy;
    }
}
