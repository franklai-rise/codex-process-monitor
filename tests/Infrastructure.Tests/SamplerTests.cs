using Codex.ProcessMonitor.Infrastructure;

namespace Infrastructure.Tests;

public sealed class SamplerTests
{
    [Fact]
    public void ResilientCommandLineProviderDowngradesFailures()
    {
        var provider = new ResilientCommandLineProvider(new ThrowingProvider(), new NullCommandLineProvider());
        var result = provider.TryGetCommandLine(1234);
        Assert.False(result.Succeeded);
        Assert.Null(result.CommandLine);
    }

    [Fact]
    public void WindowsSamplerReturnsAReadOnlySnapshot()
    {
        var sampler = new WindowsProcessSampler(new ProcessSamplerOptions
        {
            IncludeCommandLines = false,
            IncludeImagePaths = false,
            MaxProcesses = 256
        });
        var sample = sampler.Sample();
        Assert.NotNull(sample.ProcessTree);
        Assert.NotEmpty(sample.ProcessTree.Processes);
        Assert.True(sample.GlobalMemory.TotalPhysicalBytes >= 0);
    }

    [Fact]
    public void TopLevelWindowSamplerReturnsOnlyRequestedProcessIds()
    {
        var requestedIds = new HashSet<int> { Environment.ProcessId };
        var windows = new WindowsTopLevelWindowSampler().Sample(requestedIds);

        Assert.All(windows, window =>
        {
            Assert.Contains(window.ProcessId, requestedIds);
            Assert.True(window.Width >= 0);
            Assert.True(window.Height >= 0);
        });
    }

    private sealed class ThrowingProvider : IProcessCommandLineProvider
    {
        public CommandLineQueryResult TryGetCommandLine(int processId) => throw new InvalidOperationException("fixture failure");
    }
}
