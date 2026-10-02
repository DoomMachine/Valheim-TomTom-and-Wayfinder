using System;
using System.Diagnostics;
using System.Globalization;

namespace Waypointer
{
    /// <summary>
    /// VerboseLog's event lines (TomTom). Diag.Trace is [Conditional("TOMTOM")], and TOMTOM is defined only for the TomTom
    /// build (TomTom.csproj, build.sh), so the compiler drops every call - its argument with it - from Wayfinder.
    /// The lines go straight to the plugin's own log file, never through BepInEx, so they are not in LogOutput.log or
    /// Player.log.
    ///
    /// A call is one statement: Diag.Trace("literal"), or a call to a private [Conditional("TOMTOM")] helper with plain
    /// arguments that builds its text after "if (!Diag.On) return;" inside its own try - so without VerboseLog no text
    /// is built, and building one can never throw into the code around it. A helper that formats a position or a count
    /// about a Find has its body under #if !WAYFINDER as well.
    /// </summary>
    internal static class Diag
    {
        // No initialiser and no static constructor, on purpose: gameplay code reads it outside any try of the log file,
        // and a type initialiser that threw would make every such read throw. Set by LogFile while VerboseLog writes.
        internal static bool On;

        [Conditional("TOMTOM")]
        internal static void Trace(string text)
        {
            try
            {
                if (On) LogFile.Write(LogRules.Debug, Edition.Name, text);
            }
            catch (Exception) { }
        }

        /// <summary>A count, invariant culture.</summary>
        internal static string N(int n)
        {
            try { return n.ToString(CultureInfo.InvariantCulture); }
            catch (Exception) { return "?"; }
        }

        /// <summary>A distance in metres, invariant culture; "none" when negative.</summary>
        internal static string M(float metres)
        {
            try { return metres < 0f ? "none" : metres.ToString("F1", CultureInfo.InvariantCulture) + " m"; }
            catch (Exception) { return "?"; }
        }

        /// <summary>A waypoint's name (never its position).</summary>
        internal static string Wp(Waypoint wp)
        {
            try
            {
                if (wp == null) return "none";
                if (string.IsNullOrEmpty(wp.Name)) return wp.Borrowed ? "an unnamed pin" : "an unnamed point";
                return "'" + GameText.Localize(wp.Name) + "'";
            }
            catch (Exception) { return "?"; }
        }

        /// <summary>A pin's name: reads m_name only (preflight check 5 reads every other PinData field use).</summary>
        internal static string Pin(Minimap.PinData pin)
        {
            try
            {
                if (pin == null) return "none";
                return string.IsNullOrEmpty(pin.m_name) ? "(unnamed)" : "'" + GameText.Localize(pin.m_name) + "'";
            }
            catch (Exception) { return "?"; }
        }
    }
}
