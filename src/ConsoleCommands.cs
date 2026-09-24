using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Waypointer
{
    /// <summary>
    /// Registers the "waypoint" console command. Terminal.InitTerminal is where Valheim builds its
    /// command table, and other mods commonly hook the same place to add commands, so a postfix is safe.
    /// </summary>
    [HarmonyPatch(typeof(Terminal), "InitTerminal")]
    public static class Terminal_InitTerminal_Patch
    {
        private static bool _registered;

#if WAYFINDER
        private const string Usage =
            "waypoint list | remove <n> | next | clear | here [name] | closest | gui";
#else
        private const string Usage =
            "waypoint <x> <y> [elevation] [name] | list | remove <n> | next | clear | here [name] | closest | gui";
#endif

        private static void Postfix()
        {
            if (_registered) return;
            _registered = true;

            try
            {
                new Terminal.ConsoleCommand(
                    "waypoint",
                    Edition.Name + ": " + Usage,
                    new Terminal.ConsoleEvent(Handle),
                    false,  // isCheat
                    false,  // isNetwork
                    false,  // onlyServer
                    false,  // isSecret
                    false,  // allowInDevBuild
                    false,  // hideBehindDevCommands
                    null,   // optionsFetcher
                    false,  // alwaysRefreshTabOptions
                    false,  // remoteCommand
                    false); // onlyAdmin

                Plugin.Log.LogInfo("Console command 'waypoint' registered.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not register the console command: " + e);
            }
        }

        private static void Handle(Terminal.ConsoleEventArgs args)
        {
            Terminal context = args.Context;
            try
            {
                string[] argv = args.Args;
                if (argv == null || argv.Length < 2)
                {
                    Print(context, "Usage: " + Usage);
                    return;
                }

                string sub = argv[1].ToLowerInvariant();

                switch (sub)
                {
                    case "list":
                        PrintList(context);
                        return;

                    case "clear":
                        WaypointManager.Clear();
                        Print(context, "Waypoint queue cleared.");
                        return;

                    case "next":
                    case "skip":
                        if (WaypointManager.SkipActive())
                        {
                            Waypoint next = WaypointManager.Active;
                            Print(context, next == null ? "Queue is now empty." : "Now heading to " + next.DisplayName);
                        }
                        else Print(context, "No waypoints queued.");
                        return;

                    case "gui":
                    case "window":
                        WaypointWindow.Toggle();
                        Print(context, Edition.Name + (WaypointWindow.IsOpen ? " window opened." : " window closed."));
                        return;

                    case "here":
                        AddHere(context, argv);
                        return;

                    case "closest":
                        ActivateClosest(context);
                        return;

                    case "remove":
                    case "delete":
                        RemoveByIndex(context, argv);
                        return;

                    default:
#if WAYFINDER
                        // Wayfinder deliberately has no coordinate entry - see Edition.cs.
                        Print(context, "Wayfinder does not take coordinates. Hold "
                              + Plugin.MapModifierKey.Value + " and click the world map to place a waypoint, "
                              + "or click one of your own map markers to follow it. (" + Usage + ")");
#else
                        AddFromArgs(context, args);
#endif
                        return;
                }
            }
            catch (Exception e)
            {
                Print(context, "waypoint command failed: " + e.Message);
                Plugin.Log.LogError("Console command failed: " + e);
            }
        }

#if !WAYFINDER
        private static void AddFromArgs(Terminal context, Terminal.ConsoleEventArgs args)
        {
            // Everything after the command name is treated exactly like a line typed in the window.
            string line = JoinFrom(args.Args, 1);

            ParsedCoord parsed;
            string error;
            if (!CoordinateParser.TryParseOne(line, out parsed, out error))
            {
                Print(context, "Could not read that: " + error);
                return;
            }

            Vector3 world = CoordinateParser.ToWorld(parsed, Plugin.RawValheimOrder.Value);
            Waypoint wp = WaypointManager.Add(world, parsed.Name, parsed.HasElevation);
            Print(context, "Waypoint added: " + wp.DisplayName + " at " + CoordinateFormat.Format(wp.Pos, wp.HasElevation));
        }
#endif

        /// <summary>
        /// Marks where the player is standing. Allowed in both editions: it records a place the player
        /// has physically reached, so it reveals nothing they did not already know.
        /// </summary>
        private static void AddHere(Terminal context, string[] argv)
        {
            Player player = Player.m_localPlayer;
            if (player == null) { Print(context, "No player."); return; }

            string name = argv.Length > 2 ? JoinFrom(argv, 2) : "Marked spot";
            Waypoint wp = WaypointManager.Add(player.transform.position, name, true);
#if WAYFINDER
            // Printing the position here would be a GPS readout, which vanilla keeps behind devcommands.
            Print(context, "Waypoint added where you stand: " + wp.DisplayName);
#else
            Print(context, "Waypoint added here: " + CoordinateFormat.Format(wp.Pos, true));
#endif
        }

        private static void ActivateClosest(Terminal context)
        {
            Player player = Player.m_localPlayer;
            if (player == null) { Print(context, "No player."); return; }
            if (!WaypointManager.HasActive) { Print(context, "No waypoints queued."); return; }

            WaypointManager.ActivateClosest(player.transform.position);
            Waypoint active = WaypointManager.Active;
            Print(context, active == null ? "No waypoints queued." : "Now heading to " + active.DisplayName);
        }

        private static void RemoveByIndex(Terminal context, string[] argv)
        {
            if (argv.Length < 3)
            {
                Print(context, "Usage: waypoint remove <number from 'waypoint list'>");
                return;
            }

            int oneBased;
            if (!int.TryParse(argv[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out oneBased))
            {
                Print(context, "That is not a number.");
                return;
            }

            if (WaypointManager.RemoveAt(oneBased - 1))
                Print(context, "Removed waypoint " + oneBased + ".");
            else
                Print(context, "There is no waypoint " + oneBased + ".");
        }

        private static void PrintList(Terminal context)
        {
            List<Waypoint> queue = WaypointManager.Queue;
            if (queue.Count == 0)
            {
                Print(context, "No waypoints queued.");
                return;
            }

            Player player = Player.m_localPlayer;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0} waypoint(s):", queue.Count));
            for (int i = 0; i < queue.Count; i++)
            {
                Waypoint wp = queue[i];
                string distance = "";
                if (player != null)
                {
                    float d = WaypointManager.HorizontalDistance(player.transform.position, wp.Pos);
                    distance = string.Format(CultureInfo.InvariantCulture, "  ({0:0} m)", d);
                }
#if WAYFINDER
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0}{1}. {2}{3}",
                    i == 0 ? "> " : "  ", i + 1, wp.DisplayName, distance));
#else
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0}{1}. {2}  [{3}]{4}",
                    i == 0 ? "> " : "  ", i + 1, wp.DisplayName,
                    CoordinateFormat.Format(wp.Pos, wp.HasElevation), distance));
#endif
            }
            Print(context, sb.ToString());
        }

        private static string JoinFrom(string[] argv, int start)
        {
            if (argv == null || start >= argv.Length) return "";
            string[] slice = new string[argv.Length - start];
            Array.Copy(argv, start, slice, 0, slice.Length);
            return string.Join(" ", slice);
        }

        private static void Print(Terminal context, string text)
        {
            if (context != null) context.AddString(text);
            else Plugin.Log.LogInfo(text);
        }
    }
}
