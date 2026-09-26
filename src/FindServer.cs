using System;
using System.Collections.Generic;
using UnityEngine;

namespace Waypointer
{
    /// <summary>
    /// The server's side of Find, when this plugin runs on the server: a dedicated server, or the host of a game
    /// started with Start Server. It answers players' Find requests from the server's own knowledge - every location
    /// instance and whether it is placed, every generated object and chest - the same answer a host gets, and it
    /// applies the server's WhoMayFind setting to them, and to the Vegvisir-style requests of plugins without this
    /// helper (Game_RPC_DiscoverClosestLocation_Patch).
    ///
    /// A player is known by the connection the call came on (RoutedCallContext), never by the call's sender field,
    /// which the sending game writes itself: that is what the admin check reads, and where answers go.
    ///
    /// One search runs at a time, a little each frame within SearchBudget, in the order they came; a player has at
    /// most one waiting (a new one replaces it). Every place in range is sent back - nothing is trimmed here, since
    /// Wayfinder's exploration filter runs on the player's side - in messages of FindProtocol.MaxHitsPerMessage.
    /// A search that fails is dropped and its player told the server is busy; it is never sent as if complete.
    /// </summary>
    internal static class FindServer
    {
        /// <summary>At most this many players' searches wait; a further one is told the server is busy.</summary>
        private const int MaxWaiting = 16;

        private sealed class Pending
        {
            public long Peer;
            public FindRequest Request;
            public SearchQuery Query;
        }

        private static readonly List<Pending> _waiting = new List<Pending>();
        private static readonly HashSet<long> _greeted = new HashSet<long>();
        private static Pending _current;
        private static SearchJob _job;
        private static readonly HashSet<long> _loggedRefusals = new HashSet<long>();

        /// <summary>A message a player's plugin sent to this server (FindLink registers the routed call).</summary>
        public static void OnMessage(byte[] data)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            ZNetPeer peer = CallingPeer();
            if (peer == null) return;
            byte kind = FindProtocol.KindOf(data);
            if (kind == FindProtocol.KindHello)
            {
                if (FindProtocol.DecodeHello(data) < 0) return;
                _greeted.Add(peer.m_uid);
                SendInfo(peer);
            }
            else if (kind == FindProtocol.KindFind)
            {
                FindRequest request = FindProtocol.DecodeFind(data);
                if (request == null) return;
                if (!MayFind(peer))
                {
                    Send(peer.m_uid, FindProtocol.EncodeStatus(request.SearchId, FindStatus.Refused));
                    return;
                }
                SearchQuery query = SearchCatalog.ByName(request.Query);
                if (query == null)
                {
                    Send(peer.m_uid, FindProtocol.EncodeStatus(request.SearchId, FindStatus.UnknownQuery));
                    return;
                }
                for (int i = _waiting.Count - 1; i >= 0; i--) if (_waiting[i].Peer == peer.m_uid) _waiting.RemoveAt(i);
                if (_waiting.Count >= MaxWaiting)
                {
                    Send(peer.m_uid, FindProtocol.EncodeStatus(request.SearchId, FindStatus.Busy));
                    return;
                }
                Pending p = new Pending();
                p.Peer = peer.m_uid;
                p.Request = request;
                p.Query = query;
                _waiting.Add(p);
                Send(peer.m_uid, FindProtocol.EncodeStatus(request.SearchId, FindStatus.Accepted));
            }
        }

        /// <summary>Called every frame from Plugin.Update, after SearchBudget.StartFrame.</summary>
        public static void Tick()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZoneSystem.instance == null || ZDOMan.instance == null)
            {
                Clear();
                return;
            }
            while (_job == null && _waiting.Count > 0)
            {
                Pending next = _waiting[0];
                _waiting.RemoveAt(0);
                if (!Connected(next.Peer)) continue;
                _current = next;
                FindRequest r = next.Request;
                try
                {
                    _job = new SearchJob(next.Query, new Vector3(r.X, r.Y, r.Z), r.Range, true, r.CheckChests, new List<SearchHit>());
                    _job.CollectLocationsOnServer();
                }
                catch (Exception e)
                {
                    Fail(e);
                }
            }
            if (_job == null) return;
            if (!Connected(_current.Peer))
            {
                // The player left: stop working for them.
                _job = null;
                _current = null;
                return;
            }

            bool more;
            try
            {
                more = _job.Step();
            }
            catch (Exception e)
            {
                // A C# iterator that has thrown is finished: stepping it again would return false and send what was
                // collected so far as if it were the whole answer.
                Fail(e);
                return;
            }
            if (more) return;

            Pending done = _current;
            SearchJob job = _job;
            _job = null;
            _current = null;
            List<byte[]> messages = FindProtocol.EncodeResult(done.Request.SearchId, job.Hits, job.ObjectsRead, job.ChestPlacesChecked, job.ChestPlacesSkipped);
            for (int i = 0; i < messages.Count; i++) Send(done.Peer, messages[i]);
        }

        /// <summary>The running search failed: drop it, say so in the log, and tell its player to try again.</summary>
        private static void Fail(Exception e)
        {
            Pending failed = _current;
            _job = null;
            _current = null;
            Plugin.Log.LogError("A player's Find failed on the server and was dropped: " + e);
            if (failed != null && Connected(failed.Peer))
                Send(failed.Peer, FindProtocol.EncodeStatus(failed.Request.SearchId, FindStatus.Busy));
        }

        /// <summary>
        /// WhoMayFind changed: searches of players it no longer allows are dropped (and they are told), and every
        /// player who said hello is told the new rule, so their window shows it.
        /// </summary>
        public static void PolicyChanged()
        {
            _loggedRefusals.Clear();
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (_current != null && !StillAllowed(_current))
            {
                _job = null;
                _current = null;
            }
            for (int i = _waiting.Count - 1; i >= 0; i--)
                if (!StillAllowed(_waiting[i])) _waiting.RemoveAt(i);
            foreach (long uid in new List<long>(_greeted))
            {
                ZNetPeer peer = ZNet.instance.GetPeer(uid);
                if (peer != null) SendInfo(peer);
                else _greeted.Remove(uid);
            }
        }

        private static bool StillAllowed(Pending p)
        {
            ZNetPeer peer = ZNet.instance.GetPeer(p.Peer);
            if (peer == null) return false;
            if (Allowed(peer)) return true;
            Send(p.Peer, FindProtocol.EncodeStatus(p.Request.SearchId, FindStatus.Refused));
            return false;
        }

        /// <summary>
        /// Whether the game behind the routed call being handled may use Find under this server's WhoMayFind (for
        /// Game_RPC_DiscoverClosestLocation_Patch). The decision itself is FindProtocol.CallerMayFind (unit-tested).
        /// </summary>
        public static bool CallerMayFind()
        {
            FindPolicy policy = Plugin.WhoMayFind.Value;
            ZNetPeer peer = CallingPeer();
            bool admin = peer != null && policy == FindPolicy.AdminsOnly && IsAdmin(peer);
            return FindProtocol.CallerMayFind(RoutedCallContext.Available, RoutedCallContext.Current != null, peer != null, policy, admin);
        }

        /// <summary>Logs, once per player and rule, that the caller of the routed call being handled was refused.</summary>
        public static void CallerRefused()
        {
            LogRefusal(CallingPeer());
        }

        private static bool MayFind(ZNetPeer peer)
        {
            bool allowed = Allowed(peer);
            if (!allowed) LogRefusal(peer);
            return allowed;
        }

        private static bool Allowed(ZNetPeer peer)
        {
            FindPolicy policy = Plugin.WhoMayFind.Value;
            return FindProtocol.MayFind(policy, policy == FindPolicy.AdminsOnly && IsAdmin(peer));
        }

        private static void LogRefusal(ZNetPeer peer)
        {
            long key = peer != null ? peer.m_uid : 0L;
            if (_loggedRefusals.Add(key))
                Plugin.Log.LogInfo("Find refused for " + PlayerName(peer) + " (WhoMayFind = " + Plugin.WhoMayFind.Value + ").");
        }

        /// <summary>The game's own admin check (adminlist.txt), on the host name of the player's connection.</summary>
        private static bool IsAdmin(ZNetPeer peer)
        {
            ISocket socket = peer.m_rpc != null ? peer.m_rpc.GetSocket() : null;
            return socket != null && ZNet.instance.IsAdmin(socket.GetHostName());
        }

        /// <summary>The player whose connection the routed call being handled came on, or null.</summary>
        private static ZNetPeer CallingPeer()
        {
            ZRpc rpc = RoutedCallContext.Current;
            if (rpc == null || ZNet.instance == null) return null;
            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            for (int i = 0; i < peers.Count; i++)
                if (peers[i] != null && peers[i].m_rpc == rpc && peers[i].IsReady()) return peers[i];
            return null;
        }

        private static string PlayerName(ZNetPeer peer)
        {
            if (peer == null) return "a connection that is not a player's";
            return string.IsNullOrEmpty(peer.m_playerName) ? "a player" : peer.m_playerName;
        }

        private static void SendInfo(ZNetPeer peer)
        {
            ServerInfo info = new ServerInfo();
            info.Server = Plugin.NAME + " " + Plugin.VERSION;
            info.Policy = Plugin.WhoMayFind.Value;
            info.MayFind = Allowed(peer);
            Send(peer.m_uid, FindProtocol.EncodeInfo(info));
        }

        private static void Send(long peer, byte[] data)
        {
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(peer, FindProtocol.ToClient, new ZPackage(data));
        }

        private static bool Connected(long peer)
        {
            return ZNet.instance != null && ZNet.instance.GetPeer(peer) != null;
        }

        private static void Clear()
        {
            _waiting.Clear();
            _greeted.Clear();
            _loggedRefusals.Clear();
            _current = null;
            _job = null;
        }
    }
}
