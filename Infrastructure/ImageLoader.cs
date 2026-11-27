// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// Decodes an image for display with a bounded size and its EXIF orientation applied, on any thread.
    /// <para>
    /// Built on SkiaSharp (Avalonia's own renderer, so no extra dependency): <see cref="SKCodec"/> reads the header
    /// without decoding, reports the EXIF origin for JPEG/HEIF/WebP, and for JPEG decodes directly at 1/2, 1/4 or 1/8
    /// scale (DCT scaling), so a 48 MP photo shown in a 1000-pixel pane never decodes 48 MP of pixels. The decoded
    /// bitmap is resized to the bound with high-quality filtering, rotated/flipped per the EXIF origin, and copied into an
    /// immutable Avalonia <see cref="Bitmap"/>, which may be handed to the UI thread.
    /// </para>
    /// <para>
    /// The file is opened with FileShare.ReadWrite|Delete, as the WPF version did, so previewing never blocks another
    /// program from saving or deleting the file. Returns null for anything that is not a decodable image.
    /// </para>
    /// </summary>
    public static class ImageLoader
    {
        public static Bitmap Load(string path, int maxSide)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
                using var codec = SKCodec.Create(stream);
                if (codec == null)
                {
                    return null;
                }

                SKImageInfo full = codec.Info;
                if (full.Width <= 0 || full.Height <= 0)
                {
                    return null;
                }

                // Pick the smallest codec-native scale that is still at least the target size.
                float scale = maxSide > 0 && Math.Max(full.Width, full.Height) > maxSide
                    ? (float)maxSide / Math.Max(full.Width, full.Height)
                    : 1f;
                SKSizeI decodeSize = scale < 1f ? codec.GetScaledDimensions(scale) : full.Size;
                if (decodeSize.Width < full.Width * scale || decodeSize.Height < full.Height * scale)
                {
                    decodeSize = full.Size;
                }

                var decodeInfo = new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var decoded = new SKBitmap(decodeInfo);
                SKCodecResult result = codec.GetPixels(decodeInfo, decoded.GetPixels());
                if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
                {
                    return null;
                }

                SKBitmap sized = decoded;
                int targetWidth = Math.Max(1, (int)Math.Round(full.Width * scale));
                int targetHeight = Math.Max(1, (int)Math.Round(full.Height * scale));
                if (decoded.Width != targetWidth || decoded.Height != targetHeight)
                {
                    sized = decoded.Resize(new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.High) ?? decoded;
                }

                try
                {
                    using SKBitmap oriented = ApplyOrigin(sized, codec.EncodedOrigin);
                    return ToAvaloniaBitmap(oriented ?? sized);
                }
                finally
                {
                    if (!ReferenceEquals(sized, decoded))
                    {
                        sized.Dispose();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException ||
                                       ex is NotSupportedException || ex is OutOfMemoryException || ex is InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>A new bitmap with the EXIF origin applied, or null when the origin is already top-left.</summary>
        private static SKBitmap ApplyOrigin(SKBitmap source, SKEncodedOrigin origin)
        {
            if (origin == SKEncodedOrigin.TopLeft || origin == SKEncodedOrigin.Default)
            {
                return null;
            }

            bool swaps = origin == SKEncodedOrigin.LeftTop || origin == SKEncodedOrigin.RightTop ||
                         origin == SKEncodedOrigin.RightBottom || origin == SKEncodedOrigin.LeftBottom;
            int width = swaps ? source.Height : source.Width;
            int height = swaps ? source.Width : source.Height;

            var result = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(result))
            {
                switch (origin)
                {
                    case SKEncodedOrigin.TopRight: // mirrored horizontally
                        canvas.Scale(-1, 1, width / 2f, 0);
                        break;
                    case SKEncodedOrigin.BottomRight: // rotated 180
                        canvas.RotateDegrees(180, width / 2f, height / 2f);
                        break;
                    case SKEncodedOrigin.BottomLeft: // mirrored vertically
                        canvas.Scale(1, -1, 0, height / 2f);
                        break;
                    case SKEncodedOrigin.LeftTop: // transposed
                        canvas.Scale(-1, 1, width / 2f, 0);
                        canvas.Translate(width, 0);
                        canvas.RotateDegrees(90);
                        break;
                    case SKEncodedOrigin.RightTop: // rotated 90 clockwise
                        canvas.Translate(width, 0);
                        canvas.RotateDegrees(90);
                        break;
                    case SKEncodedOrigin.RightBottom: // transverse
                        canvas.Scale(-1, 1, width / 2f, 0);
                        canvas.Translate(0, height);
                        canvas.RotateDegrees(270);
                        break;
                    case SKEncodedOrigin.LeftBottom: // rotated 90 counter-clockwise
                        canvas.Translate(0, height);
                        canvas.RotateDegrees(270);
                        break;
                }

                canvas.DrawBitmap(source, 0, 0);
            }

            return result;
        }

        private static Bitmap ToAvaloniaBitmap(SKBitmap bitmap)
        {
            // The Bitmap constructor copies the pixels, so the SKBitmap can be disposed right after.
            return new Bitmap(
                PixelFormat.Bgra8888,
                AlphaFormat.Premul,
                bitmap.GetPixels(),
                new PixelSize(bitmap.Width, bitmap.Height),
                new Vector(96, 96),
                bitmap.RowBytes);
        }
    }
}
