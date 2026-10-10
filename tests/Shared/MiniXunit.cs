// A tiny, dependency-free stand-in for xUnit. It is used because the build sandbox has no NuGet access.
// The API (Xunit.Fact, Xunit.Assert) mirrors xUnit, so the tests are written exactly as xUnit tests.
// To switch to real xUnit: delete this file, add PackageReference Microsoft.NET.Test.Sdk, xunit and
// xunit.runner.visualstudio to the test projects, and remove the Program.cs runner entry point.

using System.Collections;
using System.Reflection;

namespace Xunit
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class FactAttribute : Attribute { }

    public sealed class AssertException : Exception
    {
        public AssertException(string message) : base(message) { }
    }

    public static class Assert
    {
        public static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new AssertException($"Assert.Equal failed.\n  Expected: {expected}\n  Actual:   {actual}");
        }

        public static void Equal(double expected, double actual, int precision)
        {
            if (Math.Round(expected, precision) != Math.Round(actual, precision))
                throw new AssertException($"Assert.Equal failed (precision {precision}).\n  Expected: {expected}\n  Actual:   {actual}");
        }

        public static void NotEqual<T>(T notExpected, T actual)
        {
            if (EqualityComparer<T>.Default.Equals(notExpected, actual))
                throw new AssertException($"Assert.NotEqual failed. Value: {actual}");
        }

        public static void True(bool condition, string? message = null)
        {
            if (!condition) throw new AssertException(message ?? "Assert.True failed.");
        }

        public static void False(bool condition, string? message = null)
        {
            if (condition) throw new AssertException(message ?? "Assert.False failed.");
        }

        public static void Null(object? value)
        {
            if (value is not null) throw new AssertException($"Assert.Null failed. Value: {value}");
        }

        public static void NotNull(object? value)
        {
            if (value is null) throw new AssertException("Assert.NotNull failed.");
        }

        public static void Contains(string expectedSubstring, string? actual)
        {
            if (actual is null || !actual.Contains(expectedSubstring, StringComparison.Ordinal))
                throw new AssertException($"Assert.Contains failed.\n  Looking for: {expectedSubstring}\n  In:          {actual}");
        }

        public static void Contains<T>(T expected, IEnumerable<T> collection)
        {
            if (!collection.Contains(expected))
                throw new AssertException($"Assert.Contains failed. Item not found: {expected}");
        }

        public static void DoesNotContain<T>(T unexpected, IEnumerable<T> collection)
        {
            if (collection.Contains(unexpected))
                throw new AssertException($"Assert.DoesNotContain failed. Item found: {unexpected}");
        }

        public static T Single<T>(IEnumerable<T> collection)
        {
            var items = collection.ToList();
            if (items.Count != 1) throw new AssertException($"Assert.Single failed. Expected 1 item but found {items.Count}.");
            return items[0];
        }

        public static void Empty(IEnumerable collection)
        {
            if (collection.Cast<object>().Any()) throw new AssertException("Assert.Empty failed.");
        }

        public static void NotEmpty(IEnumerable collection)
        {
            if (!collection.Cast<object>().Any()) throw new AssertException("Assert.NotEmpty failed.");
        }

        public static T Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T ex) { return ex; }
            catch (Exception ex) { throw new AssertException($"Expected {typeof(T).Name} but got {ex.GetType().Name}: {ex.Message}"); }
            throw new AssertException($"Expected {typeof(T).Name} but nothing was thrown.");
        }

        public static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
        {
            try { await action(); }
            catch (T ex) { return ex; }
            catch (Exception ex) { throw new AssertException($"Expected {typeof(T).Name} but got {ex.GetType().Name}: {ex.Message}"); }
            throw new AssertException($"Expected {typeof(T).Name} but nothing was thrown.");
        }
    }

    /// <summary>Discovers [Fact] methods by reflection and runs each on a fresh instance of its class.</summary>
    public static class MiniRunner
    {
        public static int Run(Assembly assembly, string[] args)
        {
            // usage: [nameFilter] [--junit <path>]
            var junitIndex = Array.IndexOf(args, "--junit");
            var junitPath = junitIndex >= 0 && junitIndex + 1 < args.Length ? args[junitIndex + 1] : null;
            var filter = args.Where((a, i) => !a.StartsWith("--") && (junitIndex < 0 || i != junitIndex + 1)).FirstOrDefault();
            var passed = 0;
            var failed = new List<string>();
            var results = new List<(string Class, string Name, double Seconds, string? Failure)>();
            var started = DateTime.UtcNow;

            var tests = assembly.GetTypes()
                .Where(t => t.IsClass && t.IsPublic && !t.IsAbstract)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.GetCustomAttribute<FactAttribute>() is not null)
                    .Select(m => (Type: t, Method: m)))
                .Where(x => filter is null || $"{x.Type.Name}.{x.Method.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Type.Name).ThenBy(x => x.Method.Name)
                .ToList();

            foreach (var (type, method) in tests)
            {
                var name = $"{type.Name}.{method.Name}";
                object? instance = null;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    instance = Activator.CreateInstance(type);
                    var result = method.Invoke(instance, null);
                    if (result is Task task) task.GetAwaiter().GetResult();
                    passed++;
                    results.Add((type.Name, method.Name, clock.Elapsed.TotalSeconds, null));
                    Console.WriteLine($"  PASS  {name}");
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
                    failed.Add(name);
                    results.Add((type.Name, method.Name, clock.Elapsed.TotalSeconds, $"{inner.GetType().Name}: {inner.Message}"));
                    Console.WriteLine($"  FAIL  {name}\n        {inner.GetType().Name}: {inner.Message.Replace("\n", "\n        ")}");
                }
                finally
                {
                    (instance as IDisposable)?.Dispose();
                }
            }

            Console.WriteLine();
            Console.WriteLine($"Total: {tests.Count}  Passed: {passed}  Failed: {failed.Count}  ({(DateTime.UtcNow - started).TotalSeconds:0.0}s)");
            if (junitPath is not null) WriteJUnit(junitPath, assembly.GetName().Name ?? "tests", results, DateTime.UtcNow - started);
            return failed.Count == 0 ? 0 : 1;
        }

        /// <summary>JUnit XML so CI servers (Jenkins, Azure DevOps, GitHub Actions, GitLab) can show the results.</summary>
        private static void WriteJUnit(string path, string suite, List<(string Class, string Name, double Seconds, string? Failure)> results, TimeSpan elapsed)
        {
            var doc = new System.Xml.Linq.XElement("testsuites",
                new System.Xml.Linq.XElement("testsuite",
                    new System.Xml.Linq.XAttribute("name", suite),
                    new System.Xml.Linq.XAttribute("tests", results.Count),
                    new System.Xml.Linq.XAttribute("failures", results.Count(r => r.Failure is not null)),
                    new System.Xml.Linq.XAttribute("time", elapsed.TotalSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)),
                    results.Select(r => new System.Xml.Linq.XElement("testcase",
                        new System.Xml.Linq.XAttribute("classname", $"{suite}.{r.Class}"),
                        new System.Xml.Linq.XAttribute("name", r.Name),
                        new System.Xml.Linq.XAttribute("time", r.Seconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)),
                        r.Failure is null ? null : new System.Xml.Linq.XElement("failure", new System.Xml.Linq.XAttribute("message", r.Failure.Split('\n')[0]), r.Failure)))));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            doc.Save(path);
        }
    }
}
