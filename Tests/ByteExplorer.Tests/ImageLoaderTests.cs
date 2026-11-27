// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ByteExplorer.Infrastructure;
using SkiaSharp;
using Xunit;

namespace ByteExplorer.Tests
{
    public sealed class ImageLoaderTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-img-" + Guid.NewGuid().ToString("N"));

        public ImageLoaderTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        /// <summary>A 200x100 JPEG, left half red and right half blue, with the given EXIF orientation.</summary>
        private string WriteJpeg(ushort orientation)
        {
            using var bitmap = new SKBitmap(200, 100);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Red);
                using var blue = new SKPaint { Color = SKColors.Blue };
                canvas.DrawRect(100, 0, 100, 100, blue);
            }

            byte[] jpeg;
            using (SKData data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95))
            {
                jpeg = data.ToArray();
            }

            // APP1 Exif segment: "Exif\0\0" + little-endian TIFF header + IFD0 with one entry (0x0112 Orientation, SHORT).
            byte[] tiff =
            {
                (byte)'I', (byte)'I', 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,
                0x01, 0x00,
                0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, (byte)orientation, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00
            };
            byte[] exifHeader = { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 };
            int segmentLength = 2 + exifHeader.Length + tiff.Length;

            string path = Path.Combine(_root, $"o{orientation}.jpg");
            using (var output = File.Create(path))
            {
                output.Write(jpeg, 0, 2); // SOI
                output.WriteByte(0xFF);
                output.WriteByte(0xE1);
                output.WriteByte((byte)(segmentLength >> 8));
                output.WriteByte((byte)segmentLength);
                output.Write(exifHeader);
                output.Write(tiff);
                output.Write(jpeg, 2, jpeg.Length - 2);
            }

            return path;
        }

        private static (byte R, byte B) Pixel(Bitmap bitmap, int x, int y)
        {
            using var copy = new WriteableBitmap(bitmap.PixelSize, bitmap.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (ILockedFramebuffer buffer = copy.Lock())
            {
                bitmap.CopyPixels(buffer, AlphaFormat.Premul);
                unsafe
                {
                    byte* p = (byte*)buffer.Address + y * buffer.RowBytes + x * 4;
                    return (p[2], p[0]);
                }
            }
        }

        [AvaloniaFact]
        public void Load_AppliesRotateNinetyClockwise()
        {
            using Bitmap image = ImageLoader.Load(WriteJpeg(6), 0);
            Assert.NotNull(image);
            Assert.Equal(new PixelSize(100, 200), image.PixelSize);
            // Rotated clockwise, the image's left (red) half ends up on top.
            Assert.True(Pixel(image, 50, 20).R > 200);
            Assert.True(Pixel(image, 50, 180).B > 200);
        }

        [AvaloniaFact]
        public void Load_AppliesRotateNinetyCounterClockwise()
        {
            using Bitmap image = ImageLoader.Load(WriteJpeg(8), 0);
            Assert.Equal(new PixelSize(100, 200), image.PixelSize);
            Assert.True(Pixel(image, 50, 20).B > 200);
            Assert.True(Pixel(image, 50, 180).R > 200);
        }

        [AvaloniaFact]
        public void Load_BoundsTheDecodedSize()
        {
            using Bitmap image = ImageLoader.Load(WriteJpeg(1), 50);
            Assert.Equal(new PixelSize(50, 25), image.PixelSize);
        }

        [AvaloniaFact]
        public void Load_ReturnsNullForNonImages()
        {
            string path = Path.Combine(_root, "not-an-image.jpg");
            File.WriteAllText(path, "hello");
            Assert.Null(ImageLoader.Load(path, 100));
            Assert.Null(ImageLoader.Load(Path.Combine(_root, "missing.jpg"), 100));
        }
    }
}
