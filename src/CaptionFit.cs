using System;
using System.Globalization;

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

        /// <summary>The queue count after the name: "  (N left)" when more than one waypoint is queued, else "".</summary>
        public static string Suffix(int count)
        {
            return count > 1 ? string.Format(CultureInfo.InvariantCulture, "  ({0} left)", count) : "";
        }
    }

    /// <summary>
    /// The arrow's name caption, built and measured only when the name, the queue count or the width available changes,
    /// so a frame usually costs no measuring. Unity-free: the measure is handed in (the arrow's is the label style's
    /// CalcSize; the tests use their own). Texts are compared with string.Equals, not by reference: Localization hands
    /// back a new string on every call for a name it does not cache, which would otherwise be measured every frame. A
    /// key is set only once its measuring is done, so a measure that throws is tried again on the next call.
    /// </summary>
    internal sealed class CaptionCache
    {
        private readonly Func<string, float> _measure;

        private string _nameSource, _nameCaption;
        private int _nameCount = -1;

        private string _fitSource, _fitText;
        private float _fitMaxWidth = -1f;
        private float _fitWidth;

        public CaptionCache(Func<string, float> measure)
        {
            _measure = measure;
        }

        /// <summary>name + CaptionFit.Suffix(count), rebuilt only when either changes.</summary>
        public string NameCaption(string name, int count)
        {
            if (count <= 1) return name;
            if (!string.Equals(name, _nameSource) || count != _nameCount || _nameCaption == null)
            {
                string caption = name + CaptionFit.Suffix(count);
                _nameCaption = caption;
                _nameSource = name;
                _nameCount = count;
            }
            return _nameCaption;
        }

        /// <summary>
        /// The name caption fitted to <paramref name="maxWidth"/> (CaptionFit), and the width of its box: the text's
        /// width plus 2 px for the outline drawn one pixel either side.
        /// </summary>
        public string FittedName(string name, int count, float maxWidth, out float width)
        {
            string caption = NameCaption(name, count);
            if (!string.Equals(caption, _fitSource) || maxWidth != _fitMaxWidth || _fitText == null)
            {
                string text = _measure(caption) <= maxWidth ? caption : CaptionFit.Fit(name, CaptionFit.Suffix(count), maxWidth, _measure);
                float fitted = _measure(text) + 2f;
                _fitText = text;
                _fitWidth = fitted;
                _fitSource = caption;
                _fitMaxWidth = maxWidth;
            }
            width = _fitWidth;
            return _fitText;
        }
    }

    /// <summary>One caption's box width (its text measured, plus 2 px for the outline), measured only when the text changes.</summary>
    internal sealed class CaptionWidth
    {
        private string _for;
        private float _width;

        public float Of(string text, Func<string, float> measure)
        {
            if (!string.Equals(text, _for))
            {
                float w = measure(text) + 2f;
                _width = w;
                _for = text;
            }
            return _width;
        }
    }
}
