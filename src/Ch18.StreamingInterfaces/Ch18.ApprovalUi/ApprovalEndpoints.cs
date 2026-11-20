namespace Ch18.ApprovalUi;

/// <summary>A reviewer as the host authenticated them. The body never names one.</summary>
public sealed record Reviewer(string TenantId, string SubjectId);

/// <summary>
/// The review screen's two routes. <paramref name="reviewerOf"/> is the host's
/// identity source (a validated token); null means no identity key (401).
/// </summary>
public static class ApprovalEndpoints
{
    /// <param name="onDecided">The application's next step after a stored decision (commit, state change).</param>
    public static RouteGroupBuilder MapReviews(this IEndpointRouteBuilder app, string prefix, ApprovalDesk desk,
        Func<HttpContext, Reviewer?> reviewerOf, Action<Guid, DecisionResult>? onDecided = null)
    {
        RouteGroupBuilder group = app.MapGroup(prefix);
        group.MapGet("/{id:guid}", (Guid id, HttpContext http) =>
            reviewerOf(http) is not { } who ? Results.Unauthorized()
            : desk.View(who.TenantId, who.SubjectId, id) is { } view ? Results.Ok(view)
            : Results.NotFound());

        group.MapPost("/{id:guid}/decision", (Guid id, DecisionRequest request, HttpContext http) =>
        {
            if (reviewerOf(http) is not { } who) return Results.Unauthorized();
            DecisionResult r = desk.Decide(who.TenantId, who.SubjectId, id, request);
            if (r.Status == 200 && !r.Replayed) onDecided?.Invoke(id, r);
            return Results.Json(new { result = r.Result, replayed = r.Replayed }, statusCode: r.Status);
        });
        return group;
    }
}
