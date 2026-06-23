using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ch06.ContextProviderDemo;

/// <summary>
/// A custom <see cref="AIContextProvider"/> that injects user preferences
/// into every agent request. This demonstrates how to dynamically enrich
/// the agent's context without modifying the system prompt.
/// </summary>
/// <remarks>
/// <para>
/// Context providers hook into the agent invocation lifecycle:
/// </para>
/// <list type="bullet">
///   <item><see cref="ProvideAIContextAsync"/> — runs BEFORE each call (add instructions, messages)</item>
///   <item><see cref="StoreAIContextAsync"/> — runs AFTER each call (log usage, track tokens)</item>
/// </list>
/// <para>
/// FORWARD REFERENCE: The middleware pipeline (Chapter 15) can also intercept
/// and modify conversation history.
/// </para>
/// </remarks>
public sealed class UserPreferencesContextProvider : AIContextProvider
{
    private readonly UserPreferences _preferences;

    /// <summary>
    /// Initializes a new instance with the given user preferences.
    /// </summary>
    /// <param name="preferences">The user's preferences to inject into context.</param>
    public UserPreferencesContextProvider(UserPreferences preferences)
    {
        _preferences = preferences;
    }

    /// <summary>
    /// Called before each agent invocation. Injects user preferences as
    /// additional context via an <see cref="AIContext"/>.
    /// </summary>
    protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken)
    {
        // Build a context message with user preferences
        string preferencesText = $"""
            User Preferences (automatically injected by context provider):
            - Display Name: {_preferences.DisplayName}
            - Preferred Language: {_preferences.PreferredLanguage}
            - Experience Level: {_preferences.ExperienceLevel}
            - Preferred Response Style: {_preferences.ResponseStyle}
            - Topics of Interest: {string.Join(", ", _preferences.TopicsOfInterest)}
            """;

        // Create an AIContext that enriches the agent's request
        AIContext aiContext = new()
        {
            // Append to the existing instructions
            Instructions = $"""
                Tailor your responses to the user's preferences:
                {preferencesText}

                Adjust your language complexity based on their experience level.
                Focus on their topics of interest when giving examples.
                Use their preferred response style (concise, detailed, or conversational).
                """,

            // Add the preferences as a system message for additional context
            Messages =[new ChatMessage(ChatRole.System, preferencesText)]
        };

        return new ValueTask<AIContext>(aiContext);
    }

    /// <summary>
    /// Called after each agent invocation. Logs token usage information
    /// as part of the TOKEN BUDGET MANAGEMENT pattern.
    /// </summary>
    protected override ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken cancellationToken)
    {
        // ---------------------------------------------------------------
        // TOKEN BUDGET MANAGEMENT
        // ---------------------------------------------------------------
        // In production, you would track token usage here and implement
        // strategies like sliding windows or conversation compaction.

        int responseMessageCount = context.ResponseMessages?.Count() ?? 0;
        Console.WriteLine($"  [ContextProvider] Invocation complete. " + $"Response messages: {responseMessageCount}");

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Represents a user's preferences for personalizing agent responses.
/// </summary>
/// <param name="DisplayName">The user's display name.</param>
/// <param name="PreferredLanguage">The natural language for responses (e.g., "English").</param>
/// <param name="ExperienceLevel">Expertise level: "Beginner", "Intermediate", or "Advanced".</param>
/// <param name="ResponseStyle">Preferred style: "concise", "detailed", or "conversational".</param>
/// <param name="TopicsOfInterest">Topics the user is interested in.</param>
public sealed record UserPreferences(
    string DisplayName,
    string PreferredLanguage,
    string ExperienceLevel,
    string ResponseStyle,
    IReadOnlyList<string> TopicsOfInterest);
