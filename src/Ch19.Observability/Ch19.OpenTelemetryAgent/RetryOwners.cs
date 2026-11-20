using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using OpenAI;
using Polly;

namespace Ch19.Observability;

/// <summary>
/// A fake provider endpoint that counts every HTTP attempt. Each attempt is a
/// billed request on a real provider, so this counter is the cost signal the
/// chat span does not show.
/// </summary>
public sealed class FakeProvider(Func<int, HttpStatusCode> statusForAttempt) : HttpMessageHandler
{
    private int _attempts;

    public int Attempts => _attempts;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        int attempt = Interlocked.Increment(ref _attempts);
        HttpStatusCode status = statusForAttempt(attempt);
        string body = status == HttpStatusCode.OK
            ? """{"id":"c1","object":"chat.completion","created":1,"model":"fake-model","choices":[{"index":0,"message":{"role":"assistant","content":"75 CAD per day"},"finish_reason":"stop"}],"usage":{"prompt_tokens":9,"completion_tokens":4,"total_tokens":13}}"""
            : """{"error":{"message":"rate limited","type":"rate_limit_exceeded"}}""";
        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}

/// <summary>
/// Three places that can each own retries: the HTTP pipeline, the provider SDK
/// and the application. Stack them and the attempts multiply.
/// </summary>
public static class RetryOwners
{
    public const int RetriesPerOwner = 3;

    /// <summary>Builds a chat client over the fake provider with the chosen retry owners.</summary>
    public static IChatClient CreateClient(FakeProvider provider, bool httpRetries, bool sdkRetries)
    {
        ServiceCollection services = new();
        IHttpClientBuilder http = services.AddHttpClient("llm").ConfigurePrimaryHttpMessageHandler(() => provider);
        if (httpRetries)
        {
            // The same strategy AddStandardResilienceHandler uses, without the delay, so the test is fast.
            http.AddResilienceHandler("retry", b => b.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = RetriesPerOwner,
                Delay = TimeSpan.Zero,
                BackoffType = DelayBackoffType.Constant,
                UseJitter = false,
            }));
        }
        HttpClient client = services.BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>().CreateClient("llm");

        OpenAIClientOptions options = new()
        {
            Endpoint = new Uri("http://provider.test/v1"),
            Transport = new HttpClientPipelineTransport(client),
            // The SDK retries 408/429/5xx three times unless told otherwise.
            RetryPolicy = sdkRetries ? new ImmediateRetryPolicy(RetriesPerOwner) : new ClientRetryPolicy(maxRetries: 0),
        };
        return new OpenAIClient(new ApiKeyCredential("fake"), options)
            .GetChatClient("fake-model").AsIChatClient();
    }

    /// <summary>The application's own retry loop: the third owner.</summary>
    public static async Task<ChatResponse> AskWithAppRetriesAsync(IChatClient chat, int attempts)
    {
        for (int i = 1; ; i++)
        {
            try
            {
                return await chat.GetResponseAsync("What is the meals limit?");
            }
            catch (ClientResultException ex) when (ex.Status == 429 && i < attempts)
            {
                // retry: one more call, which the two lower owners each retry again
            }
        }
    }

    /// <summary>The SDK's retry policy with its waits removed (test speed only).</summary>
    private sealed class ImmediateRetryPolicy(int maxRetries) : ClientRetryPolicy(maxRetries)
    {
        protected override Task WaitAsync(TimeSpan time, CancellationToken cancellationToken) => Task.CompletedTask;

        protected override void Wait(TimeSpan time, CancellationToken cancellationToken) { }
    }
}
