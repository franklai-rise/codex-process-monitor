using System.Text.Json;

namespace Codex.ProcessMonitor.Infrastructure;

public interface IMetadataInventoryReader
{
    DirectoryInventory Read(IEnumerable<string> roots, CancellationToken cancellationToken = default);
}

/// <summary>Read-only inventory for plugin manifests, configuration files and Skill frontmatter.</summary>
public sealed partial class MetadataInventoryReader : IMetadataInventoryReader
{
    private const int MaxMetadataFileBytes = 2 * 1024 * 1024;

    public static IReadOnlyList<string> DefaultRoots
    {
        get
        {
            var roots = new List<string>();
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                roots.Add(Path.Combine(userProfile, ".codex"));
                roots.Add(Path.Combine(userProfile, ".agents"));
            }
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
                roots.Add(Path.Combine(localAppData, "Codex"));
            return roots;
        }
    }

    public DirectoryInventory ReadDefault(CancellationToken cancellationToken = default) =>
        Read(DefaultRoots, cancellationToken);

    public DirectoryInventory Scan(IEnumerable<string> roots, CancellationToken cancellationToken = default) =>
        Read(roots, cancellationToken);

    public DirectoryInventory Read(IEnumerable<string> roots, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var plugins = new List<PluginMetadata>();
        var configs = new List<ConfigMetadata>();
        var skills = new List<SkillMetadata>();
        var warnings = new List<string>();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rootInput in roots.Where(static root => !string.IsNullOrWhiteSpace(root)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = Path.GetFullPath(rootInput);
            if (!Directory.Exists(root))
                continue;
            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                MatchCasing = MatchCasing.CaseInsensitive,
                AttributesToSkip = FileAttributes.System | FileAttributes.Temporary
            };

            // Enumerate only the three metadata file names. Walking every file
            // under .codex/.agents needlessly touches logs, caches and databases.
            foreach (var pattern in new[] { "plugin.json", "config.toml", "SKILL.md" })
            {
                try
                {
                    foreach (var path in Directory.EnumerateFiles(root, pattern, enumerationOptions))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!files.Add(path))
                            continue;
                        try
                        {
                            if (pattern.Equals("plugin.json", StringComparison.OrdinalIgnoreCase))
                            {
                                if (TryGetFileLength(path, out var length) && length <= MaxMetadataFileBytes)
                                    plugins.Add(ReadPlugin(path));
                                else
                                    warnings.Add($"Skipped oversized or inaccessible plugin manifest: {path}");
                            }
                            else if (pattern.Equals("config.toml", StringComparison.OrdinalIgnoreCase))
                            {
                                if (TryGetFileLength(path, out var length) && length <= MaxMetadataFileBytes)
                                    configs.Add(new ConfigMetadata(path, ParseToml(File.ReadAllLines(path))));
                                else
                                    warnings.Add($"Skipped oversized or inaccessible config: {path}");
                            }
                            else if (TryGetFileLength(path, out var length) && length <= MaxMetadataFileBytes)
                            {
                                skills.Add(ReadSkill(path));
                            }
                            else
                            {
                                warnings.Add($"Skipped oversized or inaccessible skill metadata: {path}");
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                        {
                            warnings.Add($"Could not parse metadata file {path}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"Could not enumerate metadata files ({pattern}) under {root}: {ex.Message}");
                }
            }
        }

        return new DirectoryInventory(
            plugins.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            configs.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            skills.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            warnings);
    }

    private static PluginMetadata ReadPlugin(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        FlattenJson(document.RootElement, string.Empty, values);
        return new PluginMetadata(
            path,
            FirstValue(values, "id", "plugin.id", "name"),
            FirstValue(values, "name", "plugin.name"),
            FirstValue(values, "version", "plugin.version"),
            FirstValue(values, "description", "plugin.description"),
            values);
    }

    private static SkillMetadata ReadSkill(string path)
    {
        var lines = File.ReadAllLines(path);
        var values = ParseFrontmatter(lines);
        return new SkillMetadata(
            path,
            FirstValue(values, "name", "title"),
            FirstValue(values, "description", "summary"),
            FirstValue(values, "version"),
            values);
    }

    private static Dictionary<string, string?> ParseFrontmatter(IReadOnlyList<string> lines)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var first = lines.Count > 0 ? lines[0].TrimStart('\uFEFF').Trim() : string.Empty;
        if (!first.Equals("---", StringComparison.Ordinal))
            return values;

        for (var index = 1; index < lines.Count; index++)
        {
            var line = lines[index].Trim();
            if (line is "---" or "...")
                break;
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            values[key] = Unquote(value);
        }
        return values;
    }

    private static Dictionary<string, string?> ParseToml(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var comment = FindTomlComment(line);
            if (comment >= 0)
                line = line[..comment].TrimEnd();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }
            var separator = line.IndexOf('=');
            if (separator <= 0)
                continue;
            var key = line[..separator].Trim();
            var value = Unquote(line[(separator + 1)..].Trim());
            values[string.IsNullOrEmpty(section) ? key : $"{section}.{key}"] = value;
        }
        return values;
    }

    private static int FindTomlComment(string line)
    {
        var single = false;
        var doubleQuote = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '\'' && !doubleQuote)
                single = !single;
            else if (character == '"' && !single && (index == 0 || line[index - 1] != '\\'))
                doubleQuote = !doubleQuote;
            else if (character == '#' && !single && !doubleQuote)
                return index;
        }
        return -1;
    }

    private static void FlattenJson(JsonElement element, string prefix, IDictionary<string, string?> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    FlattenJson(property.Value, string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}", values);
                break;
            case JsonValueKind.Array:
                values[prefix] = string.Join(",", element.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString()));
                break;
            case JsonValueKind.Null:
                values[prefix] = null;
                break;
            default:
                values[prefix] = element.ToString();
                break;
        }
    }

    private static string? FirstValue(IReadOnlyDictionary<string, string?> values, params string[] keys)
    {
        foreach (var key in keys)
            if (values.TryGetValue(key, out var value))
                return value;
        return null;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            return value[1..^1];
        return value;
    }

    private static bool TryGetFileLength(string path, out long length)
    {
        try
        {
            length = new FileInfo(path).Length;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            length = 0;
            return false;
        }
    }
}
