using Agentic.Core;

namespace Agentic.Agents;

/// <summary>
/// Chaos hook used by scenarios and tests to prove the controls work.
/// Modes: <c>exception</c> (agent throws), <c>bad-output</c> (empty artifact, fails the exit gate),
/// <c>secret</c> (output leaks a credential, triggers the critical policy).
/// </summary>
public sealed record FaultSpec(string Node, string Mode, int FailTimes = 1);

public sealed class FaultInjectingAgent : IAgent
{
    private readonly IAgent _inner;
    private readonly FaultSpec _fault;
    private int _calls;

    public FaultInjectingAgent(IAgent inner, FaultSpec fault)
    {
        _inner = inner;
        _fault = fault;
    }

    public string Name => _inner.Name;

    public async Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        var call = Interlocked.Increment(ref _calls);
        if (call > _fault.FailTimes) return await _inner.ExecuteAsync(request, ct);

        switch (_fault.Mode.ToLowerInvariant())
        {
            case "exception":
                throw new InvalidOperationException($"injected transient failure #{call}");

            case "bad-output":
            {
                var result = await _inner.ExecuteAsync(request, ct);
                var broken = result.Artifacts.Select(a => Artifact.Create(a.Name, a.Kind, "{}", a.ProducedBy)).ToList();
                return result with { Artifacts = broken };
            }

            case "secret":
            {
                var result = await _inner.ExecuteAsync(request, ct);
                var first = result.Artifacts[0];
                var leaked = Artifact.Create(first.Name, first.Kind, first.Content + "\n// password = \"hunter2-prod\"", first.ProducedBy);
                return result with { Artifacts = new[] { leaked }.Concat(result.Artifacts.Skip(1)).ToList() };
            }

            default:
                throw new InvalidOperationException($"Unknown fault mode '{_fault.Mode}'.");
        }
    }
}
