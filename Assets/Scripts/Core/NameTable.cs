using System;
using System.Collections.Generic;

namespace TFM.Core
{
    /// <summary>
    /// A read-only name table keyed by Telltale hash, loaded from one of the binary name
    /// databases in Assets/Data. Layout: magic[8], u32 version, u32 count, then per entry
    /// u64 hash, u32 nameLength, name bytes (ASCII), sorted by hash.
    /// </summary>
    public sealed class NameTable
    {
        readonly Dictionary<ulong, string> byHash = new Dictionary<ulong, string>();
        string label = "";

        static readonly NameTable empty = new NameTable();
        public static NameTable Empty { get { return empty; } }

        public string Label { get { return label; } }
        public int Count { get { return byHash.Count; } }
        public bool IsEmpty { get { return byHash.Count == 0; } }

        NameTable() { }

        /// <summary>Parses a name table. Returns an empty table when the data is not one.</summary>
        public static NameTable Load(byte[] data, string expectedMagic, string label)
        {
            NameTable t = new NameTable();
            t.label = label ?? "";
            if (data == null || data.Length < 16) return t;

            string magic = Latin1(data, 0, 8);
            if (magic != expectedMagic) return t;

            uint count = BitConverter.ToUInt32(data, 12);
            int p = 16;
            for (uint i = 0; i < count; i++)
            {
                if (p + 12 > data.Length) break;
                ulong hash = BitConverter.ToUInt64(data, p);
                int len = (int)BitConverter.ToUInt32(data, p + 8);
                p += 12;
                if (len < 0 || p + len > data.Length) break;
                string name = Latin1(data, p, len);
                p += len;
                if (name.Length > 0 && !t.byHash.ContainsKey(hash)) t.byHash[hash] = name;
            }
            return t;
        }

        public bool TryGetName(ulong hash, out string name) { return byHash.TryGetValue(hash, out name); }

        /// <summary>Builds a table from already-resolved pairs, e.g. names merged from several dumps.</summary>
        public static NameTable FromDictionary(Dictionary<ulong, string> map, string label)
        {
            NameTable t = new NameTable();
            t.label = label ?? "";
            if (map != null)
                foreach (KeyValuePair<ulong, string> kv in map)
                    if (kv.Value != null && kv.Value.Length > 0) t.byHash[kv.Key] = kv.Value;
            return t;
        }

        /// <summary>Returns the name, or null when the hash is not in the table.</summary>
        public string NameOf(ulong hash)
        {
            string n;
            return byHash.TryGetValue(hash, out n) ? n : null;
        }

        static string Latin1(byte[] d, int start, int len)
        {
            char[] chars = new char[len];
            for (int i = 0; i < len; i++) chars[i] = (char)d[start + i];
            return new string(chars);
        }
    }

    /// <summary>
    /// Name lookup for everything the props editor shows: engine type names, property key names,
    /// and file references (meshes, textures, chores...). Each source is a separate namespace, so
    /// they are kept apart instead of merged into one hash table.
    /// </summary>
    public sealed class PropNameLookup
    {
        public NameTable TypeNames = NameTable.Empty;
        public NameTable KeyNames = NameTable.Empty;
        public NameTable FileNames = NameTable.Empty;

        /// <summary>Populated from the .bin name dumps; keyed by CRC64 of the full file name.</summary>
        public void AddFileNames(NameTable table)
        {
            if (table == null || table.Count == 0) return;
            FileNames = table;
        }

        public string Type(ulong hash) { return TypeNames.NameOf(hash); }
        public string Key(ulong hash) { return KeyNames.NameOf(hash); }

        /// <summary>The referenced file name, or null when it is not in the name dumps.</summary>
        public string FileRef(ulong hash) { return FileNames.NameOf(hash); }
    }
}
