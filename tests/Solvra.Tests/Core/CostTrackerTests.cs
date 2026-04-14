using Xunit;
using Solvra.Core;

namespace Solvra.Tests.Core;

public class CostTrackerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _ledgerPath;
    private readonly CostTracker _tracker;

    public CostTrackerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"solvra-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _ledgerPath = Path.Combine(_tempDir, "cost-ledger.jsonl");
        _tracker = new CostTracker(_ledgerPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task RecordAsync_CreatesFileAndAppendsEntry()
    {
        var entry = CreateEntry("session1", 0.05m);
        await _tracker.RecordAsync(entry);

        Assert.True(File.Exists(_ledgerPath));
        var lines = await File.ReadAllLinesAsync(_ledgerPath);
        Assert.Single(lines.Where(l => !string.IsNullOrWhiteSpace(l)));
    }

    [Fact]
    public async Task RecordAsync_AppendsMultipleEntries()
    {
        await _tracker.RecordAsync(CreateEntry("s1", 0.01m));
        await _tracker.RecordAsync(CreateEntry("s2", 0.02m));
        await _tracker.RecordAsync(CreateEntry("s3", 0.03m));

        var lines = await File.ReadAllLinesAsync(_ledgerPath);
        var nonEmpty = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        Assert.Equal(3, nonEmpty.Count);
    }

    [Fact]
    public async Task GetTotalCostAsync_ReturnsZeroForEmptyLedger()
    {
        var summary = await _tracker.GetTotalCostAsync();
        Assert.Equal(0m, summary.TotalUsd);
        Assert.Empty(summary.Entries);
    }

    [Fact]
    public async Task GetTotalCostAsync_SumsCosts()
    {
        await _tracker.RecordAsync(CreateEntry("s1", 0.10m));
        await _tracker.RecordAsync(CreateEntry("s2", 0.25m));
        await _tracker.RecordAsync(CreateEntry("s3", 0.05m));

        var summary = await _tracker.GetTotalCostAsync();
        Assert.Equal(0.40m, summary.TotalUsd);
        Assert.Equal(3, summary.Entries.Count);
    }

    [Fact]
    public async Task GetTotalCostAsync_SkipsMalformedLines()
    {
        await _tracker.RecordAsync(CreateEntry("s1", 0.10m));
        await File.AppendAllTextAsync(_ledgerPath, "not-json\n");
        await _tracker.RecordAsync(CreateEntry("s2", 0.20m));

        var summary = await _tracker.GetTotalCostAsync();
        Assert.Equal(0.30m, summary.TotalUsd);
        Assert.Equal(2, summary.Entries.Count);
    }

    [Fact]
    public async Task RecordAsync_CreatesParentDirectory()
    {
        var nestedPath = Path.Combine(_tempDir, "nested", "dir", "ledger.jsonl");
        var tracker = new CostTracker(nestedPath);

        await tracker.RecordAsync(CreateEntry("s1", 0.01m));
        Assert.True(File.Exists(nestedPath));
    }

    private static CostEntry CreateEntry(string sessionId, decimal cost) => new()
    {
        Timestamp = DateTime.UtcNow.ToString("o"),
        SessionId = sessionId,
        Model = "claude-3-5-sonnet-20241022",
        Provider = "anthropic",
        InputTokens = 1000,
        OutputTokens = 500,
        CostUsd = cost,
        Turns = 3
    };
}
