namespace TicketRouting.Routing;

public sealed record Ticket(int Id, string Text, string ExpectedTeam);

public sealed record RouteDecision(
    string Router,
    string Team,
    bool Urgent,
    double? Confidence,
    long LatencyMs,
    int InputTokens,
    int OutputTokens,
    decimal CostUsd);
