using System.Text.Json;
using Anthropic;
using TicketRouting.Routing;

var builder = WebApplication.CreateBuilder(args);

// Keys: appsettings.json has empty slots; user-secrets (or env vars) override them.
foreach (var key in new[] { "Anthropic:ApiKey", "TypeSafe:ApiKey" })
    if (string.IsNullOrWhiteSpace(builder.Configuration[key]))
        throw new InvalidOperationException($"Missing {key}. Set it in appsettings.json or run: dotnet user-secrets set \"{key}\" \"<key>\"");

builder.Services.AddSingleton(new AnthropicClient { ApiKey = builder.Configuration["Anthropic:ApiKey"] });
builder.Services.AddSingleton<ClaudeRouter>();

builder.Services.AddHttpClient<JevClient>(c =>
{
    c.BaseAddress = new Uri("https://jevstation.com/api/");
    c.DefaultRequestHeaders.Authorization =
        new("Bearer", builder.Configuration["TypeSafe:ApiKey"]);
});
builder.Services.AddTransient<TicketRouter>();

var app = builder.Build();

app.MapPost("/route/llm", (TicketRequest req, ClaudeRouter llm, CancellationToken ct)
    => llm.RouteAsync(req.Text, ct));

app.MapPost("/route/jev", async (TicketRequest req, TicketRouter router, CancellationToken ct) =>
{
    try
    {
        var (decision, route, raw) = await router.RouteAsync(req.Text, ct);
        return Results.Ok(new { route, decision, raw });
    }
    catch (HttpRequestException ex)             // e.g. 402 out of credits: show JevStation's message, not a stack trace
    {
        return Results.Problem(ex.Message, statusCode: (int?)ex.StatusCode ?? 502);
    }
});

// Free call: plan, remaining credits, rate limits. Check it before running /compare (1 credit per ticket).
app.MapGet("/jev/account", async (JevClient jev, CancellationToken ct)
    => Results.Content(await jev.AccountAsync(ct), "application/json"));

// ?take=20 runs only the first 20 tickets. Stops early, with partial results, before credits run out.
app.MapGet("/compare", async (int? take, ClaudeRouter llm, TicketRouter router, CancellationToken ct) =>
{
    var tickets = JsonSerializer.Deserialize<List<Ticket>>(
        await File.ReadAllTextAsync("Data/tickets.json", ct), JsonSerializerOptions.Web)!
        .Take(take ?? int.MaxValue).ToList();

    var llmRuns = new List<(Ticket T, RouteDecision D)>();
    var jevRuns = new List<(Ticket T, RouteDecision D, string Route)>();
    string? stoppedEarly = null;
    int? creditsLeft = null;

    // JevStation free plan: 10 requests/min. Space Jev calls ≥ 6.2 s apart (pacing isn't part of the measured latency).
    var jevGap = TimeSpan.FromSeconds(6.2);
    var lastJevCall = DateTime.MinValue;

    foreach (var t in tickets)                      // sequential: we measure latency, not throughput
    {
        if (creditsLeft == 0) { stoppedEarly = $"Out of JevStation credits after {jevRuns.Count} tickets"; break; }

        var wait = lastJevCall + jevGap - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        lastJevCall = DateTime.UtcNow;

        try
        {
            // Jev first: if it can't run, we don't pay for an LLM call we can't compare
            var (d, route, raw) = await router.RouteAsync(t.Text, ct);
            creditsLeft = raw.Credits.Remaining;
            jevRuns.Add((t, d, route));
        }
        catch (HttpRequestException ex)             // 402 out of credits, 429, 502…: keep what we have
        {
            stoppedEarly = $"Stopped after {jevRuns.Count} tickets. {ex.Message}";
            break;
        }

        try
        {
            llmRuns.Add((t, await llm.RouteAsync(t.Text, ct)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)   // bad key, rate limit…: keep pairs aligned
        {
            jevRuns.RemoveAt(jevRuns.Count - 1);
            stoppedEarly = $"Stopped after {jevRuns.Count} tickets. LLM call failed: {ex.Message.Split('\n')[0]}";
            break;
        }
    }

    if (jevRuns.Count == 0)
        return Results.Problem(stoppedEarly ?? "No tickets to run", statusCode: 502);

    var ran = jevRuns.Select(r => r.T).ToList();

    return Results.Ok(new
    {
        tickets = ran.Count,
        stoppedEarly,
        creditsLeft,
        llm = Summarise(llmRuns),
        jev = Summarise(jevRuns.Select(r => (r.T, r.D)).ToList()),
        jevSentToHuman = jevRuns.Count(r => r.Route == "human-triage"),
        misses = ran.Select((t, i) => new
            {
                t.Id,
                expected = t.ExpectedTeam,
                llm = llmRuns[i].D.Team,
                jev = jevRuns[i].D.Team,
                jevConfidence = jevRuns[i].D.Confidence,
            })
            .Where(m => m.llm != m.expected || m.jev != m.expected),
    });
});

app.Run();

static object Summarise(List<(Ticket T, RouteDecision D)> runs)
{
    var latencies = runs.Select(r => r.D.LatencyMs).Order().ToList();
    return new
    {
        accuracy = $"{runs.Count(r => r.D.Team == r.T.ExpectedTeam)}/{runs.Count}",
        avgLatencyMs = Math.Round(latencies.Average()),
        p95LatencyMs = latencies[(int)Math.Ceiling(latencies.Count * 0.95) - 1],
        costPer1000Usd = Math.Round(runs.Sum(r => r.D.CostUsd) / runs.Count * 1000, 4),
    };
}

record TicketRequest(string Text);
