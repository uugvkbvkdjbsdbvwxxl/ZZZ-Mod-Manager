using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using ZZZModManager.Infrastructure;

namespace ZZZModManager.Services;

public sealed record CharacterFaceCacheStatus(
    bool IsRecognized,
    string? ProfileId,
    string? DisplayName,
    string GameVersion,
    bool HasCache,
    string? CacheDirectory,
    DateTimeOffset? CapturedAt,
    int MeshCount);

public sealed record CharacterFaceImportResult(
    string ProfileId,
    string DisplayName,
    string GameVersion,
    string CacheDirectory,
    int MeshCount,
    IReadOnlyList<string> Warnings);

public sealed record CharacterFaceCapturePreparation(
    string ProfileId,
    string DisplayName,
    bool Changed,
    string RuntimeConfigurationPath,
    string? BackupPath,
    string ActivationInstruction);

public sealed class CharacterFaceCacheManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string ProfileId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string GameVersion { get; init; } = string.Empty;
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public int MeshCount { get; init; }
    public List<string> Warnings { get; init; } = [];
}

/// <summary>
/// Imports only the vertex/index buffers and textures required for a recognized
/// character head from a user-created 3Dmigoto FrameAnalysis dump. Captured
/// assets stay outside Mods and the game directory in a versioned local cache.
/// </summary>
public sealed class CharacterFaceCaptureService
{
    private const string CacheManifestFileName = "capture.json";
    private const string CacheIniFileName = "captured-face.ini";
    private const string SafeHuntingMode = "hunting = 1";
    private const string SafeAnalyseOptions = "analyse_options = deferred_ctx_accurate dump_tex dump_vb dump_ib buf txt";
    private const int MaximumCaptureFiles = 50_000;
    private const int MaximumImportedDraws = 24;
    private const long MaximumSourceFileBytes = 256L * 1024 * 1024;
    private const long MaximumImportedBytes = 768L * 1024 * 1024;
    private static readonly Regex HashDeclaration = new(
        @"^\s*hash\s*=\s*(?<hash>[0-9a-f]{8})\s*(?:[;#].*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex StrideDeclaration = new(
        @"\bstride\s*[:=]\s*(?<stride>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TexcoordElementDeclaration = new(
        @"SemanticName:\s*TEXCOORD\s*\r?\n\s*SemanticIndex:\s*0\s*\r?\n\s*Format:\s*(?<format>[A-Z0-9_]+)\s*\r?\n\s*InputSlot:\s*1\s*\r?\n\s*AlignedByteOffset:\s*(?<offset>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AnalyseOptionsDeclaration = new(
        @"^(?<indent>[ \t]*)analyse_options[ \t]*=[^\r\n]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex HuntingDeclaration = new(
        @"^(?<indent>[ \t]*)hunting[ \t]*=[^\r\n]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly EnumerationOptions CaptureEnumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        ReturnSpecialDirectories = false,
        MaxRecursionDepth = 4
    };
    private static readonly EnumerationOptions ModEnumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        ReturnSpecialDirectories = false,
        MaxRecursionDepth = 5
    };

    private static readonly IReadOnlyList<FaceProfile> Profiles =
    [
        new(
            "RemielleMoonlight",
            "Remielle · Moonlight Whispers",
            ["f57f3e40", "09a51ed3"],
            [
                new FaceComponent(
                    "Hair",
                    "头发",
                    "789ae812",
                    "62b7da5e",
                    "fa8ab367",
                    new TextureHashes("578239d7", "ebac056e", "6f826e7d", "b5a12580"),
                    Required: false),
                new FaceComponent(
                    "Face",
                    "脸部",
                    "7fbbcf0d",
                    null,
                    null,
                    new TextureHashes("baf9e1be", null, null, null),
                    Required: true),
                new FaceComponent(
                    "Eyebrows",
                    "眉毛",
                    "fcbae9a5",
                    null,
                    null,
                    new TextureHashes("baf9e1be", null, null, null),
                    Required: false)
            ])
    ];

    private readonly AppPaths _paths;
    private readonly JsonFileStore _store;
    private readonly Func<string?> _gameExecutablePath;
    private readonly ZzmiModelPreviewLoader _cacheLoader = new();

    public CharacterFaceCaptureService(
        AppPaths paths,
        JsonFileStore store,
        Func<string?> gameExecutablePath)
    {
        _paths = paths;
        _store = store;
        _gameExecutablePath = gameExecutablePath;
        _paths.Ensure();
    }

    public CharacterFaceCacheStatus GetStatus(string modDirectory)
    {
        var profile = DetectProfile(modDirectory);
        var gameVersion = ResolveGameVersion();
        if (profile is null)
        {
            return new CharacterFaceCacheStatus(false, null, null, gameVersion, false, null, null, 0);
        }

        var cacheDirectory = GetCacheDirectory(profile.Id, gameVersion);
        var manifest = _store.Load<CharacterFaceCacheManifest?>(
            Path.Combine(cacheDirectory, CacheManifestFileName),
            () => null);
        var available = manifest is not null
            && string.Equals(manifest.ProfileId, profile.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(manifest.GameVersion, gameVersion, StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(cacheDirectory, CacheIniFileName));
        return new CharacterFaceCacheStatus(
            true,
            profile.Id,
            profile.DisplayName,
            gameVersion,
            available,
            cacheDirectory,
            available ? manifest!.CapturedAt : null,
            available ? manifest!.MeshCount : 0);
    }

    /// <summary>
    /// Enables the FrameAnalysis context and narrows global F8 analysis to
    /// resources needed by the head importer. 3Dmigoto chooses its context at
    /// device creation, so a full game restart is required after this change.
    /// Render targets and constant buffers are excluded to prevent an
    /// accidental multi-gigabyte capture. d3dx.ini is backed up first.
    /// </summary>
    public CharacterFaceCapturePreparation PrepareSafeCapture(string modDirectory)
    {
        var profile = DetectProfile(modDirectory)
            ?? throw new InvalidOperationException("当前 Mod 尚未匹配到受支持的原始头脸配置。");
        var configurationPath = Path.GetFullPath(Path.Combine(_paths.RuntimeRoot, "d3dx.ini"));
        if (!FileSystemSafety.IsWithin(_paths.RuntimeRoot, configurationPath)
            || !File.Exists(configurationPath))
        {
            throw new FileNotFoundException("找不到 ZZMI 的 d3dx.ini，无法准备安全采集。", configurationPath);
        }

        string source;
        Encoding encoding;
        using (var reader = new StreamReader(configurationPath, Encoding.UTF8, true))
        {
            source = reader.ReadToEnd();
            encoding = reader.CurrentEncoding;
        }

        var huntingMatch = HuntingDeclaration.Match(source);
        var analyseMatch = AnalyseOptionsDeclaration.Match(source);
        if (!huntingMatch.Success || !analyseMatch.Success)
        {
            throw new InvalidDataException("d3dx.ini 中缺少 hunting 或 analyse_options，未修改运行核心。");
        }

        var huntingReplacement = huntingMatch.Groups["indent"].Value + SafeHuntingMode;
        var analyseReplacement = analyseMatch.Groups["indent"].Value + SafeAnalyseOptions;
        var huntingReady = string.Equals(huntingMatch.Value.Trim(), SafeHuntingMode, StringComparison.OrdinalIgnoreCase);
        var analyseReady = string.Equals(analyseMatch.Value.Trim(), SafeAnalyseOptions, StringComparison.OrdinalIgnoreCase);
        if (huntingReady && analyseReady)
        {
            return new CharacterFaceCapturePreparation(
                profile.Id,
                profile.DisplayName,
                false,
                configurationPath,
                null,
                "必须完全退出并重新启动游戏");
        }

        var backupDirectory = Path.GetFullPath(Path.Combine(_paths.BackupsRoot, "FrameAnalysis"));
        if (!FileSystemSafety.IsWithin(_paths.BackupsRoot, backupDirectory))
        {
            throw new InvalidOperationException("帧分析配置备份路径越界。");
        }

        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(
            backupDirectory,
            $"d3dx-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.ini");
        File.Copy(configurationPath, backupPath, false);
        var edits = new[]
        {
            (huntingMatch.Index, huntingMatch.Length, Replacement: huntingReplacement),
            (analyseMatch.Index, analyseMatch.Length, Replacement: analyseReplacement)
        };
        var updated = source;
        foreach (var edit in edits.OrderByDescending(edit => edit.Index))
        {
            updated = updated[..edit.Index] + edit.Replacement + updated[(edit.Index + edit.Length)..];
        }
        var temporaryPath = configurationPath + $".capture-{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, updated, encoding);
            File.Move(temporaryPath, configurationPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return new CharacterFaceCapturePreparation(
            profile.Id,
            profile.DisplayName,
            true,
            configurationPath,
            backupPath,
            "必须完全退出并重新启动游戏");
    }

    public string? FindLatestFrameAnalysis()
    {
        if (!Directory.Exists(_paths.RuntimeRoot))
        {
            return null;
        }

        return Directory.EnumerateDirectories(_paths.RuntimeRoot, "FrameAnalysis-*", SearchOption.TopDirectoryOnly)
            .Select(path => new DirectoryInfo(path))
            .Where(directory => !directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .OrderByDescending(directory => directory.LastWriteTimeUtc)
            .Select(directory => directory.FullName)
            .FirstOrDefault();
    }

    public CharacterFaceImportResult ImportLatest(string modDirectory)
    {
        var captureDirectory = FindLatestFrameAnalysis()
            ?? throw new InvalidOperationException("没有找到 FrameAnalysis 转储。请先让对应角色出现在游戏中并按 F8。");
        return Import(modDirectory, captureDirectory);
    }

    public CharacterFaceImportResult Import(string modDirectory, string captureDirectory)
    {
        var profile = DetectProfile(modDirectory)
            ?? throw new InvalidOperationException("当前 Mod 尚未匹配到受支持的原始头脸配置。");
        var captureRoot = Path.GetFullPath(captureDirectory);
        if (!Directory.Exists(captureRoot))
        {
            throw new DirectoryNotFoundException("找不到所选 FrameAnalysis 目录。");
        }

        var captureInfo = new DirectoryInfo(captureRoot);
        if (captureInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("为避免越界读取，不能从重解析点导入帧分析数据。");
        }

        var files = EnumerateCaptureFiles(captureRoot);
        var gameVersion = ResolveGameVersion();
        var targetDirectory = GetCacheDirectory(profile.Id, gameVersion);
        var profileRoot = Path.GetDirectoryName(targetDirectory)
            ?? throw new InvalidOperationException("头脸缓存路径无效。");
        Directory.CreateDirectory(profileRoot);
        var operationId = Guid.NewGuid().ToString("N");
        var stagingDirectory = Path.Combine(profileRoot, $".importing-{operationId}");
        var displacedDirectory = Path.Combine(profileRoot, $".replaced-{operationId}");
        Directory.CreateDirectory(stagingDirectory);

        var warnings = new List<string>();
        var ini = new StringBuilder();
        var importedDraws = new List<ImportedDraw>();
        long importedBytes = 0;
        try
        {
            foreach (var component in profile.Components)
            {
                var draws = ImportComponent(
                    component,
                    files,
                    stagingDirectory,
                    ini,
                    warnings,
                    ref importedBytes);
                importedDraws.AddRange(draws);
                if (component.Required && draws.Count == 0)
                {
                    throw new InvalidDataException(
                        $"转储中找到了角色，但没有可用的{component.DisplayName}绘制数据（IB {component.IndexHash}）。");
                }
            }

            if (importedDraws.Count == 0)
            {
                throw new InvalidDataException("所选转储中没有找到可导入的头脸绘制数据。");
            }

            File.WriteAllText(Path.Combine(stagingDirectory, CacheIniFileName), ini.ToString(), new UTF8Encoding(false));
            var manifest = new CharacterFaceCacheManifest
            {
                ProfileId = profile.Id,
                DisplayName = profile.DisplayName,
                GameVersion = gameVersion,
                CapturedAt = DateTimeOffset.UtcNow,
                MeshCount = importedDraws.Count,
                Warnings = [.. warnings]
            };
            _store.Save(Path.Combine(stagingDirectory, CacheManifestFileName), manifest);

            var validation = _cacheLoader.Load(stagingDirectory);
            if (validation.Meshes.Count == 0 || validation.Meshes.Count != importedDraws.Count)
            {
                var details = validation.Warnings.Count == 0
                    ? "未生成任何兼容网格。"
                    : string.Join("；", validation.Warnings.Take(3));
                throw new InvalidDataException($"头脸缓存验证失败：{details}");
            }

            ReplaceCacheDirectory(stagingDirectory, targetDirectory, displacedDirectory);
            _cacheLoader.Invalidate(targetDirectory);
            return new CharacterFaceImportResult(
                profile.Id,
                profile.DisplayName,
                gameVersion,
                targetDirectory,
                importedDraws.Count,
                warnings);
        }
        catch
        {
            SafeDelete(stagingDirectory);
            if (!Directory.Exists(targetDirectory) && Directory.Exists(displacedDirectory))
            {
                Directory.Move(displacedDirectory, targetDirectory);
            }

            throw;
        }
    }

    public ModelPreviewScene MergeCached(string modDirectory, ModelPreviewScene modScene)
    {
        var status = GetStatus(modDirectory);
        if (!status.HasCache || status.CacheDirectory is null)
        {
            return modScene;
        }

        ModelPreviewScene captured;
        try
        {
            captured = _cacheLoader.Load(status.CacheDirectory);
        }
        catch (ModelPreviewException ex)
        {
            return modScene with { Warnings = [.. modScene.Warnings, $"原始头脸缓存：{ex.Message}"] };
        }

        var alignedCaptured = AlignCapturedHead(modScene, captured.Meshes);
        var supplement = alignedCaptured
            .Where(mesh => !HasModReplacement(modScene, mesh.Name))
            .Select(mesh => mesh with { Name = "原始头脸 · " + FriendlyCapturedName(mesh.Name) })
            .ToList();
        if (supplement.Count == 0)
        {
            return modScene;
        }

        var meshes = modScene.Meshes.Concat(supplement).ToList();
        var (minimum, maximum) = CalculateBounds(meshes);
        var warnings = modScene.Warnings
            .Concat(captured.Warnings.Select(warning => "原始头脸缓存：" + warning))
            .ToList();
        var diagnostics = new ModelPreviewDiagnostics(
            modScene.Diagnostics.CacheHit && captured.Diagnostics.CacheHit,
            modScene.Diagnostics.LoadDuration + captured.Diagnostics.LoadDuration,
            modScene.Diagnostics.SourceFileCount + captured.Diagnostics.SourceFileCount,
            modScene.Diagnostics.TextureCount + captured.Diagnostics.TextureCount,
            modScene.Diagnostics.DownsampledTextureCount + captured.Diagnostics.DownsampledTextureCount,
            modScene.Diagnostics.RetainedTextureBytes + captured.Diagnostics.RetainedTextureBytes);
        return new ModelPreviewScene(
            meshes,
            minimum,
            maximum,
            warnings,
            modScene.Variants,
            diagnostics);
    }

    private FaceProfile? DetectProfile(string modDirectory)
    {
        if (!Directory.Exists(modDirectory))
        {
            return null;
        }

        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inspected = 0;
        foreach (var iniPath in Directory.EnumerateFiles(modDirectory, "*.ini", ModEnumeration))
        {
            if (++inspected > 128)
            {
                break;
            }

            var file = new FileInfo(iniPath);
            if (file.Length > 4L * 1024 * 1024)
            {
                continue;
            }

            foreach (var line in File.ReadLines(iniPath))
            {
                var match = HashDeclaration.Match(line);
                if (match.Success)
                {
                    hashes.Add(match.Groups["hash"].Value);
                }
            }
        }

        return Profiles.FirstOrDefault(profile => profile.DetectionHashes.All(hashes.Contains));
    }

    private static IReadOnlyList<FileInfo> EnumerateCaptureFiles(string captureRoot)
    {
        var files = new List<FileInfo>();
        foreach (var path in Directory.EnumerateFiles(captureRoot, "*", CaptureEnumeration))
        {
            if (files.Count >= MaximumCaptureFiles)
            {
                throw new InvalidDataException("FrameAnalysis 文件数超过安全上限。");
            }

            var file = new FileInfo(path);
            if (file.Length > MaximumSourceFileBytes)
            {
                throw new InvalidDataException($"帧分析文件过大：{file.Name}");
            }

            files.Add(file);
        }

        return files;
    }

    private static IReadOnlyList<ImportedDraw> ImportComponent(
        FaceComponent component,
        IReadOnlyList<FileInfo> files,
        string stagingDirectory,
        StringBuilder ini,
        List<string> warnings,
        ref long importedBytes)
    {
        var candidates = files
            .Where(file => file.Extension.Equals(".buf", StringComparison.OrdinalIgnoreCase))
            .Select(file => (File: file, Draw: TryGetDrawPrefix(file.Name, "ib", component.IndexHash)))
            .Where(candidate => candidate.Draw is not null)
            .OrderBy(candidate => IsDeduplicatedPath(candidate.File.FullName))
            .ThenBy(candidate => candidate.File.FullName, StringComparer.OrdinalIgnoreCase)
            .GroupBy(candidate => candidate.Draw!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var imported = new List<ImportedDraw>();
        var seenLayouts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordinal = 0;
        foreach (var candidate in candidates)
        {
            var siblingFiles = files.Where(file => string.Equals(
                file.DirectoryName,
                candidate.File.DirectoryName,
                StringComparison.OrdinalIgnoreCase)).ToList();
            var position = FindDrawBuffer(siblingFiles, candidate.Draw!, "vb0", component.PositionHash);
            var texcoord = FindDrawBuffer(siblingFiles, candidate.Draw!, "vb1", component.TexcoordHash);
            if (position is null)
            {
                warnings.Add($"{component.DisplayName} {candidate.Draw}：缺少 vb0 Position 缓冲。");
                continue;
            }

            var layoutIdentity = string.Join(
                '|',
                GetBufferIdentity(position, "vb0"),
                position.Length,
                texcoord is null ? "no-vb1" : GetBufferIdentity(texcoord, "vb1"),
                texcoord?.Length ?? 0,
                candidate.File.Length);
            if (!seenLayouts.Add(layoutIdentity))
            {
                continue;
            }

            if (imported.Count >= MaximumImportedDraws)
            {
                warnings.Add($"{component.DisplayName}绘制数据超过 {MaximumImportedDraws} 组，其余重复项已忽略。");
                break;
            }

            try
            {
                ordinal++;
                var stem = $"Base{component.Name}{ordinal:00}";
                var normalizedPosition = NormalizePositionBuffer(position);
                var vertexCount = normalizedPosition.Length / 40;
                var normalizedTexcoord = NormalizeTexcoordBuffer(texcoord, vertexCount, warnings, component.DisplayName);
                var normalizedIndices = NormalizeIndexBuffer(candidate.File, vertexCount);
                importedBytes = checked(importedBytes
                    + normalizedPosition.Length
                    + normalizedTexcoord.Length
                    + normalizedIndices.Length);
                if (importedBytes > MaximumImportedBytes)
                {
                    throw new InvalidDataException("头脸采集数据超过缓存安全上限。");
                }

                var positionName = stem + "Position.buf";
                var texcoordName = stem + "Texcoord.buf";
                var indexName = stem + ".ib";
                File.WriteAllBytes(Path.Combine(stagingDirectory, positionName), normalizedPosition);
                File.WriteAllBytes(Path.Combine(stagingDirectory, texcoordName), normalizedTexcoord);
                File.WriteAllBytes(Path.Combine(stagingDirectory, indexName), normalizedIndices);

                var textures = CopyTextures(
                    component,
                    candidate.Draw!,
                    siblingFiles,
                    files,
                    stagingDirectory,
                    stem,
                    ref importedBytes);
                AppendIni(ini, stem, positionName, texcoordName, indexName, normalizedIndices.Length / 4, textures);
                imported.Add(new ImportedDraw(component.Name, candidate.Draw!));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException)
            {
                warnings.Add($"{component.DisplayName} {candidate.Draw}：{ex.Message}");
            }
        }

        return imported;
    }

    private static byte[] NormalizePositionBuffer(FileInfo source)
    {
        var data = File.ReadAllBytes(source.FullName);
        var stride = ReadDeclaredStride(source) ?? (data.Length % 40 == 0 ? 40 : 0);
        if (stride < 24 || stride > 128 || data.Length == 0 || data.Length % stride != 0)
        {
            throw new InvalidDataException($"{source.Name} 缺少可识别的 Position stride。");
        }

        var vertexCount = data.Length / stride;
        if (vertexCount > 1_000_000)
        {
            throw new InvalidDataException("Position 顶点数超过预览上限。");
        }

        var normalized = new byte[checked(vertexCount * 40)];
        for (var vertex = 0; vertex < vertexCount; vertex++)
        {
            var sourceOffset = vertex * stride;
            var targetOffset = vertex * 40;
            data.AsSpan(sourceOffset, 24).CopyTo(normalized.AsSpan(targetOffset, 24));
            var position = new Vector3(
                BitConverter.ToSingle(data, sourceOffset),
                BitConverter.ToSingle(data, sourceOffset + 4),
                BitConverter.ToSingle(data, sourceOffset + 8));
            var normal = new Vector3(
                BitConverter.ToSingle(data, sourceOffset + 12),
                BitConverter.ToSingle(data, sourceOffset + 16),
                BitConverter.ToSingle(data, sourceOffset + 20));
            if (!IsFinite(position) || !IsFinite(normal))
            {
                throw new InvalidDataException($"{source.Name} 包含非有限顶点数据。");
            }
        }

        return normalized;
    }

    private static byte[] NormalizeTexcoordBuffer(
        FileInfo? source,
        int vertexCount,
        List<string> warnings,
        string componentName)
    {
        var normalized = new byte[checked(vertexCount * 20)];
        if (source is null)
        {
            warnings.Add($"{componentName}：未捕获 vb1，预览将使用空 UV。 ");
            return normalized;
        }

        var data = File.ReadAllBytes(source.FullName);
        var inferredStride = vertexCount > 0 && data.Length % vertexCount == 0
            ? data.Length / vertexCount
            : 0;
        var stride = ReadDeclaredStride(source) ?? inferredStride;
        if (stride < 8 || stride > 128 || data.Length / stride < vertexCount)
        {
            warnings.Add($"{componentName}：{source.Name} 的 Texcoord stride 无法识别，使用空 UV。");
            return normalized;
        }

        var layout = ReadTexcoordLayout(source);
        for (var vertex = 0; vertex < vertexCount; vertex++)
        {
            var sourceOffset = vertex * stride;
            var targetOffset = vertex * 20;
            data.AsSpan(sourceOffset, Math.Min(4, stride)).CopyTo(normalized.AsSpan(targetOffset, 4));
            if (layout is null)
            {
                data.AsSpan(sourceOffset, Math.Min(8, stride)).CopyTo(normalized.AsSpan(targetOffset, 8));
                continue;
            }

            var uvOffset = sourceOffset + layout.Offset;
            if (layout.Format.Equals("R16G16_FLOAT", StringComparison.OrdinalIgnoreCase)
                && layout.Offset + 4 <= stride)
            {
                data.AsSpan(uvOffset, 4).CopyTo(normalized.AsSpan(targetOffset + 4, 4));
                continue;
            }

            if (layout.Format.Equals("R32G32_FLOAT", StringComparison.OrdinalIgnoreCase)
                && layout.Offset + 8 <= stride)
            {
                var u = BitConverter.ToSingle(data, uvOffset);
                var v = BitConverter.ToSingle(data, uvOffset + 4);
                if (float.IsFinite(u) && float.IsFinite(v))
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        normalized.AsSpan(targetOffset + 4, 2),
                        BitConverter.HalfToUInt16Bits((Half)u));
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        normalized.AsSpan(targetOffset + 6, 2),
                        BitConverter.HalfToUInt16Bits((Half)v));
                }
            }
        }

        return normalized;
    }

    private static byte[] NormalizeIndexBuffer(FileInfo source, int vertexCount)
    {
        var data = File.ReadAllBytes(source.FullName);
        var metadata = ReadSidecarText(source);
        uint[]? indices = null;
        if (metadata.Contains("R16_UINT", StringComparison.OrdinalIgnoreCase))
        {
            indices = ParseR16Indices(data);
        }
        else if (metadata.Contains("R32_UINT", StringComparison.OrdinalIgnoreCase))
        {
            indices = ParseR32Indices(data);
        }
        else
        {
            if (data.Length % 4 == 0)
            {
                var r32 = ParseR32Indices(data);
                if (TryNormalizeIndexValues(r32, vertexCount, out var normalizedR32))
                {
                    indices = normalizedR32;
                }
            }

            if (indices is null && data.Length % 2 == 0)
            {
                var r16 = ParseR16Indices(data);
                if (TryNormalizeIndexValues(r16, vertexCount, out var normalizedR16))
                {
                    indices = normalizedR16;
                }
            }
        }

        if (indices is null || !TryNormalizeIndexValues(indices, vertexCount, out var normalized))
        {
            throw new InvalidDataException($"{source.Name} 的索引格式或顶点范围无效。");
        }

        if (normalized.Length == 0 || normalized.Length % 3 != 0)
        {
            throw new InvalidDataException($"{source.Name} 不是完整的三角形索引流。");
        }

        var output = new byte[checked(normalized.Length * 4)];
        for (var index = 0; index < normalized.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(index * 4, 4), normalized[index]);
        }

        return output;
    }

    private static uint[] ParseR16Indices(byte[] data)
    {
        if (data.Length == 0 || data.Length % 2 != 0)
        {
            return [];
        }

        var result = new uint[data.Length / 2];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(index * 2, 2));
        }

        return result;
    }

    private static uint[] ParseR32Indices(byte[] data)
    {
        if (data.Length == 0 || data.Length % 4 != 0)
        {
            return [];
        }

        var result = new uint[data.Length / 4];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(index * 4, 4));
        }

        return result;
    }

    private static bool TryNormalizeIndexValues(
        IReadOnlyList<uint> source,
        int vertexCount,
        out uint[] normalized)
    {
        normalized = [];
        if (source.Count == 0 || vertexCount <= 0)
        {
            return false;
        }

        var minimum = source.Min();
        var maximum = source.Max();
        uint offset = 0;
        if (maximum >= vertexCount)
        {
            if ((ulong)maximum - minimum >= (ulong)vertexCount)
            {
                return false;
            }

            offset = minimum;
        }

        normalized = new uint[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            var value = source[index] - offset;
            if (value >= vertexCount)
            {
                normalized = [];
                return false;
            }

            normalized[index] = value;
        }

        return true;
    }

    private static CapturedTextures CopyTextures(
        FaceComponent component,
        string drawPrefix,
        IReadOnlyList<FileInfo> siblingFiles,
        IReadOnlyList<FileInfo> allFiles,
        string stagingDirectory,
        string stem,
        ref long importedBytes)
    {
        var currentBytes = importedBytes;
        string? Copy(string? hash, string label)
        {
            if (string.IsNullOrWhiteSpace(hash))
            {
                return null;
            }

            var source = FindTexture(siblingFiles, drawPrefix, hash)
                ?? FindTexture(allFiles, null, hash);
            if (source is null)
            {
                return null;
            }

            currentBytes = checked(currentBytes + source.Length);
            if (currentBytes > MaximumImportedBytes)
            {
                throw new InvalidDataException("头脸采集数据超过缓存安全上限。");
            }

            var fileName = stem + label + ".dds";
            File.Copy(source.FullName, Path.Combine(stagingDirectory, fileName), overwrite: false);
            return fileName;
        }

        var captured = new CapturedTextures(
            Copy(component.Textures.Diffuse, "Diffuse"),
            Copy(component.Textures.Normal, "NormalMap"),
            Copy(component.Textures.Light, "LightMap"),
            Copy(component.Textures.Material, "MaterialMap"));
        importedBytes = currentBytes;
        return captured;
    }

    private static FileInfo? FindTexture(
        IReadOnlyList<FileInfo> files,
        string? drawPrefix,
        string hash)
    {
        return files
            .Where(file => file.Extension.Equals(".dds", StringComparison.OrdinalIgnoreCase))
            .Where(file => drawPrefix is null
                || file.Name.StartsWith(drawPrefix + "-", StringComparison.OrdinalIgnoreCase))
            .Where(file => file.Name.Contains(hash, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => IsDeduplicatedPath(file.FullName))
            .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static void AppendIni(
        StringBuilder ini,
        string stem,
        string positionName,
        string texcoordName,
        string indexName,
        int indexCount,
        CapturedTextures textures)
    {
        ini.AppendLine(CultureInfo.InvariantCulture, $"[TextureOverride{stem}]");
        ini.AppendLine(CultureInfo.InvariantCulture, $"ib = Resource{stem}IB");
        AppendTextureBinding(ini, stem, "Diffuse", textures.Diffuse);
        AppendTextureBinding(ini, stem, "NormalMap", textures.Normal);
        AppendTextureBinding(ini, stem, "LightMap", textures.Light);
        AppendTextureBinding(ini, stem, "MaterialMap", textures.Material);
        ini.AppendLine(CultureInfo.InvariantCulture, $"drawindexed = {indexCount}, 0, 0");
        ini.AppendLine();
        ini.AppendLine(CultureInfo.InvariantCulture, $"[Resource{stem}Position]");
        ini.AppendLine("type = Buffer");
        ini.AppendLine("stride = 40");
        ini.AppendLine(CultureInfo.InvariantCulture, $"filename = {positionName}");
        ini.AppendLine();
        ini.AppendLine(CultureInfo.InvariantCulture, $"[Resource{stem}Texcoord]");
        ini.AppendLine("type = Buffer");
        ini.AppendLine("stride = 20");
        ini.AppendLine(CultureInfo.InvariantCulture, $"filename = {texcoordName}");
        ini.AppendLine();
        ini.AppendLine(CultureInfo.InvariantCulture, $"[Resource{stem}IB]");
        ini.AppendLine("type = Buffer");
        ini.AppendLine("format = DXGI_FORMAT_R32_UINT");
        ini.AppendLine(CultureInfo.InvariantCulture, $"filename = {indexName}");
        ini.AppendLine();
        AppendTextureResource(ini, stem, "Diffuse", textures.Diffuse);
        AppendTextureResource(ini, stem, "NormalMap", textures.Normal);
        AppendTextureResource(ini, stem, "LightMap", textures.Light);
        AppendTextureResource(ini, stem, "MaterialMap", textures.Material);
    }

    private static void AppendTextureBinding(StringBuilder ini, string stem, string kind, string? fileName)
    {
        if (fileName is not null)
        {
            ini.AppendLine(CultureInfo.InvariantCulture, $"Resource\\ZZMI\\{kind} = ref Resource{stem}{kind}");
        }
    }

    private static void AppendTextureResource(StringBuilder ini, string stem, string kind, string? fileName)
    {
        if (fileName is null)
        {
            return;
        }

        ini.AppendLine(CultureInfo.InvariantCulture, $"[Resource{stem}{kind}]");
        ini.AppendLine(CultureInfo.InvariantCulture, $"filename = {fileName}");
        ini.AppendLine();
    }

    private static FileInfo? FindDrawBuffer(
        IReadOnlyList<FileInfo> files,
        string drawPrefix,
        string slot,
        string? preferredHash)
    {
        var prefix = drawPrefix + "-" + slot + "=";
        var candidates = files
            .Where(file => file.Extension.Equals(".buf", StringComparison.OrdinalIgnoreCase))
            .Where(file => file.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => IsDeduplicatedPath(file.FullName))
            .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!string.IsNullOrWhiteSpace(preferredHash))
        {
            var preferred = candidates.FirstOrDefault(file =>
                file.Name.Contains("=" + preferredHash, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
            {
                return preferred;
            }
        }

        return candidates.FirstOrDefault();
    }

    private static string GetBufferIdentity(FileInfo file, string slot)
    {
        var token = "-" + slot + "=";
        var start = file.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return file.Name;
        }

        start += token.Length;
        var end = file.Name.IndexOf('-', start);
        return end > start ? file.Name[start..end] : file.Name[start..];
    }

    private static string? TryGetDrawPrefix(string fileName, string slot, string hash)
    {
        if (!fileName.EndsWith(".buf", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = "-" + slot + "=" + hash;
        var index = fileName.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        return index > 0 ? fileName[..index] : null;
    }

    private static bool IsDeduplicatedPath(string path) => path
        .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(segment => segment.Equals("deduped", StringComparison.OrdinalIgnoreCase));

    private static int? ReadDeclaredStride(FileInfo buffer)
    {
        var text = ReadSidecarText(buffer);
        var match = StrideDeclaration.Match(text);
        return match.Success
            && int.TryParse(match.Groups["stride"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var stride)
            ? stride
            : null;
    }

    private static TexcoordLayout? ReadTexcoordLayout(FileInfo buffer)
    {
        var match = TexcoordElementDeclaration.Match(ReadSidecarText(buffer));
        return match.Success
            && int.TryParse(match.Groups["offset"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            ? new TexcoordLayout(match.Groups["format"].Value, offset)
            : null;
    }

    private static string ReadSidecarText(FileInfo buffer)
    {
        var path = Path.ChangeExtension(buffer.FullName, ".txt");
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        var file = new FileInfo(path);
        return file.Length <= 4L * 1024 * 1024
            ? File.ReadAllText(path)
            : string.Empty;
    }

    private string ResolveGameVersion()
    {
        var executable = _gameExecutablePath();
        if (!string.IsNullOrWhiteSpace(executable))
        {
            try
            {
                var gameDirectory = Path.GetDirectoryName(Path.GetFullPath(executable));
                var versionFile = gameDirectory is null ? null : Path.Combine(gameDirectory, "version_info");
                if (versionFile is not null && File.Exists(versionFile))
                {
                    var version = File.ReadLines(versionFile).FirstOrDefault()?.Trim();
                    if (!string.IsNullOrWhiteSpace(version))
                    {
                        return FileSystemSafety.SanitizeDirectoryName(version);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A missing version marker only makes the cache less specific;
                // it must not prevent a user from importing an explicit dump.
            }
        }

        return "unknown-game-version";
    }

    private string GetCacheDirectory(string profileId, string gameVersion)
    {
        var profileDirectory = FileSystemSafety.SanitizeDirectoryName(profileId);
        var versionDirectory = FileSystemSafety.SanitizeDirectoryName(gameVersion);
        var path = Path.GetFullPath(Path.Combine(
            _paths.CharacterFaceCacheRoot,
            profileDirectory,
            versionDirectory));
        if (!FileSystemSafety.IsWithin(_paths.CharacterFaceCacheRoot, path))
        {
            throw new InvalidOperationException("头脸缓存路径越界。");
        }

        return path;
    }

    private void ReplaceCacheDirectory(string stagingDirectory, string targetDirectory, string displacedDirectory)
    {
        var displaced = false;
        var activated = false;
        try
        {
            if (Directory.Exists(targetDirectory))
            {
                Directory.Move(targetDirectory, displacedDirectory);
                displaced = true;
            }

            Directory.Move(stagingDirectory, targetDirectory);
            activated = true;
            SafeDelete(displacedDirectory);
        }
        catch
        {
            if (activated && Directory.Exists(targetDirectory) && Directory.Exists(displacedDirectory))
            {
                SafeDelete(targetDirectory);
            }

            if (displaced && !Directory.Exists(targetDirectory) && Directory.Exists(displacedDirectory))
            {
                Directory.Move(displacedDirectory, targetDirectory);
            }

            throw;
        }
    }

    private void SafeDelete(string path)
    {
        if (FileSystemSafety.IsWithin(_paths.CharacterFaceCacheRoot, path) && Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    private static IReadOnlyList<ModelPreviewMesh> AlignCapturedHead(
        ModelPreviewScene modScene,
        IReadOnlyList<ModelPreviewMesh> capturedMeshes)
    {
        if (modScene.Meshes.Count == 0 || capturedMeshes.Count == 0)
        {
            return capturedMeshes;
        }

        var targetCenter = (modScene.Minimum + modScene.Maximum) / 2f;
        var modelHeight = MathF.Max(0.1f, modScene.Maximum.Z - modScene.Minimum.Z);
        var aligned = new List<ModelPreviewMesh>(capturedMeshes.Count);
        foreach (var group in capturedMeshes.GroupBy(mesh => CapturedCategory(mesh.Name)))
        {
            if (group.Key is null)
            {
                aligned.AddRange(group);
                continue;
            }

            var positions = group.SelectMany(mesh => mesh.Positions).ToArray();
            if (positions.Length == 0)
            {
                aligned.AddRange(group);
                continue;
            }

            var minimum = positions.Aggregate(Vector3.Min);
            var maximum = positions.Aggregate(Vector3.Max);
            var sourceCenterY = (minimum.Y + maximum.Y) / 2f;
            var sourceCenterZ = (minimum.Z + maximum.Z) / 2f;
            var targetTop = group.Key switch
            {
                "Face" => modScene.Maximum.Z - (modelHeight * 0.025f),
                "Eyebrows" => modScene.Maximum.Z - (modelHeight * 0.06f),
                _ => modScene.Maximum.Z
            };
            var xOffset = targetCenter.X - sourceCenterZ;
            var yOffset = targetCenter.Y - sourceCenterY;
            var zOffset = targetTop + minimum.X;

            foreach (var mesh in group)
            {
                var transformedPositions = mesh.Positions
                    .Select(position => new Vector3(
                        position.Z + xOffset,
                        position.Y + yOffset,
                        -position.X + zOffset))
                    .ToArray();
                var transformedNormals = mesh.Normals
                    .Select(normal =>
                    {
                        var transformed = new Vector3(normal.Z, normal.Y, -normal.X);
                        return transformed.LengthSquared() > 0.000001f
                            ? Vector3.Normalize(transformed)
                            : Vector3.UnitZ;
                    })
                    .ToArray();
                aligned.Add(mesh with
                {
                    Positions = transformedPositions,
                    Normals = transformedNormals
                });
            }
        }

        return aligned;
    }

    private static bool HasModReplacement(ModelPreviewScene modScene, string capturedName)
    {
        var category = CapturedCategory(capturedName);
        return category is not null && modScene.Meshes.Any(mesh =>
            mesh.Name.Contains(category, StringComparison.OrdinalIgnoreCase));
    }

    private static string? CapturedCategory(string name)
    {
        return name.Contains("Hair", StringComparison.OrdinalIgnoreCase)
            ? "Hair"
            : name.Contains("Eyebrows", StringComparison.OrdinalIgnoreCase)
                ? "Eyebrows"
                : name.Contains("Face", StringComparison.OrdinalIgnoreCase)
                    ? "Face"
                    : null;
    }

    private static string FriendlyCapturedName(string name)
    {
        var ordinal = new string(name.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        var suffix = string.IsNullOrWhiteSpace(ordinal) ? string.Empty : " " + ordinal;
        if (name.Contains("Hair", StringComparison.OrdinalIgnoreCase))
        {
            return "头发" + suffix;
        }

        if (name.Contains("Eyebrows", StringComparison.OrdinalIgnoreCase))
        {
            return "眉毛" + suffix;
        }

        return name.Contains("Face", StringComparison.OrdinalIgnoreCase)
            ? "脸部" + suffix
            : name;
    }

    private static (Vector3 Minimum, Vector3 Maximum) CalculateBounds(IReadOnlyList<ModelPreviewMesh> meshes)
    {
        var first = meshes.SelectMany(mesh => mesh.Positions).First();
        var minimum = first;
        var maximum = first;
        foreach (var position in meshes.SelectMany(mesh => mesh.Positions))
        {
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);
        }

        return (minimum, maximum);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private sealed record FaceProfile(
        string Id,
        string DisplayName,
        IReadOnlyList<string> DetectionHashes,
        IReadOnlyList<FaceComponent> Components);

    private sealed record FaceComponent(
        string Name,
        string DisplayName,
        string IndexHash,
        string? PositionHash,
        string? TexcoordHash,
        TextureHashes Textures,
        bool Required);

    private sealed record TextureHashes(
        string? Diffuse,
        string? Normal,
        string? Light,
        string? Material);

    private sealed record CapturedTextures(
        string? Diffuse,
        string? Normal,
        string? Light,
        string? Material);

    private sealed record ImportedDraw(string Component, string DrawPrefix);

    private sealed record TexcoordLayout(string Format, int Offset);
}
