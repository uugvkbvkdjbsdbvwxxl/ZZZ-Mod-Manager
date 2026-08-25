using ZZZModManager.Infrastructure;
using ZZZModManager.Models;
using System.Text.RegularExpressions;

namespace ZZZModManager.Services;

public interface IDependencyResolver
{
    IReadOnlyList<string> GetMissingDependencies(ModManifest manifest, IEnumerable<ModManifest> installedMods);
    IReadOnlyDictionary<string, IReadOnlyList<string>> GetMissingDependencies(IEnumerable<ModManifest> installedMods);
}

/// <summary>
/// Resolves dependency availability from the current library state instead of the
/// import-time report. This means importing a dependency later immediately clears
/// stale "missing dependency" warnings after a refresh.
/// </summary>
public sealed class DependencyResolver : IDependencyResolver
{
    private static readonly Regex RequirementRegex = new(
        @"^\s*(?<name>.+?)\s*(?<operator>>=|==|=)\s*v?(?<version>\d+(?:\.\d+){1,3})\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VersionRegex = new(
        @"(?<!\d)(?:v(?:ersion)?\s*)?(?<version>\d+(?:\.\d+){1,3})(?!\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly AppPaths _paths;

    public DependencyResolver(AppPaths paths)
    {
        _paths = paths;
    }

    public IReadOnlyList<string> GetMissingDependencies(
        ModManifest manifest,
        IEnumerable<ModManifest> installedMods)
    {
        var mods = installedMods.ToList();
        return manifest.Dependencies
            .Where(dependency => !string.IsNullOrWhiteSpace(dependency))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(dependency => GetProblem(ParseRequirement(dependency), mods))
            .Where(problem => problem is not null)
            .Select(problem => problem!)
            .ToList();
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetMissingDependencies(
        IEnumerable<ModManifest> installedMods)
    {
        var mods = installedMods.ToList();
        return mods.ToDictionary(
            mod => mod.Id,
            mod => GetMissingDependencies(mod, mods),
            StringComparer.OrdinalIgnoreCase);
    }

    private string? GetProblem(DependencyRequirement requirement, IReadOnlyList<ModManifest> installedMods)
    {
        var providers = installedMods
            .Where(mod => ProvidesDependency(mod, requirement.Name))
            .Select(mod => new DependencyProvider(
                DetectVersion(mod.Id, mod.DisplayName, mod.InstalledDirectory, GetManifestPath(mod))))
            .Concat(EnumerateDependencyDirectories()
                .Where(directory => ProvidesDependency(directory, requirement.Name))
                .Select(directory => new DependencyProvider(DetectVersion(Path.GetFileName(directory), directory))))
            .ToList();
        if (providers.Count == 0)
        {
            return requirement.DisplayName;
        }

        if (requirement.MinimumVersion is null)
        {
            return null;
        }

        if (providers.Any(provider => provider.Version is not null
                                      && provider.Version >= requirement.MinimumVersion))
        {
            return null;
        }

        var detected = providers
            .Where(provider => provider.Version is not null)
            .Select(provider => provider.Version!)
            .OrderDescending()
            .FirstOrDefault();
        return detected is null
            ? $"{requirement.Name} >= {requirement.MinimumVersion}（已安装版本未知）"
            : $"{requirement.Name} >= {requirement.MinimumVersion}（已安装 {detected}）";
    }

    private string GetManifestPath(ModManifest manifest)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(_paths.ModsRoot, manifest.InstalledDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }

    private IEnumerable<string> EnumerateDependencyDirectories()
    {
        foreach (var root in new[] { _paths.DependenciesRoot })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var directory in directories)
            {
                if (!IsDisabledPath(directory))
                {
                    yield return directory;
                }
            }
        }
    }

    private static bool ProvidesDependency(ModManifest manifest, string dependency)
    {
        // Availability is about installation, not activation. A dependency that is
        // merely switched off is still on disk, and imports land disabled by design,
        // so gating on Enabled reported every fresh dependency as missing.
        return ContainsDependencyName(manifest.Id, dependency)
            || ContainsDependencyName(manifest.DisplayName, dependency)
            || ContainsDependencyName(manifest.InstalledDirectory, dependency);
    }

    private static bool ProvidesDependency(string directory, string dependency)
    {
        return ContainsDependencyName(Path.GetFileName(directory), dependency);
    }

    private static bool ContainsDependencyName(string value, string dependency)
    {
        var normalizedValue = Normalize(value);
        var normalizedDependency = Normalize(dependency);
        return normalizedDependency.Length > 0
            && normalizedValue.Contains(normalizedDependency, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value)
    {
        return new string(value
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }

    private static bool IsDisabledPath(string path)
    {
        return path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.StartsWith("DISABLED_", StringComparison.OrdinalIgnoreCase)
                || part.Equals("DISABLED", StringComparison.OrdinalIgnoreCase));
    }

    private static DependencyRequirement ParseRequirement(string value)
    {
        var match = RequirementRegex.Match(value);
        if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var version))
        {
            var name = value.Trim();
            return new DependencyRequirement(name, null, name);
        }

        var dependencyName = match.Groups["name"].Value.Trim();
        return new DependencyRequirement(
            dependencyName,
            version,
            $"{dependencyName} >= {version}");
    }

    private static Version? DetectVersion(params string[] sources)
    {
        foreach (var source in sources.Where(source => !string.IsNullOrWhiteSpace(source)))
        {
            var direct = VersionRegex.Match(source);
            if (direct.Success && Version.TryParse(direct.Groups["version"].Value, out var version))
            {
                return version;
            }

            if (!Directory.Exists(source))
            {
                continue;
            }

            try
            {
                foreach (var ini in Directory.EnumerateFiles(source, "RabbitFX.ini", SearchOption.AllDirectories))
                {
                    foreach (var line in File.ReadLines(ini).Take(20))
                    {
                        if (!line.Contains("RabbitFX", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var header = VersionRegex.Match(line);
                        if (header.Success && Version.TryParse(header.Groups["version"].Value, out version))
                        {
                            return version;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A provider with unreadable metadata is present, but its version is unknown.
            }
        }

        return null;
    }

    private sealed record DependencyRequirement(string Name, Version? MinimumVersion, string DisplayName);
    private sealed record DependencyProvider(Version? Version);
}
