using System.Globalization;
using System.Text;
using Xunit.Runners;

namespace Ladder.DI.Judge;

/// <summary>
/// Runs Microsoft.Extensions.DependencyInjection.Specification.Tests against every subject in
/// <see cref="Reference_Specification"/>'s file with xUnit v2's in-process runner, then writes one verdict per test
/// per subject and a summary comparing each of ours with the reference, test by test.
/// Exit code 1 if any variant of ours fails a test the reference passes.
/// </summary>
internal static class Program
{
    private const string Reference = "Reference";

    private static int Main(string[] args)
    {
        string resultsDir = args.Length > 0 ? args[0] : "results";
        Directory.CreateDirectory(resultsDir);

        List<Verdict> verdicts = Run(typeof(Program).Assembly.Location);

        // "Ours_Keyed" → subject "Ours", suite "Keyed". Tests are matched across subjects by suite + display name.
        var bySubject = verdicts
            .GroupBy(v => v.Subject)
            .ToDictionary(g => g.Key, g => g.ToDictionary(v => v.Test, StringComparer.Ordinal), StringComparer.Ordinal);

        foreach ((string subject, Dictionary<string, Verdict> tests) in bySubject)
        {
            var lines = tests.Values
                .OrderBy(v => v.Test, StringComparer.Ordinal)
                .Select(v => v.Outcome == "PASS" ? $"PASS  {v.Test}" : $"{v.Outcome}  {v.Test}\n      {v.Detail}");
            File.WriteAllText(Path.Combine(resultsDir, $"spec-{subject}.txt"), string.Join('\n', lines) + "\n");
        }

        Dictionary<string, Verdict> reference = bySubject[Reference];
        var summary = new StringBuilder();
        summary.AppendLine(CultureInfo.InvariantCulture, $"Microsoft.Extensions.DependencyInjection.Specification.Tests {SpecVersion()}, run by xunit.runner.utility (xUnit v2) in-process, not parallel.");
        summary.AppendLine();
        summary.AppendLine("| Subject | Passed | Failed | Skipped | Total |");
        summary.AppendLine("|---|---:|---:|---:|---:|");
        foreach ((string subject, Dictionary<string, Verdict> tests) in bySubject.OrderBy(s => s.Key == Reference ? "" : s.Key, StringComparer.Ordinal))
        {
            summary.AppendLine(CultureInfo.InvariantCulture,
                $"| {subject} | {Count(tests, "PASS")} | {Count(tests, "FAIL")} | {Count(tests, "SKIP")} | {tests.Count} |");
        }

        bool regressions = false;
        foreach ((string subject, Dictionary<string, Verdict> tests) in bySubject.Where(s => s.Key != Reference).OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            summary.AppendLine();
            summary.AppendLine(CultureInfo.InvariantCulture, $"## {subject} vs {Reference}");
            var differences = reference.Keys.Union(tests.Keys)
                .Order(StringComparer.Ordinal)
                .Select(test => (test, reference: Outcome(reference, test), ours: Outcome(tests, test)))
                .Where(d => d.reference != d.ours)
                .ToList();
            summary.AppendLine(CultureInfo.InvariantCulture, $"Tests the reference passes: {Count(reference, "PASS")}; of those, {subject} passes {reference.Count(r => r.Value.Outcome == "PASS" && Outcome(tests, r.Key) == "PASS")}.");
            summary.AppendLine(CultureInfo.InvariantCulture, $"Differences: {differences.Count}");
            foreach ((string test, string referenceOutcome, string ours) in differences)
            {
                summary.AppendLine(CultureInfo.InvariantCulture, $"- {test}: reference {referenceOutcome}, {subject} {ours}");
                regressions |= referenceOutcome == "PASS";
            }
        }

        File.WriteAllText(Path.Combine(resultsDir, "summary.txt"), summary.ToString());
        Console.Write(summary);
        return regressions ? 1 : 0;
    }

    private static int Count(Dictionary<string, Verdict> tests, string outcome) => tests.Values.Count(v => v.Outcome == outcome);

    private static string Outcome(Dictionary<string, Verdict> tests, string test) => tests.TryGetValue(test, out Verdict? v) ? v.Outcome : "ABSENT";

    private static string SpecVersion() =>
        typeof(Microsoft.Extensions.DependencyInjection.Specification.DependencyInjectionSpecificationTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "?";

    private static List<Verdict> Run(string assembly)
    {
        var verdicts = new List<Verdict>();
        using var finished = new ManualResetEventSlim();
        using AssemblyRunner runner = AssemblyRunner.WithoutAppDomain(assembly);
        runner.OnTestPassed = info => Add(info.TypeName, info.TestDisplayName, "PASS", "");
        runner.OnTestFailed = info => Add(info.TypeName, info.TestDisplayName, "FAIL", $"{info.ExceptionType}: {info.ExceptionMessage.ReplaceLineEndings(" ")}");
        runner.OnTestSkipped = info => Add(info.TypeName, info.TestDisplayName, "SKIP", info.SkipReason);
        runner.OnExecutionComplete = _ => finished.Set();

        // Not parallel: a few spec tests share static counters (CreationCountFakeService) across classes.
        runner.Start(new AssemblyRunnerStartOptions { Parallel = false });
        finished.Wait();
        while (runner.Status != AssemblyRunnerStatus.Idle)
        {
            Thread.Sleep(10); // the runner reports completion slightly before it is safe to dispose
        }

        return verdicts;

        void Add(string typeName, string displayName, string outcome, string detail)
        {
            string className = typeName[(typeName.LastIndexOf('.') + 1)..];
            string subject = className[..className.IndexOf('_', StringComparison.Ordinal)];
            string suite = className[(className.IndexOf('_', StringComparison.Ordinal) + 1)..];

            // The display name is "Namespace.Class.Method(args)"; keep "Method(args)" so subjects line up.
            string test = displayName.StartsWith(typeName + ".", StringComparison.Ordinal) ? displayName[(typeName.Length + 1)..] : displayName;
            lock (verdicts)
            {
                // Theory rows whose arguments print the same would collide; number the repeats in run order.
                string key = $"{suite}.{test}";
                int seen = verdicts.Count(v => v.Subject == subject && (v.Test == key || v.Test.StartsWith(key + " #", StringComparison.Ordinal)));
                verdicts.Add(new Verdict(subject, seen == 0 ? key : $"{key} #{seen + 1}", outcome, detail));
            }
        }
    }

    private sealed record Verdict(string Subject, string Test, string Outcome, string Detail);
}
