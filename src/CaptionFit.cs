using System;

namespace Waypointer
{
    /// <summary>
    /// Fits the arrow's name caption to the width available on screen, kept free of Unity so the tests can exercise
    /// it with any measure. A caption that fits is returned unchanged; one that does not keeps its suffix (the queue
    /// count, "  (3 left)") and loses the end of the name instead, marked with "...".
    /// </summary>
    internal static class CaptionFit
    {
        public const string Ellipsis = "...";

        /// <summary>
        /// The caption "name + suffix", or, when that is wider than <paramref name="maxWidth"/> by
        /// <paramref name="measure"/>, the longest start of the name that fits followed by "..." and the suffix.
        /// Trailing spaces are not part of the name: one that fits without them is returned whole, without "...".
        /// A cut never falls between the two halves of a surrogate pair (an emoji, say).
        /// When not even "..." and the suffix fit, "..." and the suffix are returned (the caller's box clips them).
        /// The measure must not shrink as text grows (true of any font's width).
        /// </summary>
        public static string Fit(string name, string suffix, float maxWidth, Func<string, float> measure)
        {
            if (name == null) name = "";
            if (suffix == null) suffix = "";
            string whole = name + suffix;
            if (measure(whole) <= maxWidth) return whole;

            string trimmed = name.TrimEnd();
            if (trimmed.Length < name.Length && measure(trimmed + suffix) <= maxWidth) return trimmed + suffix;
            name = trimmed;

            // Binary search for the longest prefix of the name that fits with the ellipsis and the suffix.
            int lo = 0, hi = name.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (measure(Shortened(name, mid, suffix)) <= maxWidth) lo = mid;
                else hi = mid - 1;
            }
            return Shortened(name, lo, suffix);
        }

        private static string Shortened(string name, int keep, string suffix)
        {
            // Half a surrogate pair is not a character: cut before the pair instead.
            if (keep > 0 && keep < name.Length && char.IsHighSurrogate(name[keep - 1])) keep--;
            return name.Substring(0, keep).TrimEnd() + Ellipsis + suffix;
        }
    }
}
