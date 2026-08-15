using System.Text.RegularExpressions;

namespace Codex.ProcessMonitor.Infrastructure;

/// <summary>
/// Classifies processes using only the executable name/path and (when available) command line.
/// Classification is deliberately conservative; unknown is preferable to a misleading role.
/// </summary>
public interface IProcessRoleClassifier
{
    ProcessRole Classify(ProcessRoleContext context);
}

public sealed class ProcessRoleClassifier : IProcessRoleClassifier
{
    // Do not include '.' as a token separator: a process merely living below
    // `\.codex\` is not thereby a Codex executable.
    private static readonly Regex Codex = new("(?:^|[\\\\/ _-])codex(?:\\.exe)?(?:$|[\\\\/ _-])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Browser = new("(?:chrome|msedge|firefox|brave|opera|vivaldi|browser_broker)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Developer = new("(?:devenv|code(?:\\.exe)?|rider|clion|idea(?:64)?|dotnet|msbuild|csc|vbc|node(?:js)?|npm|npx|cargo|rustc|python|pythonw|java|javac|powershell|pwsh|windows_terminal|wt(?:\\.exe)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Runtime = new("(?:dotnet|node(?:js)?|python(?:w)?|java(?:w)?|javaw|ruby|perl|php|wsl)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Service = new("(?:svchost|services|lsass|wininit|spoolsv|SearchIndexer|WaaSMedic|TrustedInstaller|MsMpEng|SecurityHealthService)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Shell = new("(?:explorer|sihost|startmenuexperiencehost|searchhost|applicationframehost|dwm|ctfmon|textinputhost)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Host = new("(?:conhost|dllhost|rundll32|taskhost|werfault|runtimebroker|backgroundtaskhost)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ProcessRole Classify(ProcessRoleContext context)
    {
        var image = Path.GetFileNameWithoutExtension(context.ImageName ?? string.Empty);
        var path = context.ImagePath ?? string.Empty;
        var commandLine = context.CommandLine ?? string.Empty;
        var all = string.Join(" ", image, path, commandLine);

        if (context.ProcessId is 0 or 4 || image.Equals("System Idle Process", StringComparison.OrdinalIgnoreCase) || image.Equals("System", StringComparison.OrdinalIgnoreCase))
            return ProcessRole.Service;

        // Chromium children must be classified before the ChatGPT desktop root.
        // Their command line is the strongest available role evidence.
        if (image.Equals("chatgpt", StringComparison.OrdinalIgnoreCase) && commandLine.Contains("--type=renderer", StringComparison.OrdinalIgnoreCase))
            return ProcessRole.Renderer;
        if (image.Equals("chatgpt", StringComparison.OrdinalIgnoreCase) && commandLine.Contains("--type=", StringComparison.OrdinalIgnoreCase))
            return ProcessRole.Worker;

        // These are Codex-owned runtime hosts, not another desktop root.
        if (image.Equals("node_repl", StringComparison.OrdinalIgnoreCase) ||
            image.Equals("node-repl", StringComparison.OrdinalIgnoreCase) ||
            image.Equals("codex-code-mode-host", StringComparison.OrdinalIgnoreCase))
            return ProcessRole.Integration;

        if (Codex.IsMatch(image) ||
            (image.Equals("chatgpt", StringComparison.OrdinalIgnoreCase) &&
             (path.Contains("OpenAI.Codex", StringComparison.OrdinalIgnoreCase) ||
              commandLine.Contains("codex", StringComparison.OrdinalIgnoreCase))))
            return ProcessRole.MainApplication;
        if (Browser.IsMatch(image))
            return ProcessRole.Browser;
        if (Shell.IsMatch(image))
            return ProcessRole.Renderer;
        if (Service.IsMatch(image))
            return ProcessRole.Service;
        if (Host.IsMatch(image))
            return ProcessRole.ExtensionHost;
        if (Developer.IsMatch(image))
        {
            if (Runtime.IsMatch(image))
                return image.Equals("node", StringComparison.OrdinalIgnoreCase) || image.Equals("nodejs", StringComparison.OrdinalIgnoreCase)
                    ? ProcessRole.ExtensionHost
                    : ProcessRole.Worker;
            if (image.Equals("powershell", StringComparison.OrdinalIgnoreCase) || image.Equals("pwsh", StringComparison.OrdinalIgnoreCase) || image.Equals("wt", StringComparison.OrdinalIgnoreCase))
                return ProcessRole.Terminal;
            return ProcessRole.Editor;
        }

        // Processes under a service host are generally workers, but do not
        // guess that every unknown process is a Codex application.
        if (context.ParentProcessId is 0 or 4)
            return ProcessRole.Worker;
        return ProcessRole.Unknown;
    }

    public static ProcessRole Classify(
        int processId,
        int parentProcessId,
        string imageName,
        string? imagePath = null,
        string? commandLine = null) =>
        new ProcessRoleClassifier().Classify(new ProcessRoleContext(
            processId, parentProcessId, imageName, imagePath, commandLine));
}
