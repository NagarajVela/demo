using System.Diagnostics;

namespace TicketRouting.Routing;

public sealed class TicketRouter(JevClient jev)
{
    // Jev list price (TypeSafe direct): $0.042 / MTok input, output free.
    // JevStation bills credits per run instead; we keep token pricing so both routers compare like for like.
    const decimal InputPerToken = 0.042m / 1_000_000;

    const double ConfidenceFloor = 0.6;   // TypeSafe's confidence-routing starting point — tune on your tickets
    const double PriorityUrgency = 0.85;

    public async Task<(RouteDecision Decision, string Route, JevResponse Raw)> RouteAsync(string ticket, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var res = await jev.RouteAsync(ticket, ct);
        sw.Stop();

        var team = res.Answers.Team;

        var route = team.Confidence switch
        {
            < ConfidenceFloor                                  => "human-triage",
            _ when team.Choice is "other" or "unclear"         => "human-triage",
            _ when res.Answers.IsUrgent.Noul > PriorityUrgency => $"{team.Choice}-priority",
            _                                                  => team.Choice,
        };

        var decision = new RouteDecision(
            Router: res.Model,
            Team: team.Choice,
            Urgent: res.Answers.IsUrgent.Noul > 0.5,
            Confidence: team.Confidence,
            LatencyMs: sw.ElapsedMilliseconds,
            InputTokens: res.Usage.InputTokens,
            OutputTokens: res.Usage.OutputTokens,
            CostUsd: res.Usage.InputTokens * InputPerToken);

        return (decision, route, res);
    }
}
