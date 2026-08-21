using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ZZZModManager.Infrastructure;
using ZZZModManager.Models;

namespace ZZZModManager.Services;

public sealed class RuntimeValidation
{
    public bool IsValid { get; init; }
    public string Message { get; init; } = string.Empty;
    public string RuntimePath { get; init; } = string.Empty;
    public string? DetectedVersion { get; init; }
}

public interface IRuntimeManager
{
    RuntimeValidation Validate();
    RuntimeManifest InstallFromFolder(string sourcePath);
    void Repair();
    void RepairConfiguration();
}

public sealed class RuntimeManager : IRuntimeManager
{
    private const string EmbeddedRuntimeResourceName = "ZZZModManager.Assets.zzmi-runtime-1.4.3.zip";
    private const string ManagerInputRelativePath = "Core\\ZZMI\\ZZZModManager.ini";
    private const string ManagerInputContent = """
        namespace = ZZMIv1
        ; Compatibility binding owned by ZZZ Mod Manager.
        ; F10 dismisses the first-run ZZMI guide without bypassing manager state sync.

        [KeyDismissUserGuide]
        condition = $show_user_guide == 1
        key = no_modifiers F10
        type = cycle
        $show_user_guide = 0, 1
        """;
    private static readonly string[] RequiredRuntimeFiles =
    [
        "d3d11.dll",
        "d3dcompiler_47.dll",
        "d3dx.ini",
        "3dmloader.dll",
        Path.Combine("Core", "ZZMI", "main.ini")
    ];
    private static readonly string[] HashedRuntimeFiles =
    [
        "d3d11.dll",
        "d3dcompiler_47.dll",
        "3dmloader.dll",
        Path.Combine("Core", "ZZMI", "main.ini")
    ];
    private static readonly Regex VersionRegex = new(@"(?:Version|version)\s*[:=]\s*([0-9]+\.[0-9]+\.[0-9]+)", RegexOptions.Compiled);
    private readonly AppPaths _paths;
    private readonly JsonFileStore _store;

    public RuntimeManager(AppPaths paths, JsonFileStore store)
    {
        _paths = paths;
        _store = store;
        _paths.Ensure();
    }

    public RuntimeValidation Validate()
    {
        var required = RequiredRuntimeFiles.Select(relative => Path.Combine(_paths.RuntimeRoot, relative)).ToArray();
        var missing = required.Where(path => !File.Exists(path)).ToList();
        var integrity = new List<string>();
        var installedManifest = _store.Load<RuntimeManifest?>(_paths.RuntimeManifestFile, () => null);
        if (installedManifest is null)
        {
            integrity.Add("runtime-manifest.json (missing or invalid)");
        }
        else
        {
            if (!IsSamePath(installedManifest.RuntimeDirectory, _paths.RuntimeRoot))
            {
                integrity.Add("runtime-manifest.json (runtime directory mismatch)");
            }

            if (installedManifest.FileSha256 is not { Count: > 0 })
            {
                integrity.Add("runtime-manifest.json (file hashes missing)");
            }
        }

        if (installedManifest?.FileSha256 is { Count: > 0 })
        {
            foreach (var expected in installedManifest.FileSha256)
            {
                string path;
                try
                {
                    path = Path.GetFullPath(Path.Combine(_paths.RuntimeRoot, expected.Key));
                    if (!FileSystemSafety.IsWithin(_paths.RuntimeRoot, path))
                    {
                        integrity.Add(expected.Key + " (unsafe path)");
                        continue;
                    }
                }
                catch (ArgumentException)
                {
                    integrity.Add(expected.Key + " (invalid path)");
                    continue;
                }

                if (!File.Exists(path))
                {
                    integrity.Add(expected.Key + "（缺失）");
                    continue;
                }

                var actual = FileSystemSafety.ComputeFileSha256(path);
                if (!actual.Equals(expected.Value, StringComparison.OrdinalIgnoreCase))
                {
                    integrity.Add(expected.Key + "（哈希不匹配）");
                }
            }

            foreach (var requiredHash in HashedRuntimeFiles)
            {
                if (!installedManifest.FileSha256.ContainsKey(requiredHash))
                {
                    integrity.Add(requiredHash + " (missing hash)");
                }
            }
        }

        var version = File.Exists(required[^1])
            ? VersionRegex.Match(File.ReadAllText(required[^1])).Groups[1].Value
            : null;
        var isValid = missing.Count == 0 && integrity.Count == 0;
        return new RuntimeValidation
        {
            IsValid = isValid,
            RuntimePath = _paths.RuntimeRoot,
            DetectedVersion = string.IsNullOrWhiteSpace(version) ? null : version,
            Message = missing.Count > 0
                ? "运行核心不完整，缺少：" + string.Join("、", missing.Select(path => Path.GetRelativePath(_paths.RuntimeRoot, path)))
                : integrity.Count > 0
                    ? "运行核心完整性校验失败：" + string.Join("、", integrity)
                    : $"ZZMI 运行核心可用{(string.IsNullOrWhiteSpace(version) ? string.Empty : $"（检测到 {version}）")}。"
        };
    }

    public RuntimeManifest InstallFromFolder(string sourcePath)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        if (!Directory.Exists(sourcePath))
        {
            throw new DirectoryNotFoundException(sourcePath);
        }

        var runtimeSource = LocateRuntimeRoot(sourcePath);
        var loaderSource = LocateLoader(sourcePath, runtimeSource);
        if (loaderSource is null)
        {
            throw new InvalidDataException("没有找到 3dmloader.dll。请选择包含 ZZMI 和 Resources\\Packages\\XXMI 的 XXMI 目录。");
        }

        var staging = Path.Combine(_paths.Root, $"runtime-install-{Guid.NewGuid():N}");
        var oldRuntime = _paths.RuntimeRoot;
        var runtimeManifestPath = _paths.RuntimeManifestFile;
        string? backup = null;
        string? runtimeManifestBackup = null;
        var newRuntimeInstalled = false;
        Directory.CreateDirectory(staging);
        try
        {
            CopyRuntimeFiles(runtimeSource, staging);
            var stagedLoader = Path.Combine(staging, "3dmloader.dll");
            if (!File.Exists(stagedLoader))
            {
                File.Copy(loaderSource, stagedLoader, false);
            }
            EnsureModsInclude(Path.Combine(staging, "d3dx.ini"));

            backup = Path.Combine(_paths.BackupsRoot, $"runtime-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
            if (Directory.Exists(oldRuntime))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                Directory.Move(oldRuntime, backup);
            }

            if (File.Exists(runtimeManifestPath))
            {
                runtimeManifestBackup = Path.Combine(_paths.Root, $"runtime-manifest-backup-{Guid.NewGuid():N}.json");
                File.Copy(runtimeManifestPath, runtimeManifestBackup, false);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(oldRuntime)!);
            Directory.Move(staging, oldRuntime);
            newRuntimeInstalled = true;

            var manifest = new RuntimeManifest
            {
                RuntimeDirectory = oldRuntime,
                ImportedAt = DateTimeOffset.UtcNow,
                FileSha256 = GetRuntimeHashes(oldRuntime)
            };
            _store.Save(runtimeManifestPath, manifest);
            SafeDeleteFile(runtimeManifestBackup);
            return manifest;
        }
        catch
        {
            SafeDelete(staging);
            if (newRuntimeInstalled && Directory.Exists(oldRuntime))
            {
                SafeDelete(oldRuntime);
            }

            if (backup is not null && !Directory.Exists(oldRuntime) && Directory.Exists(backup))
            {
                Directory.Move(backup, oldRuntime);
            }

            if (runtimeManifestBackup is not null && File.Exists(runtimeManifestBackup))
            {
                File.Copy(runtimeManifestBackup, runtimeManifestPath, true);
            }
            else if (newRuntimeInstalled)
            {
                SafeDeleteFile(runtimeManifestPath);
            }

            SafeDeleteFile(runtimeManifestBackup);
            throw;
        }
    }

    public void RepairConfiguration()
    {
        var path = Path.Combine(_paths.RuntimeRoot, "d3dx.ini");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("找不到 d3dx.ini。", path);
        }

        EnsureModsInclude(path);
    }

    public void Repair()
    {
        if (Validate().IsValid)
        {
            RepairConfiguration();
            return;
        }

        var staging = Path.Combine(_paths.StagingRoot, $"runtime-repair-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            using var stream = typeof(RuntimeManager).Assembly.GetManifestResourceStream(EmbeddedRuntimeResourceName)
                ?? throw new InvalidOperationException("程序未包含离线 ZZMI 运行核心资源。");
            ExtractEmbeddedRuntime(stream, staging);
            InstallFromFolder(staging);
        }
        finally
        {
            SafeDelete(staging);
        }
    }

    private static void ExtractEmbeddedRuntime(Stream package, string destination)
    {
        using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: false);
        long totalBytes = 0;
        var fileCount = 0;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            if (++fileCount > FileSystemSafety.MaxExtractedFiles)
            {
                throw new InvalidDataException("离线运行核心文件数量超过安全上限。");
            }

            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > FileSystemSafety.MaxExtractedBytes)
            {
                throw new InvalidDataException("离线运行核心解压大小超过安全上限。");
            }

            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!FileSystemSafety.IsWithin(destination, target))
            {
                throw new InvalidDataException($"离线运行核心包含不安全路径：{entry.FullName}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
    }

    private static string LocateRuntimeRoot(string source)
    {
        if (HasRuntimeFiles(source))
        {
            return source;
        }

        var zzmi = Path.Combine(source, "ZZMI");
        if (HasRuntimeFiles(zzmi))
        {
            return zzmi;
        }

        var candidate = Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(HasRuntimeFiles);
        return candidate ?? throw new InvalidDataException("没有找到包含 d3d11.dll 和 d3dx.ini 的 ZZMI 目录。");
    }

    private static bool HasRuntimeFiles(string path) =>
        File.Exists(Path.Combine(path, "d3d11.dll"))
        && File.Exists(Path.Combine(path, "d3dx.ini"));

    private static string? LocateLoader(string source, string runtimeSource)
    {
        var direct = Path.Combine(runtimeSource, "3dmloader.dll");
        if (File.Exists(direct))
        {
            return direct;
        }

        var candidates = new[]
        {
            Path.Combine(source, "Resources", "Packages", "XXMI", "3dmloader.dll"),
            Path.Combine(Directory.GetParent(runtimeSource)?.FullName ?? runtimeSource, "Resources", "Packages", "XXMI", "3dmloader.dll")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static void CopyRuntimeFiles(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var firstPart = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (firstPart.Equals("Mods", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, false);
        }
    }

    private static void EnsureModsInclude(string path)
    {
        var runtimeRoot = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("无法确定 ZZMI 运行核心目录。");
        var managerInputPath = Path.Combine(runtimeRoot, ManagerInputRelativePath);
        EnsureManagerInputFile(managerInputPath);

        var lines = File.ReadAllLines(path, new UTF8Encoding(false)).ToList();
        var changed = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].TrimStart().StartsWith("include_recursive", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = lines[i].Split('=', 2).Skip(1).FirstOrDefault()?.Trim();
            if (value is not null && value.Equals("Mods", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = "include_recursive = ..\\..\\Mods";
                changed = true;
            }
        }

        if (!lines.Any(line => line.TrimStart().StartsWith("include_recursive", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add(string.Empty);
            lines.Add("[Include]");
            lines.Add("include_recursive = ..\\..\\Mods");
            changed = true;
        }

        if (!lines.Any(line =>
                string.Equals(
                    line.Trim(),
                    $"include = {ManagerInputRelativePath}",
                    StringComparison.OrdinalIgnoreCase)))
        {
            var includeIndex = lines.FindIndex(line =>
                line.TrimStart().StartsWith("include_recursive", StringComparison.OrdinalIgnoreCase));
            if (includeIndex < 0)
            {
                includeIndex = lines.Count;
            }

            lines.Insert(includeIndex, $"include = {ManagerInputRelativePath}");
            changed = true;
        }

        changed |= EnsureSectionAssignments(lines, "Hunting", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["reload_fixes"] = ManagerGameBindings.ReloadIniBinding,
            ["reload_config"] = ManagerGameBindings.ReloadIniBinding
        });

        // Vertex-limit overrides must be parsed during DLL initialization. Loading
        // includes after the first frame leaves the game's original buffers too small
        // and produces partially missing meshes even after a clean game restart.
        changed |= EnsureSectionAssignments(lines, "System", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["skip_early_includes_load"] = "0",
            ["config_initialization_delay"] = "-1"
        });

        if (changed)
        {
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
        }
    }

    private static void EnsureManagerInputFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)
            && string.Equals(
                File.ReadAllText(path, new UTF8Encoding(false)).Replace("\r\n", "\n"),
                ManagerInputContent.Replace("\r\n", "\n"),
                StringComparison.Ordinal))
        {
            return;
        }

        File.WriteAllText(path, ManagerInputContent, new UTF8Encoding(false));
    }

    private static bool EnsureSectionAssignments(
        List<string> lines,
        string sectionName,
        IReadOnlyDictionary<string, string> assignments)
    {
        var sectionStart = lines.FindIndex(line =>
            string.Equals(line.Trim(), $"[{sectionName}]", StringComparison.OrdinalIgnoreCase));
        if (sectionStart < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.Add(string.Empty);
            }

            lines.Add($"[{sectionName}]");
            foreach (var assignment in assignments)
            {
                lines.Add($"{assignment.Key} = {assignment.Value}");
            }

            return true;
        }

        var sectionEnd = lines.FindIndex(sectionStart + 1, line =>
            line.TrimStart().StartsWith("[", StringComparison.Ordinal)
            && line.TrimEnd().EndsWith("]", StringComparison.Ordinal));
        if (sectionEnd < 0)
        {
            sectionEnd = lines.Count;
        }

        var changed = false;
        foreach (var assignment in assignments)
        {
            var index = -1;
            for (var candidate = sectionStart + 1; candidate < sectionEnd; candidate++)
            {
                var trimmed = lines[candidate].TrimStart();
                if (trimmed.StartsWith(';') || trimmed.StartsWith('#'))
                {
                    continue;
                }

                var separator = trimmed.IndexOf('=');
                if (separator > 0
                    && string.Equals(trimmed[..separator].Trim(), assignment.Key, StringComparison.OrdinalIgnoreCase))
                {
                    index = candidate;
                    break;
                }
            }

            var expected = $"{assignment.Key} = {assignment.Value}";
            if (index >= 0)
            {
                if (!string.Equals(lines[index].Trim(), expected, StringComparison.Ordinal))
                {
                    lines[index] = expected;
                    changed = true;
                }

                continue;
            }

            lines.Insert(sectionEnd, expected);
            sectionEnd++;
            changed = true;
        }

        return changed;
    }

    private static Dictionary<string, string> GetRuntimeHashes(string runtime)
    {
        // d3dx.ini is intentionally mutable: the manager rewrites its Mods include path.
        return HashedRuntimeFiles.Select(relative => Path.Combine(runtime, relative))
            .Where(File.Exists)
            .ToDictionary(path => Path.GetRelativePath(runtime, path), FileSystemSafety.ComputeFileSha256,
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsSamePath(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void SafeDelete(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    private static void SafeDeleteFile(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
#if false
        var managerPath = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("无法定位当前管理器程序。 ");
        var helperStart = new ProcessStartInfo
        {
            FileName = managerPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        helperStart.ArgumentList.Add("--inject-helper");
        helperStart.ArgumentList.Add("--game");
        helperStart.ArgumentList.Add(config.GameExecutablePath);
        helperStart.ArgumentList.Add("--runtime");
        helperStart.ArgumentList.Add(_paths.RuntimeRoot);
        helperStart.ArgumentList.Add("--timeout");
        helperStart.ArgumentList.Add(config.InjectionTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var helper = Process.Start(helperStart)
            ?? throw new InvalidOperationException("无法启动 ZZMI 注入助手。 ");
        var outputTask = helper.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = helper.StandardError.ReadToEndAsync(cancellationToken);
        await helper.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (helper.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? "ZZMI 注入助手失败。"
                : detail.Trim());
        }

        return string.IsNullOrWhiteSpace(output) ? "游戏已启动并完成 ZZMI 注入。" : output.Trim();
    }
}
#endif

public interface IGameSettingsManager
{
    string Configure(string gameExecutablePath, string backupRoot);
}

public sealed class GameSettingsManager : IGameSettingsManager
{
    private static readonly byte[] Magic =
    [85, 110, 209, 150, 116, 209, 131, 206, 149, 110, 103, 105, 110, 208, 181, 46, 71, 208, 176, 109, 101, 206, 159, 98, 106, 101, 209, 129, 116];

    public string Configure(string gameExecutablePath, string backupRoot)
    {
        var gameDirectory = Path.GetDirectoryName(Path.GetFullPath(gameExecutablePath))
            ?? throw new InvalidOperationException("无法确定游戏目录。");
        var path = Path.Combine(gameDirectory, "ZenlessZoneZero_Data", "Persistent", "LocalStorage", "GENERAL_DATA.bin");
        Directory.CreateDirectory(backupRoot);

        if (File.Exists(path))
        {
            var backup = Path.Combine(
                backupRoot,
                $"GENERAL_DATA-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.bin");
            File.Copy(path, backup, false);
        }

        JsonObject settings;
        if (File.Exists(path))
        {
            var content = SleepyCodec.ReadFile(path, Magic);
            settings = JsonNode.Parse(content)?.AsObject()
                ?? throw new InvalidDataException("GENERAL_DATA.bin 解码后不是 JSON 对象。");
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            settings = new JsonObject
            {
                ["$Type"] = "MoleMole.GeneralLocalDataItem",
                ["userLocalDataVersionId"] = "0.0.1"
            };
        }

        var map = settings["SystemSettingDataMap"] as JsonObject;
        if (map is null)
        {
            map = new JsonObject();
            settings["SystemSettingDataMap"] = map;
        }

        SetSetting(map, "3", 3);
        SetSetting(map, "13162", 0);
        SetSetting(map, "99", 1);
        var json = settings.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        SleepyCodec.WriteFile(path, Magic, json);
        return path;
    }

    private static void SetSetting(JsonObject map, string id, int value)
    {
        if (map[id] is JsonObject entry)
        {
            entry["Data"] = value;
            return;
        }

        map[id] = new JsonObject
        {
            ["$Type"] = "MoleMole.SystemSettingLocalData",
            ["Version"] = 0,
            ["Data"] = value
        };
    }
}

public static class SleepyCodec
{
    public static string ReadFile(string path, byte[] magic) => Decode(File.ReadAllBytes(path), magic);

    public static void WriteFile(string path, byte[] magic, string content)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp, Encode(content, magic));
        File.Move(temp, path, true);
    }

    public static string Decode(byte[] bytes, byte[] magic)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadByte() != 0
            || reader.ReadInt32() != 1
            || reader.ReadInt32() != -1
            || reader.ReadInt32() != 1
            || reader.ReadInt32() != 0
            || reader.ReadByte() != 6
            || reader.ReadInt32() != 1)
        {
            throw new InvalidDataException("GENERAL_DATA.bin 的 Sleepy 头部无效。");
        }

        var encodedLength = Read7BitEncodedInt(reader);
        if (encodedLength < 0 || encodedLength > stream.Length - stream.Position - 1)
        {
            throw new InvalidDataException("GENERAL_DATA.bin 的编码长度无效。");
        }

        var encoded = reader.ReadBytes(encodedLength);
        if (reader.ReadByte() != 11)
        {
            throw new InvalidDataException("GENERAL_DATA.bin 的 Sleepy 尾部无效。");
        }

        var evil = magic.Select(value => (value & 0xC0) == 0xC0).ToArray();
        var output = new List<byte>(encodedLength);
        var eepy = false;
        for (var i = 0; i < encoded.Length; i++)
        {
            var n = i % magic.Length;
            var ch = encoded[i] ^ magic[n];
            if (evil[n])
            {
                eepy = ch != 0;
            }
            else
            {
                if (eepy)
                {
                    ch = checked((byte)(ch + 0x40));
                    eepy = false;
                }

                output.Add((byte)ch);
            }
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    public static byte[] Encode(string content, byte[] magic)
    {
        var contentBytes = Encoding.UTF8.GetBytes(content);
        var evil = magic.Select(value => (value & 0xC0) == 0xC0).ToArray();
        var encoded = new byte[contentBytes.Length * 2 + magic.Length];
        var h = 0;
        var i = 0;
        foreach (var source in contentBytes)
        {
            var n = i % magic.Length;
            var value = source;
            var eepy = 0;
            if (evil[n])
            {
                if (value > 0x40)
                {
                    value -= 0x40;
                    eepy = 1;
                }

                encoded[h++] = (byte)(eepy ^ magic[n]);
                i++;
                n = i % magic.Length;
            }

            encoded[h++] = (byte)(value ^ magic[n]);
            i++;
        }

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)0);
            writer.Write(1);
            writer.Write(-1);
            writer.Write(1);
            writer.Write(0);
            writer.Write((byte)6);
            writer.Write(1);
            Write7BitEncodedInt(writer, h);
            writer.Write(encoded, 0, h);
            writer.Write((byte)11);
        }

        return stream.ToArray();
    }

    private static int Read7BitEncodedInt(BinaryReader reader)
    {
        var result = 0;
        var shift = 0;
        while (shift < 35)
        {
            var value = reader.ReadByte();
            result |= (value & 0x7F) << shift;
            if ((value & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }

        throw new InvalidDataException("无效的 7-bit 长度。");
    }

    private static void Write7BitEncodedInt(BinaryWriter writer, int value)
    {
        var current = (uint)value;
        while (current >= 0x80)
        {
            writer.Write((byte)(current | 0x80));
            current >>= 7;
        }

        writer.Write((byte)current);
    }
}

public interface IInjector : IDisposable
{
    void HookLibrary(string d3d11Path, string targetProcess);
    bool WaitForInjection(int timeoutSeconds);
    bool UnhookLibrary();
}

public sealed class NativeInjector : IInjector
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int HookLibraryDelegate([MarshalAs(UnmanagedType.LPWStr)] string dllPath, out IntPtr hook, out IntPtr mutex);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int InjectDelegate(uint processId, [MarshalAs(UnmanagedType.LPWStr)] string dllPath, int timeout);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int WaitForInjectionDelegate([MarshalAs(UnmanagedType.LPWStr)] string dllPath,
        [MarshalAs(UnmanagedType.LPWStr)] string targetProcess, int timeout);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int UnhookLibraryDelegate(ref IntPtr hook, ref IntPtr mutex);

    private readonly IntPtr _library;
    private readonly HookLibraryDelegate _hookLibrary;
    private readonly WaitForInjectionDelegate _waitForInjection;
    private readonly UnhookLibraryDelegate _unhookLibrary;
    private readonly InjectDelegate? _inject;
    private IntPtr _hook;
    private IntPtr _mutex;
    private string? _hookedDll;
    private string? _targetProcess;

    public NativeInjector(string loaderPath)
    {
        _library = NativeLibrary.Load(loaderPath);
        try
        {
            _hookLibrary = Get<HookLibraryDelegate>("HookLibrary");
            _waitForInjection = Get<WaitForInjectionDelegate>("WaitForInjection");
            _unhookLibrary = Get<UnhookLibraryDelegate>("UnhookLibrary");
            _inject = TryGet<InjectDelegate>("Inject");
        }
        catch
        {
            NativeLibrary.Free(_library);
            throw new InvalidDataException("3dmloader.dll 缺少 HookLibrary/WaitForInjection/UnhookLibrary 导出。");
        }
    }

    public void HookLibrary(string d3d11Path, string targetProcess)
    {
        var result = _hookLibrary(d3d11Path, out _hook, out _mutex);
        if (result != 0)
        {
            // HookLibrary creates its mutex before attempting to load d3d11.dll.
            // Release that partial state so a direct-injection fallback can run.
            try
            {
                if (_hook != IntPtr.Zero || _mutex != IntPtr.Zero)
                {
                    _unhookLibrary(ref _hook, ref _mutex);
                }
            }
            catch
            {
                // Preserve the original HookLibrary error for the caller.
            }

            _hook = IntPtr.Zero;
            _mutex = IntPtr.Zero;
            throw new HookLibraryException(result);
        }

        _hookedDll = d3d11Path;
        _targetProcess = targetProcess;
    }

    public bool WaitForInjection(int timeoutSeconds)
    {
        if (_hookedDll is null)
        {
            throw new InvalidOperationException("尚未建立 HookLibrary。");
        }

        return _waitForInjection(_hookedDll, _targetProcess ?? string.Empty, timeoutSeconds) == 0;
    }

    public bool UnhookLibrary()
    {
        if (_hook == IntPtr.Zero && _mutex == IntPtr.Zero)
        {
            return true;
        }

        var result = _unhookLibrary(ref _hook, ref _mutex);
        _hookedDll = null;
        _targetProcess = null;
        return result == 0;
    }

    public bool SupportsDirectInjection => _inject is not null;

    public int Inject(uint processId, string dllPath, int timeoutSeconds)
    {
        if (_inject is null)
        {
            throw new InvalidOperationException("3dmloader.dll 不支持 Direct Inject。");
        }

        return _inject(processId, dllPath, timeoutSeconds);
    }

    public bool VerifyInjection(string dllPath, string targetProcess, int timeoutSeconds) =>
        _waitForInjection(dllPath, targetProcess, timeoutSeconds) == 0;

    public void Dispose()
    {
        try
        {
            UnhookLibrary();
        }
        finally
        {
            if (_library != IntPtr.Zero)
            {
                NativeLibrary.Free(_library);
            }
        }
    }

    private T Get<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    private T? TryGet<T>(string name) where T : Delegate
    {
        return NativeLibrary.TryGetExport(_library, name, out var address)
            ? Marshal.GetDelegateForFunctionPointer<T>(address)
            : null;
    }
}

public sealed class HookLibraryException : InvalidOperationException
{
    public HookLibraryException(int errorCode)
        : base($"HookLibrary 失败，错误码 {errorCode}。")
    {
        ErrorCode = errorCode;
    }

    public int ErrorCode { get; }
}

public sealed class LaunchService
{
    private readonly AppPaths _paths;
    private readonly IRuntimeManager _runtime;
    private readonly IGameSettingsManager _settings;

    public LaunchService(AppPaths paths, IRuntimeManager runtime, IGameSettingsManager settings)
    {
        _paths = paths;
        _runtime = runtime;
        _settings = settings;
    }

    public async Task<string> LaunchAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.GameExecutablePath) || !File.Exists(config.GameExecutablePath))
        {
            throw new InvalidOperationException("请先选择有效的 ZenlessZoneZero.exe。 ");
        }

        var validation = _runtime.Validate();
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.Message);
        }

        var d3dx = Path.Combine(_paths.RuntimeRoot, "d3dx.ini");
        var backup = Path.Combine(_paths.BackupsRoot, $"d3dx-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.ini");
        File.Copy(d3dx, backup, false);
        _runtime.RepairConfiguration();

        if (config.ConfigureGameSettings)
        {
            _settings.Configure(config.GameExecutablePath, _paths.BackupsRoot);
        }

#if false
        var loader = Path.Combine(_paths.RuntimeRoot, "3dmloader.dll");
        var d3d11 = Path.Combine(_paths.RuntimeRoot, "d3d11.dll");
        var processName = Path.GetFileName(config.GameExecutablePath);

        using var injector = new NativeInjector(loader);
        var useDirectInjection = false;
        try
        {
            injector.HookLibrary(d3d11, processName);
        }
        catch (HookLibraryException ex) when (ex.ErrorCode == 200 && injector.SupportsDirectInjection)
        {
            // WPF/.NET may already have Direct3D loaded. In that case the native
            // loader cannot pre-load the proxy for SetWindowsHookEx, while its
            // supported Inject export can still load it into the game process.
            useDirectInjection = true;
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = config.GameExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(config.GameExecutablePath)!,
                UseShellExecute = true
            }) ?? throw new InvalidOperationException("无法启动游戏进程。");

            if (useDirectInjection)
            {
                var result = injector.Inject((uint)process.Id, d3d11, config.InjectionTimeoutSeconds);
                if (result != 0)
                {
                    throw new InvalidOperationException($"Direct Inject 失败，错误码 {result}。");
                }

                return $"游戏已启动并完成 ZZMI Direct Inject（PID {process.Id}）。";
            }

            var injected = await Task.Run(() => injector.WaitForInjection(config.InjectionTimeoutSeconds), cancellationToken);
            if (!injected)
            {
                throw new InvalidOperationException("等待 ZZMI 注入超时，请查看游戏日志和管理器日志。");
            }

            return $"游戏已启动并完成 ZZMI 注入（PID {process.Id}）。";
        }
        finally
        {
            injector.UnhookLibrary();
        }
#endif

        var managerPath = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("无法定位当前管理器程序。 ");
        var helperStart = new ProcessStartInfo
        {
            FileName = managerPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        helperStart.ArgumentList.Add("--inject-helper");
        helperStart.ArgumentList.Add("--game");
        helperStart.ArgumentList.Add(config.GameExecutablePath);
        helperStart.ArgumentList.Add("--runtime");
        helperStart.ArgumentList.Add(_paths.RuntimeRoot);
        helperStart.ArgumentList.Add("--timeout");
        helperStart.ArgumentList.Add(config.InjectionTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var helper = Process.Start(helperStart)
            ?? throw new InvalidOperationException("无法启动 ZZMI 注入助手。 ");
        var outputTask = helper.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = helper.StandardError.ReadToEndAsync(cancellationToken);
        await helper.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (helper.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? "ZZMI 注入助手失败。"
                : detail.Trim());
        }

        return string.IsNullOrWhiteSpace(output) ? "游戏已启动并完成 ZZMI 注入。" : output.Trim();
    }
}
