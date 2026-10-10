namespace Agentic.Agents.Ai;

/// <summary>Read-only views of the repository that the orchestrator puts into prompts (agents never read files themselves).</summary>
internal static class RepoContext
{
    private const string Api = "src/UrlShortener.Api";
    private const string Tests = "tests/UrlShortener.Tests";

    public static IReadOnlyList<(string Path, string Content)> ProductSources(string repoRoot) =>
        CodebaseMap.Scan(repoRoot).Files.Select(f => (f.RelativePath, f.Content)).ToList();

    public static IReadOnlyList<string> TestFiles(string repoRoot)
    {
        var dir = Path.Combine(repoRoot, Tests);
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(repoRoot, p).Replace('\\', '/'))
            .Where(p => !p.Contains("/bin/") && !p.Contains("/obj/"))
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    /// <summary>The public API surface a test author needs, plus the in-process test host and clock helpers.</summary>
    public static IReadOnlyList<(string Path, string Content)> TestAuthoringContext(string repoRoot)
    {
        var files = new List<(string, string)>();
        foreach (var rel in new[]
                 {
                     $"{Api}/Endpoints/ShortLinkEndpoints.cs", $"{Api}/Endpoints/Contracts.cs", $"{Api}/Services/Results.cs",
                     $"{Api}/Options/ShortenerOptions.cs", $"{Tests}/TestSupport.cs"
                 })
        {
            var path = Path.Combine(repoRoot, rel);
            if (File.Exists(path)) files.Add((rel, File.ReadAllText(path)));
        }

        // The ApiHost helper lives at the top of the integration tests; include just that part.
        var integration = Path.Combine(repoRoot, Tests, "ApiIntegrationTests.cs");
        if (File.Exists(integration))
        {
            var text = File.ReadAllText(integration);
            var end = text.IndexOf("public class ApiIntegrationTests", StringComparison.Ordinal);
            files.Add(($"{Tests}/ApiIntegrationTests.cs (ApiHost helper only)", end > 0 ? text[..end] : text));
        }
        return files;
    }

    /// <summary>The assertion API of the dependency-free test framework used by the suite (mirrors xUnit).</summary>
    public const string AssertApi = """
        Test framework: a dependency-free xUnit look-alike. Use `using Xunit;`, public classes, and public methods marked [Fact]
        returning void or Task. Only these members exist:
          Assert.Equal<T>(expected, actual); Assert.Equal(double expected, double actual, int precision); Assert.NotEqual<T>(a, b);
          Assert.True(bool, string? message = null); Assert.False(bool, string? message = null); Assert.Null(object?); Assert.NotNull(object?);
          Assert.Contains(string expectedSubstring, string? actual); Assert.Contains<T>(T item, IEnumerable<T> collection);
          Assert.DoesNotContain<T>(T item, IEnumerable<T> collection); Assert.Single<T>(IEnumerable<T>) returns the item;
          Assert.Empty(IEnumerable); Assert.NotEmpty(IEnumerable); Assert.Throws<T>(Action); await Assert.ThrowsAsync<T>(Func<Task>).
        There is no [Theory], no InlineData, no fixtures and no constructor injection.
        Implicit usings are enabled (System, System.Linq, System.Net.Http, System.Threading.Tasks, ...). Add `using System.Net;`
        for HttpStatusCode and `using System.Net.Http.Json;` for JsonContent.
        """;
}
