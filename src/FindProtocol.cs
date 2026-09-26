using System;
using System.Collections.Generic;
using System.Text;

namespace Waypointer
{
    /// <summary>Who of the players joining a server may use Find (the server's own setting, WhoMayFind).</summary>
    public enum FindPolicy { Everyone, AdminsOnly, Nobody }

    /// <summary>What a server answers to a Find request.</summary>
    internal enum FindStatus : byte
    {
        /// <summary>Queued; the places follow in Part messages, then Done.</summary>
        Accepted = 0,
        Part = 1,
        Done = 2,
        /// <summary>The server's WhoMayFind does not let this player use Find.</summary>
        Refused = 3,
        /// <summary>The server's plugin does not know this query (another version of the catalogue).</summary>
        UnknownQuery = 4,
        /// <summary>Too many searches are waiting on the server; try again shortly.</summary>
        Busy = 5
    }

    /// <summary>A player's Find, as a server receives it.</summary>
    internal sealed class FindRequest
    {
        public int SearchId;
        public string Query;
        public float X, Y, Z;
        public float Range;
        public bool CheckChests;
    }

    /// <summary>What a server says about itself when a player's plugin says hello.</summary>
    internal sealed class ServerInfo
    {
        public int Protocol;
        public string Server;
        public FindPolicy Policy;
        public bool MayFind;
    }

    /// <summary>One answer to a Find: a status, and for Part the places, for Done the totals.</summary>
    internal sealed class FoundMessage
    {
        public int SearchId;
        public FindStatus Status;
        public List<SearchHit> Hits = new List<SearchHit>();
        public int Total;
        public int ObjectsRead;
        public int ChestPlacesChecked;
        public int ChestPlacesSkipped;
    }

    /// <summary>
    /// The messages between a player's plugin and a server running it (a dedicated server, or the host of a game
    /// started with Start Server), kept free of Unity so the tests can exercise them. Each message is a byte array
    /// carried in a ZPackage on one of two routed RPCs, ToServer and ToClient; its first byte says what it is, and
    /// every message a player sends carries the protocol version. Decoding never throws: a message that is
    /// malformed, truncated or of another version decodes as null.
    /// </summary>
    internal static class FindProtocol
    {
        public const int Version = 1;
        public const string ToServer = "DoomMachine.Waypointer.ToServer";
        public const string ToClient = "DoomMachine.Waypointer.ToClient";

        /// <summary>Places per Part message: about 4 KB each, far below what one routed call carries.</summary>
        public const int MaxHitsPerMessage = 256;
        public const float MinRange = 100f;
        public const float MaxRange = 10000f;
        private const int MaxText = 128;

        public const byte KindHello = 1;
        public const byte KindInfo = 2;
        public const byte KindFind = 3;
        public const byte KindFound = 4;

        private const byte FlagPlaced = 1, FlagPossible = 2, FlagObject = 4;

        /// <summary>Whether a player may use Find under a server's policy. The host's own Find never asks.</summary>
        public static bool MayFind(FindPolicy policy, bool isAdmin)
        {
            if (policy == FindPolicy.Everyone) return true;
            if (policy == FindPolicy.AdminsOnly) return isAdmin;
            return false;
        }

        /// <summary>
        /// Whether the game behind a routed call may use Find on this server.
        /// - contextKnown: the server can tell which connection a call came on (its patch on ZRoutedRpc.RPC_RoutedRPC is
        ///   in place). Without it no caller can be told apart, so only Everyone lets a call through.
        /// - fromConnection: the call came over a connection; false means the server's own game (the host) made it.
        /// - knownPlayer: that connection is a player's that has finished joining.
        /// - isAdmin: that player is in the server's admin list.
        /// </summary>
        public static bool CallerMayFind(bool contextKnown, bool fromConnection, bool knownPlayer, FindPolicy policy, bool isAdmin)
        {
            if (!contextKnown) return policy == FindPolicy.Everyone;
            if (!fromConnection) return true;
            if (!knownPlayer) return false;
            return MayFind(policy, isAdmin);
        }

        /// <summary>The first byte of a message, or 0 when there is none.</summary>
        public static byte KindOf(byte[] data)
        {
            return data != null && data.Length > 0 ? data[0] : (byte)0;
        }

        // ------------------------------------------------------------ player -> server

        public static byte[] EncodeHello(string client)
        {
            Out w = new Out();
            w.Byte(KindHello);
            w.Int(Version);
            w.Text(client);
            return w.ToArray();
        }

        /// <summary>The protocol version a hello carries, or -1 when it is not a readable hello.</summary>
        public static int DecodeHello(byte[] data)
        {
            try
            {
                if (KindOf(data) != KindHello) return -1;
                In r = new In(data);
                int version = r.Int();
                r.Text();
                return version;
            }
            catch (Exception) { return -1; }
        }

        public static byte[] EncodeFind(FindRequest q)
        {
            Out w = new Out();
            w.Byte(KindFind);
            w.Int(Version);
            w.Int(q.SearchId);
            w.Text(q.Query);
            w.Float(q.X); w.Float(q.Y); w.Float(q.Z);
            w.Float(q.Range);
            w.Byte((byte)(q.CheckChests ? 1 : 0));
            return w.ToArray();
        }

        /// <summary>
        /// A player's Find, or null when it is not a readable Find of this protocol version, its position is not a
        /// finite number, or its range is outside 100 m to 10 km.
        /// </summary>
        public static FindRequest DecodeFind(byte[] data)
        {
            try
            {
                if (KindOf(data) != KindFind) return null;
                In r = new In(data);
                if (r.Int() != Version) return null;
                FindRequest q = new FindRequest();
                q.SearchId = r.Int();
                q.Query = r.Text();
                q.X = r.Float(); q.Y = r.Float(); q.Z = r.Float();
                q.Range = r.Float();
                q.CheckChests = (r.Byte() & 1) != 0;
                if (q.Query == null || !Finite(q.X) || !Finite(q.Y) || !Finite(q.Z)) return null;
                if (!(q.Range >= MinRange && q.Range <= MaxRange)) return null;
                return q;
            }
            catch (Exception) { return null; }
        }

        // ------------------------------------------------------------ server -> player

        public static byte[] EncodeInfo(ServerInfo info)
        {
            Out w = new Out();
            w.Byte(KindInfo);
            w.Int(Version);
            w.Text(info.Server);
            w.Byte((byte)info.Policy);
            w.Byte((byte)(info.MayFind ? 1 : 0));
            return w.ToArray();
        }

        /// <summary>
        /// What a server says about itself. A server of another protocol version comes back with only Protocol and
        /// Server read, and MayFind false: the player's plugin then treats it as a server without this helper.
        /// </summary>
        public static ServerInfo DecodeInfo(byte[] data)
        {
            try
            {
                if (KindOf(data) != KindInfo) return null;
                In r = new In(data);
                ServerInfo info = new ServerInfo();
                info.Protocol = r.Int();
                info.Server = r.Text();
                if (info.Protocol != Version) return info;
                byte policy = r.Byte();
                if (policy > (byte)FindPolicy.Nobody) return null;
                info.Policy = (FindPolicy)policy;
                info.MayFind = (r.Byte() & 1) != 0;
                return info;
            }
            catch (Exception) { return null; }
        }

        /// <summary>A Found message with a status and nothing else (Accepted, Refused, UnknownQuery, Busy).</summary>
        public static byte[] EncodeStatus(int searchId, FindStatus status)
        {
            Out w = new Out();
            w.Byte(KindFound);
            w.Int(searchId);
            w.Byte((byte)status);
            return w.ToArray();
        }

        /// <summary>
        /// A search's result as messages: every place found, MaxHitsPerMessage to a Part, then Done with the count
        /// and the server's totals. Routed calls between the server and a player keep their order, so Done arrives
        /// last; a player that counts fewer places than Done says knows the answer is incomplete.
        /// </summary>
        public static List<byte[]> EncodeResult(int searchId, List<SearchHit> hits, int objectsRead, int chestPlacesChecked, int chestPlacesSkipped)
        {
            List<byte[]> messages = new List<byte[]>();
            for (int start = 0; start < hits.Count; start += MaxHitsPerMessage)
            {
                int end = Math.Min(hits.Count, start + MaxHitsPerMessage);
                List<string> prefabs = new List<string>();
                List<string> labels = new List<string>();
                Dictionary<string, int> index = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = start; i < end; i++)
                {
                    string p = hits[i].Prefab ?? "";
                    if (index.ContainsKey(p)) continue;
                    index[p] = prefabs.Count;
                    prefabs.Add(p);
                    labels.Add(hits[i].Label ?? p);
                }

                Out w = new Out();
                w.Byte(KindFound);
                w.Int(searchId);
                w.Byte((byte)FindStatus.Part);
                w.UShort(prefabs.Count);
                for (int i = 0; i < prefabs.Count; i++) { w.Text(prefabs[i]); w.Text(labels[i]); }
                w.UShort(end - start);
                for (int i = start; i < end; i++)
                {
                    SearchHit h = hits[i];
                    w.UShort(index[h.Prefab ?? ""]);
                    w.Float(h.X); w.Float(h.Y); w.Float(h.Z);
                    w.Byte((byte)((h.Placed ? FlagPlaced : 0) | (h.Possible ? FlagPossible : 0) | (h.IsObject ? FlagObject : 0)));
                }
                messages.Add(w.ToArray());
            }

            Out d = new Out();
            d.Byte(KindFound);
            d.Int(searchId);
            d.Byte((byte)FindStatus.Done);
            d.Int(hits.Count);
            d.Int(objectsRead);
            d.Int(chestPlacesChecked);
            d.Int(chestPlacesSkipped);
            messages.Add(d.ToArray());
            return messages;
        }

        /// <summary>One answer to a Find, or null when it is not readable.</summary>
        public static FoundMessage DecodeFound(byte[] data)
        {
            try
            {
                if (KindOf(data) != KindFound) return null;
                In r = new In(data);
                FoundMessage m = new FoundMessage();
                m.SearchId = r.Int();
                byte status = r.Byte();
                if (status > (byte)FindStatus.Busy) return null;
                m.Status = (FindStatus)status;
                if (m.Status == FindStatus.Part)
                {
                    int tableSize = r.UShort();
                    if (tableSize > MaxHitsPerMessage) return null;
                    string[] prefabs = new string[tableSize];
                    string[] labels = new string[tableSize];
                    for (int i = 0; i < tableSize; i++)
                    {
                        prefabs[i] = r.Text();
                        labels[i] = r.Text();
                        if (prefabs[i] == null || labels[i] == null) return null;
                    }
                    int count = r.UShort();
                    if (count > MaxHitsPerMessage) return null;
                    for (int i = 0; i < count; i++)
                    {
                        int p = r.UShort();
                        if (p >= tableSize) return null;
                        SearchHit h = new SearchHit();
                        h.Prefab = prefabs[p];
                        h.Label = labels[p];
                        h.X = r.Float(); h.Y = r.Float(); h.Z = r.Float();
                        if (!Finite(h.X) || !Finite(h.Y) || !Finite(h.Z)) return null;
                        byte flags = r.Byte();
                        h.Placed = (flags & FlagPlaced) != 0;
                        h.Possible = (flags & FlagPossible) != 0;
                        h.IsObject = (flags & FlagObject) != 0;
                        m.Hits.Add(h);
                    }
                }
                else if (m.Status == FindStatus.Done)
                {
                    m.Total = r.Int();
                    m.ObjectsRead = r.Int();
                    m.ChestPlacesChecked = r.Int();
                    m.ChestPlacesSkipped = r.Int();
                    if (m.Total < 0) return null;
                }
                return m;
            }
            catch (Exception) { return null; }
        }

        // ------------------------------------------------------------ helpers

        private static string Clip(string s)
        {
            if (s == null) return "";
            return s.Length > MaxText ? s.Substring(0, MaxText) : s;
        }

        /// <summary>
        /// Writes a message: little-endian integers, floats by their bits, text as UTF-8 with a two-byte length.
        /// (Not BinaryWriter: preflight treats every writer that could reach a file as one, and this needs none.)
        /// </summary>
        private sealed class Out
        {
            private readonly List<byte> _b = new List<byte>(64);

            public void Byte(byte v) { _b.Add(v); }
            public void UShort(int v) { _b.Add((byte)v); _b.Add((byte)(v >> 8)); }
            public void Int(int v) { _b.Add((byte)v); _b.Add((byte)(v >> 8)); _b.Add((byte)(v >> 16)); _b.Add((byte)(v >> 24)); }
            public void Float(float v) { Int(BitConverter.ToInt32(BitConverter.GetBytes(v), 0)); }
            public void Text(string s)
            {
                byte[] b = Encoding.UTF8.GetBytes(Clip(s));
                UShort(b.Length);
                _b.AddRange(b);
            }
            public byte[] ToArray() { return _b.ToArray(); }
        }

        /// <summary>Reads what Out wrote, after the kind byte; throws on anything short or out of bounds.</summary>
        private sealed class In
        {
            private readonly byte[] _d;
            private int _p = 1;

            public In(byte[] data) { _d = data; }

            private void Need(int n) { if (n < 0 || _p + n > _d.Length) throw new ArgumentException("message too short"); }
            public byte Byte() { Need(1); return _d[_p++]; }
            public int UShort() { Need(2); int v = _d[_p] | (_d[_p + 1] << 8); _p += 2; return v; }
            public int Int() { Need(4); int v = _d[_p] | (_d[_p + 1] << 8) | (_d[_p + 2] << 16) | (_d[_p + 3] << 24); _p += 4; return v; }
            public float Float() { return BitConverter.ToSingle(BitConverter.GetBytes(Int()), 0); }
            public string Text()
            {
                int n = UShort();
                if (n > MaxText * 4) throw new ArgumentException("text too long");
                Need(n);
                string s = Encoding.UTF8.GetString(_d, _p, n);
                _p += n;
                return s;
            }
        }

        private static bool Finite(float f)
        {
            return !float.IsNaN(f) && !float.IsInfinity(f);
        }
    }
}
