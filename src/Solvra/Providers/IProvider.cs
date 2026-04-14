namespace Solvra.Providers;

public interface IProvider
{
    string Id { get; }
    string DisplayName { get; }
    Task<LlmResponse> CompleteAsync(CompletionOptions options, CancellationToken ct = default);
    IAsyncEnumerable<string> StreamAsync(CompletionOptions options, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default);
    Task<bool> ValidateAsync(CancellationToken ct = default);
    decimal EstimateCost(string model, int inputTokens, int outputTokens);
}
