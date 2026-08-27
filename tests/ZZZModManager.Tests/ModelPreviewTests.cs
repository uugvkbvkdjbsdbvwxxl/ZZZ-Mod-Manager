using System.Numerics;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
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
    public void LoaderOmitsInactiveConditionalMeshAndLoadsItWhenSelected()
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

        var loader = new ZzmiModelPreviewLoader();

        Assert.Throws<ModelPreviewException>(() => loader.Load(temp.Path));
        var scene = loader.Load(
            temp.Path,
            new Dictionary<string, double> { ["Fixture.ini::$weapon"] = 1 });
        var mesh = Assert.Single(scene.Meshes);

        var texture = Assert.IsType<ModelPreviewTexture>(mesh.DiffuseTexture);
        Assert.True(texture.HasTransparency);
        Assert.Equal([80, 60, 40, 200], texture.Bgra32Pixels[..4]);
        var variant = Assert.Single(scene.Variants);
        Assert.Equal("$Weapon", variant.Variable);
        Assert.Equal(1, variant.SelectedValue);
    }

    [Fact]
    public void LoaderAppliesIndependentVariantsWithoutRetainingPreviousMeshes()
    {
        using var temp = new TemporaryDirectory();
        var bodyIndexPath = Path.Combine(temp.Path, "FixtureBodyA.ib");
        WritePositionBuffer(Path.Combine(temp.Path, "FixtureBodyPosition.buf"));
        WriteTexcoordBuffer(Path.Combine(temp.Path, "FixtureBodyTexcoord.buf"));
        WriteUInt32Buffer(bodyIndexPath, [0, 1, 2, 0, 2, 1]);
        WriteBc7Dds(Path.Combine(temp.Path, "FixtureBodyDiffuse.dds"), 20, 40, 60, 254);
        WritePositionBuffer(Path.Combine(temp.Path, "FixtureWeaponPosition.buf"));
        WriteTexcoordBuffer(Path.Combine(temp.Path, "FixtureWeaponTexcoord.buf"));
        WriteUInt32Buffer(Path.Combine(temp.Path, "FixtureWeaponA.ib"), [0, 1, 2]);
        WriteBc7Dds(Path.Combine(temp.Path, "FixtureWeaponDiffuse.dds"), 80, 100, 120, 254);
        File.WriteAllText(Path.Combine(temp.Path, "Fixture.ini"), """
            [Constants]
            global persist $body = 0
            global persist $weapon = 0

            [KeyBody]
            type = cycle
            $body = 0,1

            [KeyWeapon]
            type = cycle
            $weapon = 0,1

            [TextureOverrideFixtureBody]
            if $\ZZZModManager\zzzmgr_enabled_e35942dd04a6\enabled
                ib = ResourceFixtureBodyAIB
                Resource\ZZMI\Diffuse = ref ResourceFixtureBodyDiffuse
                if $body == 0
                    drawindexed = 3, 0, 0
                else
                    drawindexed = 3, 3, 0
                endif
            endif

            [TextureOverrideFixtureWeapon]
            if $\ZZZModManager\zzzmgr_enabled_e35942dd04a6\enabled && $weapon == 1
                ib = ResourceFixtureWeaponAIB
                Resource\ZZMI\Diffuse = ref ResourceFixtureWeaponDiffuse
                drawindexed = 3, 0, 0
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

            [ResourceFixtureBodyDiffuse]
            filename = FixtureBodyDiffuse.dds

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

            [ResourceFixtureWeaponDiffuse]
            filename = FixtureWeaponDiffuse.dds
            """, Encoding.UTF8);

        var loader = new ZzmiModelPreviewLoader();

        var initial = loader.Load(temp.Path);

        Assert.False(initial.Diagnostics.CacheHit);
        Assert.Equal(2, initial.Variants.Count);
        var initialBody = Assert.Single(initial.Meshes);
        Assert.Equal("FixtureBodyA", initialBody.Name);
        Assert.Equal(new Vector3(1, 0, 0), initialBody.Positions[1]);

        var selected = loader.Load(
            temp.Path,
            new Dictionary<string, double>
            {
                ["Fixture.ini::$body"] = 1,
                ["Fixture.ini::$weapon"] = 1
            });

        Assert.Equal(2, selected.Meshes.Count);
        var selectedBody = Assert.Single(selected.Meshes, mesh => mesh.Name == "FixtureBodyA");
        Assert.Equal(new Vector3(0, 1, 0), selectedBody.Positions[1]);
        Assert.Contains(selected.Meshes, mesh => mesh.Name == "FixtureWeaponA");

        var reopened = loader.Load(temp.Path);

        Assert.True(reopened.Diagnostics.CacheHit);
        Assert.Single(reopened.Meshes);
        Assert.DoesNotContain(reopened.Meshes, mesh => mesh.Name == "FixtureWeaponA");

        File.SetLastWriteTimeUtc(bodyIndexPath, DateTime.UtcNow.AddMinutes(1));
        var invalidated = loader.Load(temp.Path);

        Assert.False(invalidated.Diagnostics.CacheHit);
        Assert.Single(invalidated.Meshes);
    }

    [Fact]
    public void LoaderDownsamplesFourKTexturesBeforeRetainingTheScene()
    {
        using var temp = new TemporaryDirectory();
        WritePositionBuffer(Path.Combine(temp.Path, "FixtureBodyPosition.buf"));
        WriteTexcoordBuffer(Path.Combine(temp.Path, "FixtureBodyTexcoord.buf"));
        WriteUInt32Buffer(Path.Combine(temp.Path, "FixtureBodyA.ib"), [0, 1, 2]);
        WriteBc1Dds(Path.Combine(temp.Path, "FixtureBodyDiffuse.dds"), 4096, 4096, includeMipChain: true);
        File.WriteAllText(Path.Combine(temp.Path, "Fixture.ini"), """
            [TextureOverrideFixtureBody]
            ib = ResourceFixtureBodyAIB
            Resource\ZZMI\Diffuse = ref ResourceFixtureBodyDiffuse
            drawindexed = 3, 0, 0

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

            [ResourceFixtureBodyDiffuse]
            filename = FixtureBodyDiffuse.dds
            """, Encoding.UTF8);

        var scene = new ZzmiModelPreviewLoader().Load(temp.Path);

        var texture = Assert.IsType<ModelPreviewTexture>(Assert.Single(scene.Meshes).DiffuseTexture);
        Assert.Equal(4096, texture.OriginalWidth);
        Assert.Equal(4096, texture.OriginalHeight);
        Assert.Equal(1024, texture.Width);
        Assert.Equal(1024, texture.Height);
        Assert.True(texture.IsDownsampled);
        Assert.Equal(1, scene.Diagnostics.DownsampledTextureCount);
        Assert.Equal(1024L * 1024 * 4, scene.Diagnostics.RetainedTextureBytes);
    }

    [Fact]
    public void LoaderKeepsCompatibleAndAuxiliaryMeshesWhenOneLayoutIsDamaged()
    {
        using var temp = new TemporaryDirectory();
        WritePositionBuffer(Path.Combine(temp.Path, "FixtureBodyPosition.buf"));
        WriteUInt32Buffer(Path.Combine(temp.Path, "FixtureBodyA.ib"), [0, 1, 2]);
        WriteUInt32Buffer(Path.Combine(temp.Path, "FixtureBodyHelper.ib"), [0, 2, 1]);
        File.WriteAllBytes(Path.Combine(temp.Path, "FixtureBrokenPosition.buf"), new byte[41]);
        WriteUInt32Buffer(Path.Combine(temp.Path, "FixtureBrokenA.ib"), [0, 1, 2]);
        File.WriteAllText(Path.Combine(temp.Path, "Fixture.ini"), """
            [TextureOverrideFixtureBroken]
            ib = ResourceFixtureBrokenAIB
            drawindexed = 3, 0, 0

            [TextureOverrideFixtureBody]
            ib = ResourceFixtureBodyAIB
            drawindexed = 3, 0, 0

            [TextureOverrideFixtureHelper]
            ib = ResourceFixtureBodyHelperIB
            drawindexed = 3, 0, 0

            [ResourceFixtureBrokenPosition]
            type = Buffer
            stride = 40
            filename = FixtureBrokenPosition.buf

            [ResourceFixtureBrokenAIB]
            type = Buffer
            format = DXGI_FORMAT_R32_UINT
            filename = FixtureBrokenA.ib

            [ResourceFixtureBodyPosition]
            type = Buffer
            stride = 40
            filename = FixtureBodyPosition.buf

            [ResourceFixtureBodyAIB]
            type = Buffer
            format = DXGI_FORMAT_R32_UINT
            filename = FixtureBodyA.ib

            [ResourceFixtureBodyHelperIB]
            type = Buffer
            format = DXGI_FORMAT_R32_UINT
            filename = FixtureBodyHelper.ib
            """, Encoding.UTF8);

        var scene = new ZzmiModelPreviewLoader().Load(temp.Path);

        Assert.Equal(2, scene.Meshes.Count);
        Assert.Contains(scene.Meshes, mesh => mesh.Name == "FixtureBodyA");
        Assert.Contains(scene.Meshes, mesh => mesh.Name == "FixtureBodyHelper");
        Assert.Contains(scene.Warnings, warning => warning.Contains("FixtureBroken", StringComparison.Ordinal));
    }

    [Fact]
    public void LoaderBindsLightAndMaterialMapsAndComposesApproximateMaterial()
    {
        using var temp = new TemporaryDirectory();
        WritePositionBuffer(Path.Combine(temp.Path, "FixtureBodyPosition.buf"));
        WriteTexcoordBuffer(Path.Combine(temp.Path, "FixtureBodyTexcoord.buf"));
        WriteUInt32Buffer(Path.Combine(temp.Path, "FixtureBodyA.ib"), [0, 1, 2]);
        WriteBc7Dds(Path.Combine(temp.Path, "FixtureBodyDiffuse.dds"), 100, 100, 100, 128);
        WriteBc6Dds(Path.Combine(temp.Path, "FixtureBodyLightMap.dds"), 0.25f, 0.25f, 0.25f);
        WriteBc6Dds(Path.Combine(temp.Path, "FixtureBodyMaterialMap.dds"), 0.78f, 0f, 0f);
        WriteBc6Dds(Path.Combine(temp.Path, "FixtureBodyNormalMap.dds"), 1f, 0.5f, 0.5f);
        File.WriteAllText(Path.Combine(temp.Path, "Fixture.ini"), """
            [TextureOverrideFixtureBody]
            ib = ResourceFixtureBodyAIB
            Resource\ZZMI\Diffuse = ref ResourceFixtureBodyDiffuse
            Resource\ZZMI\NormalMap = ref ResourceFixtureBodyNormalMap
            Resource\ZZMI\LightMap = ref ResourceFixtureBodyLightMap
            Resource\ZZMI\MaterialMap = ref ResourceFixtureBodyMaterialMap
            drawindexed = 3, 0, 0

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

            [ResourceFixtureBodyDiffuse]
            filename = FixtureBodyDiffuse.dds

            [ResourceFixtureBodyLightMap]
            filename = FixtureBodyLightMap.dds

            [ResourceFixtureBodyNormalMap]
            filename = FixtureBodyNormalMap.dds

            [ResourceFixtureBodyMaterialMap]
            filename = FixtureBodyMaterialMap.dds
            """, Encoding.UTF8);

        var scene = new ZzmiModelPreviewLoader().Load(temp.Path);
        var mesh = Assert.Single(scene.Meshes);
        Assert.NotNull(mesh.NormalTexture);
        Assert.NotNull(mesh.LightTexture);
        Assert.NotNull(mesh.MaterialTexture);
        Assert.Equal(4, scene.Diagnostics.TextureCount);

        var composed = Assert.IsType<ModelPreviewTexture>(ModelPreviewMaterialComposer.ComposeApproximate(mesh));
        Assert.NotSame(mesh.DiffuseTexture, composed);
        Assert.NotEqual(mesh.DiffuseTexture!.Bgra32Pixels[0], composed.Bgra32Pixels[0]);
        Assert.Equal(mesh.DiffuseTexture.Bgra32Pixels[3], composed.Bgra32Pixels[3]);
        Assert.True(composed.HasTransparency);

        var backend = new CpuModelPreviewShaderBackend();
        var withoutNormal = Assert.IsType<ModelPreviewTexture>(backend.Render(
            mesh,
            new ModelPreviewShaderOptions(UseNormalMap: false)));
        var withNormal = Assert.IsType<ModelPreviewTexture>(backend.Render(
            mesh,
            new ModelPreviewShaderOptions(UseNormalMap: true)));
        Assert.NotEqual(withoutNormal.Bgra32Pixels[0], withNormal.Bgra32Pixels[0]);
        Assert.Contains("Normal", backend.DisplayName, StringComparison.Ordinal);

        var edgeTexture = new ModelPreviewTexture(
            "edge-fixture",
            5,
            1,
            [
                0, 0, 0, 255,
                0, 0, 0, 255,
                255, 255, 255, 255,
                255, 255, 255, 255,
                255, 255, 255, 255
            ],
            false);
        var edgeMesh = mesh with
        {
            DiffuseTexture = edgeTexture,
            NormalTexture = null,
            LightTexture = null,
            MaterialTexture = null
        };
        var withoutOutline = Assert.IsType<ModelPreviewTexture>(backend.Render(
            edgeMesh,
            new ModelPreviewShaderOptions(false, false, false, false)));
        var withOutline = Assert.IsType<ModelPreviewTexture>(backend.Render(
            edgeMesh,
            new ModelPreviewShaderOptions(false, false, false, true)));
        Assert.Same(edgeTexture, withoutOutline);
        Assert.True(withOutline.Bgra32Pixels[8] < withoutOutline.Bgra32Pixels[8]);
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
            writer.Write(BitConverter.HalfToUInt16Bits((System.Half)0.25f));
            writer.Write(BitConverter.HalfToUInt16Bits((System.Half)0.75f));
            writer.Write(0f);
            writer.Write(0f);
            writer.Write(BitConverter.HalfToUInt16Bits((System.Half)0.5f));
            writer.Write(BitConverter.HalfToUInt16Bits((System.Half)0.5f));
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

    private static void WriteBc1Dds(string path, int width, int height, bool includeMipChain = false)
    {
        var mipCount = includeMipChain ? 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height))) : 1;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("DDS "));
        writer.Write(124u);
        writer.Write(includeMipChain ? 0x000A1007u : 0x00081007u);
        writer.Write((uint)height);
        writer.Write((uint)width);
        writer.Write((uint)(((width + 3) / 4) * ((height + 3) / 4) * 8));
        writer.Write(0u);
        writer.Write((uint)mipCount);
        for (var i = 0; i < 11; i++)
        {
            writer.Write(0u);
        }

        writer.Write(32u);
        writer.Write(4u);
        writer.Write(Encoding.ASCII.GetBytes("DXT1"));
        for (var i = 0; i < 5; i++)
        {
            writer.Write(0u);
        }

        writer.Write(includeMipChain ? 0x00401008u : 0x1000u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);

        var mipWidth = width;
        var mipHeight = height;
        for (var level = 0; level < mipCount; level++)
        {
            var blockCount = ((mipWidth + 3) / 4) * ((mipHeight + 3) / 4);
            for (var block = 0; block < blockCount; block++)
            {
                writer.Write((ushort)0xF800);
                writer.Write((ushort)0xF800);
                writer.Write(0u);
            }

            mipWidth = Math.Max(1, mipWidth / 2);
            mipHeight = Math.Max(1, mipHeight / 2);
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

    private static void WriteBc6Dds(string path, float red, float green, float blue)
    {
        var colors = Enumerable.Repeat(new ColorRgbFloat(red, green, blue), 16).ToArray();
        var block = new BcEncoder(CompressionFormat.Bc6U).EncodeBlockHdr(colors);
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
        for (var i = 0; i < 5; i++)
        {
            writer.Write(0u);
        }

        writer.Write(0x1000u);
        for (var i = 0; i < 4; i++)
        {
            writer.Write(0u);
        }

        writer.Write(95u);
        writer.Write(3u);
        writer.Write(0u);
        writer.Write(1u);
        writer.Write(0u);
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
