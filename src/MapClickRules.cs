using System.Globalization;

namespace Waypointer
{
    /// <summary>What a map click with the waypoint modifier does (MapClickRules.Decide).</summary>
    internal enum MapClickAction
    {
        /// <summary>The nearest pin is a marker this mod placed: its waypoint is removed.</summary>
        RemoveOwnMarker,
        /// <summary>The nearest pin is one of the player's (or a shared or vanilla one) that a waypoint follows: that
        /// waypoint is removed, and the pin stays on the map.</summary>
        StopFollowing,
        /// <summary>The nearest pin is one not yet followed: follow it.</summary>
        Follow,
        /// <summary>No pin within reach, or a transient one (a ping, a player's marker) is nearest: a waypoint on the spot.</summary>
        AddPoint,
    }

    /// <summary>
    /// The map click's decision and the words it shows, and what the screen calls a waypoint. Unity-free, so the tests
    /// exercise them.
    /// </summary>
    internal static class MapClickRules
    {
        /// <summary>
        /// The pin nearest the click decides, whichever kind it is ("Nearest always", chosen on 2026-10-01): a marker this
        /// mod placed or a pin being followed - its waypoint is removed - a pin not yet followed, which is followed, or a
        /// transient pin (a ping, a shout, another player's marker, an event marker), which is never followed: the click
        /// then places a waypoint on the spot, as on open ground. Equally near, a waypoint's pin wins over a pin not yet
        /// followed (removed rather than followed twice) and over a transient pin, a marker of ours over a followed pin,
        /// and a pin not yet followed over a transient pin (followed). Unlike a right-click delete, where a marker of ours anywhere within reach wins because deleting a pin of the
        /// player's cannot be undone, an Alt-click never deletes a pin of the player's, so the one under the pointer counts.
        /// Until 1.5.1 a pin a waypoint used anywhere within reach won over the one under the pointer, so with identical
        /// pins close together (the death pins of one in-game day all carry the same name) a click aimed at a new pin
        /// stopped following an older one. Distances are horizontal, from the click; a negative one (or NaN) means no such
        /// pin within reach.
        /// </summary>
        /// <param name="ownMarker">The nearest marker this mod placed (a waypoint's own marker, or a stand-in).</param>
        /// <param name="followedPin">The nearest pin a waypoint follows.</param>
        /// <param name="otherPin">The nearest pin that can be followed and is not followed yet.</param>
        /// <param name="transientPin">The nearest ping, shout, other player's marker or event marker.</param>
        public static MapClickAction Decide(float ownMarker, float followedPin, float otherPin, float transientPin)
        {
            bool haveOwn = ownMarker >= 0f;
            bool haveFollowed = followedPin >= 0f;
            bool haveOther = otherPin >= 0f;
            MapClickAction action = MapClickAction.AddPoint;
            float nearest = -1f;
            if (haveOwn) { action = MapClickAction.RemoveOwnMarker; nearest = ownMarker; }
            if (haveFollowed && (nearest < 0f || followedPin < nearest)) { action = MapClickAction.StopFollowing; nearest = followedPin; }
            if (haveOther && (nearest < 0f || otherPin < nearest)) { action = MapClickAction.Follow; nearest = otherPin; }
            if (transientPin >= 0f && (nearest < 0f || transientPin < nearest)) return MapClickAction.AddPoint;
            return action;
        }

        /// <summary>
        /// Whether the large map shows a pin, so that a map click may weigh it - Valheim's own click skips a pin it does
        /// not show. Minimap.UpdatePins hides a pin whose icon type the map's filter hides (m_visibleIconTypes) and a
        /// shared pin (an owner other than 0, from a Cartography Table) while shared pins are faded out
        /// (m_sharedMapDataFade not above 0). Its third reason, a pin off the part of the map on screen, is not
        /// weighed here: a pin within reach of the pointer is on screen but at the map's very edge. An icon filter that
        /// could not be read (null) hides nothing, and neither does an icon type it does not list.
        /// </summary>
        /// <param name="shownIconTypes">The map's icon filter, indexed by pin type, or null when unknown.</param>
        /// <param name="type">The pin's type.</param>
        /// <param name="sharedPinsFade">How far shared pins are faded in, 0 to 1 (1 when unknown).</param>
        /// <param name="ownerId">The pin's owner: 0 for the player's own pins and for markers no one shared.</param>
        public static bool PinShown(bool[] shownIconTypes, int type, float sharedPinsFade, long ownerId)
        {
            if (shownIconTypes != null && type >= 0 && type < shownIconTypes.Length && !shownIconTypes[type]) return false;
            if (ownerId != 0L && !(sharedPinsFade > 0f)) return false;
            return true;
        }

        /// <summary>1st, 2nd, 3rd, 4th ... 11th, 12th, 13th ... 21st, 22nd ...</summary>
        public static string Ordinal(int n)
        {
            string number = n.ToString(CultureInfo.InvariantCulture);
            if (n <= 0) return number;
            int lastTwo = n % 100;
            if (lastTwo >= 11 && lastTwo <= 13) return number + "th";
            switch (n % 10)
            {
                case 1: return number + "st";
                case 2: return number + "nd";
                case 3: return number + "rd";
                default: return number + "th";
            }
        }

        /// <summary>
        /// The message for a pin the click follows: "Waypoint set: Day 3" when it is the arrow's target now, or
        /// "Waypoint queued (2nd): Day 3" when it waits behind others - a newly followed pin joins the end of the route,
        /// and the arrow keeps leading to the waypoint at its front.
        /// </summary>
        /// <param name="name">The pin's name as shown, or empty.</param>
        /// <param name="index">Its place in the route, 0 for the front.</param>
        public static string FollowMessage(string name, int index)
        {
            string shown = string.IsNullOrEmpty(name) ? "marker" : name;
            if (index <= 0) return "Waypoint set: " + shown;
            return "Waypoint queued (" + Ordinal(index + 1) + "): " + shown;
        }

        /// <summary>
        /// The message for a waypoint the click adds on the spot: "Waypoint added" when it is the arrow's target now,
        /// "Waypoint queued (2nd)" when it waits behind others. TomTom's overload below can add the spot's position.
        /// </summary>
        public static string AddedMessage(int index)
        {
            if (index <= 0) return "Waypoint added";
            return "Waypoint queued (" + Ordinal(index + 1) + ")";
        }

#if !WAYFINDER
        /// <summary>TomTom: AddedMessage(index), then " at " and the spot's coordinates while ShowCoordinates is on and
        /// they are known ("Waypoint added at 1234, -567", as 1.6.0 wrote it); without them while it is off.</summary>
        public static string AddedMessage(int index, string coordText, bool showCoordinates)
        {
            string message = AddedMessage(index);
            return showCoordinates && !string.IsNullOrEmpty(coordText) ? message + " at " + coordText : message;
        }
#endif

        /// <summary>What the arrow's caption and the messages at the top left call a waypoint (Waypoint.ScreenName): its
        /// name; for one without, its coordinates while they are to be shown and known (TomTom's ShowCoordinates), else
        /// the PinLabel setting, or "Waypoint" when that is empty. Returns one of its arguments or that constant, never new
        /// text. The window, the console and the log use Waypoint.DisplayName.</summary>
        public static string ScreenName(string name, string label, string coordText, bool showCoordinates)
        {
            if (!string.IsNullOrEmpty(name)) return name;
            if (showCoordinates && !string.IsNullOrEmpty(coordText)) return coordText;
            return string.IsNullOrEmpty(label) ? "Waypoint" : label;
        }
    }
}
