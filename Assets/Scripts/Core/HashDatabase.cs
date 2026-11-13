using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TFM.Core
{
    /// <summary>
    /// Bone name database (BoneNames.HashDB from TelltaleGames_D3DMesh).
    /// Format: int32 count, then per entry: uint32 low, uint32 high, null-terminated string.
    /// Telltale bone hashes are CRC-64/ECMA-182 (non-reflected, init 0) of the lowercased name,
    /// so any name can be hashed; the database is only needed to turn hashes back into names.
    /// </summary>
    public sealed class HashDatabase
    {
        readonly Dictionary<ulong, string> byHash = new Dictionary<ulong, string>();
        readonly Dictionary<string, ulong> byName = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        List<string> sorted = new List<string>();

        static readonly HashDatabase empty = new HashDatabase();
        public static HashDatabase Empty { get { return empty; } }

        /// <summary>Number of distinct hashes known.</summary>
        public int Count { get { return byHash.Count; } }

        /// <summary>All known names, unique, sorted case-insensitively.</summary>
        public IReadOnlyList<string> SortedNames { get { return sorted; } }

        /// <summary>Records in the file whose stored hash did not match CRC64(name) (recovered where possible).</summary>
        public int RepairedRecords { get; private set; }

        /// <summary>Entry count declared in the file header.</summary>
        public int DeclaredCount { get; private set; }

        HashDatabase() { }

        public static HashDatabase Load(byte[] data)
        {
            var db = new HashDatabase();
            if (data == null || data.Length < 4) return db;

            int declared = BitConverter.ToInt32(data, 0);
            db.DeclaredCount = declared;
            int p = 4;
            for (int i = 0; i < declared; i++)
            {
                if (p + 8 > data.Length) break;
                ulong lo = BitConverter.ToUInt32(data, p);
                ulong hi = BitConverter.ToUInt32(data, p + 4);
                ulong stored = (hi << 32) | lo;
                int nameStart = p + 8;
                int end = Array.IndexOf(data, (byte)0, nameStart);
                if (end < 0) end = data.Length;
                string name = Latin1(data, nameStart, end - nameStart);

                if (name.Length > 0 && Crc64(name) == stored)
                {
                    db.Add(stored, name);
                }
                else
                {
                    // Corrupt record (the shipped DB has one: a misplaced terminator merges two entries).
                    // Recover any name inside it whose CRC64 matches an 8-byte value stored just before it.
                    db.RepairedRecords++;
                    foreach (var kv in RecoverNames(data, Math.Max(4, p - 9), end))
                        db.Add(kv.Key, kv.Value);
                }
                p = end + 1;
            }

            db.sorted = new List<string>(db.byName.Keys);
            db.sorted.Sort(StringComparer.OrdinalIgnoreCase);
            return db;
        }

        void Add(ulong hash, string name)
        {
            if (!byHash.ContainsKey(hash)) byHash[hash] = name;
            if (!byName.ContainsKey(name)) byName[name] = hash;
        }

        /// <summary>
        /// Scans [from, to) for printable names whose CRC64 equals the 8 bytes located 8 or 9 bytes
        /// before them (hash directly before the name, or hash + stray terminator). Only verified names are returned.
        /// </summary>
        static IEnumerable<KeyValuePair<ulong, string>> RecoverNames(byte[] d, int from, int to)
        {
            to = Math.Min(to, d.Length);
            for (int s = from; s < to; s++)
            {
                var sb = new StringBuilder();
                for (int e = s; e < to && e - s < 128; e++)
                {
                    byte b = d[e];
                    if (b < 0x21 || b >= 0x7F) break;
                    sb.Append((char)b);
                    if (sb.Length < 2) continue;
                    ulong h = Crc64(sb.ToString());
                    for (int back = 8; back <= 9; back++)
                    {
                        int hp = s - back;
                        if (hp >= 4 && hp + 8 <= d.Length && BitConverter.ToUInt64(d, hp) == h)
                            yield return new KeyValuePair<ulong, string>(h, sb.ToString());
                    }
                }
            }
        }

        static string Latin1(byte[] d, int start, int len)
        {
            var chars = new char[len];
            for (int i = 0; i < len; i++) chars[i] = (char)d[start + i];
            return new string(chars);
        }

        public bool TryGetName(ulong hash, out string name) { return byHash.TryGetValue(hash, out name); }

        public string NameOrHex(ulong hash)
        {
            string n;
            return byHash.TryGetValue(hash, out n) ? n : Hex(hash);
        }

        public bool Contains(ulong hash) { return byHash.ContainsKey(hash); }

        public bool TryGetHash(string name, out ulong hash)
        {
            if (string.IsNullOrEmpty(name)) { hash = 0; return false; }
            return byName.TryGetValue(name.Trim(), out hash);
        }

        // ------------------------------------------------------------------ CRC64
        static readonly ulong[] Table = BuildTable();

        static ulong[] BuildTable()
        {
            const ulong poly = 0x42F0E1EBA9EA3693UL; // ECMA-182
            var t = new ulong[256];
            for (int i = 0; i < 256; i++)
            {
                ulong c = (ulong)i << 56;
                for (int k = 0; k < 8; k++)
                    c = (c & 0x8000000000000000UL) != 0 ? (c << 1) ^ poly : c << 1;
                t[i] = c;
            }
            return t;
        }

        /// <summary>Telltale symbol hash: CRC-64/ECMA-182 of the lowercased ASCII name.</summary>
        public static ulong Crc64(string s)
        {
            ulong crc = 0;
            if (s == null) return crc;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch >= 'A' && ch <= 'Z') ch = (char)(ch + 32);
                byte b = ch < 128 ? (byte)ch : (byte)'?'; // same as Encoding.ASCII
                crc = Table[((crc >> 56) ^ b) & 0xFF] ^ (crc << 8);
            }
            return crc;
        }

        public static string Hex(ulong h) { return "0x" + h.ToString("X16"); }

        public static bool TryParseHex(string s, out ulong h)
        {
            h = 0;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            if (s.Length == 0 || s.Length > 16) return false;
            return ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out h);
        }
    }
}
