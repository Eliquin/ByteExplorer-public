// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace ByteExplorer.Core.Omnibar
{
    /// <summary>
    /// Fuzzy matching for the omnibar, in the style of fzf/VS Code's quick open: every character of the pattern must
    /// appear in the candidate in order (case-insensitive); the score rewards matches at the start, at word starts
    /// ("mp" scores higher on "my-photos" than on "temp"), and runs of consecutive characters, and penalises gaps. Returns the matched positions so the list can bold them.
    /// <para>
    /// The scattered match comes from fzf v1's two passes (forward to where the first complete match ends, backward to its
    /// tightest start); every contiguous occurrence is scored as well and the best wins, so "doc" on "xdxoxc docs"
    /// highlights the word "docs", not the scattered letters. No allocation unless there is a match. Candidate length is capped.
    /// </para>
    /// </summary>
    public static class FuzzyMatcher
    {
        private const int MaxCandidateLength = 512;

        private const int ScoreMatch = 16;
        private const int BonusConsecutive = 10;
        private const int BonusWordStart = 12;
        private const int BonusFirstChar = 20;
        private const int BonusPrefix = 40;
        private const int BonusExact = 60;
        private const int PenaltyGap = 2;
        private const int MaxGapPenalty = 24;

        /// <summary>
        /// Matches <paramref name="pattern"/> against <paramref name="candidate"/>. Returns the score (higher is better,
        /// always &gt; 0 for a match) or -1 when it doesn't match; <paramref name="positions"/> gets the matched character
        /// indexes in ascending order (null when no match). An empty pattern matches everything with score 1.
        /// </summary>
        public static int Score(string pattern, string candidate, out int[] positions)
        {
            positions = null;
            if (string.IsNullOrEmpty(pattern))
            {
                positions = Array.Empty<int>();
                return 1;
            }

            if (string.IsNullOrEmpty(candidate) || pattern.Length > candidate.Length || candidate.Length > MaxCandidateLength)
            {
                return -1;
            }

            // Forward: where does the first complete in-order match end?
            int p = 0;
            int end = -1;
            for (int i = 0; i < candidate.Length; i++)
            {
                if (EqualsIgnoreCase(candidate[i], pattern[p]) && ++p == pattern.Length)
                {
                    end = i;
                    break;
                }
            }

            if (end < 0)
            {
                return -1;
            }

            // Backward from that end: the tightest window.
            int start = end;
            p = pattern.Length - 1;
            for (int i = end; i >= 0; i--)
            {
                if (EqualsIgnoreCase(candidate[i], pattern[p]))
                {
                    if (--p < 0)
                    {
                        start = i;
                        break;
                    }
                }
            }

            // The scattered match inside that window...
            int[] matched = new int[pattern.Length];
            p = 0;
            for (int i = start; i <= end && p < pattern.Length; i++)
            {
                if (EqualsIgnoreCase(candidate[i], pattern[p]))
                {
                    matched[p++] = i;
                }
            }

            int best = ScorePositions(candidate, pattern.Length, matched);

            // ...against every contiguous occurrence (a word beats the same letters scattered inside other words).
            int found = candidate.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            int[] contiguous = null;
            while (found >= 0)
            {
                contiguous ??= new int[pattern.Length];
                for (int k = 0; k < pattern.Length; k++)
                {
                    contiguous[k] = found + k;
                }

                int score = ScorePositions(candidate, pattern.Length, contiguous);
                if (score > best)
                {
                    best = score;
                    Array.Copy(contiguous, matched, pattern.Length);
                }

                found = found + 1 < candidate.Length ? candidate.IndexOf(pattern, found + 1, StringComparison.OrdinalIgnoreCase) : -1;
            }

            // Shorter candidates win ties ("Docs" over "Documents and settings" for "doc").
            positions = matched;
            return Math.Max(1, best - candidate.Length / 8);
        }

        private static int ScorePositions(string candidate, int patternLength, int[] matched)
        {
            int score = 0;
            int gapPenalty = 0;
            for (int k = 0; k < patternLength; k++)
            {
                int i = matched[k];
                score += ScoreMatch;
                if (k > 0)
                {
                    if (i == matched[k - 1] + 1)
                    {
                        score += BonusConsecutive;
                    }
                    else
                    {
                        gapPenalty += (i - matched[k - 1] - 1) * PenaltyGap;
                    }
                }

                if (IsWordStart(candidate, i))
                {
                    score += BonusWordStart;
                }
            }

            score -= Math.Min(gapPenalty, MaxGapPenalty);
            if (matched[0] == 0)
            {
                score += BonusFirstChar;
                if (matched[patternLength - 1] == patternLength - 1)
                {
                    score += BonusPrefix;
                    if (candidate.Length == patternLength)
                    {
                        score += BonusExact;
                    }
                }
            }

            return score;
        }

        /// <summary>True when the candidate starts with the pattern (case-insensitive): the strongest kind of match.</summary>
        public static bool IsPrefix(string pattern, string candidate) =>
            !string.IsNullOrEmpty(candidate) && candidate.StartsWith(pattern ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        private static bool IsWordStart(string text, int index)
        {
            if (index == 0)
            {
                return true;
            }

            char before = text[index - 1];
            char current = text[index];
            if (before == ' ' || before == '-' || before == '_' || before == '.' || before == '/' || before == '\\' || before == '(' || before == '[')
            {
                return true;
            }

            // camelCase and digit boundaries: "myDocuments", "photos2024".
            return (char.IsLower(before) && char.IsUpper(current)) || (!char.IsDigit(before) && char.IsDigit(current));
        }

        private static bool EqualsIgnoreCase(char a, char b) =>
            a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
    }
}
