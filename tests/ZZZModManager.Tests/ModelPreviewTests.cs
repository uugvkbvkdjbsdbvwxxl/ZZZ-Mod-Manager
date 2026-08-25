using System.Numerics;
using ZZZModManager.Services;
using Xunit;

namespace ZZZModManager.Tests;

public sealed class ModelPreviewTests
{
    [Fact]
    public void LoaderRebuildsKnownZzzStreamsAndHonoursDefaultDrawBranch()
    {
        using var temp = new TemporaryDirectory();
        WritePositionBuffer(Path.Combine(temp.Path, "FixtureBodyPosition.buf"));
        WriteTexcoordBuffer(Path.Combine(temp.Path, "FixtureBodyTexcoord.buf"));
        WriteUInt32Buffer(Path.Combine(temp.Path, "FixtureBodyA.ib"), [0, 1, 2, 0, 2, 1]);
        WriteBc7Dds(Path.Combine(temp.Path, "FixtureBodyADiffuse.dds"), 10, 20, 30, 128);
        File.WriteAllText(Path.Combine(temp.Path, "Fixture.ini"), """
            [Constants]
            global persist $Variant = 1

            [TextureOverrideFixtureBodyA]
            ib = ResourceFixtureBodyAIB
            Resource\ZZMI\Diffuse = ref ResourceFixtureBodyADiffuse
            if $Variant == 0
                drawindexed = 3, 0, 0
            else
                drawindexed = 3, 3, 0
            endif

            [ResourceFixtureBodyPosition]
            type = Buffer
            stride = 40
            filename = FixtureBodyPosition.buf

            [ResourceFixtureBodyTexcoord]
            type = Buffer
            stride = 20
            filename = FixtureBodyTexcoord.buf

            [ResourceFixtureBodyAIB]
            type = Buffer
            format = DXGI_FORMAT_R32_UINT
            filename = FixtureBodyA.ib

            [ResourceFixtureBodyADiffuse]
            filename = FixtureBodyADiffuse.dds
            """, Encoding.UTF8);

        var loader = new ZzmiModelPreviewLoader();

        Assert.True(loader.CanLoad(temp.Path));
        var scene = loader.Load(temp.Path);

        var mesh = Assert.Single(scene.Meshes);
        Assert.Equal("FixtureBodyA", mesh.Name);
        Assert.Equal([0, 1, 2], mesh.Indices);
        Assert.Equal(3, mesh.Positions.Length);
        Assert.Equal(new Vector3(0, 0, 0), mesh.Positions[0]);
        Assert.Equal(new Vector3(0, 0, 1), mesh.Normals[0]);
        Assert.Equal(new Vector2(0.25f, 0.75f), mesh.TextureCoordinates[0]);
        Assert.True(mesh.DiffuseTexture is not null, string.Join(" | ", scene.Warnings));
        var texture = Assert.IsType<ModelPreviewTexture>(mesh.DiffuseTexture);
        Assert.Equal(4, texture.Width);
        Assert.Equal(4, texture.Height);
        Assert.True(texture.HasTransparency);
        Assert.Equal([30, 20, 10, 128], texture.Bgra32Pixels[..4]);
        Assert.Equal(1, scene.TriangleCount);
        Assert.Empty(scene.Warnings);
    }

    [Fact]
    public void LoaderRejectsIndexOutsideThePositionStream()
    {
        using var temp = new TemporaryDirectory();
        WritePositionBuffer(Path.Combine(temp.Path, "FixturePosition.buf"));
        WriteUInt32Buffer(Path.Combine(temp.Path, "FixtureA.ib"), [0, 1, 99]);
        File.WriteAllText(Path.Combine(temp.Path, "Fixture.ini"), """
            [ResourceFixturePosition]
            type = Buffer
            stride = 40
            filename = FixturePosition.buf

            [ResourceFixtureAIB]
            type = Buffer
            format = DXGI_FORMAT_R32_UINT
            filename = FixtureA.ib
            """, Encoding.UTF8);

        var error = Assert.Throws<ModelPreviewException>(() => new ZzmiModelPreviewLoader().Load(temp.Path));

        Assert.Contains("Position 流之外", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoaderUsesInactiveVariantTextureForTheFullIbFallback()
    {
        using var temp = new TemporaryDirectory();
        WritePositionBuffer(Path.Combine(temp.Path, "FixtureWeaponPosition.buf"));
        WriteTexcoordBuffer(Path.Combine(temp.Path, "FixtureWeaponTexcoord.buf"));
        WriteUInt32Buffer(Path.Combine(temp.Path, "FixtureWeaponA.ib"), [0, 1, 2]);
        WriteBc7Dds(Path.Combine(temp.Path, "FixtureWeaponADiffuse.dds"), 40, 60, 80, 200);
        File.WriteAllText(Path.Combine(temp.Path, "Fixture.ini"), """
            [Constants]
            global persist $Weapon = 0

            [TextureOverrideFixtureWeaponA]
            if $Weapon == 1
                ib = ResourceFixtureWeaponAIB
                Resource\ZZMI\Diffuse = ref ResourceFixtureWeaponADiffuse
                drawindexed = 3, 0, 0
            endif

            [ResourceFixtureWeaponPosition]
            type = Buffer
            stride = 40
            filename = FixtureWeaponPosition.buf

            [ResourceFixtureWeaponTexcoord]
            type = Buffer
            stride = 20
            filename = FixtureWeaponTexcoord.buf

            [ResourceFixtureWeaponAIB]
            type = Buffer
            format = DXGI_FORMAT_R32_UINT
            filename = FixtureWeaponA.ib

            [ResourceFixtureWeaponADiffuse]
            filename = FixtureWeaponADiffuse.dds
            """, Encoding.UTF8);

        var mesh = Assert.Single(new ZzmiModelPreviewLoader().Load(temp.Path).Meshes);

        var texture = Assert.IsType<ModelPreviewTexture>(mesh.DiffuseTexture);
        Assert.True(texture.HasTransparency);
        Assert.Equal([80, 60, 40, 200], texture.Bgra32Pixels[..4]);
    }

    private static void WritePositionBuffer(string path)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        WriteVertex(writer, new Vector3(0, 0, 0));
        WriteVertex(writer, new Vector3(1, 0, 0));
        WriteVertex(writer, new Vector3(0, 1, 0));
    }

    private static void WriteVertex(BinaryWriter writer, Vector3 position)
    {
        writer.Write(position.X);
        writer.Write(position.Y);
        writer.Write(position.Z);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(1f);
        writer.Write(1f);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(1f);
    }

    private static void WriteTexcoordBuffer(string path)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        for (var i = 0; i < 3; i++)
        {
            writer.Write((byte)128);
            writer.Write((byte)128);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write(BitConverter.HalfToUInt16Bits((Half)0.25f));
            writer.Write(BitConverter.HalfToUInt16Bits((Half)0.75f));
            writer.Write(0f);
            writer.Write(0f);
            writer.Write(BitConverter.HalfToUInt16Bits((Half)0.5f));
            writer.Write(BitConverter.HalfToUInt16Bits((Half)0.5f));
        }
    }

    private static void WriteUInt32Buffer(string path, IReadOnlyList<uint> values)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        foreach (var value in values)
        {
            writer.Write(value);
        }
    }

    private static void WriteBc7Dds(string path, byte red, byte green, byte blue, byte alpha)
    {
        if (((red | green | blue | alpha) & 1) != 0)
        {
            throw new ArgumentException("Mode 6 fixture channels must share an even p-bit.");
        }

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("DDS "));
        writer.Write(124u);
        writer.Write(0x00081007u);
        writer.Write(4u);
        writer.Write(4u);
        writer.Write(16u);
        writer.Write(0u);
        writer.Write(0u);
        for (var i = 0; i < 11; i++)
        {
            writer.Write(0u);
        }

        writer.Write(32u);
        writer.Write(4u);
        writer.Write(Encoding.ASCII.GetBytes("DX10"));
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0x1000u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(99u);
        writer.Write(3u);
        writer.Write(0u);
        writer.Write(1u);
        writer.Write(0u);

        var block = new byte[16];
        var bitOffset = 0;
        WriteBits(block, ref bitOffset, 1u << 6, 7);
        WriteBits(block, ref bitOffset, (uint)(red >> 1), 7);
        WriteBits(block, ref bitOffset, (uint)(red >> 1), 7);
        WriteBits(block, ref bitOffset, (uint)(green >> 1), 7);
        WriteBits(block, ref bitOffset, (uint)(green >> 1), 7);
        WriteBits(block, ref bitOffset, (uint)(blue >> 1), 7);
        WriteBits(block, ref bitOffset, (uint)(blue >> 1), 7);
        WriteBits(block, ref bitOffset, (uint)(alpha >> 1), 7);
        WriteBits(block, ref bitOffset, (uint)(alpha >> 1), 7);
        WriteBits(block, ref bitOffset, 0, 1);
        WriteBits(block, ref bitOffset, 0, 1);
        WriteBits(block, ref bitOffset, 0, 3);
        for (var i = 1; i < 16; i++)
        {
            WriteBits(block, ref bitOffset, 0, 4);
        }

        Assert.Equal(128, bitOffset);
        writer.Write(block);
    }

    private static void WriteBits(byte[] destination, ref int bitOffset, uint value, int count)
    {
        for (var bit = 0; bit < count; bit++, bitOffset++)
        {
            if ((value & (1u << bit)) != 0)
            {
                destination[bitOffset / 8] |= (byte)(1 << (bitOffset % 8));
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ZZZModManager.Tests",
            Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
