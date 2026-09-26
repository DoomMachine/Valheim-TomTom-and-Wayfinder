using System;
using HarmonyLib;
using UnityEngine;

namespace Waypointer
{
    /// <summary>
    /// Keeps the server's answers to a location search from becoming map pins.
    ///
    /// As a client, LocationSearch asks the server with the request a Vegvisir makes, and the server answers each
    /// location with Game.RPC_DiscoverLocationResponse, whose vanilla body calls Minimap.DiscoverLocation, which adds
    /// a save:true pin - the kind a Cartography Table shares - and turns the player's view towards it. An answer to
    /// this mod carries SearchRules.TokenPrefix in its pin name, and for those this prefix returns false, so vanilla
    /// never runs; every other answer (a Vegvisir's, a runestone's) goes to vanilla untouched. LocationSearch refuses
    /// to send a request unless it finds this prefix patched in (AnswersIntercepted), and preflight checks both.
    /// </summary>
    [HarmonyPatch(typeof(Game), "RPC_DiscoverLocationResponse")]
    public static class Game_RPC_DiscoverLocationResponse_Patch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(string pinName, int pinType, Vector3 pos)
        {
            try
            {
                LocationSearch.OnServerAnswer(pinName, pinType, pos);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Location answer handling failed: " + e.Message);
            }
            // Decided by the pin name alone, even when handling the answer failed: an answer to this mod never
            // reaches vanilla (preflight checks that this is the only return, and what it returns).
            return SearchRules.VanillaMayHandle(pinName);
        }
    }

    /// <summary>
    /// On the server: applies WhoMayFind to the Vegvisir-style requests a player's plugin sends when it does not
    /// use this server's helper (a plugin older than 1.3.0, or a server of another protocol version). Such a request carries
    /// SearchRules.TokenPrefix in its pin name; a refused one is dropped, so that player's search gets no answers.
    /// Every other request - a Vegvisir's, a runestone's - goes to vanilla untouched. Registered by the game on the
    /// server only, so on a player's game this never runs.
    /// </summary>
    [HarmonyPatch(typeof(Game), "RPC_DiscoverClosestLocation")]
    public static class Game_RPC_DiscoverClosestLocation_Patch
    {
        private static bool Prefix(string pinName)
        {
            if (SearchRules.VanillaMayHandle(pinName)) return true;
            bool allowed = false;
            try
            {
                allowed = FindServer.CallerMayFind();
                if (!allowed) FindServer.CallerRefused();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not apply WhoMayFind to a player's search: " + e.Message);
            }
            return allowed;
        }
    }

    /// <summary>
    /// The connection the routed call being handled arrived on, for the server's WhoMayFind and for sending answers
    /// back. A routed call's sender field is whatever the sending game wrote (ZRoutedRpc.RPC_RoutedRPC copies it
    /// from the packet, and RouteRPC forwards it unchanged), so a player could name someone else - an admin, or the
    /// host. The connection cannot be named that way. Null outside a call from another game: the host's own calls,
    /// and the game's own code.
    /// </summary>
    internal static class RoutedCallContext
    {
        public static ZRpc Current;

        /// <summary>
        /// Set by Plugin.ApplyPatches once ZRoutedRpc_RPC_RoutedRPC_Patch is in place. Without it Current is always null,
        /// every call would look like the host's own, and WhoMayFind could not tell anyone apart (FindProtocol.CallerMayFind).
        /// </summary>
        public static bool Available;
    }

    /// <summary>Sets RoutedCallContext.Current while ZRoutedRpc handles a call that came over a connection.</summary>
    [HarmonyPatch(typeof(ZRoutedRpc), "RPC_RoutedRPC")]
    public static class ZRoutedRpc_RPC_RoutedRPC_Patch
    {
        private static void Prefix(ZRpc rpc)
        {
            RoutedCallContext.Current = rpc;
        }

        // Runs however the call ends, an exception included, so a later local call never sees a stale connection.
        private static void Finalizer()
        {
            RoutedCallContext.Current = null;
        }
    }
}
