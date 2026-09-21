using System.Buffers.Binary;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using YikzClipboard.Core.Imaging;
using YikzClipboard.Core.Platform;

namespace YikzClipboard.App.Platform;

internal sealed class WinImageTools : IImageTools
{
    public static async Task<InMemoryRandomAccessStream> ToStreamAsync(byte[] bytes)
    {
        var ras = new InMemoryRandomAccessStream();
        await ras.WriteAsync(bytes.AsBuffer());
        ras.Seek(0);
        return ras;
    }

    public static async Task<DecodedBitmap> DecodeAsync(byte[] encoded, uint? maxSide = null)
    {
        using var ras = await ToStreamAsync(encoded);
        var decoder = await BitmapDecoder.CreateAsync(ras);
        var width = decoder.OrientedPixelWidth;
        var height = decoder.OrientedPixelHeight;
        var transform = new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant };
        if (maxSide.HasValue && (width > maxSide.Value || height > maxSide.Value))
        {
            var scale = Math.Min((double)maxSide.Value / width, (double)maxSide.Value / height);
            width = Math.Max(1u, (uint)Math.Round(width * scale));
            height = Math.Max(1u, (uint)Math.Round(height * scale));
            transform.ScaledWidth = width;
            transform.ScaledHeight = height;
        }
        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);
        var pixels = data.DetachPixelData();
        return new DecodedBitmap((int)width, (int)height, pixels, true);
    }

    public static async Task<byte[]> DibToPngAsync(byte[] dib)
    {
        try
        {
            return Dib.ToPng(dib);
        }
        catch (FormatException)
        {
            var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
            var file = new byte[14 + dib.Length];
            file[0] = (byte)'B';
            file[1] = (byte)'M';
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), file.Length);
            var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
            var clrUsed = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(32));
            var palette = bitCount <= 8 ? (clrUsed > 0 ? clrUsed : 1 << bitCount) * 4 : 0;
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10), 14 + headerSize + palette);
            dib.CopyTo(file, 14);
            var bmp = await DecodeAsync(file);
            return PngWriter.EncodeBgra(bmp.Width, bmp.Height, bmp.Bgra, true);
        }
    }

    public static async Task<byte[]> PngToDibAsync(byte[] png)
    {
        var bmp = await DecodeAsync(png);
        return Dib.FromBgra(bmp.Width, bmp.Height, bmp.Bgra);
    }

    public static async Task<BitmapImage?> ToImageSourceAsync(byte[] bytes, int? decodeWidth = null)
    {
        try
        {
            using var ras = await ToStreamAsync(bytes);
            var image = new BitmapImage();
            if (decodeWidth.HasValue)
            {
                image.DecodePixelWidth = decodeWidth.Value;
                image.DecodePixelType = DecodePixelType.Logical;
            }
            await image.SetSourceAsync(ras);
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<byte[]?> MakeJpegThumbnailAsync(byte[] png, int maxSide, int maxBytes, CancellationToken ct)
    {
        var side = (uint)maxSide;
        foreach (var quality in new[] { 0.8, 0.65, 0.5, 0.35 })
        {
            ct.ThrowIfCancellationRequested();
            var bmp = await DecodeAsync(png, side);
            var pixels = bmp.Bgra;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var a = pixels[i + 3];
                if (a != 255)
                {
                    pixels[i] = (byte)((pixels[i] * a + 255 * (255 - a)) / 255);
                    pixels[i + 1] = (byte)((pixels[i + 1] * a + 255 * (255 - a)) / 255);
                    pixels[i + 2] = (byte)((pixels[i + 2] * a + 255 * (255 - a)) / 255);
                    pixels[i + 3] = 255;
                }
            }
            using var output = new InMemoryRandomAccessStream();
            var props = new BitmapPropertySet
            {
                { "ImageQuality", new BitmapTypedValue((float)quality, PropertyType.Single) },
            };
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, props);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)bmp.Width, (uint)bmp.Height, 96, 96, pixels);
            await encoder.FlushAsync();
            var bytes = new byte[output.Size];
            using (var reader = new DataReader(output.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)output.Size);
                reader.ReadBytes(bytes);
            }
            if (bytes.Length <= maxBytes)
            {
                return bytes;
            }
            side = Math.Max(64, side * 3 / 4);
        }
        return null;
    }
}
