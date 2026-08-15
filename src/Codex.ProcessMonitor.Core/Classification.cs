namespace Codex.ProcessMonitor.Core;

/// <summary>Deterministic, platform-neutral process role classification.</summary>
public static class ProcessClassifier
{
    public static ProcessClassificationResult Classify(
        ProcessIdentity identity,
        IEnumerable<IntegrationDescriptor>? integrations = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var descriptors = integrations ?? Array.Empty<IntegrationDescriptor>();
        var name = Normalize(identity.Name);
        var executable = Normalize(Path.GetFileName(identity.ExecutablePath ?? identity.Name));

        foreach (var descriptor in descriptors)
        {
            if (!descriptor.IsInstalled || !descriptor.IsEnabled || !Matches(identity, name, executable, descriptor))
            {
                continue;
            }

            var (role, category) = Map(descriptor.Role);
            return new ProcessClassificationResult(
                role,
                category,
                descriptor.Id,
                confidence: 1,
                isRelevant: true,
                reason: $"Matched integration '{descriptor.Id}'.");
        }

        if (IsBrowser(name) || IsBrowser(executable))
        {
            return new ProcessClassificationResult(
                ProcessRole.Browser,
                ProcessCategory.Browser,
                confidence: 0.8,
                reason: "Recognized browser process name.");
        }

        if (IsEditor(name) || IsEditor(executable))
        {
            return new ProcessClassificationResult(
                ProcessRole.Editor,
                ProcessCategory.DevelopmentTool,
                confidence: 0.7,
                reason: "Recognized editor process name.");
        }

        if (IsTerminal(name) || IsTerminal(executable))
        {
            return new ProcessClassificationResult(
                ProcessRole.Terminal,
                ProcessCategory.Shell,
                confidence: 0.7,
                reason: "Recognized terminal process name.");
        }

        return new ProcessClassificationResult(
            ProcessRole.Unknown,
            ProcessCategory.Unknown,
            confidence: 0,
            isRelevant: false,
            reason: "No integration or built-in role matched.");
    }

    private static bool Matches(
        ProcessIdentity identity,
        string normalizedName,
        string executable,
        IntegrationDescriptor descriptor)
    {
        var values = new[]
        {
            descriptor.Id,
            descriptor.DisplayName,
            descriptor.ExecutableName,
            descriptor.Path is null ? null : Path.GetFileName(descriptor.Path),
        };

        return values.Any(value => !string.IsNullOrWhiteSpace(value) &&
            (normalizedName.Equals(Normalize(value), StringComparison.OrdinalIgnoreCase) ||
             executable.Equals(Normalize(value), StringComparison.OrdinalIgnoreCase) ||
             identity.Name.Contains(value, StringComparison.OrdinalIgnoreCase)));
    }

    private static (ProcessRole Role, ProcessCategory Category) Map(IntegrationRole role)
    {
        return role switch
        {
            IntegrationRole.HostApplication => (ProcessRole.MainApplication, ProcessCategory.Application),
            IntegrationRole.Browser => (ProcessRole.Browser, ProcessCategory.Browser),
            IntegrationRole.Editor => (ProcessRole.Editor, ProcessCategory.DevelopmentTool),
            IntegrationRole.Terminal => (ProcessRole.Terminal, ProcessCategory.Shell),
            IntegrationRole.SourceControl => (ProcessRole.SourceControl, ProcessCategory.DevelopmentTool),
            IntegrationRole.Tooling => (ProcessRole.Integration, ProcessCategory.Integration),
            IntegrationRole.Extension or IntegrationRole.Plugin =>
                (ProcessRole.ExtensionHost, ProcessCategory.Integration),
            IntegrationRole.Runtime => (ProcessRole.Worker, ProcessCategory.BackgroundService),
            IntegrationRole.Service => (ProcessRole.Service, ProcessCategory.BackgroundService),
            _ => (ProcessRole.Integration, ProcessCategory.Integration),
        };
    }

    private static string Normalize(string value)
    {
        var fileName = Path.GetFileNameWithoutExtension(value.Trim());
        return fileName.Replace(".exe", string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBrowser(string value) =>
        value.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("firefox", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("brave", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("opera", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("safari", StringComparison.OrdinalIgnoreCase);

    private static bool IsEditor(string value) =>
        value.Contains("code", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("devenv", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("rider", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("idea", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("vim", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("emacs", StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminal(string value) =>
        value.Contains("terminal", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("powershell", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("bash", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("zsh", StringComparison.OrdinalIgnoreCase);
}

public interface IProcessClassifier
{
    ProcessClassificationResult Classify(
        ProcessIdentity identity,
        IEnumerable<IntegrationDescriptor>? integrations = null);
}

public sealed class DefaultProcessClassifier : IProcessClassifier
{
    public ProcessClassificationResult Classify(
        ProcessIdentity identity,
        IEnumerable<IntegrationDescriptor>? integrations = null) =>
        ProcessClassifier.Classify(identity, integrations);
}
