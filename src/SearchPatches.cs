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
}
