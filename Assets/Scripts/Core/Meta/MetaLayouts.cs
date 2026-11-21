using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TFM.Core.Meta
{
    /// <summary>One serialized member of a class layout.</summary>
    public sealed class MetaMemberLayout
    {
        public readonly string Name;
        public readonly string Type;
        public readonly int Flags;
        /// <summary>True when the member is written behind a u32 size that counts itself.</summary>
        public readonly bool Blocked;

        public MetaMemberLayout(string name, string type, int flags, bool blocked)
        {
            Name = name;
            Type = type;
            Flags = flags;
            Blocked = blocked;
        }
    }

    /// <summary>The member list of one class at one version (its version CRC).</summary>
    public sealed class MetaClassLayout
    {
        public readonly string Type;
        public readonly uint Crc;
        /// <summary>True when the block bits were verified against the version CRC.</summary>
        public readonly bool Solved;
        public readonly List<MetaMemberLayout> Members = new List<MetaMemberLayout>();

        public MetaClassLayout(string type, uint crc, bool solved)
        {
            Type = type;
            Crc = crc;
            Solved = solved;
        }
    }

    /// <summary>
    /// Class layouts for MetaStream (MSV6) files, loaded from Assets/Data/MetaLayouts.bytes.
    /// That table is generated from TelltaleToolKit's version database (MIT, see the notice in the
    /// file) plus size-block bits solved from each layout's version CRC. A file's header names the
    /// version of every class it uses; the per-game default fills in the rest.
    /// </summary>
    public sealed class MetaLayouts
    {
        readonly Dictionary<string, MetaClassLayout> _byKey = new Dictionary<string, MetaClassLayout>(StringComparer.Ordinal);
        readonly Dictionary<string, List<MetaClassLayout>> _byType = new Dictionary<string, List<MetaClassLayout>>(StringComparer.Ordinal);
        readonly Dictionary<string, Dictionary<string, uint>> _defaults = new Dictionary<string, Dictionary<string, uint>>(StringComparer.Ordinal);
        readonly Dictionary<string, bool> _typeBlocked = new Dictionary<string, bool>(StringComparer.Ordinal);
        readonly Dictionary<ulong, string> _nameByHash = new Dictionary<ulong, string>();

        public const string DefaultGame = "TWDTTDS";

        static MetaLayouts _current;

        /// <summary>The table loaded at startup; empty until <see cref="SetCurrent"/> is called.</summary>
        public static MetaLayouts Current
        {
            get { return _current ?? (_current = new MetaLayouts()); }
        }

        public static void SetCurrent(MetaLayouts layouts) { _current = layouts; }

        public int LayoutCount { get { return _byKey.Count; } }

        public bool IsEmpty { get { return _byKey.Count == 0; } }

        /// <summary>Parses the tab-separated table. Lines starting with '#' are comments.</summary>
        public static MetaLayouts Load(byte[] data)
        {
            MetaLayouts t = new MetaLayouts();
            if (data == null || data.Length == 0) return t;
            string text = Encoding.UTF8.GetString(data);
            string game = DefaultGame;
            MetaClassLayout cur = null;
            int pos = 0;
            while (pos < text.Length)
            {
                int nl = text.IndexOf('\n', pos);
                if (nl < 0) nl = text.Length;
                string line = text.Substring(pos, nl - pos).TrimEnd('\r');
                pos = nl + 1;
                if (line.Length < 2 || line[0] == '#') continue;
                string[] p = line.Split('\t');
                switch (p[0])
                {
                    case "G":
                        game = p.Length > 1 ? p[1] : DefaultGame;
                        break;
                    case "D":
                        if (p.Length >= 3)
                        {
                            Dictionary<string, uint> d;
                            if (!t._defaults.TryGetValue(game, out d))
                            {
                                d = new Dictionary<string, uint>(StringComparer.Ordinal);
                                t._defaults[game] = d;
                            }
                            d[p[1]] = ParseHex(p[2]);
                            t.AddName(p[1]);
                        }
                        break;
                    case "T":
                        if (p.Length >= 4)
                        {
                            cur = new MetaClassLayout(p[1], ParseHex(p[2]), p[3] == "1");
                            t._byKey[Key(cur.Type, cur.Crc)] = cur;
                            List<MetaClassLayout> list;
                            if (!t._byType.TryGetValue(cur.Type, out list))
                            {
                                list = new List<MetaClassLayout>();
                                t._byType[cur.Type] = list;
                            }
                            list.Add(cur);
                            t.AddName(cur.Type);
                        }
                        break;
                    case "M":
                        if (cur != null && p.Length >= 5)
                        {
                            int flags;
                            int.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out flags);
                            cur.Members.Add(new MetaMemberLayout(p[1], p[2], flags, p[4] == "1"));
                            t.AddName(p[2]);
                            if (!t._typeBlocked.ContainsKey(p[2])) t._typeBlocked[p[2]] = p[4] == "1";
                        }
                        break;
                    case "B":
                        if (p.Length >= 3)
                        {
                            t._typeBlocked[p[1]] = p[2] == "1";
                            t.AddName(p[1]);
                        }
                        break;
                }
            }
            return t;
        }

        static string Key(string type, uint crc) { return type + "\u0001" + crc.ToString("X8"); }

        static uint ParseHex(string s)
        {
            uint v;
            uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
            return v;
        }

        void AddName(string type)
        {
            if (string.IsNullOrEmpty(type)) return;
            ulong h = HashDatabase.Crc64(type);
            if (!_nameByHash.ContainsKey(h)) _nameByHash[h] = type;
        }

        /// <summary>Registers a type name so its CRC64 can be resolved (for names built at runtime).</summary>
        public void RegisterTypeName(string type) { AddName(type); }

        /// <summary>The type name whose CRC64 is <paramref name="hash"/>, or null.</summary>
        public string TypeName(ulong hash)
        {
            string n;
            return _nameByHash.TryGetValue(hash, out n) ? n : null;
        }

        /// <summary>The default version CRC of a type in a game, or false when none is listed.</summary>
        public bool TryDefaultCrc(string game, string type, out uint crc)
        {
            crc = 0;
            Dictionary<string, uint> d;
            return _defaults.TryGetValue(game ?? DefaultGame, out d) && d.TryGetValue(type, out crc);
        }

        public MetaClassLayout Find(string type, uint crc)
        {
            MetaClassLayout l;
            return _byKey.TryGetValue(Key(type, crc), out l) ? l : null;
        }

        /// <summary>
        /// The layout to use for <paramref name="type"/>: the exact version when known, otherwise
        /// the game's default version, otherwise any known version (flagged through <paramref name="exact"/>).
        /// </summary>
        public MetaClassLayout Resolve(string type, uint? fileCrc, string game, out bool exact)
        {
            exact = true;
            MetaClassLayout l;
            if (fileCrc.HasValue)
            {
                l = Find(type, fileCrc.Value);
                if (l != null) return l;
            }
            uint d;
            if (TryDefaultCrc(game, type, out d))
            {
                l = Find(type, d);
                if (l != null)
                {
                    exact = !fileCrc.HasValue || fileCrc.Value == d;
                    return l;
                }
            }
            List<MetaClassLayout> list;
            if (_byType.TryGetValue(type, out list) && list.Count > 0)
            {
                exact = false;
                return list[0];
            }
            return null;
        }

        public bool HasLayout(string type) { return _byType.ContainsKey(type); }

        /// <summary>Whether a value of this type, used as a class member, sits behind a size block.</summary>
        public bool IsTypeBlocked(string type)
        {
            bool b;
            if (_typeBlocked.TryGetValue(type, out b)) return b;
            return !MetaTypes.IsUnblockedIntrinsic(type);
        }
    }
}
