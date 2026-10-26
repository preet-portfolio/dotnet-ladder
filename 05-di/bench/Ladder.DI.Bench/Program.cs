using BenchmarkDotNet.Running;
using Ladder.DI.Bench;

BenchmarkSwitcher.FromAssembly(typeof(ResolutionBenchmarks).Assembly).Run(args);
