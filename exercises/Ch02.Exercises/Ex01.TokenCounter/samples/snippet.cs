using Microsoft.Extensions.AI;

namespace TokenCounter.Samples;

/// <summary>A tiny service that answers one question per call.</summary>
public sealed class QuestionService(IChatClient chatClient)
{
    public async Task<string> AskAsync(string question, CancellationToken cancellationToken)
    {
        ChatResponse response = await chatClient.GetResponseAsync(
            question, cancellationToken: cancellationToken);

        return response.Text ?? string.Empty;
    }
}
