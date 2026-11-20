using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using AgenticAI.Common.Extensions;
using Ch08.ActionPolicy;
using Ch18.ApprovalUi;
using Ch18.SseStatusStream;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.AI;

namespace Ch18.AguiServer;

/// <summary>What a test may change. The defaults are the fixed host.</summary>
public sealed record AguiHostOptions
{
    /// <summary>null: the model comes from the LlmProvider section (AgenticAI.Common).</summary>
    public IChatClient? ChatClient { get; init; }

    /// <summary>false reproduces the defect: the AG-UI thread id alone finds a stored session.</summary>
    public bool GuardThreads { get; init; } = true;

    /// <summary>
    /// false reproduces the second defect: the guard reads "threadId" case-sensitively, while
    /// MAF's AG-UI parser matches it case-insensitively and keeps the last duplicate.
    /// </summary>
    public bool ReadThreadIdLikeMaf { get; init; } = true;

    /// <summary>Called each time an agent instance is built (the self-test counts them).</summary>
    public Action<string>? AgentBuilt { get; init; }

    public string DatabasePath { get; init; } =
        Path.Combine(Path.GetTempPath(), "AgenticAI", "Ch18.AguiServer", "approvals.db");
}

/// <summary>
/// The AG-UI host's composition, shared by Program.cs, the self-tests, the
/// client's self-test and Ch18.InterfaceTests.
///   POST /agui/assistant           AG-UI, any Contoso employee (submit, list own claims)
///   POST /agui/review              AG-UI, finance reviewers only (pending reviews)
///   GET  /conversations/{thread}/events|snapshot   the operation's state (GA SSE), thread owner only
///   GET  /reviews/{id}, POST /reviews/{id}/decision  the review screen (Ch18.ApprovalUi)
/// </summary>
public static class AguiHost
{
    public const string Tenant = "contoso";
    public const string ThreadItem = "agui.thread";

    public static WebApplicationBuilder Configure(WebApplicationBuilder builder, AguiHostOptions? options = null)
    {
        options ??= new AguiHostOptions();
        if (options.ChatClient is { } fake) builder.Services.AddSingleton(fake);
        else builder.Services.AddLlmChatClient(builder.Configuration);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(options);

        Rights rights = Rights.Seeded();
        Directory.CreateDirectory(Path.GetDirectoryName(options.DatabasePath)!);
        ApprovalDesk desk = new(options.DatabasePath, rights.Of, () => DateTimeOffset.UtcNow);
        desk.Commits.SeedAccount(new EmployeeAccount(Tenant, "E-1001", "EUR", 500m, 7));
        desk.Commits.SeedAccount(new EmployeeAccount(Tenant, "E-2002", "EUR", 300m, 3));
        builder.Services.AddSingleton(rights);
        builder.Services.AddSingleton(desk);
        builder.Services.AddSingleton(new OperationLog());
        builder.Services.AddSingleton<ClaimOperations>();
        builder.Services.AddSingleton<ThreadOwners>();

        // Bearer validation as in the hosting chapter: "tid" and "sub" keep their names,
        // audience and issuer checks pinned on.
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwt =>
        {
            jwt.MapInboundClaims = false;
            jwt.TokenValidationParameters.ValidateAudience = true;
            jwt.TokenValidationParameters.ValidateIssuer = true;
        });
        // Selection is a route; authorization decides who reaches it, with rights loaded per request.
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy("employee", p => p.RequireAssertion(c =>
                CallerKey.From(c.User) is { } who && rights.Of(who.TenantId, who.SubjectId).Contains(Permissions.SubmitReimbursement)))
            .AddPolicy("finance-reviewer", p => p.RequireAssertion(c =>
                CallerKey.From(c.User) is { } who && rights.Of(who.TenantId, who.SubjectId).Contains(Roles.FinanceReviewer)));

        builder.Services.AddAGUI();
        // Registered Transient, as a per-request agent would be. MapAGUI 1.5 still builds it once,
        // when the route is mapped (the self-test counts it), so the tools read the caller per call.
        builder.AddAIAgent("assistant", (sp, name) => Built(options, name, sp).AsAIAgent(name: name,
            instructions: """
                You are Contoso's expense assistant. To submit a claim, call submit_claim with the amount and
                currency. To list the caller's claims, call list_my_claims. Answer in at most two sentences.
                Never say that a claim was approved or paid: the screen shows its state.
                """,
            tools: [.. sp.GetRequiredService<ClaimOperations>().EmployeeTools()]), ServiceLifetime.Transient)
            .WithInMemorySessionStore();
        builder.AddAIAgent("review", (sp, name) => Built(options, name, sp).AsAIAgent(name: name,
            instructions: "You help a Contoso finance reviewer. Call pending_reviews to list claims waiting for review. Two sentences.",
            tools: [.. sp.GetRequiredService<ClaimOperations>().ReviewerTools()]), ServiceLifetime.Transient)
            .WithInMemorySessionStore();
        return builder;
    }

    private static IChatClient Built(AguiHostOptions options, string name, IServiceProvider sp)
    {
        options.AgentBuilt?.Invoke(name);
        return sp.GetRequiredService<IChatClient>();
    }

    public static WebApplication Map(WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        ThreadOwners owners = app.Services.GetRequiredService<ThreadOwners>();
        AguiHostOptions options = app.Services.GetRequiredService<AguiHostOptions>();
        bool guard = options.GuardThreads;

        // The AG-UI thread id is chosen by the client: a locator, not an authorization.
        // The first caller to use one owns it; anyone else gets 404, as for an unknown id.
        app.Use(async (http, next) =>
        {
            if (HttpMethods.IsPost(http.Request.Method) && http.Request.Path.StartsWithSegments("/agui")
                && CallerKey.From(http.User) is { } who)
            {
                http.Request.EnableBuffering();
                string? thread = options.ReadThreadIdLikeMaf ? await ThreadIdOf(http) : await FirstExactThreadId(http);
                http.Request.Body.Position = 0;
                if (string.IsNullOrEmpty(thread)) { http.Response.StatusCode = 400; return; }
                if (guard && !owners.TryClaim(thread, who)) { http.Response.StatusCode = 404; return; }
                http.Items[ThreadItem] = thread;
            }
            await next(http);
        });

        app.MapAGUI("assistant", "/agui/assistant").RequireAuthorization("employee");
        app.MapAGUI("review", "/agui/review").RequireAuthorization("finance-reviewer");

        OperationLog log = app.Services.GetRequiredService<OperationLog>();
        app.MapStatusStream("/conversations", log, (http, thread) =>
            CallerKey.From(http.User) is { } who && owners.IsOwner(thread, who)).RequireAuthorization();

        ApprovalDesk desk = app.Services.GetRequiredService<ApprovalDesk>();
        ClaimOperations claims = app.Services.GetRequiredService<ClaimOperations>();
        app.MapReviews("/reviews", desk, http => CallerKey.From(http.User) is { } who ? new Reviewer(who.TenantId, who.SubjectId) : null,
            claims.OnDecided).RequireAuthorization();
        return app;
    }

    /// <summary>
    /// The thread id as MAF will read it, or null (400). MAF's parser matches "threadId"
    /// case-insensitively and keeps the last of duplicates, so the guard refuses any body
    /// in which that could name another thread than the one checked here: two such
    /// properties, or one not spelled exactly "threadId".
    /// </summary>
    private static async Task<string?> ThreadIdOf(HttpContext http)
    {
        try
        {
            using JsonDocument doc = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            JsonProperty[] ids = [.. doc.RootElement.EnumerateObject()
                .Where(p => string.Equals(p.Name, "threadId", StringComparison.OrdinalIgnoreCase))];
            return ids is [{ Name: "threadId", Value.ValueKind: JsonValueKind.String } only] ? only.Value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The defect, kept for the self-test: an exact, case-sensitive lookup of the first "threadId".
    private static async Task<string?> FirstExactThreadId(HttpContext http)
    {
        using JsonDocument doc = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        return doc.RootElement.TryGetProperty("threadId", out JsonElement t) ? t.GetString() : null;
    }
}

/// <summary>The validated (tenant, subject) of a request; the hosting chapter's rule, read from the token only.</summary>
public sealed record CallerKey(string TenantId, string SubjectId)
{
    public static CallerKey? From(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
        && user.FindFirst("tid")?.Value is { Length: > 0 } tenant
        && user.FindFirst("sub")?.Value is { Length: > 0 } subject
            ? new CallerKey(tenant, subject)
            : null;

    public static CallerKey? From(IHttpContextAccessor accessor) => accessor.HttpContext is { } http ? From(http.User) : null;
}

/// <summary>The identity system's answer, asked on every request: a revoked right is gone at once.</summary>
public sealed class Rights
{
    private readonly ConcurrentDictionary<(string Tenant, string Subject), HashSet<string>> _rights = new();

    public static Rights Seeded()
    {
        Rights r = new();
        r.Grant(AguiHost.Tenant, "E-1001", Permissions.SubmitReimbursement);
        r.Grant(AguiHost.Tenant, "E-2002", Permissions.SubmitReimbursement);
        r.Grant(AguiHost.Tenant, "rita", Roles.FinanceReviewer);
        r.Grant(AguiHost.Tenant, "ana", Roles.FinanceReviewer);
        return r;
    }

    public void Grant(string tenant, string subject, string right)
    {
        HashSet<string> set = _rights.GetOrAdd((tenant, subject), _ => []);
        lock (set) set.Add(right);
    }

    public void Revoke(string tenant, string subject, string right)
    {
        if (_rights.TryGetValue((tenant, subject), out HashSet<string>? set)) lock (set) set.Remove(right);
    }

    public IReadOnlySet<string> Of(string tenant, string subject)
    {
        if (!_rights.TryGetValue((tenant, subject), out HashSet<string>? set)) return new HashSet<string>();
        lock (set) return new HashSet<string>(set);
    }
}

/// <summary>
/// Who owns each AG-UI thread id: the first validated caller to use it. A claim is never
/// released or expired, so a guessable id can be taken first by someone else; a production
/// host issues unguessable thread ids (or issues them itself) and expires them.
/// </summary>
public sealed class ThreadOwners
{
    private readonly ConcurrentDictionary<string, CallerKey> _owners = new();

    public bool TryClaim(string thread, CallerKey who) => _owners.GetOrAdd(thread, who) == who;

    public bool IsOwner(string thread, CallerKey who) => _owners.TryGetValue(thread, out CallerKey? owner) && owner == who;
}
