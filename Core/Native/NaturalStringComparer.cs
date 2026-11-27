// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ByteExplorer.Core.Native
{
    /// <summary>
    /// File-name order as people expect it: case ignored and runs of digits compared by value, so "file2" comes before
    /// "file10" and "IMG_9" before "img_10". On Windows it is the shell's own StrCmpLogicalW, so the order matches
    /// Explorer exactly. Elsewhere a managed implementation of the same rule (GNOME Files and Finder sort the same way):
    /// it used to fall back to plain case-insensitive order there, which put "file10" before "file2".
    /// </summary>
    public sealed class NaturalStringComparer : IComparer<string>
    {
        public static readonly NaturalStringComparer Instance = new NaturalStringComparer();

        private static readonly bool UseShell = OperatingSystem.IsWindows() && Probe();

        public int Compare(string x, string y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;
            return UseShell ? StrCmpLogicalW(x, y) : CompareManaged(x, y);
        }

        /// <summary>
        /// The managed rule, on any platform: digit runs by numeric value (any length: compared as digit strings without
        /// their leading zeros, so no overflow), other characters case-insensitively (ordinal upper case, with an ASCII
        /// fast path). Ties: fewer leading zeros first ("1" before "01"), then the ordinal order, so two names that differ
        /// only in case ("a" and "A" on a case-sensitive disk) still have a fixed order. No allocation.
        /// </summary>
        public static int CompareManaged(ReadOnlySpan<char> x, ReadOnlySpan<char> y)
        {
            // Skip the identical start in one vectorized step (names in one folder often share long prefixes: "IMG_20",
            // "Report 2024-"), backing up to the start of a digit run cut by it so that run is still compared by value.
            int prefix = x.CommonPrefixLength(y);
            while (prefix > 0 && (uint)(x[prefix - 1] - '0') <= 9)
            {
                prefix--;
            }

            int i = prefix;
            int j = prefix;
            int zerosTieBreak = 0;
            while (i < x.Length && j < y.Length)
            {
                char a = x[i];
                char b = y[j];
                bool aDigit = (uint)(a - '0') <= 9;
                bool bDigit = (uint)(b - '0') <= 9;
                if (aDigit && bDigit)
                {
                    int aStart = i;
                    int bStart = j;
                    while (i < x.Length && x[i] == '0') i++;
                    while (j < y.Length && y[j] == '0') j++;
                    int aDigitsStart = i;
                    int bDigitsStart = j;
                    while (i < x.Length && (uint)(x[i] - '0') <= 9) i++;
                    while (j < y.Length && (uint)(y[j] - '0') <= 9) j++;
                    int aLength = i - aDigitsStart;
                    int bLength = j - bDigitsStart;
                    if (aLength != bLength)
                    {
                        return aLength < bLength ? -1 : 1; // more significant digits = larger number
                    }

                    int digits = x.Slice(aDigitsStart, aLength).SequenceCompareTo(y.Slice(bDigitsStart, bLength));
                    if (digits != 0)
                    {
                        return digits < 0 ? -1 : 1;
                    }

                    if (zerosTieBreak == 0)
                    {
                        int aZeros = aDigitsStart - aStart;
                        int bZeros = bDigitsStart - bStart;
                        zerosTieBreak = aZeros.CompareTo(bZeros);
                    }

                    continue;
                }

                if (a != b)
                {
                    char ua = a < 128 ? (char)(a >= 'a' && a <= 'z' ? a - 32 : a) : char.ToUpperInvariant(a);
                    char ub = b < 128 ? (char)(b >= 'a' && b <= 'z' ? b - 32 : b) : char.ToUpperInvariant(b);
                    if (ua != ub)
                    {
                        return ua < ub ? -1 : 1;
                    }
                }

                i++;
                j++;
            }

            int remaining = (x.Length - i).CompareTo(y.Length - j);
            if (remaining != 0)
            {
                return remaining; // a name that is a prefix of the other comes first
            }

            if (zerosTieBreak != 0)
            {
                return zerosTieBreak;
            }

            int ordinal = x.SequenceCompareTo(y);
            return ordinal < 0 ? -1 : ordinal > 0 ? 1 : 0;
        }

        private static bool Probe()
        {
            try
            {
                StrCmpLogicalW("a", "b");
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int StrCmpLogicalW(string psz1, string psz2);
    }
}
