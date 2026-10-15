using System.Text.Json.Serialization;

namespace TicketRouting.Routing;

// No official .NET SDK for Jev — one REST endpoint and a bearer key (JevStation-hosted).
public sealed class JevClient(HttpClient http)
{
    public async Task<JevResponse> RouteAsync(string ticket, CancellationToken ct = default)
    {
        var body = new
        {
            model = "jev",                       // JevStation pins the version server-side
            state = ticket,
            questions = new
            {
                team = new
                {
                    type = "choice",
                    instructions = "Which team should handle this ticket",
                    criteria = new Dictionary<string, string>
                    {
                        ["billing"]   = "Invoices, refunds, payment failures, plan changes",
                        ["technical"] = "Bugs, errors, outages, integration problems",
                        ["sales"]     = "Pricing questions, demos, new purchases",
                        ["other"]     = "Anything that fits none of the above",
                        ["unclear"]   = "Too little detail to tell which team",
                    },
                },
                is_urgent = new { type = "noul", instructions = "The customer is blocked right now" },
            },
        };

        using var res = await http.PostAsJsonAsync("v1/systemone", body, ct);

        // JevStation wraps every reply: { code: 0 | -1, message, data }. Errors carry the reason in message.
        JevEnvelope? envelope = null;
        try { envelope = await res.Content.ReadFromJsonAsync<JevEnvelope>(ct); }
        catch (System.Text.Json.JsonException) { }   // e.g. a gateway HTML page on 502

        if (!res.IsSuccessStatusCode || envelope is not { Code: 0, Data: not null })
            throw new HttpRequestException(
                $"Jev call failed ({(int)res.StatusCode}): {envelope?.Message ?? res.ReasonPhrase}", null, res.StatusCode);

        return envelope.Data;
    }

    // GET on the same endpoint is free: plan, remaining credits, rate limits.
    public Task<string> AccountAsync(CancellationToken ct = default) => http.GetStringAsync("v1/systemone", ct);
}

public sealed record JevEnvelope(int Code, string Message, JevResponse? Data);

public sealed record JevResponse(string Model, JevAnswers Answers, JevUsage Usage, JevCredits Credits);

public sealed record JevAnswers(
    JevChoice Team,
    [property: JsonPropertyName("is_urgent")] JevNoul IsUrgent);

public sealed record JevChoice(string Choice, double Confidence, Dictionary<string, double> Probabilities);

public sealed record JevNoul(double Noul);

public sealed record JevUsage(
    [property: JsonPropertyName("input_tokens")] int InputTokens,
    [property: JsonPropertyName("output_tokens")] int OutputTokens);

public sealed record JevCredits(int Charged, int Remaining);
