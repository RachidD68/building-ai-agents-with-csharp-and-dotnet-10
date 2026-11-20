// ============================================================================
// Self-test: the real app on a loopback port, called over HTTP.
//   dotnet run -- --self-test
// No model, no outside network. Prints each response body and a PASS/FAIL line;
// exits 0 only when every check passes.
// ============================================================================

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AppA.MinimalApi.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

/// <summary>Calls every endpoint of the running app and checks status codes and bodies.</summary>
internal static class ApiSelfTest
{
    private static int s_passed, s_total;

    /// <summary>Runs the checks against an app that has already been started.</summary>
    /// <param name="app">The started application.</param>
    /// <returns>The number of failed checks.</returns>
    public static async Task<int> RunAsync(WebApplication app)
    {
        Console.WriteLine("=== Minimal API demo (self-test) ===\n");
        string address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();
        using HttpClient http = new() { BaseAddress = new Uri(address) };

        HttpResponseMessage r = await http.GetAsync("/hello");
        await Show("GET /hello", r);
        Expect("GET /hello is 200", r.StatusCode == HttpStatusCode.OK);

        r = await http.PostAsJsonAsync("/echo", new { text = "Hello AI" });
        await Show("POST /echo {\"text\":\"Hello AI\"}", r);
        Expect("the record binds from camelCase JSON and comes back camelCase",
            await r.Content.ReadAsStringAsync() == """{"echo":"Hello AI","length":8}""");

        EchoRequest? loose = JsonSerializer.Deserialize<EchoRequest>("{}", JsonSerializerOptions.Web);
        Expect("DEFECT REPRODUCED: default options bind {} to an EchoRequest whose Text is null",
            loose is { Text: null });

        r = await PostRaw(http, "/echo", "{}");
        await Show("POST /echo {}", r);
        Expect("a body without text is 400, not a 500 from the null the annotation ruled out",
            r.StatusCode == HttpStatusCode.BadRequest);

        r = await PostRaw(http, "/echo", """{"text":null}""");
        await Show("POST /echo {\"text\":null}", r);
        Expect("an explicit null text is 400 too", r.StatusCode == HttpStatusCode.BadRequest);

        r = await http.GetAsync("/weather/Tokyo");
        await Show("GET /weather/Tokyo", r);
        Expect("a known city is 200 with the computed temperatureF", r.StatusCode == HttpStatusCode.OK
            && (await r.Content.ReadAsStringAsync()).Contains("\"temperatureF\":"));

        r = await http.GetAsync("/weather/Paris");
        await Show("GET /weather/Paris", r);
        Expect("an unknown city is 404", r.StatusCode == HttpStatusCode.NotFound);

        IWeatherService first = app.Services.GetRequiredKeyedService<IWeatherService>("cached");
        IWeatherService second = app.Services.GetRequiredKeyedService<IWeatherService>("cached");
        Expect("the \"cached\" key resolves one shared singleton", ReferenceEquals(first, second)
            && first is CachedWeatherService);
        Expect("the \"live\" key resolves a different implementation",
            app.Services.GetRequiredKeyedService<IWeatherService>("live") is LiveWeatherService);

        WeatherOptions options = app.Services.GetRequiredService<IOptions<WeatherOptions>>().Value;
        Expect("IOptions<WeatherOptions> is bound from the Weather section", options.DefaultCity == "Seattle");

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_total - s_passed;
    }

    private static Task<HttpResponseMessage> PostRaw(HttpClient http, string path, string json) =>
        http.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task Show(string request, HttpResponseMessage response)
    {
        // First line only: an unhandled exception would otherwise print its stack trace.
        string body = (await response.Content.ReadAsStringAsync()).Split('\n')[0].TrimEnd();
        Console.WriteLine($"  {request} -> {(int)response.StatusCode} {body}");
    }

    private static void Expect(string description, bool ok)
    {
        s_total++;
        if (ok)
        {
            s_passed++;
        }
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {description}");
    }
}
