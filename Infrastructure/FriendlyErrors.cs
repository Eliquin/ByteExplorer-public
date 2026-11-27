// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.ComponentModel;
using System.IO;
using System.Security;

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// Turns an exception into a sentence a person can act on, for dialogs and the status bar. The operating system's
    /// messages for file errors are kept when they read well ("The process cannot access the file…"); programming errors
    /// ("Object reference not set…") and messages carrying codes (HRESULT, 0x80…) become a plain sentence pointing to the
    /// log, where the details are written. Whoever shows the message logs the exception itself.
    /// </summary>
    public static class FriendlyErrors
    {
        private const int SharingViolation = unchecked((int)0x80070020);
        private const int LockViolation = unchecked((int)0x80070021);
        private const int DiskFull = unchecked((int)0x80070070);
        private const int HandleDiskFull = unchecked((int)0x80070027);

        /// <summary>The generic sentence for a failure with nothing more useful to say.</summary>
        public const string Generic = "Something went wrong. The details were written to the log (Settings › About › Logs).";

        /// <summary>A person-readable reason for the failure (never empty).</summary>
        public static string Explain(Exception ex)
        {
            switch (ex)
            {
                case null:
                    return Generic;
                case OperationCanceledException:
                    return "It was cancelled.";
                case AggregateException aggregate when aggregate.InnerExceptions.Count == 1:
                    return Explain(aggregate.InnerException);
                case UnauthorizedAccessException:
                case SecurityException:
                    return "Access was denied. You may not have permission, or the item is in use or read-only.";
                case FileNotFoundException:
                case DirectoryNotFoundException:
                    return "The item couldn't be found. It may have been moved, renamed or deleted.";
                case PathTooLongException:
                    return "The path is too long.";
                case DriveNotFoundException:
                    return "The drive isn't available.";
                case IOException io when io.HResult == SharingViolation || io.HResult == LockViolation:
                    return "The item is open in another program. Close it and try again.";
                case IOException io when io.HResult == DiskFull || io.HResult == HandleDiskFull:
                    return "There isn't enough space on the disk.";
                case IOException:
                case InvalidDataException:
                case Win32Exception:
                case System.Security.Cryptography.CryptographicException:
                    return Readable(ex.Message) ? ex.Message.Trim() : Generic;
                default:
                    return Generic; // a bug, not something the person can fix: details in the log
            }
        }

        /// <summary>"Lead. Reason" as one message, for a dialog's body.</summary>
        public static string With(string lead, Exception ex) => lead + "\n\n" + Explain(ex);

        private static bool Readable(string message)
        {
            if (string.IsNullOrWhiteSpace(message) || message.Length > 400)
            {
                return false;
            }

            return message.IndexOf("HRESULT", StringComparison.OrdinalIgnoreCase) < 0
                && message.IndexOf("0x8", StringComparison.OrdinalIgnoreCase) < 0
                && message.IndexOf("Exception", StringComparison.Ordinal) < 0
                && message.IndexOf(" at ", StringComparison.Ordinal) < 0;
        }
    }
}
