using System.Numerics;

namespace ZZZModManager.Services;

public sealed record ModelPreviewShaderOptions(
    bool UseLightMap = true,
    bool UseMaterialMap = true,
    bool UseNormalMap = true,
    bool UseOutline = false);

public interface IModelPreviewShaderBackend
{
    string DisplayName { get; }
    ModelPreviewTexture? Render(ModelPreviewMesh mesh, ModelPreviewShaderOptions options);
}

/// <summary>
/// A deterministic, offline material shader. It combines the author-provided
/// texture maps on the CPU, so previewing never needs a game process, injection
/// or a guessed game shader binary.
/// </summary>
public sealed class CpuModelPreviewShaderBackend : IModelPreviewShaderBackend
{
    private static readonly Vector3 LightDirection = Vector3.Normalize(new Vector3(-0.35f, -0.45f, 0.82f));
    private static readonly Vector3 HalfDirection = Vector3.Normalize(LightDirection + Vector3.UnitZ);

    public string DisplayName => "CPU Shader · Normal / Light / Material";

    public ModelPreviewTexture? Render(ModelPreviewMesh mesh, ModelPreviewShaderOptions options)
    {
        if (mesh.DiffuseTexture is not { } diffuse)
        {
            return null;
        }

        var lightMap = options.UseLightMap ? mesh.LightTexture : null;
        var materialMap = options.UseMaterialMap ? mesh.MaterialTexture : null;
        var normalMap = options.UseNormalMap ? mesh.NormalTexture : null;
        if (lightMap is null && materialMap is null && normalMap is null && !options.UseOutline)
        {
            return diffuse;
        }

        var pixels = new byte[diffuse.Bgra32Pixels.Length];
        for (var y = 0; y < diffuse.Height; y++)
        {
            for (var x = 0; x < diffuse.Width; x++)
            {
                var targetOffset = ((y * diffuse.Width) + x) * 4;
                var light = lightMap is null
                    ? 1f
                    : Luminance(lightMap, x, y, diffuse.Width, diffuse.Height);
                var ambientOcclusion = 0.58f + (0.42f * light);
                var metallic = 0f;
                var roughness = 0.65f;
                if (materialMap is not null)
                {
                    var materialOffset = SampleOffset(materialMap, x, y, diffuse.Width, diffuse.Height);
                    metallic = materialMap.Bgra32Pixels[materialOffset + 2] / 255f;
                    roughness = materialMap.Bgra32Pixels[materialOffset + 1] / 255f;
                }

                var normal = normalMap is null
                    ? Vector3.UnitZ
                    : DecodeNormal(normalMap, x, y, diffuse.Width, diffuse.Height);
                var normalLight = normalMap is null
                    ? 1f
                    : 0.78f + (0.22f * MathF.Max(0, Vector3.Dot(normal, LightDirection)));
                var materialBoost = 1f + (metallic * (1f - roughness) * 0.24f);
                var shininess = 8f + ((1f - roughness) * 56f);
                var specular = normalMap is null
                    ? 0f
                    : MathF.Pow(MathF.Max(0, Vector3.Dot(normal, HalfDirection)), shininess)
                        * (0.05f + (metallic * 0.18f));

                pixels[targetOffset] = Shade(
                    diffuse.Bgra32Pixels[targetOffset], ambientOcclusion, normalLight, materialBoost, specular);
                pixels[targetOffset + 1] = Shade(
                    diffuse.Bgra32Pixels[targetOffset + 1], ambientOcclusion, normalLight, materialBoost, specular);
                pixels[targetOffset + 2] = Shade(
                    diffuse.Bgra32Pixels[targetOffset + 2], ambientOcclusion, normalLight, materialBoost, specular);
                pixels[targetOffset + 3] = diffuse.Bgra32Pixels[targetOffset + 3];
            }
        }

        if (options.UseOutline)
        {
            ApplyOutline(diffuse, pixels);
        }

        var sourceKey = string.Join(
            "|",
            new[]
            {
                diffuse.SourceFile,
                lightMap?.SourceFile,
                materialMap?.SourceFile,
                normalMap?.SourceFile,
                $"shader:{options.UseLightMap}:{options.UseMaterialMap}:{options.UseNormalMap}:{options.UseOutline}"
            }.Where(path => !string.IsNullOrWhiteSpace(path)));
        return new ModelPreviewTexture(sourceKey, diffuse.Width, diffuse.Height, pixels, diffuse.HasTransparency)
        {
            OriginalWidth = diffuse.OriginalWidth,
            OriginalHeight = diffuse.OriginalHeight
        };
    }

    private static byte Shade(
        byte channel,
        float ambientOcclusion,
        float normalLight,
        float materialBoost,
        float specular)
    {
        var shaded = (channel * ambientOcclusion * normalLight * materialBoost) + (255f * specular);
        return (byte)Math.Clamp((int)MathF.Round(shaded), 0, 255);
    }

    private static Vector3 DecodeNormal(
        ModelPreviewTexture texture,
        int x,
        int y,
        int targetWidth,
        int targetHeight)
    {
        var offset = SampleOffset(texture, x, y, targetWidth, targetHeight);
        var normal = new Vector3(
            (texture.Bgra32Pixels[offset + 2] / 127.5f) - 1f,
            1f - (texture.Bgra32Pixels[offset + 1] / 127.5f),
            (texture.Bgra32Pixels[offset] / 127.5f) - 1f);
        return normal.LengthSquared() < 0.0001f ? Vector3.UnitZ : Vector3.Normalize(normal);
    }

    private static float Luminance(ModelPreviewTexture texture, int x, int y, int targetWidth, int targetHeight)
    {
        var offset = SampleOffset(texture, x, y, targetWidth, targetHeight);
        var blue = texture.Bgra32Pixels[offset] / 255f;
        var green = texture.Bgra32Pixels[offset + 1] / 255f;
        var red = texture.Bgra32Pixels[offset + 2] / 255f;
        return (red * 0.2126f) + (green * 0.7152f) + (blue * 0.0722f);
    }

    private static int SampleOffset(ModelPreviewTexture texture, int x, int y, int targetWidth, int targetHeight)
    {
        var sourceX = Math.Min(texture.Width - 1, (int)((long)x * texture.Width / Math.Max(1, targetWidth)));
        var sourceY = Math.Min(texture.Height - 1, (int)((long)y * texture.Height / Math.Max(1, targetHeight)));
        return ((sourceY * texture.Width) + sourceX) * 4;
    }

    private static void ApplyOutline(ModelPreviewTexture source, byte[] target)
    {
        const int radius = 2;
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var offset = ((y * source.Width) + x) * 4;
                if (source.Bgra32Pixels[offset + 3] <= 8)
                {
                    continue;
                }

                var center = PixelLuminance(source.Bgra32Pixels, offset);
                var centerAlpha = source.Bgra32Pixels[offset + 3] / 255f;
                var edge = 0f;
                AccumulateEdge(source, x - radius, y, center, centerAlpha, ref edge);
                AccumulateEdge(source, x + radius, y, center, centerAlpha, ref edge);
                AccumulateEdge(source, x, y - radius, center, centerAlpha, ref edge);
                AccumulateEdge(source, x, y + radius, center, centerAlpha, ref edge);
                var strength = Math.Clamp((edge - 0.10f) * 1.35f, 0f, 0.68f);
                if (strength <= 0)
                {
                    continue;
                }

                var multiplier = 1f - strength;
                target[offset] = (byte)MathF.Round(target[offset] * multiplier);
                target[offset + 1] = (byte)MathF.Round(target[offset + 1] * multiplier);
                target[offset + 2] = (byte)MathF.Round(target[offset + 2] * multiplier);
            }
        }
    }

    private static void AccumulateEdge(
        ModelPreviewTexture source,
        int x,
        int y,
        float centerLuminance,
        float centerAlpha,
        ref float edge)
    {
        x = Math.Clamp(x, 0, source.Width - 1);
        y = Math.Clamp(y, 0, source.Height - 1);
        var offset = ((y * source.Width) + x) * 4;
        var alphaDelta = MathF.Abs((source.Bgra32Pixels[offset + 3] / 255f) - centerAlpha);
        edge = MathF.Max(edge, MathF.Max(
            MathF.Abs(centerLuminance - PixelLuminance(source.Bgra32Pixels, offset)),
            alphaDelta));
    }

    private static float PixelLuminance(byte[] pixels, int offset)
    {
        var blue = pixels[offset] / 255f;
        var green = pixels[offset + 1] / 255f;
        var red = pixels[offset + 2] / 255f;
        return (red * 0.2126f) + (green * 0.7152f) + (blue * 0.0722f);
    }
}

public static class ModelPreviewMaterialComposer
{
    private static readonly IModelPreviewShaderBackend ApproximateBackend = new CpuModelPreviewShaderBackend();

    public static ModelPreviewTexture? ComposeApproximate(ModelPreviewMesh mesh) =>
        ApproximateBackend.Render(mesh, new ModelPreviewShaderOptions(UseNormalMap: false));
}
