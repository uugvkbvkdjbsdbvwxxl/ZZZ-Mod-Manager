using System.Buffers.Binary;
using System.Numerics;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;

namespace ZZZModManager.Services;

internal static class DdsTextureDecoder
{
    private const int LegacyHeaderLength = 128;
    private const int Dx10HeaderLength = 148;
    private const int MaximumDimension = 8192;
    private const int MaximumPixels = 32 * 1024 * 1024;
    private const long MaximumInputBytes = 256L * 1024 * 1024;

    public static ModelPreviewTexture Decode(string path, int maximumOutputDimension = int.MaxValue)
    {
        if (maximumOutputDimension <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOutputDimension));
        }

        var file = new FileInfo(path);
        if (file.Length < LegacyHeaderLength || file.Length > MaximumInputBytes)
        {
            throw new InvalidDataException("DDS 文件大小无效或超过预览上限。");
        }

        var data = File.ReadAllBytes(path);
        if (!data.AsSpan(0, 4).SequenceEqual("DDS "u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4)) != 124
            || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(76, 4)) != 32)
        {
            throw new InvalidDataException("不是有效的 DDS 文件头。");
        }

        var height = ReadDimension(data, 12);
        var width = ReadDimension(data, 16);
        var originalWidth = width;
        var originalHeight = height;
        if (width > MaximumDimension || height > MaximumDimension
            || checked((long)width * height) > MaximumPixels)
        {
            throw new InvalidDataException("DDS 分辨率超过静态预览上限。");
        }

        var fourCc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(84, 4));
        var format = ResolveFormat(data, fourCc, out var payloadOffset);
        var mip = SelectPreviewMip(
            data,
            format,
            payloadOffset,
            width,
            height,
            ReadMipCount(data),
            maximumOutputDimension);
        payloadOffset = mip.PayloadOffset;
        width = mip.Width;
        height = mip.Height;
        var pixels = format switch
        {
            DdsFormat.Bc1 => DecodeBlockCompressed(data, payloadOffset, width, height, 8, DecodeBc1Block),
            DdsFormat.Bc2 => DecodeBlockCompressed(data, payloadOffset, width, height, 16, DecodeBc2Block),
            DdsFormat.Bc3 => DecodeBlockCompressed(data, payloadOffset, width, height, 16, DecodeBc3Block),
            DdsFormat.Bc6U => DecodeBc6(data, payloadOffset, width, height, signed: false),
            DdsFormat.Bc6S => DecodeBc6(data, payloadOffset, width, height, signed: true),
            DdsFormat.Bc7 => DecodeBc7(data, payloadOffset, width, height),
            DdsFormat.Bgra32 => DecodeBgra32(data, payloadOffset, width, height),
            _ => throw new InvalidDataException("DDS 压缩格式暂不受支持。")
        };

        if (Math.Max(width, height) > maximumOutputDimension)
        {
            (pixels, width, height) = Downsample(pixels, width, height, maximumOutputDimension);
        }

        var hasTransparency = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] < byte.MaxValue)
            {
                hasTransparency = true;
                break;
            }
        }

        return new ModelPreviewTexture(path, width, height, pixels, hasTransparency)
        {
            OriginalWidth = originalWidth,
            OriginalHeight = originalHeight
        };
    }

    private static int ReadMipCount(byte[] data)
    {
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(28, 4));
        return declared is > 0 and <= 16 ? (int)declared : 1;
    }

    private static DdsMip SelectPreviewMip(
        byte[] data,
        DdsFormat format,
        int payloadOffset,
        int width,
        int height,
        int mipCount,
        int maximumOutputDimension)
    {
        if (maximumOutputDimension == int.MaxValue || Math.Max(width, height) <= maximumOutputDimension)
        {
            return new DdsMip(payloadOffset, width, height);
        }

        var selectedOffset = payloadOffset;
        var selectedWidth = width;
        var selectedHeight = height;
        for (var level = 1; level < mipCount && Math.Max(selectedWidth, selectedHeight) > maximumOutputDimension; level++)
        {
            var currentLength = GetMipPayloadLength(format, selectedWidth, selectedHeight);
            if (selectedOffset < 0 || currentLength < 0 || selectedOffset > data.Length - currentLength)
            {
                return new DdsMip(payloadOffset, width, height);
            }

            selectedOffset += currentLength;
            selectedWidth = Math.Max(1, selectedWidth / 2);
            selectedHeight = Math.Max(1, selectedHeight / 2);
        }

        var selectedLength = GetMipPayloadLength(format, selectedWidth, selectedHeight);
        return selectedOffset >= 0 && selectedLength >= 0 && selectedOffset <= data.Length - selectedLength
            ? new DdsMip(selectedOffset, selectedWidth, selectedHeight)
            : new DdsMip(payloadOffset, width, height);
    }

    private static int GetMipPayloadLength(DdsFormat format, int width, int height) => format switch
    {
        DdsFormat.Bc1 => checked(((width + 3) / 4) * ((height + 3) / 4) * 8),
        DdsFormat.Bc2 or DdsFormat.Bc3 or DdsFormat.Bc6U or DdsFormat.Bc6S or DdsFormat.Bc7 =>
            checked(((width + 3) / 4) * ((height + 3) / 4) * 16),
        DdsFormat.Bgra32 => checked(width * height * 4),
        _ => 0
    };

    private static (byte[] Pixels, int Width, int Height) Downsample(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int maximumDimension)
    {
        var scale = (double)maximumDimension / Math.Max(sourceWidth, sourceHeight);
        var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        var destination = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Min(sourceHeight - 1, (int)((long)y * sourceHeight / height));
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(sourceWidth - 1, (int)((long)x * sourceWidth / width));
                var sourceOffset = ((sourceY * sourceWidth) + sourceX) * 4;
                var destinationOffset = ((y * width) + x) * 4;
                source.AsSpan(sourceOffset, 4).CopyTo(destination.AsSpan(destinationOffset, 4));
            }
        }

        return (destination, width, height);
    }

    private static int ReadDimension(byte[] data, int offset)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        if (value == 0 || value > int.MaxValue)
        {
            throw new InvalidDataException("DDS 宽高无效。");
        }

        return (int)value;
    }

    private static DdsFormat ResolveFormat(byte[] data, uint fourCc, out int payloadOffset)
    {
        payloadOffset = LegacyHeaderLength;
        if (fourCc == FourCc("DX10"))
        {
            if (data.Length < Dx10HeaderLength)
            {
                throw new InvalidDataException("DDS 缺少 DX10 扩展头。");
            }

            payloadOffset = Dx10HeaderLength;
            return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(128, 4)) switch
            {
                71 or 72 => DdsFormat.Bc1,
                74 or 75 => DdsFormat.Bc2,
                77 or 78 => DdsFormat.Bc3,
                87 => DdsFormat.Bgra32,
                95 => DdsFormat.Bc6U,
                96 => DdsFormat.Bc6S,
                98 or 99 => DdsFormat.Bc7,
                _ => DdsFormat.Unsupported
            };
        }

        if (fourCc == FourCc("DXT1"))
        {
            return DdsFormat.Bc1;
        }

        if (fourCc == FourCc("DXT2") || fourCc == FourCc("DXT3"))
        {
            return DdsFormat.Bc2;
        }

        if (fourCc == FourCc("DXT4") || fourCc == FourCc("DXT5"))
        {
            return DdsFormat.Bc3;
        }

        var rgbBitCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(88, 4));
        return fourCc == 0 && rgbBitCount == 32 ? DdsFormat.Bgra32 : DdsFormat.Unsupported;
    }

    private static byte[] DecodeBc7(byte[] data, int payloadOffset, int width, int height)
    {
        var blockWidth = (width + 3) / 4;
        var blockHeight = (height + 3) / 4;
        var payloadLength = checked(blockWidth * blockHeight * 16);
        EnsurePayload(data, payloadOffset, payloadLength);

        var paddedWidth = blockWidth * 4;
        var paddedHeight = blockHeight * 4;
        var padded = DecodeBc7Stripes(data, payloadOffset, blockWidth, blockHeight, paddedWidth, paddedHeight);
        if (paddedWidth == width && paddedHeight == height)
        {
            return padded;
        }

        var result = new byte[checked(width * height * 4)];
        var sourceStride = paddedWidth * 4;
        var destinationStride = width * 4;
        for (var y = 0; y < height; y++)
        {
            padded.AsSpan(y * sourceStride, destinationStride)
                .CopyTo(result.AsSpan(y * destinationStride, destinationStride));
        }

        return result;
    }

    private static byte[] DecodeBc6(
        byte[] data,
        int payloadOffset,
        int width,
        int height,
        bool signed)
    {
        var payloadLength = checked(((width + 3) / 4) * ((height + 3) / 4) * 16);
        EnsurePayload(data, payloadOffset, payloadLength);
        try
        {
            var compressed = data.AsSpan(payloadOffset, payloadLength).ToArray();
            var format = signed ? CompressionFormat.Bc6S : CompressionFormat.Bc6U;
            var decoded = new BcDecoder().DecodeRawHdr(compressed, width, height, format);
            if (decoded.Length != checked(width * height))
            {
                throw new InvalidDataException("BC6H 解码后的像素数量无效。");
            }

            var pixels = new byte[checked(width * height * 4)];
            for (var i = 0; i < decoded.Length; i++)
            {
                var destination = i * 4;
                pixels[destination] = ToByte(decoded[i].b, signed);
                pixels[destination + 1] = ToByte(decoded[i].g, signed);
                pixels[destination + 2] = ToByte(decoded[i].r, signed);
                pixels[destination + 3] = byte.MaxValue;
            }

            return pixels;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidDataException("BC6H 贴图解码失败。", ex);
        }
    }

    private static byte ToByte(float value, bool signed)
    {
        if (!float.IsFinite(value))
        {
            return 0;
        }

        var normalized = signed ? (value * 0.5f) + 0.5f : value;
        return (byte)Math.Clamp((int)MathF.Round(normalized * byte.MaxValue), 0, byte.MaxValue);
    }

    private static byte[] DecodeBc7Stripes(
        byte[] data,
        int payloadOffset,
        int blockWidth,
        int blockHeight,
        int paddedWidth,
        int paddedHeight)
    {
        const int BlockRowsPerStripe = 8;
        if (blockHeight <= BlockRowsPerStripe)
        {
            var compressed = data.AsSpan(payloadOffset, checked(blockWidth * blockHeight * 16)).ToArray();
            return new Bc7Decoder(compressed, paddedWidth, paddedHeight).Unpack();
        }

        var outputStride = checked(paddedWidth * 4);
        var output = new byte[checked(outputStride * paddedHeight)];
        var stripeCount = (blockHeight + BlockRowsPerStripe - 1) / BlockRowsPerStripe;
        var nextStripe = -1;
        Exception? failure = null;
        var failureGate = new object();
        void DecodeNextStripe()
        {
            while (failure is null)
            {
                var stripeIndex = Interlocked.Increment(ref nextStripe);
                if (stripeIndex >= stripeCount)
                {
                    return;
                }

                try
                {
                    var firstBlockRow = stripeIndex * BlockRowsPerStripe;
                    var stripeBlockRows = Math.Min(BlockRowsPerStripe, blockHeight - firstBlockRow);
                    var compressedLength = checked(blockWidth * stripeBlockRows * 16);
                    var compressedOffset = checked(payloadOffset + (firstBlockRow * blockWidth * 16));
                    var compressed = data.AsSpan(compressedOffset, compressedLength).ToArray();
                    var stripeHeight = stripeBlockRows * 4;
                    var decoded = new Bc7Decoder(compressed, paddedWidth, stripeHeight).Unpack();
                    decoded.CopyTo(output, checked(firstBlockRow * 4 * outputStride));
                }
                catch (Exception ex)
                {
                    lock (failureGate)
                    {
                        failure ??= ex;
                    }
                }
            }
        }

        var workerCount = Math.Min(Math.Min(Environment.ProcessorCount, 8), stripeCount);
        var workers = new Thread[Math.Max(0, workerCount - 1)];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = new Thread(DecodeNextStripe)
            {
                IsBackground = true,
                Name = $"BC7 preview decoder {i + 1}"
            };
            workers[i].Start();
        }

        DecodeNextStripe();
        foreach (var worker in workers)
        {
            worker.Join();
        }

        if (failure is not null)
        {
            throw new InvalidDataException("BC7 贴图并行解码失败。", failure);
        }

        return output;
    }

    private static byte[] DecodeBlockCompressed(
        byte[] data,
        int payloadOffset,
        int width,
        int height,
        int blockSize,
        BlockDecoder decoder)
    {
        var blockWidth = (width + 3) / 4;
        var blockHeight = (height + 3) / 4;
        var payloadLength = checked(blockWidth * blockHeight * blockSize);
        EnsurePayload(data, payloadOffset, payloadLength);

        var result = new byte[checked(width * height * 4)];
        Span<uint> block = stackalloc uint[16];
        for (var blockY = 0; blockY < blockHeight; blockY++)
        {
            for (var blockX = 0; blockX < blockWidth; blockX++)
            {
                var sourceOffset = payloadOffset + (((blockY * blockWidth) + blockX) * blockSize);
                decoder(data.AsSpan(sourceOffset, blockSize), block);
                CopyBlock(block, result, width, height, blockX * 4, blockY * 4);
            }
        }

        return result;
    }

    private static byte[] DecodeBgra32(byte[] data, int payloadOffset, int width, int height)
    {
        var payloadLength = checked(width * height * 4);
        EnsurePayload(data, payloadOffset, payloadLength);
        var result = new byte[payloadLength];

        var redMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(92, 4));
        var greenMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(96, 4));
        var blueMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(100, 4));
        var alphaMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(104, 4));
        if ((redMask | greenMask | blueMask | alphaMask) == 0)
        {
            data.AsSpan(payloadOffset, payloadLength).CopyTo(result);
            return result;
        }

        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var packed = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(payloadOffset + (pixel * 4), 4));
            var destination = pixel * 4;
            result[destination] = ExtractChannel(packed, blueMask, byte.MaxValue);
            result[destination + 1] = ExtractChannel(packed, greenMask, byte.MaxValue);
            result[destination + 2] = ExtractChannel(packed, redMask, byte.MaxValue);
            result[destination + 3] = ExtractChannel(packed, alphaMask, byte.MaxValue);
        }

        return result;
    }

    private static byte ExtractChannel(uint value, uint mask, byte fallback)
    {
        if (mask == 0)
        {
            return fallback;
        }

        var shift = BitOperations.TrailingZeroCount(mask);
        var normalizedMask = mask >> shift;
        var component = (value & mask) >> shift;
        return (byte)(((ulong)component * byte.MaxValue + (normalizedMask / 2)) / normalizedMask);
    }

    private static void DecodeBc1Block(ReadOnlySpan<byte> source, Span<uint> destination) =>
        DecodeColorBlock(source, destination, false);

    private static void DecodeBc2Block(ReadOnlySpan<byte> source, Span<uint> destination)
    {
        DecodeColorBlock(source[8..], destination, true);
        var alpha = BinaryPrimitives.ReadUInt64LittleEndian(source);
        for (var i = 0; i < 16; i++)
        {
            var value = (uint)(((alpha >> (i * 4)) & 0xF) * 17);
            destination[i] = (destination[i] & 0x00FFFFFF) | (value << 24);
        }
    }

    private static void DecodeBc3Block(ReadOnlySpan<byte> source, Span<uint> destination)
    {
        DecodeColorBlock(source[8..], destination, true);
        Span<byte> palette = stackalloc byte[8];
        palette[0] = source[0];
        palette[1] = source[1];
        if (palette[0] > palette[1])
        {
            for (var i = 1; i <= 6; i++)
            {
                palette[i + 1] = (byte)(((7 - i) * palette[0] + (i * palette[1])) / 7);
            }
        }
        else
        {
            for (var i = 1; i <= 4; i++)
            {
                palette[i + 1] = (byte)(((5 - i) * palette[0] + (i * palette[1])) / 5);
            }

            palette[6] = 0;
            palette[7] = byte.MaxValue;
        }

        ulong indices = 0;
        for (var i = 0; i < 6; i++)
        {
            indices |= (ulong)source[i + 2] << (i * 8);
        }

        for (var i = 0; i < 16; i++)
        {
            var alpha = palette[(int)((indices >> (i * 3)) & 0x7)];
            destination[i] = (destination[i] & 0x00FFFFFF) | ((uint)alpha << 24);
        }
    }

    private static void DecodeColorBlock(ReadOnlySpan<byte> source, Span<uint> destination, bool forceOpaque)
    {
        var color0 = BinaryPrimitives.ReadUInt16LittleEndian(source);
        var color1 = BinaryPrimitives.ReadUInt16LittleEndian(source[2..]);
        Span<uint> palette = stackalloc uint[4];
        palette[0] = ExpandRgb565(color0);
        palette[1] = ExpandRgb565(color1);
        if (color0 > color1 || forceOpaque)
        {
            palette[2] = InterpolateBgra(palette[0], palette[1], 2, 1, 3);
            palette[3] = InterpolateBgra(palette[0], palette[1], 1, 2, 3);
        }
        else
        {
            palette[2] = InterpolateBgra(palette[0], palette[1], 1, 1, 2);
            palette[3] = 0;
        }

        var indices = BinaryPrimitives.ReadUInt32LittleEndian(source[4..]);
        for (var i = 0; i < 16; i++)
        {
            destination[i] = palette[(int)((indices >> (i * 2)) & 0x3)];
        }
    }

    private static uint ExpandRgb565(ushort value)
    {
        var red5 = (value >> 11) & 0x1F;
        var green6 = (value >> 5) & 0x3F;
        var blue5 = value & 0x1F;
        var red = (red5 << 3) | (red5 >> 2);
        var green = (green6 << 2) | (green6 >> 4);
        var blue = (blue5 << 3) | (blue5 >> 2);
        return (uint)(blue | (green << 8) | (red << 16) | (byte.MaxValue << 24));
    }

    private static uint InterpolateBgra(uint first, uint second, int firstWeight, int secondWeight, int divisor)
    {
        uint result = 0;
        for (var channel = 0; channel < 4; channel++)
        {
            var shift = channel * 8;
            var a = (first >> shift) & 0xFF;
            var b = (second >> shift) & 0xFF;
            result |= ((a * (uint)firstWeight + b * (uint)secondWeight) / (uint)divisor) << shift;
        }

        return result;
    }

    private static void CopyBlock(
        ReadOnlySpan<uint> source,
        byte[] destination,
        int width,
        int height,
        int destinationX,
        int destinationY)
    {
        for (var y = 0; y < 4 && destinationY + y < height; y++)
        {
            for (var x = 0; x < 4 && destinationX + x < width; x++)
            {
                var offset = (((destinationY + y) * width) + destinationX + x) * 4;
                BinaryPrimitives.WriteUInt32LittleEndian(destination.AsSpan(offset, 4), source[(y * 4) + x]);
            }
        }
    }

    private static void EnsurePayload(byte[] data, int payloadOffset, int payloadLength)
    {
        if (payloadOffset < 0 || payloadLength < 0 || payloadOffset > data.Length - payloadLength)
        {
            throw new InvalidDataException("DDS 顶层纹理数据不完整。");
        }
    }

    private static uint FourCc(string value) =>
        (uint)value[0] | ((uint)value[1] << 8) | ((uint)value[2] << 16) | ((uint)value[3] << 24);

    private delegate void BlockDecoder(ReadOnlySpan<byte> source, Span<uint> destination);

    private readonly record struct DdsMip(int PayloadOffset, int Width, int Height);

    private enum DdsFormat
    {
        Unsupported,
        Bc1,
        Bc2,
        Bc3,
        Bc6U,
        Bc6S,
        Bc7,
        Bgra32
    }
}
