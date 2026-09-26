using System;
using UnityEngine;

namespace Waypointer
{
    /// <summary>
    /// The player's side of this plugin's messages with a server that runs it too (FindServer): says hello once per
    /// connection, remembers what the server answered (its plugin, and whether this player may use Find), sends
    /// Find requests and hands the answers to LocationSearch.
    ///
    /// A server without this plugin never answers the hello, and Find then asks the way a Vegvisir does, as 1.2.0
    /// did. The two routed calls are registered on every ZRoutedRpc the game makes (one per connection), on
    /// players and servers alike; each side ignores what is not meant for it.
    /// </summary>
    internal static class FindLink
    {
        /// <summary>How long after the hello a Find waits for the server's answer before asking the other way.</summary>
        private const float InfoWait = 5f;

        private static ZRoutedRpc _registeredOn;
        private static bool _helloSent;
        private static float _helloTime;
        private static ServerInfo _info;
        private static bool _loggedOtherVersion;

        /// <summary>The server runs this plugin with the same protocol, so Find can ask it directly.</summary>
        public static bool ServerHasPlugin
        {
            get { return _info != null && _info.Protocol == FindProtocol.Version; }
        }

        /// <summary>The server runs this plugin and does not let this player use Find.</summary>
        public static bool RefusedByServer
        {
            get { return ServerHasPlugin && !_info.MayFind; }
        }

        /// <summary>Hello sent a moment ago, no answer yet.</summary>
        public static bool AwaitingServerInfo
        {
            get { return _helloSent && _info == null && Time.unscaledTime - _helloTime < InfoWait; }
        }

        public static string RefusalText
        {
            get
            {
                return _info != null && _info.Policy == FindPolicy.AdminsOnly
                    ? "Find is for this server's admins only."
                    : "Find is turned off on this server.";
            }
        }

        /// <summary>The window's Find header while the server does not let this player use Find.</summary>
        public static string RefusalHeader
        {
            get
            {
                return _info != null && _info.Policy == FindPolicy.AdminsOnly
                    ? "Find - this server's admins only"
                    : "Find - turned off on this server";
            }
        }

        /// <summary>Called every frame from Plugin.Update, on players and servers alike.</summary>
        public static void Tick()
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null || ZNet.instance == null)
            {
                Reset();
                return;
            }
            if (!ReferenceEquals(rpc, _registeredOn))
            {
                // The game makes a new ZRoutedRpc for every connection and never clears the old one; registering the
                // same name twice on one throws, so a failure here is logged once rather than every frame.
                Reset();
                _registeredOn = rpc;
                try
                {
                    rpc.Register<ZPackage>(FindProtocol.ToServer, OnToServer);
                    rpc.Register<ZPackage>(FindProtocol.ToClient, OnToClient);
                    if (Plugin.Dedicated)
                        Plugin.Log.LogInfo("Ready to answer players' Find (WhoMayFind = " + Plugin.WhoMayFind.Value + ").");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Could not register this plugin's server messages: " + e.Message);
                }
            }
            // A player of someone else's world says hello once, when the character is in the world.
            ZNetPeer server = ZNet.instance.IsServer() ? null : ZNet.instance.GetServerPeer();
            if (!_helloSent && server != null && Player.m_localPlayer != null)
            {
                _helloSent = true;
                _helloTime = Time.unscaledTime;
                rpc.InvokeRoutedRPC(server.m_uid, FindProtocol.ToServer, new ZPackage(FindProtocol.EncodeHello(Plugin.NAME + " " + Plugin.VERSION)));
            }
        }

        public static bool SendFind(FindRequest request)
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            ZNetPeer server = ZNet.instance != null ? ZNet.instance.GetServerPeer() : null;
            if (rpc == null || server == null || !ReferenceEquals(rpc, _registeredOn) || !ServerHasPlugin) return false;
            rpc.InvokeRoutedRPC(server.m_uid, FindProtocol.ToServer, new ZPackage(FindProtocol.EncodeFind(request)));
            return true;
        }

        /// <summary>The server refused a Find: the window shows it until the server says otherwise.</summary>
        public static void NoteRefused()
        {
            if (_info != null) _info.MayFind = false;
        }

        /// <summary>The server accepted a Find (an admin list can change while a player is connected).</summary>
        public static void NoteAllowed()
        {
            if (_info != null) _info.MayFind = true;
        }

        private static void Reset()
        {
            _registeredOn = null;
            _helloSent = false;
            _info = null;
        }

        private static void OnToServer(long sender, ZPackage pkg)
        {
            try
            {
                // FindServer knows the player by the connection the call came on, not by this sender field.
                if (pkg != null) FindServer.OnMessage(pkg.GetArray());
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("A player's Find message could not be handled: " + e.Message);
            }
        }

        private static void OnToClient(long sender, ZPackage pkg)
        {
            try
            {
                // Only the server's word counts. This stops a stray call from another player's plugin; it cannot stop
                // a deliberately forged one, since the game does not check a routed call's sender (see
                // RoutedCallContext) and every call reaches a player through the server.
                if (pkg == null || ZNet.instance == null || ZNet.instance.IsServer()) return;
                ZNetPeer server = ZNet.instance.GetServerPeer();
                if (server == null || sender != server.m_uid) return;
                byte[] data = pkg.GetArray();
                byte kind = FindProtocol.KindOf(data);
                if (kind == FindProtocol.KindInfo)
                {
                    ServerInfo info = FindProtocol.DecodeInfo(data);
                    if (info == null) return;
                    _info = info;
                    if (info.Protocol != FindProtocol.Version && !_loggedOtherVersion)
                    {
                        _loggedOtherVersion = true;
                        Plugin.Log.LogInfo("The server runs " + info.Server + ", which speaks another version of this plugin's messages; Find asks the server the way a Vegvisir does.");
                    }
                    else if (info.Protocol == FindProtocol.Version)
                        Plugin.Log.LogInfo("The server runs " + info.Server + ": Find asks its plugin" + (info.MayFind ? "." : ", which does not let you use Find."));
                }
                else if (kind == FindProtocol.KindFound)
                {
                    FoundMessage m = FindProtocol.DecodeFound(data);
                    if (m != null) LocationSearch.OnServerPluginAnswer(m);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("A message from the server's plugin could not be handled: " + e.Message);
            }
        }
    }
}
