using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CsCheck;
using Ladder.DI.Tests.Graph;
using Microsoft.Extensions.DependencyInjection;

namespace Ladder.DI.Tests;

/// <summary>
/// Claim 2: on random service graphs, ours and Microsoft's container (10.0.13, the reference implementation) agree
/// on everything a caller can observe: which resolutions share an instance, the order instances are created and
/// disposed in, and the type and kind of every exception, including at build time with ValidateOnBuild and
/// ValidateScopes. Our resolution engine is drawn at random per graph.
/// </summary>
public partial class DifferentialTests(ITestOutputHelper output)
{
    // 10,000 is the claim. LADDER_DI_GRAPHS lowers it for mutation testing, where the suite runs once per mutant.
    private static readonly int Graphs = int.TryParse(Environment.GetEnvironmentVariable("LADDER_DI_GRAPHS"), out int n) ? n : 10_000;

    private static readonly Gen<bool> s_sometimes = Gen.Int[0, 3].Select(i => i == 0);

    private static readonly Template[] s_disposables = [Template.Disposable, Template.AsyncDisposable, Template.BothDisposable, Template.OpenDisposable];

    // Disposal order is half of what is claimed, so disposable implementations are drawn more often than the rest.
    private static readonly Gen<Template> s_template = Gen.Frequency(
        (7, Gen.Enum<Template>()),
        (3, Gen.Int[0, s_disposables.Length - 1].Select(i => s_disposables[i])));

    private static readonly Gen<Registration> s_registration = Gen.Select(
        Gen.Int[0, Scenario.Services - 1], Gen.Enum<ServiceLifetime>(), s_template, Gen.Int[0, 3], Gen.Int[0, 2 * Scenario.Services], Gen.Int[0, 2 * Scenario.Services],
        (service, lifetime, template, key, a, b) => new Registration(service, lifetime, template, key, a, b));

    // Mostly resolutions; scopes are created often enough to matter and disposed now and then.
    private static readonly Gen<OpKind> s_opKind = Gen.Frequency(
        (5, Gen.Const(OpKind.Get)), (3, Gen.Const(OpKind.GetKeyed)), (3, Gen.Const(OpKind.GetAll)), (2, Gen.Const(OpKind.GetAllKeyed)),
        (2, Gen.Const(OpKind.GetRequired)), (3, Gen.Const(OpKind.CreateScope)), (1, Gen.Const(OpKind.Dispose)), (1, Gen.Const(OpKind.DisposeAsync)));

    private static readonly Gen<Op> s_op = Gen.Select(
        s_opKind, Gen.Int[0, Scenario.Services - 1], Gen.Int[0, Scenario.Keys.Length - 1], Gen.Int[0, 7],
        (kind, service, key, scope) => new Op(kind, service, key, scope));

    private static readonly Gen<Scenario> s_scenario = Gen.Select(
        Gen.Bool, s_sometimes, s_sometimes, Gen.Enum<ResolutionMode>(), s_registration.Array[0, 14], s_op.Array[1, 30], Gen.Bool,
        (acyclic, validateScopes, validateOnBuild, mode, registrations, ops, async) => new Scenario(acyclic, validateScopes, validateOnBuild, mode, registrations, ops, async));

    [Fact]
    public void Random_graphs_resolve_and_dispose_exactly_like_Microsofts_container()
    {
        var stats = new Stats();
        s_scenario.Sample(
            scenario =>
            {
                string expected = ScenarioRunner.Run(scenario, (services, s) =>
                    services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = s.ValidateScopes, ValidateOnBuild = s.ValidateOnBuild }));
                string actual = ScenarioRunner.Run(scenario, (services, s) =>
                    services.BuildLadderServiceProvider(new ServiceContainerOptions { ValidateScopes = s.ValidateScopes, ValidateOnBuild = s.ValidateOnBuild, Mode = s.Mode }));
                Assert.True(Describer.Comparable(expected) == Describer.Comparable(actual), $"Microsoft's container:\n{expected}\n\nours:\n{actual}");
                stats.Add(Describer.Comparable(expected));
            },
            iter: Graphs,
            print: scenario => scenario.ToString());

        // The generator must actually reach the interesting regions, or agreement means little.
        output.WriteLine(stats.ToString());
        output.WriteLine(stats.Examples());
        Assert.Equal(Graphs, stats.Count("graphs"));
        (string What, int AtLeast)[] floors =
        [
            ("shared", 1_000), ("disposals>=3", 1_000), (":cycle", 100), (":ambiguous", 10), (":captive", 50), (":scoped-from-root", 50),
            ("build !", 50), (":async-only", 10), ("!ArgumentException", 10), ("!ObjectDisposedException", 100), ("!NotSupportedException", 100), (":key-type", 1),
        ];
        foreach ((string what, int atLeast) in floors)
        {
            Assert.True(stats.Count(what) >= Math.Max(1, (long)atLeast * Graphs / 10_000), $"only {stats.Count(what)} graphs with {what}\n{stats}");
        }
    }

    [GeneratedRegex(@"[(,\[] ?#\d+")]
    private static partial Regex SharedInstance();

    [GeneratedRegex(@"disposals [DA]\d+,[DA]\d+,[DA]\d+")]
    private static partial Regex ThreeDisposals();

    /// <summary>How many graphs' (agreed-upon) observations contained each kind of event.</summary>
    private sealed class Stats
    {
        private static readonly string[] Markers =
        [
            ":cycle", ":ambiguous", ":captive", ":scoped-from-root", ":unresolvable", ":no-satisfiable-constructor", ":anykey", ":missing",
            ":async-only", ":key-type", ":other", "build !", "!ArgumentException", "!ObjectDisposedException", "!NotSupportedException",
        ];

        private readonly ConcurrentDictionary<string, int> _counts = new();
        private readonly ConcurrentDictionary<string, string> _examples = new();

        public int Count(string what) => _counts.GetValueOrDefault(what);

        public void Add(string observations)
        {
            Bump("graphs");
            if (SharedInstance().IsMatch(observations))
            {
                Bump("shared"); // some resolution returned an instance already seen
            }

            if (ThreeDisposals().IsMatch(observations))
            {
                Bump("disposals>=3");
            }

            foreach (string marker in Markers.Where(m => observations.Contains(m, StringComparison.Ordinal)))
            {
                Bump(marker);
                _examples.TryAdd(marker, observations);
            }
        }

        public string Examples() => string.Join("\n\n", _examples.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"--- first graph with {e.Key}:\n{e.Value}"));

        public override string ToString() => string.Join("; ", _counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key} {c.Value}"));

        private void Bump(string what) => _counts.AddOrUpdate(what, 1, (_, n) => n + 1);
    }
}
