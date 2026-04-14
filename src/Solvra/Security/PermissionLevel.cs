#nullable enable

namespace Solvra.Security;

public enum PermissionLevel
{
    Read = 0,
    Write = 1,
    Network = 2,
    Execute = 3,
    Agent = 4
}

public enum PermissionMode
{
    Default,
    Auto,
    Plan,
    BypassPermissions
}
