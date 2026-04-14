#nullable enable

namespace Solvra.Skills;

public record SkillDefinition(
    string Name,
    string Description,
    List<string> TriggerPatterns,
    bool AlwaysInject,
    List<string> ToolsRequired,
    string Content,
    string FilePath);
