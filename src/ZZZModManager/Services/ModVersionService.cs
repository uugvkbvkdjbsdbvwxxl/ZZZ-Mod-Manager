using ZZZModManager.Infrastructure;
using ZZZModManager.Models;

namespace ZZZModManager.Services;

public sealed class ModVersionService
{
    private const string DescriptorFileName = "version.json";
    private const string ContentDirectoryName = "content";
    private static readonly EnumerationOptions RecursiveFiles = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false
    };

    private readonly AppPaths _paths;
    private readonly JsonFileStore _store;

    public ModVersionService(AppPaths paths, JsonFileStore store)
    {
        _paths = paths;
        _store = store;
        _paths.Ensure();
    }

    public ModUpdatePreview Compare(string installedDirectory, string candidateDirectory)
    {
        var current = Enumerate(installedDirectory);
        var candidate = Enumerate(candidateDirectory);
        var paths = current.Keys
            .Concat(candidate.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        var files = new List<ModFileDifference>();
        foreach (var relativePath in paths)
        {
            var hasCurrent = current.TryGetValue(relativePath, out var previous);
            var hasCandidate = candidate.TryGetValue(relativePath, out var next);
            if (!hasCurrent)
            {
                files.Add(new ModFileDifference(relativePath, ModFileDifferenceKind.Added, 0, next!.Length));
                continue;
            }

            if (!hasCandidate)
            {
                files.Add(new ModFileDifference(relativePath, ModFileDifferenceKind.Removed, previous!.Length, 0));
                continue;
            }

            var same = previous!.Length == next!.Length && FilesEqual(previous.FullName, next.FullName);
            files.Add(new ModFileDifference(
                relativePath,
                same ? ModFileDifferenceKind.Unchanged : ModFileDifferenceKind.Modified,
                previous.Length,
                next.Length));
        }

        return new ModUpdatePreview { Files = files };
    }

    public ModVersionBackup CreateBackup(ModManifest manifest, string installedDirectory, string reason)
    {
        if (!FileSystemSafety.IsWithin(_paths.ModsRoot, installedDirectory))
        {
            throw new InvalidOperationException("拒绝备份 Mod 库之外的目录。");
        }

        var modRoot = GetModBackupRoot(manifest.Id);
        Directory.CreateDirectory(modRoot);
        var backupId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var temporary = Path.Combine(modRoot, ".creating-" + backupId);
        var final = Path.Combine(modRoot, backupId);
        try
        {
            var content = Path.Combine(temporary, ContentDirectoryName);
            FileSystemSafety.CopyDirectory(installedDirectory, content);
            var stats = GetStats(content);
            var backup = new ModVersionBackup
            {
                BackupId = backupId,
                ModId = manifest.Id,
                DisplayName = manifest.DisplayName,
                CreatedAt = DateTimeOffset.UtcNow,
                Reason = reason,
                FileCount = stats.FileCount,
                TotalBytes = stats.TotalBytes,
                Snapshot = Snapshot(manifest)
            };
            var descriptor = Path.Combine(temporary, DescriptorFileName);
            _store.Save(descriptor, backup);
            if (!File.Exists(descriptor))
            {
                throw new IOException("版本备份清单写入失败。");
            }

            Directory.Move(temporary, final);
            return backup;
        }
        catch
        {
            SafeDelete(temporary);
            throw;
        }
    }

    public IReadOnlyList<ModVersionBackup> ListBackups(string modId)
    {
        var root = GetModBackupRoot(modId);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var backups = new List<ModVersionBackup>();
        foreach (var directory in Directory.EnumerateDirectories(root)
                     .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal)))
        {
            var descriptor = Path.Combine(directory, DescriptorFileName);
            var backup = _store.Load<ModVersionBackup?>(descriptor, () => null);
            if (backup is null
                || !string.Equals(backup.ModId, modId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(backup.BackupId, Path.GetFileName(directory), StringComparison.Ordinal)
                || !Directory.Exists(Path.Combine(directory, ContentDirectoryName)))
            {
                continue;
            }

            backups.Add(backup);
        }

        return backups.OrderByDescending(backup => backup.CreatedAt).ToList();
    }

    public (ModVersionBackup Backup, string ContentDirectory) ResolveBackup(string modId, string backupId)
    {
        var backup = ListBackups(modId).FirstOrDefault(candidate =>
            string.Equals(candidate.BackupId, backupId, StringComparison.Ordinal));
        if (backup is null)
        {
            throw new InvalidOperationException("找不到选定的 Mod 版本备份。");
        }

        var directory = Path.Combine(GetModBackupRoot(modId), backup.BackupId, ContentDirectoryName);
        if (!FileSystemSafety.IsWithin(_paths.ModBackupsRoot, directory) || !Directory.Exists(directory))
        {
            throw new InvalidOperationException("版本备份路径无效。");
        }

        return (backup, directory);
    }

    private static Dictionary<string, FileInfo> Enumerate(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"找不到用于版本比较的目录：{directory}");
        }

        return Directory.EnumerateFiles(directory, "*", RecursiveFiles)
            .Select(path => new FileInfo(path))
            .Select(file => new
            {
                RelativePath = Path.GetRelativePath(directory, file.FullName).Replace(Path.DirectorySeparatorChar, '/'),
                File = file
            })
            .Where(item => !string.Equals(item.RelativePath, "import-report.json", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(item => item.RelativePath, item => item.File, StringComparer.OrdinalIgnoreCase);
    }

    private static bool FilesEqual(string leftPath, string rightPath)
    {
        using var left = new FileStream(leftPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        using var right = new FileStream(rightPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        Span<byte> leftBuffer = stackalloc byte[16 * 1024];
        Span<byte> rightBuffer = stackalloc byte[16 * 1024];
        while (true)
        {
            var leftRead = left.Read(leftBuffer);
            var rightRead = right.Read(rightBuffer);
            if (leftRead != rightRead)
            {
                return false;
            }

            if (leftRead == 0)
            {
                return true;
            }

            if (!leftBuffer[..leftRead].SequenceEqual(rightBuffer[..rightRead]))
            {
                return false;
            }
        }
    }

    private string GetModBackupRoot(string modId)
    {
        var safeId = FileSystemSafety.SanitizeDirectoryName(modId);
        var root = Path.GetFullPath(Path.Combine(_paths.ModBackupsRoot, safeId));
        if (!FileSystemSafety.IsWithin(_paths.ModBackupsRoot, root))
        {
            throw new InvalidOperationException("Mod 版本备份路径无效。");
        }

        return root;
    }

    private static (int FileCount, long TotalBytes) GetStats(string directory)
    {
        var count = 0;
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", RecursiveFiles))
        {
            count = checked(count + 1);
            bytes = checked(bytes + new FileInfo(file).Length);
        }

        return (count, bytes);
    }

    private static ModVersionSnapshot Snapshot(ModManifest manifest) => new()
    {
        SourcePath = manifest.SourcePath,
        SourceSha256 = manifest.SourceSha256,
        ImportStatus = manifest.ImportStatus,
        Hashes = new HashSet<string>(manifest.Hashes, StringComparer.OrdinalIgnoreCase),
        Dependencies = [.. manifest.Dependencies],
        AppliedFixes = [.. manifest.AppliedFixes],
        ReportFile = manifest.ReportFile,
        PreviewFile = manifest.PreviewFile,
        UpdatedAt = manifest.UpdatedAt,
        VersionRevision = manifest.VersionRevision
    };

    private void SafeDelete(string path)
    {
        if (FileSystemSafety.IsWithin(_paths.ModBackupsRoot, path) && Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }
}
