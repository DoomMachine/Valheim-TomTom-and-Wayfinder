using System;

namespace Waypointer
{
    /// <summary>Translates game text for display.</summary>
    public static class GameText
    {
        /// <summary>
        /// Resolves Valheim localization tokens in a pin name. Some vanilla markers are named with a
        /// token rather than text - the tombstone marker is "$hud_mapday 9", for example - and following
        /// one otherwise shows the raw token under the arrow. Plain names pass through unchanged.
        ///
        /// Only ever applied for display: the stored name keeps the token, so the text follows the
        /// game language if it changes.
        /// </summary>
        public static string Localize(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('$') < 0) return text;
            try
            {
                if (Localization.instance != null) return Localization.instance.Localize(text);
            }
            catch (Exception)
            {
                // Fall back to the raw text rather than lose the name entirely.
            }
            return text;
        }
    }
}
