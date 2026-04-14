using System.Text.Json;
using System.Text.Json.Serialization;

namespace Solvra.Core;

public record CostEntry
{
    [JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("input_tokens")]
    public required int InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public required int OutputTokens { get; init; }

    [JsonPropertyName("cost_usd")]
    public required decimal CostUsd { get; init; }

    [JsonPropertyName("turns")]
    public required int Turns { get; init; }
}

public record CostSummary
{
    public decimal TotalUsd { get; init; }
    public IReadOnlyList<CostEntry> Entries { get; init; } = [];
}

public sealed class CostTracker
{
    private readonly string _ledgerPath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public CostTracker(string? ledgerPath = null)
    {
        _ledgerPath = ledgerPath ?? Path.Combine("sessions", "cost-ledger.jsonl");
    }

    public async Task RecordAsync(CostEntry entry)
    {
        try
        {
            var dir = Path.GetDirectoryName(_ledgerPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var line = JsonSerializer.Serialize(entry, JsonOptions) + "\n";
            await File.AppendAllTextAsync(_ledgerPath, line);
        }
        catch
        {
            // Silently ignore write failures (matches TypeScript behavior)
        }
    }

    public async Task<CostSummary> GetTotalCostAsync()
    {
        if (!File.Exists(_ledgerPath))
            return new CostSummary();

        var entries = new List<CostEntry>();
        decimal total = 0;

        var lines = await File.ReadAllLinesAsync(_ledgerPath);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<CostEntry>(line, JsonOptions);
                if (entry != null)
                {
                    entries.Add(entry);
                    total += entry.CostUsd;
                }
            }
            catch
            {
                // Skip malformed lines
            }
        }

        return new CostSummary { TotalUsd = total, Entries = entries };
    }
}
