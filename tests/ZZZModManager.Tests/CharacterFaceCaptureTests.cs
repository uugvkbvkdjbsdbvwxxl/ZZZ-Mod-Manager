using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Xunit;
using ZZZModManager.Infrastructure;
using ZZZModManager.Services;

namespace ZZZModManager.Tests;

public sealed class CharacterFaceCaptureTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "zzz-mm-face-capture-tests",
        Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly string _gameExecutable;

    public CharacterFaceCaptureTests()
    {
        _paths = new AppPaths(_root);
        _paths.Ensure();
        var gameDirectory = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameDirectory);
        _gameExecutable = Path.Combine(gameDirectory, "ZenlessZoneZero.exe");
        File.WriteAllBytes(_gameExecutable, []);
        File.WriteAllText(Path.Combine(gameDirectory, "version_info"), "CNPRODWin3.1.0", Encoding.ASCII);
    }

    [Fact]
    public void RemielleFrameDumpImportsToVersionedCacheAndMergesWithPreview()
    {
        var mod = CreateRecognizedMod();
        var capture = CreateFaceCapture("FrameAnalysis-2026-08-25-120000");
        var originalIni = File.ReadAllBytes(Path.Combine(mod, "Remielle2.ini"));
        var service = NewService();

        var before = service.GetStatus(mod);
        var result = service.Import(mod, capture);
        var after = service.GetStatus(mod);

        Assert.True(before.IsRecognized);
        Assert.False(before.HasCache);
        Assert.Equal("RemielleMoonlight", result.ProfileId);
        Assert.Equal("CNPRODWin3.1.0", result.GameVersion);
        Assert.Equal(1, result.MeshCount);
        Assert.True(after.HasCache);
        Assert.Equal(1, after.MeshCount);
        Assert.StartsWith(_paths.CharacterFaceCacheRoot, result.CacheDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.False(FileSystemSafety.IsWithin(_paths.ModsRoot, result.CacheDirectory));
        Assert.Equal(originalIni, File.ReadAllBytes(Path.Combine(mod, "Remielle2.ini")));

        var cacheScene = new ZzmiModelPreviewLoader().Load(result.CacheDirectory);
        var face = Assert.Single(cacheScene.Meshes);
        Assert.Equal(3, face.Positions.Length);
        Assert.Equal(3, face.Indices.Length);
        Assert.NotNull(face.DiffuseTexture);

        var merged = service.MergeCached(mod, EmptyScene());
        var mergedFace = Assert.Single(merged.Meshes);
        Assert.Contains("原始头脸", mergedFace.Name, StringComparison.Ordinal);
        Assert.Contains("脸部", mergedFace.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportRejectsDumpWithoutRequiredFaceDrawAndLeavesNoCache()
    {
        var mod = CreateRecognizedMod();
        var capture = Path.Combine(_root, "FrameAnalysis-empty");
        Directory.CreateDirectory(capture);
        File.WriteAllBytes(Path.Combine(capture, "000001-ib=789ae812.buf"), [0, 0, 0, 0]);
        var service = NewService();

        var error = Assert.Throws<InvalidDataException>(() => service.Import(mod, capture));

        Assert.Contains("脸部", error.Message, StringComparison.Ordinal);
        Assert.False(service.GetStatus(mod).HasCache);
        Assert.Empty(Directory.EnumerateDirectories(
            Path.Combine(_paths.CharacterFaceCacheRoot, "RemielleMoonlight"),
            ".importing-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void LatestFrameAnalysisUsesNewestRuntimeDirectory()
    {
        var older = Path.Combine(_paths.RuntimeRoot, "FrameAnalysis-older");
        var newer = Path.Combine(_paths.RuntimeRoot, "FrameAnalysis-newer");
        Directory.CreateDirectory(older);
        Directory.CreateDirectory(newer);
        Directory.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-5));
        Directory.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

        var latest = NewService().FindLatestFrameAnalysis();

        Assert.Equal(newer, latest, ignoreCase: true);
    }

    [Fact]
    public void SafeCapturePreparationRemovesBroadDumpsBacksUpAndIsIdempotent()
    {
        var mod = CreateRecognizedMod();
        var d3dx = Path.Combine(_paths.RuntimeRoot, "d3dx.ini");
        var original = "[Hunting]\r\nhunting = 0\r\nanalyse_options = dump_rt dump_tex dump_cb dump_vb dump_ib buf txt\r\n";
        File.WriteAllText(d3dx, original, new UTF8Encoding(false));
        var service = NewService();

        var prepared = service.PrepareSafeCapture(mod);
        var second = service.PrepareSafeCapture(mod);

        Assert.True(prepared.Changed);
        Assert.NotNull(prepared.BackupPath);
        Assert.Equal(original, File.ReadAllText(prepared.BackupPath!));
        var updated = File.ReadAllText(d3dx);
        Assert.Contains("analyse_options = deferred_ctx_accurate dump_tex dump_vb dump_ib buf txt", updated, StringComparison.Ordinal);
        Assert.Contains("hunting = 1", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("dump_rt", updated, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dump_cb", updated, StringComparison.OrdinalIgnoreCase);
        Assert.False(second.Changed);
        Assert.Null(second.BackupPath);
        Assert.Equal("必须完全退出并重新启动游戏", second.ActivationInstruction);
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(_paths.BackupsRoot, "FrameAnalysis"),
            "d3dx-*.ini",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void FloatTexcoordsAreConvertedAndHeadLocalAxesAlignToTheMod()
    {
        var mod = CreateRecognizedMod();
        var capture = Path.Combine(_root, "FrameAnalysis-float-uv");
        Directory.CreateDirectory(capture);
        WriteLocalFacePositionBuffer(Path.Combine(capture, "000321-vb0=facepos1.buf"));
        WriteFloatTexcoordBuffer(Path.Combine(capture, "000321-vb1=faceuv01.buf"));
        WriteIndexBuffer(Path.Combine(capture, "000321-ib=7fbbcf0d.buf"));
        WriteBgraDds(Path.Combine(capture, "000321-ps-t0=baf9e1be.dds"));
        var service = NewService();

        service.Import(mod, capture);
        var merged = service.MergeCached(mod, SceneWithBody());

        var face = Assert.Single(merged.Meshes, mesh => mesh.Name.Contains("原始头脸", StringComparison.Ordinal));
        Assert.InRange(face.Positions.Max(position => position.Z), 1.949f, 1.951f);
        Assert.InRange(face.Positions.Min(position => position.Z), 1.749f, 1.751f);
        Assert.Equal(0.25f, face.TextureCoordinates[0].X, 3);
        Assert.Equal(0.75f, face.TextureCoordinates[0].Y, 3);
    }

    private CharacterFaceCaptureService NewService() =>
        new(_paths, new JsonFileStore(), () => _gameExecutable);

    private string CreateRecognizedMod()
    {
        var directory = Path.Combine(_paths.ModsRoot, "remielle-fixture");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "Remielle2.ini"),
            """
            [TextureOverrideBody]
            hash = f57f3e40

            [TextureOverrideLegs]
            hash = 09a51ed3
            """,
            Encoding.UTF8);
        return directory;
    }

    private string CreateFaceCapture(string name)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        WritePositionBuffer(Path.Combine(directory, "000123-vb0=dynamicface.buf"));
        WriteTexcoordBuffer(Path.Combine(directory, "000123-vb1=dynamicuv.buf"));
        WriteIndexBuffer(Path.Combine(directory, "000123-ib=7fbbcf0d.buf"));
        WriteBgraDds(Path.Combine(directory, "000123-ps-t0=baf9e1be.dds"));
        WritePositionBuffer(Path.Combine(directory, "000124-vb0=dynamicface.buf"));
        WriteTexcoordBuffer(Path.Combine(directory, "000124-vb1=dynamicuv.buf"));
        WriteIndexBuffer(Path.Combine(directory, "000124-ib=7fbbcf0d.buf"));
        WriteBgraDds(Path.Combine(directory, "000124-ps-t0=baf9e1be.dds"));
        return directory;
    }

    private static void WritePositionBuffer(string path)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        WriteVertex(writer, new Vector3(-0.2f, 1.6f, 0));
        WriteVertex(writer, new Vector3(0.2f, 1.6f, 0));
        WriteVertex(writer, new Vector3(0, 2f, 0));
    }

    private static void WriteVertex(BinaryWriter writer, Vector3 position)
    {
        writer.Write(position.X);
        writer.Write(position.Y);
        writer.Write(position.Z);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(1f);
        writer.Write(new byte[16]);
    }

    private static void WriteTexcoordBuffer(string path)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        for (var vertex = 0; vertex < 3; vertex++)
        {
            writer.Write(0u);
            writer.Write(BitConverter.HalfToUInt16Bits((Half)(vertex / 2f)));
            writer.Write(BitConverter.HalfToUInt16Bits((Half)(vertex % 2)));
            writer.Write(new byte[12]);
        }
    }

    private static void WriteLocalFacePositionBuffer(string path)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        WriteVertex(writer, new Vector3(-0.2f, 0, -0.1f));
        WriteVertex(writer, new Vector3(-0.2f, 0, 0.1f));
        WriteVertex(writer, Vector3.Zero);
    }

    private static void WriteFloatTexcoordBuffer(string path)
    {
        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            for (var vertex = 0; vertex < 3; vertex++)
            {
                writer.Write(new byte[16]);
                writer.Write(vertex == 0 ? 0.25f : 0.5f);
                writer.Write(vertex == 0 ? 0.75f : 0.5f);
                writer.Write(new byte[24]);
            }
        }

        File.WriteAllText(
            Path.ChangeExtension(path, ".txt"),
            """
            stride: 48
            element[0]:
              SemanticName: TEXCOORD
              SemanticIndex: 0
              Format: R32G32_FLOAT
              InputSlot: 1
              AlignedByteOffset: 16
              InputSlotClass: per-vertex
              InstanceDataStepRate: 0
            """,
            Encoding.UTF8);
    }

    private static void WriteIndexBuffer(string path)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(0u);
        writer.Write(1u);
        writer.Write(2u);
    }

    private static void WriteBgraDds(string path)
    {
        var data = new byte[132];
        "DDS "u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(76, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(88, 4), 32);
        data[128] = 80;
        data[129] = 120;
        data[130] = 240;
        data[131] = 255;
        File.WriteAllBytes(path, data);
    }

    private static ModelPreviewScene EmptyScene() => new(
        [],
        Vector3.Zero,
        Vector3.Zero,
        [],
        [],
        new ModelPreviewDiagnostics(false, TimeSpan.Zero, 0, 0, 0, 0));

    private static ModelPreviewScene SceneWithBody()
    {
        var positions = new[]
        {
            new Vector3(-0.5f, -0.2f, 0),
            new Vector3(0.5f, 0.2f, 2),
            new Vector3(0, 0, 1)
        };
        var mesh = new ModelPreviewMesh(
            "Body",
            "fixture",
            positions,
            [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            [Vector2.Zero, Vector2.Zero, Vector2.Zero],
            [0, 1, 2],
            null,
            null,
            null,
            null);
        return new ModelPreviewScene(
            [mesh],
            new Vector3(-0.5f, -0.2f, 0),
            new Vector3(0.5f, 0.2f, 2),
            [],
            [],
            new ModelPreviewDiagnostics(false, TimeSpan.Zero, 0, 0, 0, 0));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
