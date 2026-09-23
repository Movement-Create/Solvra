#nullable enable

namespace Solvra.Core;

/// <summary>An idempotent inverse for one successful registration.</summary>
internal sealed class RegistrationHandle(Action dispose) : IDisposable
{
    private Action? _dispose = dispose;

    public void Dispose()
    {
        Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
