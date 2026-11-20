using System.Text.Json;
using Microsoft.AspNetCore.Routing;

namespace Contoso.Qualified;

/// <summary>One capability: the routes it covers, whether this release ships it, and why not.</summary>
public sealed record Capability(string Name, List<string> Routes, bool Enabled, string? Reason);

public sealed record CapabilityFile(string Profile, string Candidate, List<Capability> Capabilities);

/// <summary>
/// What this release ships, read from release/capabilities.json. The "release" profile
/// applies the file as written; the "qualification" profile (the campaign and the drill,
/// simulated effects only) enables every capability. A disabled capability's routes answer
/// 503 capability_disabled with the recorded reason, after authentication. Every mapped
/// route must belong to a declared capability, or the host refuses to start.
/// </summary>
public sealed class ReleaseProfile
{
    public string Profile { get; }
    public string Candidate { get; }
    public IReadOnlyList<Capability> Capabilities { get; }

    private ReleaseProfile(string profile, CapabilityFile file, IReadOnlyList<Capability> capabilities)
    {
        Profile = profile;
        Candidate = file.Candidate;
        Capabilities = capabilities;
    }

    public static string DefaultFile => Path.Combine(AppContext.BaseDirectory, "release", "capabilities.json");

    public static ReleaseProfile Load(string path, string profile)
    {
        CapabilityFile file = JsonSerializer.Deserialize<CapabilityFile>(File.ReadAllText(path), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException($"empty {path}");
        List<string> problems = [];
        if (profile is not ("release" or "qualification")) problems.Add($"unknown profile '{profile}' (release or qualification)");
        problems.AddRange(file.Capabilities.Where(c => !c.Enabled && string.IsNullOrWhiteSpace(c.Reason))
            .Select(c => $"{c.Name} is disabled with no reason"));
        problems.AddRange(file.Capabilities.SelectMany(c => c.Routes).GroupBy(r => r).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} is declared twice"));
        if (problems.Count > 0) throw new InvalidDataException("release profile refused: " + string.Join("; ", problems));
        IReadOnlyList<Capability> caps = profile == "qualification"
            ? [.. file.Capabilities.Select(c => c with { Enabled = true, Reason = null })]
            : file.Capabilities;
        return new ReleaseProfile(profile, file, caps);
    }

    /// <summary>The route key the capability file uses: "POST /claims/{id}/approval", "ANY /health/live".</summary>
    public static string Key(RouteEndpoint e) =>
        $"{e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.SingleOrDefault() ?? "ANY"} {e.RoutePattern.RawText}";

    /// <summary>Maps GET /release and the gate. Call it last: it checks every route mapped before it.</summary>
    public WebApplication Map(WebApplication app)
    {
        app.MapGet("/release", () => Results.Ok(new
        {
            candidate = Candidate,
            profile = Profile,
            capabilities = Capabilities.Select(c => new { c.Name, c.Enabled, c.Reason }),
        })).RequireAuthorization();

        Dictionary<string, Capability> byRoute = Capabilities.SelectMany(c => c.Routes.Select(r => (r, c))).ToDictionary(x => x.r, x => x.c);
        List<string> mapped = [.. ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().Select(Key)];
        List<string> undeclared = [.. mapped.Where(k => !byRoute.ContainsKey(k)).Order(StringComparer.Ordinal)];
        List<string> unmapped = [.. byRoute.Keys.Where(k => !mapped.Contains(k)).Order(StringComparer.Ordinal)];
        if (undeclared.Count > 0 || unmapped.Count > 0)
            throw new InvalidOperationException(("release profile refused: " +
                (undeclared.Count > 0 ? $"routes in no capability: {string.Join(", ", undeclared)}. " : "") +
                (unmapped.Count > 0 ? $"declared routes that are not mapped: {string.Join(", ", unmapped)}." : "")).TrimEnd());

        // After UseAuthentication/UseAuthorization (AgentHost.Map), before the endpoint runs.
        app.Use(async (http, next) =>
        {
            if (http.GetEndpoint() is RouteEndpoint e && byRoute.TryGetValue(Key(e), out Capability? c) && !c.Enabled)
            {
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await http.Response.WriteAsJsonAsync(new { refused = "capability_disabled", capability = c.Name, reason = c.Reason });
                return;
            }
            await next(http);
        });
        return app;
    }
}
