using Ch20.MiddlewarePipeline.Middleware;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Ch20.MiddlewarePipeline;

/// <summary>The one composition the live run and the self-test share.</summary>
public static class GuardedPipeline
{
    public static readonly string[] DefaultBlockedKeywords = ["hack", "exploit", "malware", "bypass security"];

    public static IChatClient Build(IChatClient model, ILoggerFactory loggers, IEnumerable<string> blockedKeywords) =>
        model.AsBuilder()
            .Use(inner => new LoggingMiddleware(inner, loggers.CreateLogger("Logging")))          // outermost
            .Use(inner => new ContentFilterMiddleware(inner, loggers.CreateLogger("ContentFilter"), blockedKeywords))
            .Use(inner => new TimingMiddleware(inner, loggers.CreateLogger("Timing")))            // innermost
            .Build();
}
