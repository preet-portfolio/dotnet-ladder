using BenchmarkDotNet.Running;
using Ladder.Resp.Bench;

BenchmarkSwitcher.FromAssembly(typeof(PipelineBenchmarks).Assembly).Run(args);
