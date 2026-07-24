// ============================================================================
// Chapter 6 — Conversations and Memory (Context Provider Demo)
// Demonstrates AIContextProvider to inject dynamic context into agent requests.
//
// Key concepts:
//   - AIContextProvider: enriches agent requests with additional context
//   - ProvideAIContextAsync: runs BEFORE each agent call (add instructions, messages)
//   - StoreAIContextAsync: runs AFTER each agent call (log usage, track tokens)
//   - Token budget management: monitoring and controlling token consumption
//
// FORWARD REFERENCE: The middleware pipeline (Chapter 16) can also intercept
// and modify conversation history.
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;
using Ch06.ContextProviderDemo;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== Chapter 6: Context Provider Demo ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Step 1: Define user preferences
// ---------------------------------------------------------------
UserPreferences userPrefs = new(
    DisplayName: "Rachid",
    PreferredLanguage: "English",
    ExperienceLevel: "Intermediate",
    ResponseStyle: "conversational",
    TopicsOfInterest: ["C#", "AI agents", "cloud architecture", "clean code"]);

Console.WriteLine($"User preferences loaded for: {userPrefs.DisplayName}");
Console.WriteLine($"  Experience: {userPrefs.ExperienceLevel}");
Console.WriteLine($"  Style: {userPrefs.ResponseStyle}");
Console.WriteLine($"  Interests: {string.Join(", ", userPrefs.TopicsOfInterest)}\n");

// ---------------------------------------------------------------
// Step 2: Create the agent with the context provider
// ---------------------------------------------------------------
UserPreferencesContextProvider contextProvider = new(userPrefs);
InMemoryChatHistoryProvider historyProvider = new(new InMemoryChatHistoryProviderOptions());

ChatClientAgentOptions agentOptions = new()
{
    Name = "PersonalizedAssistant",
    Description = "An assistant that adapts to user preferences via context providers",
    ChatHistoryProvider = historyProvider,
    AIContextProviders = [contextProvider],
    ChatOptions = new ChatOptions
    {
        Temperature = 0.7f,
        MaxOutputTokens = 500
    }
};

ChatClientAgent agent = new(chatClient, options: agentOptions);
AgentSession session = await agent.CreateSessionAsync(cts.Token);

try
{
    // ---------------------------------------------------------------
    // Step 3: Ask questions — the context provider personalizes responses
    // ---------------------------------------------------------------
    string[] questions =
    [
        "Suggest a project I could build this weekend to improve my skills.",
        "What design pattern should I learn next?",
        "How does the context provider pattern compare to middleware?"
    ];

    foreach (string question in questions)
    {
        Console.WriteLine($"You: {question}");

        AgentResponse response = await agent.RunAsync(
            question,
            session,
            cancellationToken: cts.Token);

        Console.WriteLine($"Agent: {response.Text}\n");

        // ---------------------------------------------------------------
        // TOKEN BUDGET MANAGEMENT
        // ---------------------------------------------------------------
        // Monitor token usage after each call. In production, you would
        // implement sliding windows or compaction when approaching limits.
        if (response.Usage is { } usage)
        {
            long totalTokens = usage.TotalTokenCount ?? 0;
            long inputTokens = usage.InputTokenCount ?? 0;

            Console.WriteLine($"  [Token Budget] Input: {inputTokens}, " +
                              $"Output: {usage.OutputTokenCount ?? 0}, " +
                              $"Total: {totalTokens}");

            // Warn if we're approaching a typical context window limit
            // (this threshold is illustrative — real limits vary by model)
            const int warningThreshold = 100_000;
            if (inputTokens > warningThreshold)
            {
                Console.WriteLine($"  [Token Budget] WARNING: Input tokens ({inputTokens}) " +
                                  $"exceed {warningThreshold}. Consider compacting history.");
            }
        }

        Console.WriteLine();
    }

    // ---------------------------------------------------------------
    // Step 4: Show how history grows with context provider messages
    // ---------------------------------------------------------------
    Console.WriteLine(new string('=', 60));
    Console.WriteLine("CONVERSATION HISTORY WITH CONTEXT PROVIDER");
    Console.WriteLine(new string('=', 60));

    List<ChatMessage> messages = historyProvider.GetMessages(session);
    Console.WriteLine($"\nTotal messages stored: {messages.Count}");
    Console.WriteLine("(Context provider messages are injected at request time,");
    Console.WriteLine("but the stored history shows the user/assistant turns.)\n");

    foreach (ChatMessage msg in messages)
    {
        string role = msg.Role.Value;
        string preview = (msg.Text ?? string.Empty).Length > 100
            ? string.Concat(msg.Text.AsSpan(0, 100), "...")
            : msg.Text ?? "(empty)";
        Console.WriteLine($"  [{role,-10}] {preview}");
    }

    // ---------------------------------------------------------------
    // Step 5: Demonstrate the effect of different preferences
    // ---------------------------------------------------------------
    Console.WriteLine($"\n{new string('=', 60)}");
    Console.WriteLine("COMPARISON: Different User Preferences");
    Console.WriteLine(new string('=', 60));

    // Create a "beginner" version of the same agent
    UserPreferences beginnerPrefs = new(
        DisplayName: "Alex",
        PreferredLanguage: "English",
        ExperienceLevel: "Beginner",
        ResponseStyle: "detailed",
        TopicsOfInterest: ["web development", "JavaScript", "HTML"]);

    UserPreferencesContextProvider beginnerProvider = new(beginnerPrefs);

    ChatClientAgentOptions beginnerOptions = new()
    {
        Name = "BeginnerHelper",
        Description = "An assistant tailored for beginners",
        AIContextProviders = [beginnerProvider],
        ChatOptions = new ChatOptions { MaxOutputTokens = 500 }
    };

    ChatClientAgent beginnerAgent = new(chatClient, options: beginnerOptions);
    AgentSession beginnerSession = await beginnerAgent.CreateSessionAsync(cts.Token);

    string comparisonQuestion = "What is dependency injection?";
    Console.WriteLine($"\nSame question to both agents: \"{comparisonQuestion}\"\n");

    // Ask the intermediate agent
    AgentSession comparisonSession = await agent.CreateSessionAsync(cts.Token);
    AgentResponse intermediateAnswer = await agent.RunAsync(
        comparisonQuestion, comparisonSession, cancellationToken: cts.Token);
    Console.WriteLine($"[Intermediate/{userPrefs.ResponseStyle}]: {intermediateAnswer.Text}\n");

    // Ask the beginner agent
    AgentResponse beginnerAnswer = await beginnerAgent.RunAsync(
        comparisonQuestion, beginnerSession, cancellationToken: cts.Token);
    Console.WriteLine($"[Beginner/{beginnerPrefs.ResponseStyle}]: {beginnerAnswer.Text}");

    Console.WriteLine("\nNotice how the same question gets different responses based on");
    Console.WriteLine("the user's experience level and preferred response style.");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
}

Console.WriteLine("\n=== Context Provider Demo complete! ===");
