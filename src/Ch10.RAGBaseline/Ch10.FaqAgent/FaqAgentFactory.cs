using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ch10.FaqAgent;

/// <summary>Which context provider feeds the agent its evidence.</summary>
public enum RetrievalMode
{
    /// <summary>The built-in TextSearchProvider searches before every call (the default).</summary>
    TextSearch,
    /// <summary>The built-in TextSearchProvider exposed as a tool the model may call.</summary>
    OnDemand,
    /// <summary>The stateless custom provider, FaqContextProvider.</summary>
    Custom,
}

/// <summary>Builds the FAQ agent the same way for the live run and the self-test.</summary>
public static class FaqAgentFactory
{
    public const string Abstention =
        "I don't have information about that in our knowledge base. Please contact HR.";

    public const string Instructions = $"""
        You are the Contoso FAQ agent. Answer employee questions about company
        policies from the handbook excerpts provided in this conversation.

        Rules:
        - Use only the handbook excerpts. Never invent a policy or a number.
        - Cite the source of every fact in square brackets, e.g. [pto-policy].
        - If the excerpts do not contain the answer, reply exactly:
          "{Abstention}"
        - Be concise.
        """;

    public static AIContextProvider CreateProvider(FaqSearchService search, RetrievalMode mode) => mode switch
    {
        RetrievalMode.Custom => new FaqContextProvider(search),
        RetrievalMode.OnDemand => new TextSearchProvider(search.SearchAsync, new TextSearchProviderOptions
        {
            SearchTime = TextSearchProviderOptions.TextSearchBehavior.OnDemandFunctionCalling,
            FunctionToolName = "search_handbook",
            FunctionToolDescription = "Searches the Contoso employee handbook.",
        }),
        _ => new TextSearchProvider(search.SearchAsync, new TextSearchProviderOptions
        {
            SearchTime = TextSearchProviderOptions.TextSearchBehavior.BeforeAIInvoke,
            // The last turns are prepended to the search input, so a follow-up
            // such as "What about for contractors?" still finds its topic.
            RecentMessageMemoryLimit = 6,
        }),
    };

    public static ChatClientAgent Create(IChatClient chatClient, FaqSearchService search, RetrievalMode mode) =>
        new(chatClient, new ChatClientAgentOptions
        {
            Name = "ContosoFaqAgent",
            Description = "Answers Contoso policy questions from the employee handbook",
            AIContextProviders = [CreateProvider(search, mode)],
            ChatOptions = new ChatOptions
            {
                Instructions = Instructions,
                Temperature = 0.2f,
                MaxOutputTokens = 600,
            },
        });
}
