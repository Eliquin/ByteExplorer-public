// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core;
using ByteExplorer.Core.Native;
using LibVLCSharp.Shared;
using Serilog;
using SkiaSharp;

namespace ByteExplorer.Infrastructure
{
    /// <summary>One line of the details panel: "Dimensions  4032 × 3024".</summary>
    public sealed class FileDetailRow
    {
        public FileDetailRow(string label, string value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }
        public string Value { get; }
    }

    /// <summary>
    /// The extra facts the details panel shows for the selected item, read off the UI thread and only as far as needed:
    /// <list type="bullet">
    /// <item>Every item: where it is and when it was created (where the file system records it).</item>
    /// <item>Folders: how many items they contain (counting stops at <see cref="FolderCountLimit"/> or after a moment, then
    /// says "more than").</item>
    /// <item>Images: dimensions from the header (no decode); for JPEG also the date taken and the camera from EXIF.</item>
    /// <item>Audio and video: length, video resolution, title, artist and album, from LibVLC's parser (local only, with a
    /// timeout; skipped when LibVLC is not installed).</item>
    /// <item>ZIP archives: number of entries and their total uncompressed size.</item>
    /// </list>
    /// Anything unreadable is simply left out; this never throws except for cancellation.
    /// </summary>
    public static class FileMetadataReader
    {
        private const int FolderCountLimit = 100_000;
        private static readonly TimeSpan FolderCountBudget = TimeSpan.FromMilliseconds(400);
        private const int MediaParseTimeoutMs = 4000;

        public static async Task<IReadOnlyList<FileDetailRow>> ReadAsync(string path, bool isDirectory, CancellationToken token)
        {
            var rows = new List<FileDetailRow>();
            await Task.Run(() => ReadBasics(path, isDirectory, rows, token), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            if (isDirectory)
            {
                return rows;
            }

            if (FileCategoryClassifier.Matches(path, false, FileCategoryFilter.Images))
            {
                await Task.Run(() => ReadImage(path, rows), token).ConfigureAwait(false);
            }
            else if (FileCategoryClassifier.Matches(path, false, FileCategoryFilter.Videos) ||
                     FileCategoryClassifier.Matches(path, false, FileCategoryFilter.Music))
            {
                await ReadMediaAsync(path, rows, token).ConfigureAwait(false);
            }
            else if (string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Run(() => ReadZip(path, rows, token), token).ConfigureAwait(false);
            }

            return rows;
        }

        private static void ReadBasics(string path, bool isDirectory, List<FileDetailRow> rows, CancellationToken token)
        {
            string parent = PathUtil.GetParent(path);
            if (!string.IsNullOrEmpty(parent))
            {
                rows.Add(new FileDetailRow("Location", parent));
            }

            try
            {
                DateTime created = isDirectory ? Directory.GetCreationTime(path) : File.GetCreationTime(path);
                // Unix file systems without a birth time report the epoch (or the change time; nothing to do there).
                if (created.Year > 1980)
                {
                    rows.Add(new FileDetailRow("Created", created.ToString("g", CultureInfo.CurrentCulture)));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }

            if (!isDirectory)
            {
                return;
            }

            int files = 0;
            int folders = 0;
            bool more = false;
            DateTime deadline = DateTime.UtcNow + FolderCountBudget;
            try
            {
                foreach (var entry in NativeFileEnumerator.Enumerate(path, token))
                {
                    if (entry.IsDirectory)
                    {
                        folders++;
                    }
                    else
                    {
                        files++;
                    }

                    if (files + folders >= FolderCountLimit || ((files + folders) % 1024 == 0 && DateTime.UtcNow > deadline))
                    {
                        more = true;
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return;
            }

            string Count(int n, string one, string many) => n.ToString("N0", CultureInfo.CurrentCulture) + " " + (n == 1 ? one : many);
            string contains = files + folders == 0
                ? "Nothing"
                : Count(folders, "folder", "folders") + ", " + Count(files, "file", "files");
            rows.Add(new FileDetailRow("Contains", more ? "More than " + contains : contains));
        }

        private static void ReadImage(string path, List<FileDetailRow> rows)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16 * 1024);

                // EXIF first: SKCodec takes ownership of the stream and closes it.
                JpegExif exif = default;
                string extension = Path.GetExtension(path);
                if (string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase))
                {
                    exif = JpegExif.Read(stream);
                    stream.Position = 0;
                }

                using (var codec = SKCodec.Create(stream))
                {
                    if (codec != null && codec.Info.Width > 0)
                    {
                        // Dimensions as the image is shown: a 90°-rotated photo is taller than it is stored.
                        bool swapped = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
                        int width = swapped ? codec.Info.Height : codec.Info.Width;
                        int height = swapped ? codec.Info.Width : codec.Info.Height;
                        rows.Add(new FileDetailRow("Dimensions", $"{width:N0} × {height:N0}"));
                    }
                }

                if (exif.DateTaken.HasValue)
                {
                    rows.Add(new FileDetailRow("Date taken", exif.DateTaken.Value.ToString("g", CultureInfo.CurrentCulture)));
                }

                string camera = JoinCamera(exif.Make, exif.Model);
                if (!string.IsNullOrEmpty(camera))
                {
                    rows.Add(new FileDetailRow("Camera", camera));
                }
            }
            catch (Exception ex)
            {
                // A damaged or unusual file just shows fewer details.
                Log.Logger.Debug(ex, "Image details unavailable for {Path}", path);
            }
        }

        /// <summary>
        /// A picture's size as shown (rotation applied), when it was taken and with what camera, read from its header and
        /// EXIF only (the pixels are never decoded); zeros and nulls for what isn't there. For the Details columns.
        /// </summary>
        internal static (int Width, int Height, DateTime? Taken, string Camera) ReadImageFacts(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16 * 1024);
                JpegExif exif = default;
                string extension = Path.GetExtension(path);
                if (string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase))
                {
                    exif = JpegExif.Read(stream);
                    stream.Position = 0;
                }

                int width = 0, height = 0;
                using (var codec = SKCodec.Create(stream))
                {
                    if (codec != null && codec.Info.Width > 0)
                    {
                        bool swapped = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
                        width = swapped ? codec.Info.Height : codec.Info.Width;
                        height = swapped ? codec.Info.Width : codec.Info.Height;
                    }
                }

                return (width, height, exif.DateTaken, JoinCamera(exif.Make, exif.Model));
            }
            catch (Exception ex)
            {
                Log.Logger.Debug(ex, "Image facts unavailable for {Path}", path);
                return (0, 0, null, null);
            }
        }

        /// <summary>
        /// A song's or video's length, video size, artist and album (LibVLC's local parse: tags and headers, no playback);
        /// nulls when unavailable. For the Details columns.
        /// </summary>
        internal static async Task<(TimeSpan? Length, int Width, int Height, string Artist, string Album)> ReadMediaFactsAsync(string path, CancellationToken token)
        {
            LibVLC libVlc = SharedLibVlc();
            if (libVlc == null)
            {
                return (null, 0, 0, null, null);
            }

            try
            {
                using var media = new Media(libVlc, new Uri(path));
                MediaParsedStatus status = await media.Parse(MediaParseOptions.ParseLocal, MediaParseTimeoutMs, token).ConfigureAwait(false);
                if (status != MediaParsedStatus.Done)
                {
                    return (null, 0, 0, null, null);
                }

                int width = 0, height = 0;
                foreach (MediaTrack track in media.Tracks)
                {
                    if (track.TrackType == TrackType.Video && track.Data.Video.Width > 0)
                    {
                        width = (int)track.Data.Video.Width;
                        height = (int)track.Data.Video.Height;
                        break;
                    }
                }

                string artist = media.Meta(MetadataType.Artist);
                string album = media.Meta(MetadataType.Album);
                return (media.Duration > 0 ? TimeSpan.FromMilliseconds(media.Duration) : null, width, height,
                    string.IsNullOrWhiteSpace(artist) ? null : artist.Trim(), string.IsNullOrWhiteSpace(album) ? null : album.Trim());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Logger.Debug(ex, "Media facts unavailable for {Path}", path);
                return (null, 0, 0, null, null);
            }
        }

        /// <summary>"Canon" + "Canon EOS R5" reads "Canon EOS R5", not "Canon Canon EOS R5".</summary>
        private static string JoinCamera(string make, string model)
        {
            make = make?.Trim();
            model = model?.Trim();
            if (string.IsNullOrEmpty(model))
            {
                return make;
            }

            if (string.IsNullOrEmpty(make) || model.StartsWith(make, StringComparison.OrdinalIgnoreCase))
            {
                return model;
            }

            return make + " " + model;
        }

        private static void ReadZip(string path, List<FileDetailRow> rows, CancellationToken token)
        {
            try
            {
                using ZipArchive archive = ZipFile.OpenRead(path);
                int files = 0;
                long size = 0;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (!entry.FullName.EndsWith("/", StringComparison.Ordinal))
                    {
                        files++;
                        size += entry.Length;
                    }
                }

                rows.Add(new FileDetailRow("Archive", $"{files:N0} file{(files == 1 ? string.Empty : "s")}, {ViewModels.FileOperations.FileOperationJob.FormatBytes(size)} unpacked"));
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                Log.Logger.Debug(ex, "Archive details unavailable for {Path}", path);
            }
        }

        private static async Task ReadMediaAsync(string path, List<FileDetailRow> rows, CancellationToken token)
        {
            LibVLC libVlc = SharedLibVlc();
            if (libVlc == null)
            {
                return;
            }

            try
            {
                using var media = new Media(libVlc, new Uri(path));
                MediaParsedStatus status = await media.Parse(MediaParseOptions.ParseLocal, MediaParseTimeoutMs, token).ConfigureAwait(false);
                if (status != MediaParsedStatus.Done)
                {
                    return;
                }

                if (media.Duration > 0)
                {
                    rows.Add(new FileDetailRow("Length", FormatDuration(TimeSpan.FromMilliseconds(media.Duration))));
                }

                foreach (MediaTrack track in media.Tracks)
                {
                    if (track.TrackType == TrackType.Video && track.Data.Video.Width > 0)
                    {
                        rows.Add(new FileDetailRow("Resolution", $"{track.Data.Video.Width} × {track.Data.Video.Height}"));
                        break;
                    }
                }

                AddMeta(rows, "Title", media.Meta(MetadataType.Title), Path.GetFileName(path));
                AddMeta(rows, "Artist", media.Meta(MetadataType.Artist), null);
                AddMeta(rows, "Album", media.Meta(MetadataType.Album), null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Logger.Debug(ex, "Media details unavailable for {Path}", path);
            }
        }

        private static void AddMeta(List<FileDetailRow> rows, string label, string value, string ignoreIfEqual)
        {
            // LibVLC reports the file name as the title of an untagged file: not worth a row.
            if (!string.IsNullOrWhiteSpace(value) && !string.Equals(value, ignoreIfEqual, StringComparison.Ordinal))
            {
                rows.Add(new FileDetailRow(label, value.Trim()));
            }
        }

        internal static string FormatDuration(TimeSpan duration) =>
            duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
                : $"{duration.Minutes}:{duration.Seconds:00}";

        /// <summary>The app's one LibVLC instance (shared with playback, see MediaEngine.Shared); null when unavailable.</summary>
        private static LibVLC SharedLibVlc() => MediaEngine.Shared;
    }

    /// <summary>
    /// The few EXIF tags the details panel shows, read from a JPEG's APP1 segment: DateTimeOriginal (or DateTime), Make
    /// and Model. Bounded and defensive: a malformed file yields nothing rather than throwing or reading far.
    /// </summary>
    internal readonly struct JpegExif
    {
        private const int MaxSegmentScan = 64;

        public JpegExif(DateTime? dateTaken, string make, string model)
        {
            DateTaken = dateTaken;
            Make = make;
            Model = model;
        }

        public DateTime? DateTaken { get; }
        public string Make { get; }
        public string Model { get; }

        public static JpegExif Read(Stream stream)
        {
            try
            {
                var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
                if (reader.ReadByte() != 0xFF || reader.ReadByte() != 0xD8)
                {
                    return default;
                }

                for (int i = 0; i < MaxSegmentScan; i++)
                {
                    if (reader.ReadByte() != 0xFF)
                    {
                        return default;
                    }

                    byte marker = reader.ReadByte();
                    if (marker == 0xD9 || marker == 0xDA)
                    {
                        return default; // end of image, or start of the pixel data: no EXIF before it
                    }

                    int length = (reader.ReadByte() << 8) | reader.ReadByte();
                    if (length < 2)
                    {
                        return default;
                    }

                    if (marker == 0xE1)
                    {
                        byte[] segment = reader.ReadBytes(length - 2);
                        return Parse(segment);
                    }

                    stream.Seek(length - 2, SeekOrigin.Current);
                }
            }
            catch (EndOfStreamException)
            {
            }

            return default;
        }

        /// <summary>Parses an APP1 payload ("Exif\0\0" + TIFF). Public behaviour is tested through crafted segments.</summary>
        internal static JpegExif Parse(byte[] segment)
        {
            if (segment.Length < 14 || Encoding.ASCII.GetString(segment, 0, 4) != "Exif")
            {
                return default;
            }

            const int tiff = 6;
            bool little = segment[tiff] == 'I' && segment[tiff + 1] == 'I';
            bool big = segment[tiff] == 'M' && segment[tiff + 1] == 'M';
            if (!little && !big)
            {
                return default;
            }

            ushort U16(int offset) => offset + 2 > segment.Length ? (ushort)0 : little
                ? (ushort)(segment[offset] | (segment[offset + 1] << 8))
                : (ushort)((segment[offset] << 8) | segment[offset + 1]);
            uint U32(int offset) => offset + 4 > segment.Length ? 0u : little
                ? (uint)(segment[offset] | (segment[offset + 1] << 8) | (segment[offset + 2] << 16) | (segment[offset + 3] << 24))
                : (uint)((segment[offset] << 24) | (segment[offset + 1] << 16) | (segment[offset + 2] << 8) | segment[offset + 3]);

            string Ascii(int entry)
            {
                uint count = U32(entry + 4);
                if (count == 0 || count > 256)
                {
                    return null;
                }

                int start = count <= 4 ? entry + 8 : tiff + (int)U32(entry + 8);
                if (start < 0 || start + count > segment.Length)
                {
                    return null;
                }

                return Encoding.ASCII.GetString(segment, start, (int)count).TrimEnd('\0', ' ');
            }

            string make = null, model = null, dateTime = null, original = null;
            uint exifIfd = 0;
            void ScanIfd(int ifd, bool isExifIfd)
            {
                if (ifd <= tiff || ifd + 2 > segment.Length)
                {
                    return;
                }

                int count = Math.Min(U16(ifd), (ushort)256);
                for (int i = 0; i < count; i++)
                {
                    int entry = ifd + 2 + i * 12;
                    if (entry + 12 > segment.Length)
                    {
                        return;
                    }

                    ushort tag = U16(entry);
                    switch (tag)
                    {
                        case 0x010F when !isExifIfd: make = Ascii(entry); break;
                        case 0x0110 when !isExifIfd: model = Ascii(entry); break;
                        case 0x0132 when !isExifIfd: dateTime = Ascii(entry); break;
                        case 0x8769 when !isExifIfd: exifIfd = U32(entry + 8); break;
                        case 0x9003 when isExifIfd: original = Ascii(entry); break;
                    }
                }
            }

            ScanIfd(tiff + (int)U32(tiff + 4), false);
            if (exifIfd > 0 && exifIfd < segment.Length)
            {
                ScanIfd(tiff + (int)exifIfd, true);
            }

            return new JpegExif(ParseExifDate(original) ?? ParseExifDate(dateTime), make, model);
        }

        /// <summary>EXIF dates are "yyyy:MM:dd HH:mm:ss" in the camera's local time.</summary>
        private static DateTime? ParseExifDate(string text) =>
            !string.IsNullOrEmpty(text) && DateTime.TryParseExact(text, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime value)
                ? value
                : null;
    }
}
