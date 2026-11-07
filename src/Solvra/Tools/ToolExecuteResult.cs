#nullable enable

using Solvra.Models;

namespace Solvra.Tools;

public record ToolExecuteResult(string Output, bool IsError, ImageContent? Image = null)
{
    /// <summary>Process exit code for bash/code_run; null for tools that run nothing.</summary>
    public int? ExitCode { get; init; }

    /// <summary>
    /// Stable references to what the output came from (file + hash + line range, match path + line,
    /// command + hashes, URL + ETag). Written to the session and audit logs; never shown to the model.
    /// </summary>
    public IReadOnlyList<object>? Records { get; init; }
}
