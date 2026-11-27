// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace ByteExplorer.Core.Services.FileOperations
{
    /// <summary>
    /// The names the copy engine gives its temporary files (".report.pdf.byteexplorer-&lt;id&gt;.partial", beside the final
    /// name until the copy is verified). One definition for everything that must recognise them: crash recovery deletes
    /// only these, and listings treat them as hidden on every OS. The leading dot hides them on Linux and macOS only, so
    /// on Windows a copy in progress used to appear in the file list as a second, growing file.
    /// </summary>
    public static class OperationTemporaries
    {
        public const string Marker = ".byteexplorer-";
        public const string Suffix = ".partial";

        public static bool IsTemporaryName(string name) =>
            !string.IsNullOrEmpty(name) && name.StartsWith(".", StringComparison.Ordinal) &&
            name.EndsWith(Suffix, StringComparison.Ordinal) && name.Contains(Marker, StringComparison.Ordinal);
    }
}
