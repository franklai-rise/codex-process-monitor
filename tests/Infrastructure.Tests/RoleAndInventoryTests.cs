using Codex.ProcessMonitor.Infrastructure;

namespace Infrastructure.Tests;

public sealed class RoleAndInventoryTests
{
    [Fact]
    public void ClassifierRecognizesCodexAndBrowserProcesses()
    {
        var classifier = new ProcessRoleClassifier();

        Assert.Equal(Codex.ProcessMonitor.Core.ProcessRole.MainApplication,
            classifier.Classify(new ProcessRoleContext(42, 1, "codex.exe", null, "codex --monitor")));
        Assert.Equal(Codex.ProcessMonitor.Core.ProcessRole.Browser,
            classifier.Classify(new ProcessRoleContext(43, 1, "msedge.exe", null, null)));
        Assert.Equal(Codex.ProcessMonitor.Core.ProcessRole.Integration,
            classifier.Classify(new ProcessRoleContext(44, 42, "node_repl.exe", null, null)));
        Assert.Equal(Codex.ProcessMonitor.Core.ProcessRole.Unknown,
            classifier.Classify(new ProcessRoleContext(45, 42, "unrelated.exe", null, null)));
    }

    [Fact]
    public void InventoryReadsOnlySmallMetadataFixtures()
    {
        using var fixture = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(fixture.Path, "plugin.json"), "{\"id\":\"fixture\",\"name\":\"Fixture plugin\",\"version\":\"1.2.3\"}");
        File.WriteAllText(Path.Combine(fixture.Path, "config.toml"), "[monitor]\ninterval_ms = 500\n");
        File.WriteAllText(Path.Combine(fixture.Path, "SKILL.md"), "---\nname: Fixture skill\ndescription: Read-only fixture\n---\n# ignored body");

        var inventory = new MetadataInventoryReader().Read(new[] { fixture.Path });

        var plugin = Assert.Single(inventory.Plugins);
        Assert.Equal("fixture", plugin.Id);
        Assert.Equal("500", inventory.Configs.Single().Values["monitor.interval_ms"]);
        Assert.Equal("Fixture skill", Assert.Single(inventory.Skills).Name);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codex-infra-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, true);
        }
    }
}
