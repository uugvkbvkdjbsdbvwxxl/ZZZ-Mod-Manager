using SharpCompress.Archives;
using SharpCompress.Common;
using ZZZModManager.Infrastructure;
using ZZZModManager.Models;

namespace ZZZModManager.Services;

public interface IModImporter
{
    Task<ImportSession> StageAsync(string sourcePath, CancellationToken cancellationToken = default);
    void Cleanup(ImportSession session);
}

public sealed class ModImporter : IModImporter
{
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar"
    };

    private static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dds", ".jpg", ".jpeg", ".png", ".buf", ".ib", ".hlsl", ".bin", ".txt"
    };

    private readonly AppPaths _paths;

    public ModImporter(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<ImportSession> StageAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("源路径不能为空。", nameof(sourcePath));
        }

        sourcePath = Path.GetFullPath(sourcePath);
        if (!Directory.Exists(sourcePath) && !File.Exists(sourcePath))
        {
            throw new FileNotFoundException("找不到导入源。", sourcePath);
        }

        var staging = _paths.CreateStagingDirectory();
        try
        {
            if (Directory.Exists(sourcePath))
            {
                await Task.Run(
                    () => FileSystemSafety.CopyDirectory(sourcePath, staging, cancellationToken: cancellationToken),
                    cancellationToken);
            }
            else
            {
                await ExtractArchiveAsync(sourcePath, staging, cancellationToken);
            }

            var sourceHash = await ComputeSourceHashAsync(sourcePath, cancellationToken);
            var candidates = DiscoverCandidates(staging, sourcePath, sourceHash);
            if (candidates.Count == 0)
            {
                throw new InvalidDataException("没有找到包含 INI 和模型/纹理资源的 Mod 目录。请确认下载的是 3DMigoto/ZZMI Mod 文件。");
            }

            return new ImportSession
            {
                StagingPath = staging,
                SourcePath = sourcePath,
                SourceSha256 = sourceHash,
                Candidates = candidates
            };
        }
        catch
        {
            SafeDelete(staging);
            throw;
        }
    }

    public void Cleanup(ImportSession session)
    {
        SafeDelete(session.StagingPath);
    }

    private static async Task ExtractArchiveAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(source);
        if (!ArchiveExtensions.Contains(extension))
        {
            throw new InvalidDataException($"不支持的导入类型：{extension}。请选择 ZIP、7Z、RAR 或文件夹。");
        }

        await Task.Run(async () =>
        {
            using var archive = ArchiveFactory.OpenArchive(source, new SharpCompress.Readers.ReaderOptions());
            long totalDeclaredBytes = 0;
            long totalWrittenBytes = 0;
            var fileCount = 0;
            var extractedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsDirectory)
                {
                    continue;
                }

                if (++fileCount > FileSystemSafety.MaxExtractedFiles)
                {
                    throw new InvalidDataException("压缩包文件数量超过安全上限。");
                }

                if (entry.Size < 0)
                {
                    throw new InvalidDataException($"无法验证压缩包条目的解压大小：{entry.Key}");
                }

                if (!string.IsNullOrWhiteSpace(entry.LinkTarget))
                {
                    throw new InvalidDataException($"压缩包包含不支持的链接条目：{entry.Key}");
                }

                var key = entry.Key?.Replace('/', Path.DirectorySeparatorChar)
                    ?? throw new InvalidDataException("压缩包存在空文件名。");
                var target = Path.GetFullPath(Path.Combine(destination, key));
                if (!FileSystemSafety.IsWithin(destination, target))
                {
                    throw new InvalidDataException($"压缩包包含不安全路径：{entry.Key}");
                }

                if (!extractedTargets.Add(target))
                {
                    throw new InvalidDataException($"压缩包包含重复目标路径：{entry.Key}");
                }

                totalDeclaredBytes = checked(totalDeclaredBytes + entry.Size);
                if (totalDeclaredBytes > FileSystemSafety.MaxExtractedBytes)
                {
                    throw new InvalidDataException("压缩包解压后大小超过安全上限。");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = entry.OpenEntryStream();
                await using var output = new FileStream(
                    target,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[128 * 1024];
                long entryWritten = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    entryWritten = checked(entryWritten + read);
                    totalWrittenBytes = checked(totalWrittenBytes + read);
                    if (entryWritten > entry.Size || totalWrittenBytes > FileSystemSafety.MaxExtractedBytes)
                    {
                        throw new InvalidDataException($"压缩包条目实际大小超过声明值：{entry.Key}");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                if (entryWritten != entry.Size)
                {
                    throw new InvalidDataException($"压缩包条目实际大小与声明值不一致：{entry.Key}");
                }
            }
        }, cancellationToken);
    }

    private static async Task<string> ComputeSourceHashAsync(string source, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Directory.Exists(source)
                ? FileSystemSafety.ComputeDirectoryFingerprint(source)
                : FileSystemSafety.ComputeFileSha256(source);
        }, cancellationToken);
    }

    private static List<ImportCandidate> DiscoverCandidates(string staging, string sourcePath, string sourceHash)
    {
        var directories = new[] { staging }
            .Concat(Directory.EnumerateDirectories(staging, "*", SearchOption.AllDirectories))
            .OrderBy(path => path.Count(ch => ch == Path.DirectorySeparatorChar))
            .ToList();

        var candidates = new List<string>();
        foreach (var directory in directories)
        {
            var hasIni = Directory.EnumerateFiles(directory, "*.ini", SearchOption.TopDirectoryOnly).Any();
            var hasAsset = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Any(file => AssetExtensions.Contains(Path.GetExtension(file)));
            if (!hasIni || !hasAsset || !IsLikelyModRoot(directory))
            {
                continue;
            }

            // Prefer the shallowest directory that has its own Mod INI. A package
            // such as Hatsune Seedku has Seed.ini at the root and optional
            // Misc/SoundWave subdirectories; selecting the nested folders would
            // drop the root INI and make the installed Mod appear to do nothing.
            // Independent Mods in one wrapper still remain separate because the
            // wrapper itself has no usable top-level INI.
            if (candidates.Any(existing => FileSystemSafety.IsWithin(existing, directory)))
            {
                continue;
            }

            candidates.Add(directory);
        }

        var sourceName = Directory.Exists(sourcePath)
            ? new DirectoryInfo(sourcePath).Name
            : Path.GetFileNameWithoutExtension(sourcePath);
        var usableSourceName = !string.IsNullOrWhiteSpace(sourceName)
            && !new[] { "mods", "mod", "package", "download", "archive" }
                .Contains(sourceName, StringComparer.OrdinalIgnoreCase);

        return candidates.Select(directory =>
        {
            var relative = Path.GetRelativePath(staging, directory);
            var leafName = new DirectoryInfo(directory).Name;
            var name = FindDisplayName(directory)
                ?? (usableSourceName ? sourceName : leafName);
            if (candidates.Count > 1 && usableSourceName && !string.Equals(name, leafName, StringComparison.OrdinalIgnoreCase))
            {
                name = $"{name} - {leafName}";
            }

            return new ImportCandidate
            {
                StagedPath = directory,
                RelativeRoot = relative == "." ? string.Empty : relative,
                DisplayName = name,
                SourcePath = sourcePath,
                SourceSha256 = sourceHash
            };
        }).ToList();
    }

    private static bool IsLikelyModRoot(string directory)
    {
        var directAssets = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Any(file => AssetExtensions.Contains(Path.GetExtension(file)));
        if (directAssets)
        {
            return true;
        }

        // A root can keep its assets in a subdirectory, but a wrapper README
        // should not make the whole archive look like one Mod. Only accept a
        // top-level INI when it contains a 3DMigoto/ZZMI-style declaration.
        foreach (var ini in Directory.EnumerateFiles(directory, "*.ini", SearchOption.TopDirectoryOnly))
        {
            try
            {
                foreach (var line in File.ReadLines(ini).Take(400))
                {
                    var value = line.TrimStart();
                    if (value.StartsWith(";", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (value.Contains("TextureOverride", StringComparison.OrdinalIgnoreCase)
                        || value.Contains("Resource", StringComparison.OrdinalIgnoreCase)
                        || value.Contains("filename", StringComparison.OrdinalIgnoreCase)
                        || value.Contains("hash", StringComparison.OrdinalIgnoreCase)
                        || value.Contains("include", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                // The validator will report an unreadable INI later; do not
                // let one malformed wrapper file hide other candidates.
                _ = ex;
            }
        }

        return false;
    }

    private static string? FindDisplayName(string directory)
    {
        foreach (var nameFile in Directory.EnumerateFiles(directory, "modname", SearchOption.TopDirectoryOnly)
                     .Concat(Directory.EnumerateFiles(directory, "modname.txt", SearchOption.TopDirectoryOnly)))
        {
            var value = ReadText(nameFile).Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (DecoderFallbackException)
        {
            return File.ReadAllText(path, System.Text.Encoding.Default);
        }
    }

    private void SafeDelete(string path)
    {
        try
        {
            if (FileSystemSafety.IsWithin(_paths.StagingRoot, path) && Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // Staging cleanup is best effort; diagnostics can still point to the path.
        }
    }
}
