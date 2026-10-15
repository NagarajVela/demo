using System.Diagnostics;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace TicketRouting.Routing;

public sealed class ClaudeRouter(AnthropicClient client)
{
    // Claude Haiku 4.5 list price: $1 / MTok input, $5 / MTok output
    const decimal InputPerToken = 1.00m / 1_000_000;
    const decimal OutputPerToken = 5.00m / 1_000_000;

    static readonly Dictionary<string, JsonElement> RouteSchema = new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(new
        {
            team = new { type = "string", @enum = new[] { "billing", "technical", "sales", "other", "unclear" } },
            urgent = new { type = "boolean" },
        }),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "team", "urgent" }),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
    };

    public async Task<RouteDecision> RouteAsync(string ticket, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = "claude-haiku-4-5",
            MaxTokens = 256,
            System = "Route the support ticket to exactly one team: billing (invoices, refunds, payment failures, plan changes), "
                   + "technical (bugs, errors, outages, integration problems), sales (pricing questions, demos, new purchases) "
                   + "other (fits none of these) or unclear (too little detail to tell). Set urgent to true only if the customer is blocked right now.",
            Messages = [new() { Role = Role.User, Content = ticket }],
            OutputConfig = new OutputConfig { Format = new JsonOutputFormat { Schema = RouteSchema } },
        }, cancellationToken: ct);

        sw.Stop();

        var json = response.Content.Select(b => b.Value).OfType<TextBlock>().First().Text;
        using var doc = JsonDocument.Parse(json);
        var input = (int)response.Usage.InputTokens;
        var output = (int)response.Usage.OutputTokens;

        return new RouteDecision(
            Router: "claude-haiku-4-5",
            Team: doc.RootElement.GetProperty("team").GetString()!,
            Urgent: doc.RootElement.GetProperty("urgent").GetBoolean(),
            Confidence: null,                                   // an LLM label has no probability
            LatencyMs: sw.ElapsedMilliseconds,
            InputTokens: input,
            OutputTokens: output,
            CostUsd: input * InputPerToken + output * OutputPerToken);
    }
}
