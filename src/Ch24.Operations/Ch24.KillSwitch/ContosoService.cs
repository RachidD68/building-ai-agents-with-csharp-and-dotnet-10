using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using Ch08.ActionPolicy;
using Ch20.PolicyEnforcement;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ch24.KillSwitch;

/// <summary>The two capabilities of the Contoso assistant: a read-only FAQ and a tool with an effect.</summary>
public static class Capabilities
{
    public const string Faq = "search_faq";
    public const string Reimburse = ReimbursementService.Tool;   // "reimburse"

    public static string AnswerFaq(string question) =>
        question.Contains("receipt", StringComparison.OrdinalIgnoreCase)
            ? "[expense-policy#4] Claims above 25 EUR need a receipt."
            : "[expense-policy#1] Submit claims within 30 days.";
}

public static class ContosoAssistant
{
    /// <summary>
    /// The assistant for one signed-in caller. The reimburse tool ends in the
    /// security chapter's ReimbursementService, which decides at the effect.
    /// <paramref name="beforeEffect"/> lets a test hold a call between the
    /// tool-side check and the effect.
    /// </summary>
    public static ChatClientAgent Create(IChatClient model, World world, Caller caller,
        string instructions = "You help Contoso employees with expenses.", Func<Task>? beforeEffect = null)
    {
        [Description("Search the Contoso expense FAQ.")]
        string SearchFaq(string question) => Capabilities.AnswerFaq(question);

        [Description("Submit a reimbursement for an employee.")]
        async Task<string> Reimburse(string employeeId, decimal amount, string currency, string operationId)
        {
            if (beforeEffect is not null) await beforeEffect();
            return Guid.TryParse(operationId, out Guid id)
                ? world.Service.Reimburse("agent", caller, new ReimbursementCommand(id, employeeId, amount, currency)).ToString()
                : "Deny missing_operation_id";
        }

        return new ChatClientAgent(model, new ChatClientAgentOptions
        {
            Name = "ContosoAssistant",
            ChatOptions = new ChatOptions
            {
                Instructions = instructions,
                Tools =
                [
                    AIFunctionFactory.Create(SearchFaq, name: Capabilities.Faq),
                    AIFunctionFactory.Create(Reimburse, name: Capabilities.Reimburse),
                ],
            },
        });
    }
}

/// <summary>
/// The HTTP boundary on 127.0.0.1: GET /faq (read-only) and POST /reimbursements,
/// each behind its capability's switch. Callers come from the security
/// chapter's bearer-token table; the decision stays in ReimbursementService.
/// </summary>
public sealed class ContosoApi(World world, CapabilityRegistry registry) : IAsyncDisposable
{
    private WebApplication? _app;
    public HttpClient Http { get; private set; } = null!;

    /// <summary>The test's view of the last response (status and readable body).</summary>
    public HttpStatusCode LastStatus { get; set; }
    public string LastBody { get; set; } = "";

    public async Task StartAsync(IReadOnlyDictionary<string, Caller> tokens)
    {
        Uri baseUri = new($"http://127.0.0.1:{FreePort()}");
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [], ContentRootPath = AppContext.BaseDirectory, EnvironmentName = Environments.Production,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(baseUri.ToString());
        builder.Services.AddReimbursementApi(new TokenTable(tokens));   // authentication only
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();

        _app.MapGet("/faq", (string q) => Results.Ok(Capabilities.AnswerFaq(q)))
            .RequireCapability(registry, Capabilities.Faq);

        _app.MapPost("/reimbursements", (ReimbursementCommand command, ClaimsPrincipal user) =>
        {
            Caller caller = new(user.FindFirstValue("tid") ?? "", user.FindFirstValue("sub") ?? "",
                user.FindAll("perm").Select(c => c.Value).ToHashSet());
            Outcome outcome = world.Service.Reimburse("http", caller, command);
            return Results.Json(outcome, statusCode: outcome.Decision == Decision.Allow ? 200 : 403);
        }).RequireAuthorization().RequireCapability(registry, Capabilities.Reimburse);

        await _app.StartAsync();
        Http = new HttpClient { BaseAddress = baseUri };
    }

    public async ValueTask DisposeAsync()
    {
        Http?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
    }

    private static int FreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
