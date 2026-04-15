using Solvra.Models;

namespace Solvra.Providers;

/// <summary>Tagged union for structured streaming events from LLM providers.</summary>
public abstract record StreamEvent;

/// <summary>A chunk of assistant text.</summary>
public sealed record StreamText(string Delta) : StreamEvent;

/// <summary>Start of a tool_use content block.</summary>
public sealed record StreamToolUseStart(string Id, string Name) : StreamEvent;

/// <summary>A JSON fragment for an in-progress tool_use block.</summary>
public sealed record StreamToolUseDelta(string Id, string JsonFragment) : StreamEvent;

/// <summary>End of a tool_use content block. Consumer should parse accumulated JSON.</summary>
public sealed record StreamToolUseEnd(string Id) : StreamEvent;

/// <summary>End of the entire message. Carries token usage and stop reason.</summary>
public sealed record StreamMessageEnd(TokenUsage Usage, string StopReason) : StreamEvent;
