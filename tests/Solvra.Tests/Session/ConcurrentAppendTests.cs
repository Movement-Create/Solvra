#nullable enable

using Solvra.Core;
using Xunit;

namespace Solvra.Tests.Session;

/// <summary>Parallel tool batches append tool_result events concurrently; none may be lost.</summary>
public class ConcurrentAppendTests
{
    [Fact]
    public async Task ConcurrentToolResults_AllLandInTheSessionFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"solvra-append-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var mgr = new SessionManager(dir);
            var session = await mgr.CreateAsync(new SessionConfig { Id = "s", CreatedAt = DateTime.UtcNow.ToString("o") });
            const int n = 40;
            await Task.WhenAll(Enumerable.Range(0, n).Select(i =>
                mgr.AppendToolResultAsync(session, $"call_{i}", new string('x', 2000 + i), false)));

            var lines = await File.ReadAllLinesAsync(session.FilePath!);
            var results = lines.Count(l => l.Contains("\"type\":\"tool_result\""));
            Assert.Equal(n, results);
            for (var i = 0; i < n; i++)
                Assert.Contains($"\"call_id\":\"call_{i}\"", string.Join("\n", lines));

            var resumed = await mgr.ResumeAsync("s");
            Assert.Equal(n, resumed.Messages.SelectMany(m => m.Content).Count(c => c is Solvra.Models.ToolResultContent));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
