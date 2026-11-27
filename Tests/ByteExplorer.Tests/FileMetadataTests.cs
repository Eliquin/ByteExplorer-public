// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Infrastructure;
using SkiaSharp;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>The details panel's extra facts: image dimensions and EXIF, folder contents, archive summary.</summary>
    public sealed class FileMetadataTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-meta-" + Guid.NewGuid().ToString("N"));

        public FileMetadataTests() => Directory.CreateDirectory(_root);

        public void Dispose() => Directory.Delete(_root, true);

        [Fact]
        public async Task Jpeg_ShowsDimensionsDateTakenAndCamera()
        {
            string path = Path.Combine(_root, "photo.jpg");
            File.WriteAllBytes(path, JpegWithExif(64, 32, "Canon", "Canon EOS R5", "2024:07:14 18:30:05"));

            IReadOnlyList<FileDetailRow> rows = await FileMetadataReader.ReadAsync(path, false, CancellationToken.None);

            Assert.Equal("64 × 32", Value(rows, "Dimensions"));
            Assert.Equal(new DateTime(2024, 7, 14, 18, 30, 5).ToString("g"), Value(rows, "Date taken"));
            Assert.Equal("Canon EOS R5", Value(rows, "Camera")); // not "Canon Canon EOS R5"
            Assert.Equal(_root, Value(rows, "Location"));
        }

        [Fact]
        public async Task Png_ShowsDimensions_WithoutExifRows()
        {
            string path = Path.Combine(_root, "image.png");
            using (var bitmap = new SKBitmap(10, 20))
            using (SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100))
            {
                File.WriteAllBytes(path, data.ToArray());
            }

            IReadOnlyList<FileDetailRow> rows = await FileMetadataReader.ReadAsync(path, false, CancellationToken.None);

            Assert.Equal("10 × 20", Value(rows, "Dimensions"));
            Assert.Null(Value(rows, "Camera"));
        }

        [Fact]
        public async Task Folder_CountsFoldersAndFiles()
        {
            string folder = Path.Combine(_root, "box");
            Directory.CreateDirectory(Path.Combine(folder, "a"));
            File.WriteAllText(Path.Combine(folder, "x.txt"), "x");
            File.WriteAllText(Path.Combine(folder, "y.txt"), "y");

            IReadOnlyList<FileDetailRow> rows = await FileMetadataReader.ReadAsync(folder, true, CancellationToken.None);

            Assert.Equal("1 folder, 2 files", Value(rows, "Contains"));
        }

        [Fact]
        public async Task Zip_ShowsEntriesAndUnpackedSize()
        {
            string path = Path.Combine(_root, "pack.zip");
            using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (string name in new[] { "a.txt", "dir/b.txt" })
                {
                    using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                    writer.Write(new string('z', 1000));
                }

                zip.CreateEntry("empty/");
            }

            IReadOnlyList<FileDetailRow> rows = await FileMetadataReader.ReadAsync(path, false, CancellationToken.None);

            Assert.StartsWith("2 files, ", Value(rows, "Archive"));
        }

        [Fact]
        public void Exif_IgnoresGarbage()
        {
            Assert.Null(JpegExif.Parse(new byte[3]).Make);
            Assert.Null(JpegExif.Parse(Encoding.ASCII.GetBytes("Exif\0\0XX\0\0\0\0\0\0\0\0")).Model);
            byte[] corrupt = ExifSegment("Make", "Model", "2024:01:01 00:00:00");
            corrupt[corrupt.Length / 2] = 0xFF; // damage the middle: must not throw
            JpegExif.Parse(corrupt);
        }

        [Fact]
        public void Duration_IsFormattedLikeAPlayer()
        {
            Assert.Equal("3:07", FileMetadataReader.FormatDuration(TimeSpan.FromSeconds(187)));
            Assert.Equal("1:02:03", FileMetadataReader.FormatDuration(new TimeSpan(1, 2, 3)));
        }

        private static string Value(IReadOnlyList<FileDetailRow> rows, string label) => rows.FirstOrDefault(r => r.Label == label)?.Value;

        /// <summary>A real JPEG (Skia-encoded) with an APP1 EXIF segment inserted after SOI.</summary>
        private static byte[] JpegWithExif(int width, int height, string make, string model, string date)
        {
            byte[] jpeg;
            using (var bitmap = new SKBitmap(width, height))
            using (SKData data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 80))
            {
                jpeg = data.ToArray();
            }

            byte[] segment = ExifSegment(make, model, date);
            var output = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE1, (byte)((segment.Length + 2) >> 8), (byte)((segment.Length + 2) & 0xFF) };
            output.AddRange(segment);
            output.AddRange(jpeg.Skip(2));
            return output.ToArray();
        }

        /// <summary>"Exif\0\0" + little-endian TIFF: IFD0 (Make, Model, ExifIFD pointer) and an Exif IFD (DateTimeOriginal).</summary>
        private static byte[] ExifSegment(string make, string model, string date)
        {
            var tiff = new List<byte>();
            void U16(int v) { tiff.Add((byte)v); tiff.Add((byte)(v >> 8)); }
            void U32(int v) { U16(v & 0xFFFF); U16(v >> 16); }

            byte[] makeBytes = Encoding.ASCII.GetBytes(make + "\0");
            byte[] modelBytes = Encoding.ASCII.GetBytes(model + "\0");
            byte[] dateBytes = Encoding.ASCII.GetBytes(date + "\0");

            const int ifd0 = 8;
            const int ifd0Size = 2 + 3 * 12 + 4;
            const int exifIfd = ifd0 + ifd0Size;
            const int exifIfdSize = 2 + 12 + 4;
            int data = exifIfd + exifIfdSize;

            tiff.AddRange(new byte[] { (byte)'I', (byte)'I', 42, 0 });
            U32(ifd0);
            U16(3);
            U16(0x010F); U16(2); U32(makeBytes.Length); U32(data);
            U16(0x0110); U16(2); U32(modelBytes.Length); U32(data + makeBytes.Length);
            U16(0x8769); U16(4); U32(1); U32(exifIfd);
            U32(0);
            U16(1);
            U16(0x9003); U16(2); U32(dateBytes.Length); U32(data + makeBytes.Length + modelBytes.Length);
            U32(0);
            tiff.AddRange(makeBytes);
            tiff.AddRange(modelBytes);
            tiff.AddRange(dateBytes);

            var segment = new List<byte>(Encoding.ASCII.GetBytes("Exif\0\0"));
            segment.AddRange(tiff);
            return segment.ToArray();
        }
    }
}
