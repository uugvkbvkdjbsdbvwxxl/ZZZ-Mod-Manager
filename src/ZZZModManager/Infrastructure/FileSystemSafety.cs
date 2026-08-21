using System.Security.Cryptography;
using System.Text;

namespace ZZZModManager.Infrastructure;

public static class FileSystemSafety
{
    public const long MaxExtractedBytes = 20L * 1024 * 1024 * 1024;
    public const int MaxExtractedFiles = 200_000;
    private static readonly EnumerationOptions SafeRecursiveEnumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false
    };

    public static bool IsWithin(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(candidate);
        return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullCandidate, fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    public static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string ComputeDirectoryFingerprint(string directory)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SafeRecursiveEnumeration)
                     .OrderBy(path => Path.GetRelativePath(directory, path), StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/');
            var nameBytes = Encoding.UTF8.GetBytes(relative + "\0");
            sha.AppendData(nameBytes);
            using var input = File.OpenRead(file);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.AppendData(buffer, 0, read);
            }
        }

        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    public static void CopyDirectory(
        string source,
        string destination,
        long maxBytes = MaxExtractedBytes,
        CancellationToken cancellationToken = default)
    {
        var sourceFull = Path.GetFullPath(source);
        var destinationFull = Path.GetFullPath(destination);
        if (string.Equals(sourceFull, destinationFull, StringComparison.OrdinalIgnoreCase)
            || IsWithin(sourceFull, destinationFull))
        {
            throw new InvalidOperationException("不能把目录复制到自身或其子目录。");
        }

        Directory.CreateDirectory(destinationFull);
        long total = 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(sourceFull, "*", SafeRecursiveEnumeration))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > MaxExtractedFiles)
            {
                throw new InvalidOperationException("文件数量超过安全上限。");
            }

            var relative = Path.GetRelativePath(sourceFull, file);
            var target = Path.GetFullPath(Path.Combine(destinationFull, relative));
            if (!IsWithin(destinationFull, target))
            {
                throw new InvalidOperationException("检测到不安全的目标路径。");
            }

            var length = new FileInfo(file).Length;
            total = checked(total + length);
            if (total > maxBytes)
            {
                throw new InvalidOperationException("目录大小超过安全上限。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.SequentialScan);
            var buffer = new byte[128 * 1024];
            long copied = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                copied = checked(copied + read);
                if (copied > length || checked(total - length + copied) > maxBytes)
                {
                    throw new InvalidOperationException("源文件在复制期间增长或目录大小超过安全上限。");
                }

                output.Write(buffer, 0, read);
            }
        }
    }

    public static string SanitizeDirectoryName(string name)
    {
        var invalid = new string(Path.GetInvalidFileNameChars());
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
        {
            builder.Append(invalid.IndexOf(ch) >= 0 ? '_' : ch);
        }

        var value = builder.ToString().Trim().Trim('.');
        if (string.IsNullOrWhiteSpace(value))
        {
            value = "mod";
        }

        return value.Length <= 80 ? value : value[..80].TrimEnd();
    }
}
