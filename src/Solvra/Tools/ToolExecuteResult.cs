#nullable enable

using Solvra.Models;

namespace Solvra.Tools;

public record ToolExecuteResult(string Output, bool IsError, ImageContent? Image = null);
