using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

// BenchmarkDotNet switches Windows to the High performance power plan by default, which changes display brightness
// and other user settings; keep the user's plan for every job, including those picked with --job.
var config = DefaultConfig.Instance.AddJob(Job.Default.DontEnforcePowerPlan().AsMutator());

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
