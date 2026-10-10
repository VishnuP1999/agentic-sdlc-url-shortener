using System.Text.Json;

namespace Agentic.Model;

/// <summary>
/// Deterministic stand-in for a model, used by automated tests. Responses are produced per request by a handler
/// (typically keyed on <see cref="ModelRequest.Role"/>); throwing from the handler simulates model failures.
/// </summary>
public sealed class FakeModelClient : IModelClient
{
    private readonly Func<ModelRequest, int, object> _handler;
    private readonly List<ModelRequest> _requests = new();
    private readonly object _gate = new();

    /// <param name="handler">Receives the request and the 1-based call number for its role; returns any JSON-serialisable object.</param>
    public FakeModelClient(Func<ModelRequest, int, object> handler) => _handler = handler;

    public string Describe => "fake-model";

    public IReadOnlyList<ModelRequest> Requests
    {
        get { lock (_gate) return _requests.ToList(); }
    }

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        int call;
        lock (_gate)
        {
            _requests.Add(request);
            call = _requests.Count(r => r.Role == request.Role);
        }
        var value = _handler(request, call);
        var element = value is JsonElement je ? je : JsonSerializer.SerializeToElement(value, JsonOptions);
        return Task.FromResult(new ModelResponse(element, "fake-model", new ModelUsage(100, 50, 0), 1));
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
