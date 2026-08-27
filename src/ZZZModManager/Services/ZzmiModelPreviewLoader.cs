using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using ZZZModManager.Infrastructure;

namespace ZZZModManager.Services;

public interface IModModelPreviewLoader
{
    bool CanLoad(string modDirectory);
    ModelPreviewScene Load(string modDirectory);
    ModelPreviewScene Load(string modDirectory, IReadOnlyDictionary<string, double> variantValues);
    void Invalidate(string modDirectory);
}

public sealed record ModelPreviewScene(
    IReadOnlyList<ModelPreviewMesh> Meshes,
    Vector3 Minimum,
    Vector3 Maximum,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ModelPreviewVariant> Variants,
    ModelPreviewDiagnostics Diagnostics)
{
    public int VertexCount => Meshes.Sum(mesh => mesh.Positions.Length);
    public int TriangleCount => Meshes.Sum(mesh => mesh.Indices.Length / 3);
}

public sealed record ModelPreviewVariant(
    string Key,
    string Variable,
    string SourceFile,
    double DefaultValue,
    double SelectedValue,
    IReadOnlyList<double> Values)
{
    public string DisplayName => $"{Variable.TrimStart('$')} · {Path.GetFileName(SourceFile)}";
}

public sealed record ModelPreviewDiagnostics(
    bool CacheHit,
    TimeSpan LoadDuration,
    int SourceFileCount,
    int TextureCount,
    int DownsampledTextureCount,
    long RetainedTextureBytes);

public sealed record ModelPreviewMesh(
    string Name,
    string SourceFile,
    Vector3[] Positions,
    Vector3[] Normals,
    Vector2[] TextureCoordinates,
    int[] Indices,
    ModelPreviewTexture? DiffuseTexture,
    ModelPreviewTexture? NormalTexture,
    ModelPreviewTexture? LightTexture,
    ModelPreviewTexture? MaterialTexture);

public sealed record ModelPreviewTexture(
    string SourceFile,
    int Width,
    int Height,
    byte[] Bgra32Pixels,
    bool HasTransparency)
{
    public int OriginalWidth { get; init; } = Width;
    public int OriginalHeight { get; init; } = Height;
    public bool IsDownsampled => Width != OriginalWidth || Height != OriginalHeight;
}

public sealed class ModelPreviewException(string message) : Exception(message);

/// <summary>
/// Rebuilds the split GPU buffers emitted by XXMI for ZZZ. The loader intentionally
/// supports the stable 40-byte position stream and the 20/24-byte texcoord streams;
/// unknown layouts are skipped instead of being guessed as a different game format.
/// </summary>
public sealed partial class ZzmiModelPreviewLoader : IModModelPreviewLoader
{
    private const int MaximumInspectedFiles = 6000;
    private const int MaximumIniFiles = 128;
    private const int MaximumVerticesPerStream = 1_000_000;
    private const int MaximumIndicesPerMesh = 6_000_000;
    internal const int MaximumPreviewTextureDimension = 1024;
    private const int MaximumCachedScenes = 8;
    private const long MaximumCachedSceneBytes = 256L * 1024 * 1024;

    private readonly object _cacheGate = new();
    private readonly Dictionary<SceneCacheKey, LinkedListNode<SceneCacheEntry>> _sceneCache = [];
    private readonly LinkedList<SceneCacheEntry> _sceneLru = [];
    private long _cachedSceneBytes;

    private static readonly IReadOnlyDictionary<string, double> EmptyVariantValues =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    private static readonly EnumerationOptions Enumeration = new()
    {
        RecurseSubdirectories = true,
        MaxRecursionDepth = 5,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        ReturnSpecialDirectories = false
    };

    public bool CanLoad(string modDirectory)
    {
        if (!Directory.Exists(modDirectory))
        {
            return false;
        }

        try
        {
            var hasIni = false;
            var hasPosition = false;
            var hasIndex = false;
            var inspected = 0;
            foreach (var file in Directory.EnumerateFiles(modDirectory, "*", Enumeration))
            {
                if (++inspected > MaximumInspectedFiles)
                {
                    break;
                }

                var name = Path.GetFileName(file);
                hasIni |= name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase);
                hasPosition |= name.EndsWith("Position.buf", StringComparison.OrdinalIgnoreCase);
                hasIndex |= name.EndsWith(".ib", StringComparison.OrdinalIgnoreCase);
                if (hasIni && hasPosition && hasIndex)
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    public ModelPreviewScene Load(string modDirectory) => Load(modDirectory, EmptyVariantValues);

    public ModelPreviewScene Load(
        string modDirectory,
        IReadOnlyDictionary<string, double> variantValues)
    {
        if (!Directory.Exists(modDirectory))
        {
            throw new ModelPreviewException("Mod 安装目录不存在，无法生成 3D 预览。");
        }

        ArgumentNullException.ThrowIfNull(variantValues);
        var stopwatch = Stopwatch.StartNew();
        var root = Path.GetFullPath(modDirectory);
        var iniPaths = EnumerateIniFiles(root).ToList();
        if (iniPaths.Count == 0)
        {
            throw new ModelPreviewException("没有找到包含模型资源声明的 INI 文件。");
        }

        var stamp = ComputeSourceStamp(root);
        var cacheKey = new SceneCacheKey(NormalizeRoot(root), stamp.Value, BuildSelectionKey(variantValues));
        if (TryGetCachedScene(cacheKey, stopwatch.Elapsed, out var cached))
        {
            return cached;
        }

        var meshes = new List<ModelPreviewMesh>();
        var warnings = new List<string>();
        var variants = new List<ModelPreviewVariant>();
        var textureCache = new Dictionary<string, ModelPreviewTexture?>(StringComparer.OrdinalIgnoreCase);
        foreach (var iniPath in iniPaths)
        {
            try
            {
                var document = ApplyVariantValues(ParseDocument(root, iniPath), variantValues);
                variants.AddRange(document.Variants.Select(variant => variant.Selected));
                LoadIni(document, meshes, warnings, textureCache);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException)
            {
                warnings.Add($"{Path.GetFileName(iniPath)}：{ex.Message}");
            }
        }

        if (meshes.Count == 0)
        {
            var detail = warnings.Count == 0 ? string.Empty : $" {warnings[0]}";
            throw new ModelPreviewException($"没有找到可安全解析的 ZZZ/XXMI 网格。{detail}".TrimEnd());
        }

        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        foreach (var position in meshes.SelectMany(mesh => mesh.Positions))
        {
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);
        }

        stopwatch.Stop();
        var textures = meshes
            .SelectMany(EnumerateTextures)
            .Where(texture => texture is not null)
            .Cast<ModelPreviewTexture>()
            .DistinctBy(texture => texture.SourceFile, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var scene = new ModelPreviewScene(
            meshes,
            minimum,
            maximum,
            warnings,
            variants,
            new ModelPreviewDiagnostics(
                false,
                stopwatch.Elapsed,
                stamp.FileCount,
                textures.Count,
                textures.Count(texture => texture.IsDownsampled),
                textures.Sum(texture => (long)texture.Bgra32Pixels.Length)));
        AddCachedScene(cacheKey, scene);
        return scene;
    }

    public void Invalidate(string modDirectory)
    {
        if (string.IsNullOrWhiteSpace(modDirectory))
        {
            return;
        }

        var root = NormalizeRoot(Path.GetFullPath(modDirectory));
        lock (_cacheGate)
        {
            foreach (var entry in _sceneLru.Where(entry => string.Equals(entry.Key.Root, root, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                RemoveCachedScene(entry.Key);
            }
        }
    }

    private static IEnumerable<string> EnumerateIniFiles(string root)
    {
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*.ini", Enumeration)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (++count > MaximumIniFiles)
            {
                yield break;
            }

            yield return path;
        }
    }

    private static void LoadIni(
        IniDocument document,
        List<ModelPreviewMesh> destination,
        List<string> warnings,
        Dictionary<string, ModelPreviewTexture?> textureCache)
    {
        var positionResources = document.Resources.Values
            .Where(resource => resource.FilePath.EndsWith("Position.buf", StringComparison.OrdinalIgnoreCase))
            .Where(resource => resource.Stride == 40 && File.Exists(resource.FilePath))
            .ToList();
        var indexResources = document.Resources.Values
            .Where(resource => resource.FilePath.EndsWith(".ib", StringComparison.OrdinalIgnoreCase))
            .Where(resource => File.Exists(resource.FilePath))
            .ToList();
        if (positionResources.Count == 0 || indexResources.Count == 0)
        {
            return;
        }

        var streams = new Dictionary<string, VertexStreams>(StringComparer.OrdinalIgnoreCase);
        var indices = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);
        var usedIndexResources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var declaredIndexResources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in document.Sections)
        {
            var declaredIndexResourceName = FindIndexResourceReference(section.Lines);
            if (declaredIndexResourceName is not null)
            {
                declaredIndexResources.Add(declaredIndexResourceName);
            }

            try
            {
                var indexResourceName = FindActiveIndexResourceReference(section, document.Constants);
                if (indexResourceName is null
                    || !document.Resources.TryGetValue(indexResourceName, out var indexResource)
                    || !indexResources.Contains(indexResource))
                {
                    continue;
                }

                var positionResource = MatchPositionResource(indexResource, positionResources);
                if (positionResource is null)
                {
                    warnings.Add($"{Path.GetFileName(indexResource.FilePath)}：没有匹配的 Position.buf。");
                    continue;
                }

                var vertexStreams = GetVertexStreams(document, positionResource, streams);
                var sourceIndices = GetIndices(indexResource, indices);
                var drawRanges = ParseActiveDrawRanges(section, document.Constants);
                var hasConditionalDraws = section.Lines.Any(line => DrawIndexedRegex().IsMatch(line));
                if (drawRanges.Count == 0 && hasConditionalDraws)
                {
                    continue;
                }

                var selectedIndices = SelectIndices(sourceIndices, drawRanges, vertexStreams.Positions.Length);
                if (selectedIndices.Length == 0)
                {
                    continue;
                }

                var includeInactiveBranches = drawRanges.Count == 0;
                var diffuseTexture = GetTexture(
                    section,
                    document,
                    warnings,
                    textureCache,
                    DiffuseBindingRegex(),
                    includeInactiveBranches);
                var lightTexture = GetTexture(
                    section,
                    document,
                    warnings,
                    textureCache,
                    LightMapBindingRegex(),
                    includeInactiveBranches);
                var normalTexture = GetTexture(
                    section,
                    document,
                    warnings,
                    textureCache,
                    NormalMapBindingRegex(),
                    includeInactiveBranches);
                var materialTexture = GetTexture(
                    section,
                    document,
                    warnings,
                    textureCache,
                    MaterialMapBindingRegex(),
                    includeInactiveBranches);
                destination.Add(CreateMesh(
                    indexResource.FilePath,
                    vertexStreams,
                    selectedIndices,
                    diffuseTexture,
                    normalTexture,
                    lightTexture,
                    materialTexture));
                usedIndexResources.Add(indexResource.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException)
            {
                warnings.Add($"{Path.GetFileName(document.IniPath)} [{section.Name}]：{ex.Message}");
            }
        }

        // Some simple mods bind an IB directly and never issue custom drawindexed ranges.
        // Rendering the full IB is deterministic in that case and keeps those mods previewable.
        foreach (var indexResource in indexResources.Where(resource =>
                     !usedIndexResources.Contains(resource.Name)
                     && !declaredIndexResources.Contains(resource.Name)))
        {
            try
            {
                var positionResource = MatchPositionResource(indexResource, positionResources);
                if (positionResource is null)
                {
                    continue;
                }

                var vertexStreams = GetVertexStreams(document, positionResource, streams);
                var sourceIndices = GetIndices(indexResource, indices);
                var selectedIndices = SelectIndices(sourceIndices, [], vertexStreams.Positions.Length);
                if (selectedIndices.Length > 0)
                {
                    destination.Add(CreateMesh(indexResource.FilePath, vertexStreams, selectedIndices, null, null, null, null));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException)
            {
                warnings.Add($"{Path.GetFileName(indexResource.FilePath)}：{ex.Message}");
            }
        }
    }

    private static ModelPreviewTexture? GetTexture(
        IniSection section,
        IniDocument document,
        List<string> warnings,
        Dictionary<string, ModelPreviewTexture?> cache,
        Regex bindingRegex,
        bool includeInactiveBranches = false)
    {
        var resourceName = includeInactiveBranches
            ? FindTextureResourceReference(section.Lines, bindingRegex)
            : FindActiveTextureResourceReference(section, document.Constants, bindingRegex);
        if (resourceName is null
            || !document.Resources.TryGetValue(resourceName, out var resource)
            || !resource.FilePath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(resource.FilePath))
        {
            return null;
        }

        if (cache.TryGetValue(resource.FilePath, out var cached))
        {
            return cached;
        }

        try
        {
            var texture = DdsTextureDecoder.Decode(resource.FilePath, MaximumPreviewTextureDimension);
            cache[resource.FilePath] = texture;
            return texture;
        }
        catch (InvalidDataException ex)
        {
            cache[resource.FilePath] = null;
            warnings.Add($"{Path.GetFileName(resource.FilePath)}：{ex.Message}");
            return null;
        }
    }

    private static ModelPreviewMesh CreateMesh(
        string indexPath,
        VertexStreams streams,
        IReadOnlyList<int> sourceIndices,
        ModelPreviewTexture? diffuseTexture,
        ModelPreviewTexture? normalTexture,
        ModelPreviewTexture? lightTexture,
        ModelPreviewTexture? materialTexture)
    {
        var remap = new Dictionary<int, int>();
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var textureCoordinates = new List<Vector2>();
        var indices = new int[sourceIndices.Count];
        for (var i = 0; i < sourceIndices.Count; i++)
        {
            var sourceIndex = sourceIndices[i];
            if (!remap.TryGetValue(sourceIndex, out var compactIndex))
            {
                compactIndex = positions.Count;
                remap[sourceIndex] = compactIndex;
                positions.Add(streams.Positions[sourceIndex]);
                normals.Add(streams.Normals[sourceIndex]);
                textureCoordinates.Add(streams.TextureCoordinates[sourceIndex]);
            }

            indices[i] = compactIndex;
        }

        return new ModelPreviewMesh(
            FriendlyMeshName(indexPath),
            indexPath,
            positions.ToArray(),
            normals.ToArray(),
            textureCoordinates.ToArray(),
            indices,
            diffuseTexture,
            normalTexture,
            lightTexture,
            materialTexture);
    }

    private static VertexStreams GetVertexStreams(
        IniDocument document,
        ResourceDefinition positionResource,
        Dictionary<string, VertexStreams> cache)
    {
        if (cache.TryGetValue(positionResource.Name, out var cached))
        {
            return cached;
        }

        var family = PositionFamily(positionResource.FilePath);
        var texcoordResource = document.Resources.Values.FirstOrDefault(resource =>
            string.Equals(
                Path.GetFileNameWithoutExtension(resource.FilePath),
                family + "Texcoord",
                StringComparison.OrdinalIgnoreCase));
        var result = ReadVertexStreams(positionResource, texcoordResource);
        cache[positionResource.Name] = result;
        return result;
    }

    private static uint[] GetIndices(
        ResourceDefinition resource,
        Dictionary<string, uint[]> cache)
    {
        if (cache.TryGetValue(resource.Name, out var cached))
        {
            return cached;
        }

        var data = File.ReadAllBytes(resource.FilePath);
        uint[] result;
        if (resource.Format?.Contains("R16_UINT", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (data.Length % 2 != 0)
            {
                throw new InvalidDataException($"{Path.GetFileName(resource.FilePath)} 的 R16 索引长度无效。");
            }

            result = new uint[data.Length / 2];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i * 2, 2));
            }
        }
        else if (resource.Format?.Contains("R32_UINT", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (data.Length % 4 != 0)
            {
                throw new InvalidDataException($"{Path.GetFileName(resource.FilePath)} 的 R32 索引长度无效。");
            }

            result = new uint[data.Length / 4];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 4, 4));
            }
        }
        else
        {
            throw new InvalidDataException($"{Path.GetFileName(resource.FilePath)} 缺少受支持的 R16/R32 索引格式声明。");
        }

        cache[resource.Name] = result;
        return result;
    }

    private static VertexStreams ReadVertexStreams(
        ResourceDefinition positionResource,
        ResourceDefinition? texcoordResource)
    {
        var positionData = File.ReadAllBytes(positionResource.FilePath);
        if (positionData.Length == 0 || positionData.Length % 40 != 0)
        {
            throw new InvalidDataException($"{Path.GetFileName(positionResource.FilePath)} 不是完整的 40 字节 ZZZ Position 流。");
        }

        var vertexCount = positionData.Length / 40;
        if (vertexCount > MaximumVerticesPerStream)
        {
            throw new InvalidDataException($"{Path.GetFileName(positionResource.FilePath)} 顶点数超过预览上限。");
        }

        var positions = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        for (var i = 0; i < vertexCount; i++)
        {
            var offset = i * 40;
            positions[i] = new Vector3(
                ReadSingle(positionData, offset),
                ReadSingle(positionData, offset + 4),
                ReadSingle(positionData, offset + 8));
            normals[i] = new Vector3(
                ReadSingle(positionData, offset + 12),
                ReadSingle(positionData, offset + 16),
                ReadSingle(positionData, offset + 20));
            if (!IsFinite(positions[i]) || !IsFinite(normals[i]))
            {
                throw new InvalidDataException($"{Path.GetFileName(positionResource.FilePath)} 包含非有限顶点数据。");
            }
        }

        var textureCoordinates = new Vector2[vertexCount];
        if (texcoordResource is not null && File.Exists(texcoordResource.FilePath)
            && texcoordResource.Stride is >= 8 and <= 64)
        {
            var texcoordData = File.ReadAllBytes(texcoordResource.FilePath);
            var stride = texcoordResource.Stride.Value;
            if (texcoordData.Length == checked(vertexCount * stride))
            {
                for (var i = 0; i < vertexCount; i++)
                {
                    var offset = (i * stride) + 4;
                    textureCoordinates[i] = new Vector2(
                        (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(texcoordData.AsSpan(offset, 2))),
                        (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(texcoordData.AsSpan(offset + 2, 2))));
                }
            }
        }

        return new VertexStreams(positions, normals, textureCoordinates);
    }

    private static int[] SelectIndices(
        IReadOnlyList<uint> source,
        IReadOnlyList<DrawRange> ranges,
        int vertexCount)
    {
        var requested = ranges.Count == 0 ? source.Count : ranges.Sum(range => range.Count);
        if (requested > MaximumIndicesPerMesh)
        {
            throw new InvalidDataException("索引数量超过单个预览网格上限。");
        }

        var selected = new List<int>(requested);
        if (ranges.Count == 0)
        {
            AppendRange(source, selected, 0, source.Count, 0, vertexCount);
        }
        else
        {
            foreach (var range in ranges)
            {
                AppendRange(source, selected, range.FirstIndex, range.Count, range.BaseVertex, vertexCount);
            }
        }

        return selected.ToArray();
    }

    private static void AppendRange(
        IReadOnlyList<uint> source,
        List<int> destination,
        int firstIndex,
        int count,
        int baseVertex,
        int vertexCount)
    {
        if (count <= 0 || count % 3 != 0 || firstIndex < 0 || firstIndex > source.Count - count)
        {
            throw new InvalidDataException("drawindexed 范围超出 IB 或不是完整三角形。");
        }

        for (var i = firstIndex; i < firstIndex + count; i++)
        {
            var value = (long)source[i] + baseVertex;
            if (value < 0 || value >= vertexCount)
            {
                throw new InvalidDataException("IB 引用了 Position 流之外的顶点。");
            }

            destination.Add((int)value);
        }
    }

    private static ResourceDefinition? MatchPositionResource(
        ResourceDefinition indexResource,
        IReadOnlyList<ResourceDefinition> positions)
    {
        var indexStem = Path.GetFileNameWithoutExtension(indexResource.FilePath);
        return positions
            .Select(resource => (Resource: resource, Family: PositionFamily(resource.FilePath)))
            .Where(item => indexStem.StartsWith(item.Family, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Family.Length)
            .Select(item => item.Resource)
            .FirstOrDefault();
    }

    private static string PositionFamily(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        return stem.EndsWith("Position", StringComparison.OrdinalIgnoreCase)
            ? stem[..^"Position".Length]
            : stem;
    }

    private static string FriendlyMeshName(string indexPath) => Path.GetFileNameWithoutExtension(indexPath);

    private static string? FindIndexResourceReference(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            var match = IndexBindingRegex().Match(line);
            if (match.Success)
            {
                return match.Groups["resource"].Value;
            }
        }

        return null;
    }

    private static string? FindActiveIndexResourceReference(
        IniSection section,
        IReadOnlyDictionary<string, double> constants)
    {
        foreach (var line in EnumerateActiveLines(section, constants))
        {
            var match = IndexBindingRegex().Match(line);
            if (match.Success)
            {
                return match.Groups["resource"].Value;
            }
        }

        return null;
    }

    private static string? FindActiveTextureResourceReference(
        IniSection section,
        IReadOnlyDictionary<string, double> constants,
        Regex bindingRegex)
    {
        foreach (var line in EnumerateActiveLines(section, constants))
        {
            var match = bindingRegex.Match(line);
            if (match.Success)
            {
                return match.Groups["resource"].Value;
            }
        }

        return null;
    }

    private static string? FindTextureResourceReference(IEnumerable<string> lines, Regex bindingRegex)
    {
        foreach (var line in lines)
        {
            var match = bindingRegex.Match(line);
            if (match.Success)
            {
                return match.Groups["resource"].Value;
            }
        }

        return null;
    }

    private static IEnumerable<ModelPreviewTexture?> EnumerateTextures(ModelPreviewMesh mesh)
    {
        yield return mesh.DiffuseTexture;
        yield return mesh.NormalTexture;
        yield return mesh.LightTexture;
        yield return mesh.MaterialTexture;
    }

    private static IReadOnlyList<DrawRange> ParseActiveDrawRanges(
        IniSection section,
        IReadOnlyDictionary<string, double> constants)
    {
        var result = new List<DrawRange>();
        foreach (var line in EnumerateActiveLines(section, constants))
        {
            var draw = DrawIndexedRegex().Match(line);
            if (draw.Success
                && int.TryParse(draw.Groups["count"].Value, CultureInfo.InvariantCulture, out var count)
                && int.TryParse(draw.Groups["first"].Value, CultureInfo.InvariantCulture, out var first)
                && int.TryParse(draw.Groups["base"].Value, CultureInfo.InvariantCulture, out var baseVertex))
            {
                result.Add(new DrawRange(count, first, baseVertex));
            }
        }

        return result;
    }

    private static IEnumerable<string> EnumerateActiveLines(
        IniSection section,
        IReadOnlyDictionary<string, double> constants)
    {
        var conditions = new Stack<ConditionFrame>();
        var active = true;
        foreach (var sourceLine in section.Lines)
        {
            var line = sourceLine.Trim();
            if (line.StartsWith("if ", StringComparison.OrdinalIgnoreCase))
            {
                var condition = EvaluateExpression(line[3..], constants);
                conditions.Push(new ConditionFrame(active, condition, active && condition));
                active = active && condition;
                continue;
            }

            if (line.StartsWith("else if ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("elif ", StringComparison.OrdinalIgnoreCase))
            {
                if (conditions.TryPop(out var frame))
                {
                    var expression = line.StartsWith("elif ", StringComparison.OrdinalIgnoreCase)
                        ? line[5..]
                        : line[8..];
                    var condition = EvaluateExpression(expression, constants);
                    var branchActive = frame.ParentActive && !frame.BranchTaken && condition;
                    conditions.Push(frame with { BranchTaken = frame.BranchTaken || condition, Active = branchActive });
                    active = branchActive;
                }

                continue;
            }

            if (line.Equals("else", StringComparison.OrdinalIgnoreCase))
            {
                if (conditions.TryPop(out var frame))
                {
                    var branchActive = frame.ParentActive && !frame.BranchTaken;
                    conditions.Push(frame with { BranchTaken = true, Active = branchActive });
                    active = branchActive;
                }

                continue;
            }

            if (line.StartsWith("endif", StringComparison.OrdinalIgnoreCase))
            {
                if (conditions.TryPop(out _))
                {
                    active = conditions.TryPeek(out var parent) ? parent.Active : true;
                }

                continue;
            }

            if (!active)
            {
                continue;
            }

            yield return line;
        }
    }

    private static bool EvaluateExpression(string expression, IReadOnlyDictionary<string, double> constants)
    {
        foreach (var alternative in OrRegex().Split(expression))
        {
            var all = true;
            foreach (var requirement in AndRegex().Split(alternative))
            {
                if (!EvaluateComparison(requirement.Trim().Trim('(', ')'), constants))
                {
                    all = false;
                    break;
                }
            }

            if (all)
            {
                return true;
            }
        }

        return false;
    }

    private static bool EvaluateComparison(string expression, IReadOnlyDictionary<string, double> constants)
    {
        var comparison = ComparisonRegex().Match(expression);
        if (!comparison.Success)
        {
            var variableExpression = VariableExpressionRegex().Match(expression);
            return variableExpression.Success
                ? ResolveVariable(variableExpression.Groups["variable"].Value, constants) != 0
                : expression.Equals("1", StringComparison.OrdinalIgnoreCase);
        }

        var variable = comparison.Groups["variable"].Value;
        var left = ResolveVariable(variable, constants);
        if (!double.TryParse(comparison.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var right))
        {
            return false;
        }

        return comparison.Groups["operator"].Value switch
        {
            "==" => left == right,
            "!=" => left != right,
            ">" => left > right,
            "<" => left < right,
            ">=" => left >= right,
            "<=" => left <= right,
            _ => false
        };
    }

    private static double ResolveVariable(string variable, IReadOnlyDictionary<string, double> constants) =>
        constants.TryGetValue(variable, out var configured)
            ? configured
            : variable.Contains("ZZZModManager", StringComparison.OrdinalIgnoreCase)
                || variable.Contains("ZZZMM", StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0;

    private static IniDocument ParseDocument(string root, string iniPath)
    {
        var sections = new List<IniSection>();
        var current = new IniSection(string.Empty, []);
        sections.Add(current);
        foreach (var line in ReadLines(iniPath))
        {
            var sectionMatch = SectionRegex().Match(line);
            if (sectionMatch.Success)
            {
                current = new IniSection(sectionMatch.Groups["name"].Value.Trim(), []);
                sections.Add(current);
            }
            else
            {
                current.Lines.Add(line);
            }
        }

        var resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase);
        var constants = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var persistentVariables = new List<PersistentVariable>();
        var persistentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in sections)
        {
            foreach (var line in section.Lines)
            {
                var constant = ConstantRegex().Match(line);
                if (constant.Success
                    && double.TryParse(constant.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    constants[constant.Groups["variable"].Value] = value;
                }

                var persistent = PersistentVariableRegex().Match(line);
                if (persistent.Success && persistentNames.Add(persistent.Groups["variable"].Value))
                {
                    var defaultValue = double.TryParse(
                        persistent.Groups["value"].Value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var parsedDefault)
                        ? parsedDefault
                        : 0;
                    persistentVariables.Add(new PersistentVariable(persistent.Groups["variable"].Value, defaultValue));
                    constants[persistent.Groups["variable"].Value] = defaultValue;
                }
            }

            if (!section.Name.StartsWith("Resource", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? filename = null;
            string? format = null;
            int? stride = null;
            foreach (var line in section.Lines)
            {
                var property = ResourcePropertyRegex().Match(line);
                if (!property.Success)
                {
                    continue;
                }

                var key = property.Groups["key"].Value;
                var rawValue = property.Groups["value"].Value.Trim().Trim('"');
                if (key.Equals("filename", StringComparison.OrdinalIgnoreCase))
                {
                    filename = rawValue;
                }
                else if (key.Equals("format", StringComparison.OrdinalIgnoreCase))
                {
                    format = rawValue;
                }
                else if (key.Equals("stride", StringComparison.OrdinalIgnoreCase)
                         && int.TryParse(rawValue, CultureInfo.InvariantCulture, out var parsedStride))
                {
                    stride = parsedStride;
                }
            }

            if (string.IsNullOrWhiteSpace(filename))
            {
                continue;
            }

            var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(iniPath)!, filename));
            if (!FileSystemSafety.IsWithin(root, path))
            {
                continue;
            }

            resources[section.Name] = new ResourceDefinition(section.Name, path, stride, format);
        }

        var variants = BuildVariantDefinitions(root, iniPath, sections, persistentVariables);
        return new IniDocument(iniPath, sections, resources, constants, variants);
    }

    private static IReadOnlyList<VariantDefinition> BuildVariantDefinitions(
        string root,
        string iniPath,
        IReadOnlyList<IniSection> sections,
        IReadOnlyList<PersistentVariable> persistentVariables)
    {
        if (persistentVariables.Count == 0)
        {
            return [];
        }

        var values = persistentVariables.ToDictionary(
            variable => variable.Name,
            variable => new List<double> { variable.DefaultValue },
            StringComparer.OrdinalIgnoreCase);
        var usedByPreviewBranch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in sections.SelectMany(section => section.Lines))
        {
            var cycle = CycleValuesRegex().Match(line);
            if (cycle.Success && values.TryGetValue(cycle.Groups["variable"].Value, out var cycleValues))
            {
                foreach (var rawValue in cycle.Groups["values"].Value.Split(','))
                {
                    if (double.TryParse(rawValue.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    {
                        AddUnique(cycleValues, parsed);
                    }
                }

                usedByPreviewBranch.Add(cycle.Groups["variable"].Value);
            }

            foreach (Match comparison in VariantComparisonValueRegex().Matches(line))
            {
                var variable = comparison.Groups["variable"].Value;
                if (!values.TryGetValue(variable, out var comparisonValues)
                    || !double.TryParse(
                        comparison.Groups["value"].Value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var parsed))
                {
                    continue;
                }

                AddUnique(comparisonValues, parsed);
                usedByPreviewBranch.Add(variable);
            }
        }

        var relativeIniPath = Path.GetRelativePath(root, iniPath).Replace('\\', '/');
        var result = new List<VariantDefinition>();
        foreach (var variable in persistentVariables)
        {
            var availableValues = values[variable.Name];
            if (!usedByPreviewBranch.Contains(variable.Name) || availableValues.Count < 2)
            {
                continue;
            }

            var key = $"{relativeIniPath}::{variable.Name.ToLowerInvariant()}";
            var selected = new ModelPreviewVariant(
                key,
                variable.Name,
                relativeIniPath,
                variable.DefaultValue,
                variable.DefaultValue,
                availableValues.ToArray());
            result.Add(new VariantDefinition(variable.Name, selected));
        }

        return result;
    }

    private static IniDocument ApplyVariantValues(
        IniDocument document,
        IReadOnlyDictionary<string, double> requestedValues)
    {
        var constants = new Dictionary<string, double>(document.Constants, StringComparer.OrdinalIgnoreCase);
        var variants = new List<VariantDefinition>(document.Variants.Count);
        foreach (var variant in document.Variants)
        {
            var selectedValue = variant.Selected.DefaultValue;
            if (requestedValues.TryGetValue(variant.Selected.Key, out var requested))
            {
                var matchingValue = variant.Selected.Values.FirstOrDefault(value => AreEqual(value, requested));
                if (variant.Selected.Values.Any(value => AreEqual(value, requested)))
                {
                    selectedValue = matchingValue;
                }
            }

            constants[variant.Variable] = selectedValue;
            variants.Add(variant with
            {
                Selected = variant.Selected with { SelectedValue = selectedValue }
            });
        }

        return document with { Constants = constants, Variants = variants };
    }

    private static void AddUnique(List<double> values, double value)
    {
        if (!values.Any(existing => AreEqual(existing, value)))
        {
            values.Add(value);
        }
    }

    private static bool AreEqual(double left, double right) =>
        Math.Abs(left - right) <= 0.000001 * Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));

    private static SourceStamp ComputeSourceStamp(string root)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offsetBasis;
        var sourceFileCount = 0;
        var inspected = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", Enumeration)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (++inspected > MaximumInspectedFiles)
            {
                break;
            }

            if (!IsPreviewSourceFile(path))
            {
                continue;
            }

            var file = new FileInfo(path);
            AddHashValue(ref hash, Path.GetRelativePath(root, path), prime);
            AddHashValue(ref hash, file.Length, prime);
            AddHashValue(ref hash, file.LastWriteTimeUtc.Ticks, prime);
            sourceFileCount++;
        }

        return new SourceStamp(hash, sourceFileCount);
    }

    private static bool IsPreviewSourceFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".ini", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".buf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ib", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".dds", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddHashValue(ref ulong hash, string value, ulong prime)
    {
        foreach (var character in value)
        {
            hash ^= character;
            hash *= prime;
        }

        hash ^= 0xFF;
        hash *= prime;
    }

    private static void AddHashValue(ref ulong hash, long value, ulong prime)
    {
        for (var shift = 0; shift < 64; shift += 8)
        {
            hash ^= (byte)(value >> shift);
            hash *= prime;
        }
    }

    private static string BuildSelectionKey(IReadOnlyDictionary<string, double> variantValues) =>
        string.Join(
            ';',
            variantValues
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => $"{item.Key}={item.Value.ToString("R", CultureInfo.InvariantCulture)}"));

    private static string NormalizeRoot(string root) =>
        root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();

    private bool TryGetCachedScene(SceneCacheKey key, TimeSpan lookupDuration, out ModelPreviewScene scene)
    {
        lock (_cacheGate)
        {
            if (!_sceneCache.TryGetValue(key, out var node))
            {
                scene = null!;
                return false;
            }

            _sceneLru.Remove(node);
            _sceneLru.AddFirst(node);
            scene = node.Value.Scene with
            {
                Diagnostics = node.Value.Scene.Diagnostics with
                {
                    CacheHit = true,
                    LoadDuration = lookupDuration
                }
            };
            return true;
        }
    }

    private void AddCachedScene(SceneCacheKey key, ModelPreviewScene scene)
    {
        var estimatedBytes = EstimateSceneBytes(scene);
        if (estimatedBytes > MaximumCachedSceneBytes)
        {
            return;
        }

        lock (_cacheGate)
        {
            foreach (var stale in _sceneLru
                         .Where(entry => string.Equals(entry.Key.Root, key.Root, StringComparison.OrdinalIgnoreCase)
                             && entry.Key.SourceStamp != key.SourceStamp)
                         .ToList())
            {
                RemoveCachedScene(stale.Key);
            }

            RemoveCachedScene(key);
            var entry = new SceneCacheEntry(key, scene, estimatedBytes);
            var node = _sceneLru.AddFirst(entry);
            _sceneCache[key] = node;
            _cachedSceneBytes += estimatedBytes;
            while (_sceneLru.Count > MaximumCachedScenes || _cachedSceneBytes > MaximumCachedSceneBytes)
            {
                var oldest = _sceneLru.Last;
                if (oldest is null)
                {
                    break;
                }

                RemoveCachedScene(oldest.Value.Key);
            }
        }
    }

    private void RemoveCachedScene(SceneCacheKey key)
    {
        if (!_sceneCache.Remove(key, out var node))
        {
            return;
        }

        _sceneLru.Remove(node);
        _cachedSceneBytes -= node.Value.EstimatedBytes;
    }

    private static long EstimateSceneBytes(ModelPreviewScene scene)
    {
        var geometryBytes = scene.Meshes.Sum(mesh =>
            ((long)mesh.Positions.Length * 12)
            + ((long)mesh.Normals.Length * 12)
            + ((long)mesh.TextureCoordinates.Length * 8)
            + ((long)mesh.Indices.Length * sizeof(int)));
        var textureBytes = scene.Meshes
            .SelectMany(EnumerateTextures)
            .Where(texture => texture is not null)
            .Cast<ModelPreviewTexture>()
            .DistinctBy(texture => texture.SourceFile, StringComparer.OrdinalIgnoreCase)
            .Sum(texture => (long)texture.Bgra32Pixels.Length);
        return checked(geometryBytes + textureBytes);
    }

    private static IReadOnlyList<string> ReadLines(string path)
    {
        try
        {
            return File.ReadAllLines(path, new UTF8Encoding(false, true));
        }
        catch (DecoderFallbackException)
        {
            return File.ReadAllLines(path, Encoding.Default);
        }
    }

    private static float ReadSingle(byte[] data, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)));

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private sealed record IniDocument(
        string IniPath,
        IReadOnlyList<IniSection> Sections,
        IReadOnlyDictionary<string, ResourceDefinition> Resources,
        IReadOnlyDictionary<string, double> Constants,
        IReadOnlyList<VariantDefinition> Variants);

    private sealed record IniSection(string Name, List<string> Lines);
    private sealed record ResourceDefinition(string Name, string FilePath, int? Stride, string? Format);
    private sealed record PersistentVariable(string Name, double DefaultValue);
    private sealed record VariantDefinition(string Variable, ModelPreviewVariant Selected);
    private sealed record VertexStreams(Vector3[] Positions, Vector3[] Normals, Vector2[] TextureCoordinates);
    private sealed record SceneCacheEntry(SceneCacheKey Key, ModelPreviewScene Scene, long EstimatedBytes);
    private readonly record struct SceneCacheKey(string Root, ulong SourceStamp, string SelectionKey);
    private readonly record struct SourceStamp(ulong Value, int FileCount);
    private readonly record struct DrawRange(int Count, int FirstIndex, int BaseVertex);
    private readonly record struct ConditionFrame(bool ParentActive, bool BranchTaken, bool Active);

    [GeneratedRegex(@"^\s*\[(?<name>[^\]]+)\]\s*$")]
    private static partial Regex SectionRegex();

    [GeneratedRegex(@"^\s*(?<key>filename|stride|format)\s*=\s*(?<value>[^;#]+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ResourcePropertyRegex();

    [GeneratedRegex(@"^\s*(?:global\s+)?(?:persist\s+)?(?<variable>\$[A-Za-z0-9_]+)\s*=\s*(?<value>-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ConstantRegex();

    [GeneratedRegex(@"^\s*global\s+persist\s+(?<variable>\$[A-Za-z0-9_]+)(?:\s*=\s*(?<value>-?\d+(?:\.\d+)?))?\s*(?:[;#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex PersistentVariableRegex();

    [GeneratedRegex(@"^\s*(?<variable>\$[A-Za-z0-9_]+)\s*=\s*(?<values>-?\d+(?:\.\d+)?(?:\s*,\s*-?\d+(?:\.\d+)?)+)\s*(?:[;#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex CycleValuesRegex();

    [GeneratedRegex(@"(?<variable>\$[A-Za-z0-9_]+)\s*(?:==|!=|>=|<=|>|<)\s*(?<value>-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex VariantComparisonValueRegex();

    [GeneratedRegex(@"^\s*ib\s*=\s*(?<resource>Resource[A-Za-z0-9_.-]+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex IndexBindingRegex();

    [GeneratedRegex(@"^\s*Resource\\ZZMI\\Diffuse\s*=\s*ref\s+(?<resource>Resource[A-Za-z0-9_.-]+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DiffuseBindingRegex();

    [GeneratedRegex(@"^\s*Resource\\ZZMI\\LightMap\s*=\s*ref\s+(?<resource>Resource[A-Za-z0-9_.-]+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex LightMapBindingRegex();

    [GeneratedRegex(@"^\s*Resource\\ZZMI\\NormalMap\s*=\s*ref\s+(?<resource>Resource[A-Za-z0-9_.-]+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex NormalMapBindingRegex();

    [GeneratedRegex(@"^\s*Resource\\ZZMI\\MaterialMap\s*=\s*ref\s+(?<resource>Resource[A-Za-z0-9_.-]+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex MaterialMapBindingRegex();

    [GeneratedRegex(@"^\s*drawindexed\s*=\s*(?<count>\d+)\s*,\s*(?<first>\d+)\s*,\s*(?<base>-?\d+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DrawIndexedRegex();

    [GeneratedRegex(@"\s*\|\|\s*")]
    private static partial Regex OrRegex();

    [GeneratedRegex(@"\s*&&\s*")]
    private static partial Regex AndRegex();

    [GeneratedRegex(@"^(?<variable>\$(?:[A-Za-z0-9_.-]+|\\[A-Za-z0-9_.-]+(?:\\[A-Za-z0-9_.-]+)*))\s*(?<operator>==|!=|>=|<=|>|<)\s*(?<value>-?\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex ComparisonRegex();

    [GeneratedRegex(@"^(?<variable>\$(?:[A-Za-z0-9_.-]+|\\[A-Za-z0-9_.-]+(?:\\[A-Za-z0-9_.-]+)*))$", RegexOptions.IgnoreCase)]
    private static partial Regex VariableExpressionRegex();
}
