using System.ComponentModel;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Ch08.ActionPolicy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Ch20.PolicyEnforcement;

// Each adapter does two things only: build the Caller from its own
// authentication context, and map its wire shape onto ReimbursementCommand.
// None of them decides anything. A tenant id in the arguments is never read.

// ── HTTP API ───────────────────────────────────────────────────────────────

/// <summary>Opaque bearer tokens issued by this run, standing in for an identity provider.</summary>
public sealed class TokenTable(IReadOnlyDictionary<string, Caller> tokens)
{
    public bool TryResolve(string token, out Caller caller) => tokens.TryGetValue(token, out caller!);
}

public sealed class TokenTableHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, TokenTable tokens)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return Task.FromResult(AuthenticateResult.NoResult());
        if (!tokens.TryResolve(header["Bearer ".Length..], out Caller caller))
            return Task.FromResult(AuthenticateResult.Fail("unknown token"));

        List<Claim> claims = [new("tid", caller.TenantId), new("sub", caller.SubjectId)];
        claims.AddRange(caller.Permissions.Select(p => new Claim("perm", p)));
        ClaimsPrincipal principal = new(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}

public static class HttpTransport
{
    public static void AddReimbursementApi(this IServiceCollection services, TokenTable tokens)
    {
        services.AddSingleton(tokens);
        services.AddAuthentication("Bearer").AddScheme<AuthenticationSchemeOptions, TokenTableHandler>("Bearer", null);
        services.AddAuthorization();
    }

    public static void MapReimbursementApi(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPost("/reimbursements", (ReimbursementCommand command, ClaimsPrincipal user, ReimbursementService service) =>
        {
            Caller caller = new(user.FindFirstValue("tid") ?? "", user.FindFirstValue("sub") ?? "",
                user.FindAll("perm").Select(c => c.Value).ToHashSet());
            Outcome outcome = service.Reimburse("http", caller, command);
            return Results.Json(outcome, statusCode: outcome.Decision switch
            {
                Decision.Allow => StatusCodes.Status200OK,
                Decision.RequireApproval => StatusCodes.Status202Accepted,
                _ => StatusCodes.Status403Forbidden,
            });
        }).RequireAuthorization();
    }
}

// ── MCP (stdio wiring) ─────────────────────────────────────────────────────

/// <summary>
/// A stdio MCP server runs for one signed-in user: the host binds that user
/// when it starts the process. The protected HTTP variant (the MCP chapter)
/// builds the caller from the token instead; the service call is the same.
/// </summary>
public sealed record McpSessionCaller(Caller Caller);

[McpServerToolType]
public sealed class ReimbursementMcpTools(ReimbursementService service, McpSessionCaller session)
{
    [McpServerTool(Name = ReimbursementService.Tool), Description("Submit a reimbursement for an employee.")]
    public string Reimburse(string employeeId, decimal amount, string currency, string operationId) =>
        Guid.TryParse(operationId, out Guid id)
            ? service.Reimburse("mcp", session.Caller, new ReimbursementCommand(id, employeeId, amount, currency)).ToString()
            : "Deny missing_operation_id";
}
