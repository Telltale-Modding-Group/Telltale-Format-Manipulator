using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TFM.Core
{
    public sealed class PropParseException : Exception
    {
        public PropParseException(string message) : base(message) { }
    }

    /// <summary>How a property's element values are laid out in the file.</summary>
    public enum PropKind
    {
        Unknown = 0,
        Bool,
        Float,
        Color,
        Vec3,
        Symbol,
        Text,
        Blob8
    }

    /// <summary>
    /// One value of a property. <see cref="Value"/> holds the raw value bytes only: for text the
    /// leading u32 length is not included, every other kind stores its value verbatim.
    /// </summary>
    public sealed class PropElement
    {
        public ulong KeyHash;
        public byte[] Value = new byte[0];

        public PropElement() { }

        public PropElement(ulong keyHash, byte[] value)
        {
            KeyHash = keyHash;
            Value = value ?? new byte[0];
        }

        public PropElement Clone()
        {
            return new PropElement(KeyHash, (byte[])Value.Clone());
        }
    }

    /// <summary>
    /// A single property record: a type hash, an element count and the elements that follow.
    /// Records are rebuilt from their elements, so a property can change size freely.
    /// </summary>
    public sealed class PropField
    {
        public ulong TypeHash;
        public PropKind Kind;
        public List<PropElement> Elements = new List<PropElement>();

        /// <summary>Stable identity used when comparing or porting between files.</summary>
        public string Signature
        {
            get
            {
                ulong first = Elements.Count > 0 ? Elements[0].KeyHash : 0UL;
                return TypeHash.ToString("X16") + ":" + first.ToString("X16");
            }
        }

        public string TypeName
        {
            get { return PropTypeNames.Describe(TypeHash, Kind); }
        }

        public int ValueLengthOf(PropElement e)
        {
            return Kind == PropKind.Text ? 4 + e.Value.Length : e.Value.Length;
        }

        public int ByteLength
        {
            get
            {
                int n = 12;
                for (int i = 0; i < Elements.Count; i++) n += 8 + ValueLengthOf(Elements[i]);
                return n;
            }
        }

        /// <summary>Serializes the record exactly as it is laid out in the file.</summary>
        public byte[] Build()
        {
            byte[] b = new byte[ByteLength];
            PutU64(b, 0, TypeHash);
            PutU32(b, 8, (uint)Elements.Count);
            int o = 12;
            for (int i = 0; i < Elements.Count; i++)
            {
                PropElement e = Elements[i];
                PutU64(b, o, e.KeyHash);
                o += 8;
                if (Kind == PropKind.Text)
                {
                    PutU32(b, o, (uint)e.Value.Length);
                    o += 4;
                }
                if (e.Value.Length > 0) Buffer.BlockCopy(e.Value, 0, b, o, e.Value.Length);
                o += e.Value.Length;
            }
            return b;
        }

        public PropField Clone()
        {
            PropField f = new PropField();
            f.TypeHash = TypeHash;
            f.Kind = Kind;
            for (int i = 0; i < Elements.Count; i++) f.Elements.Add(Elements[i].Clone());
            return f;
        }

        internal static void PutU32(byte[] a, int o, uint v)
        {
            a[o] = (byte)v; a[o + 1] = (byte)(v >> 8); a[o + 2] = (byte)(v >> 16); a[o + 3] = (byte)(v >> 24);
        }

        internal static void PutU64(byte[] a, int o, ulong v)
        {
            PutU32(a, o, (uint)v);
            PutU32(a, o + 4, (uint)(v >> 32));
        }
    }

    /// <summary>
    /// A run of payload bytes that could not be decoded because of an unknown type layout.
    /// It is kept verbatim and rewritten in place, so the file stays byte-exact.
    /// </summary>
    public sealed class PropOpaque
    {
        public byte[] Bytes = new byte[0];
        public string Reason = "";
        public ulong TypeHash;
        public uint StoredCount;

        public int ByteLength { get { return Bytes.Length; } }

        public PropOpaque Clone()
        {
            PropOpaque o = new PropOpaque();
            o.Bytes = (byte[])Bytes.Clone();
            o.Reason = Reason;
            o.TypeHash = TypeHash;
            o.StoredCount = StoredCount;
            return o;
        }
    }

    /// <summary>Type hashes recovered from the reference set, with the layout each one uses.</summary>
    public static class PropTypeNames
    {
        public const ulong TypeBool = 0x9004C5587575D6C0UL;
        public const ulong TypeFloat = 0xBAE4CBD77F139A91UL;
        public const ulong TypeString = 0xCD9C6E605F5AF4B4UL;
        public const ulong TypeColor = 0xC16762F7763D62ABUL;
        public const ulong TypeBounds = 0x394C43AF4FF52C94UL;
        public const ulong TypeSymbol = 0x004F023463D89FB0UL;
        public const ulong TypeProp8A = 0x73E09E0F84717F0AUL;
        public const ulong TypeProp8B = 0x1B3355ECACF8ED7CUL;
        public const ulong TypeProp8C = 0x823DB9F90B846250UL;

        static readonly Dictionary<ulong, PropKind> Kinds = new Dictionary<ulong, PropKind>();
        static readonly Dictionary<ulong, string> Names = new Dictionary<ulong, string>();

        static PropTypeNames()
        {
            Add(TypeBool, PropKind.Bool, "bool");
            Add(TypeFloat, PropKind.Float, "float");
            Add(TypeString, PropKind.Text, "string");
            Add(TypeColor, PropKind.Color, "color");
            Add(TypeBounds, PropKind.Vec3, "vec3");
            Add(TypeSymbol, PropKind.Symbol, "symbol");
            Add(TypeProp8A, PropKind.Blob8, "prop8:73e09e0f");
            Add(TypeProp8B, PropKind.Blob8, "prop8:1b3355ec");
            Add(TypeProp8C, PropKind.Blob8, "prop8:823db9f9");
        }

        static void Add(ulong hash, PropKind kind, string name)
        {
            Kinds[hash] = kind;
            Names[hash] = name;
        }

        /// <summary>Returns Unknown for type hashes the reference set did not identify.</summary>
        public static PropKind KindOf(ulong typeHash)
        {
            PropKind k;
            return Kinds.TryGetValue(typeHash, out k) ? k : PropKind.Unknown;
        }

        public static int FixedSizeOf(ulong typeHash)
        {
            switch (KindOf(typeHash))
            {
                case PropKind.Bool: return 1;
                case PropKind.Float: return 4;
                case PropKind.Color: return 16;
                case PropKind.Vec3: return 12;
                case PropKind.Symbol: return 8;
                case PropKind.Blob8: return 8;
                default: return -1;
            }
        }

        public static string Describe(ulong typeHash, PropKind kind)
        {
            string n;
            if (Names.TryGetValue(typeHash, out n)) return n;
            return "0x" + typeHash.ToString("X16");
        }
    }

    /// <summary>
    /// A loaded Telltale .prop file. The MSV6 header is kept verbatim. Properties whose type
    /// layout is unknown are preserved as opaque byte runs; the parser resynchronises after them,
    /// so later properties stay editable. Saving an untouched file reproduces it byte for byte.
    /// </summary>
    public sealed class PropFile
    {
        public const int MagicMSV6 = 1297307190; // "MSV6"

        /// <summary>A decoded property or an opaque run, in file order.</summary>
        sealed class Item
        {
            public PropField Field;      // null for an opaque run
            public PropOpaque Opaque;    // null for a decoded property
            public bool IsOpaque { get { return Field == null; } }
        }

        public string SourcePath { get; private set; }
        public string MagicText { get; private set; }

        /// <summary>Every decoded property, in file order. Opaque runs are not listed here.</summary>
        public IReadOnlyList<PropField> Fields { get { return fields; } }

        /// <summary>Opaque runs kept verbatim, in file order.</summary>
        public IReadOnlyList<PropOpaque> OpaqueBlocks { get { return opaqueView; } }

        /// <summary>True when some part of the payload could not be decoded.</summary>
        public bool IsPartial { get { return opaquePropCount > 0; } }

        /// <summary>Total bytes kept as opaque runs.</summary>
        public int RawTailLength
        {
            get
            {
                int n = 0;
                for (int i = 0; i < items.Count; i++) if (items[i].IsOpaque) n += items[i].Opaque.ByteLength;
                return n;
            }
        }

        /// <summary>Why properties are undecodable, when IsPartial is true.</summary>
        public string PartialReason { get; private set; }

        public int HeaderSize { get { return headerEnd; } }
        public int OriginalSize { get { return original.Length; } }
        public IReadOnlyList<ulong> HeaderTable { get { return headerTable; } }

        /// <summary>Byte length of the leading key block that precedes the property count.</summary>
        public int PrologueLength { get { return prologue.Length; } }

        /// <summary>Property count as stored in the file, including undecodable ones.</summary>
        public int StoredPropCount { get { return storedPropCount; } }

        /// <summary>Number of properties decoded at load time, before any edits.</summary>
        public int DecodedAtLoad { get { return decodedAtLoad; } }

        /// <summary>True when at least one property could not be decoded.</summary>
        public bool HasOpaque { get { return opaqueView.Count > 0; } }

        byte[] original;
        readonly List<Item> items = new List<Item>();
        readonly List<PropField> fields = new List<PropField>();
        readonly List<PropOpaque> opaqueView = new List<PropOpaque>();
        List<ulong> headerTable = new List<ulong>();

        int headerEnd;      // H: first byte after the platform/type table
        int sizeOffset;     // field at 0x04, equals file length - H
        int blockSizeOffset;// H+8, covers payload plus 8
        int payloadStart;   // H+16
        byte[] prologue = new byte[0]; // the k leading hashes, copied verbatim
        int prologueKeys;   // H+12
        int storedPropCount;    // property count as written in the file
        int opaquePropCount;    // how many of those sit inside opaque runs
        int decodedAtLoad;      // properties decoded before any edits

        PropFile() { }

        // ================================================================ loading
        public static PropFile Load(string path)
        {
            if (!File.Exists(path)) throw new PropParseException("File not found: " + path);
            return Parse(File.ReadAllBytes(path), path);
        }

        public static PropFile Parse(byte[] data, string sourcePath)
        {
            if (data == null || data.Length < 0x20)
                throw new PropParseException("File is too small to be a property file.");

            PropFile f = new PropFile();
            f.original = data;
            f.SourcePath = sourcePath;

            uint magic = ReadU32(data, 0);
            if (magic != MagicMSV6)
            {
                string txt = Encoding.ASCII.GetString(data, 0, 4);
                f.MagicText = txt;
                throw new PropParseException("Not a MetaStream 6 property file (magic '" + txt + "').");
            }
            f.MagicText = "MSV6";

            uint tableCount = ReadU32(data, 0x10);
            if (tableCount > 512) throw new PropParseException("Implausible header table size.");

            f.headerEnd = 0x14 + (int)tableCount * 12;
            if (f.headerEnd + 16 > data.Length)
                throw new PropParseException("Header runs past the end of the file.");

            for (int i = 0; i < tableCount; i++)
                f.headerTable.Add(ReadU64(data, 0x14 + i * 12));

            f.sizeOffset = 0x04;
            f.blockSizeOffset = f.headerEnd + 8;
            f.payloadStart = f.headerEnd + 16;
            f.prologueKeys = (int)ReadU32(data, f.headerEnd + 12);

            int payloadEnd = f.headerEnd + 8 + (int)ReadU32(data, f.blockSizeOffset);
            if (payloadEnd > data.Length) payloadEnd = data.Length;

            int prologueBytes = f.prologueKeys * 8;
            if (f.payloadStart + prologueBytes + 4 > payloadEnd)
            {
                f.PartialReason = "payload too short for the leading key block";
                f.AddOpaque(Slice(data, f.payloadStart, payloadEnd), 0, 0, "short payload");
                f.opaquePropCount = 1;
                return f;
            }

            f.prologue = Slice(data, f.payloadStart, f.payloadStart + prologueBytes);

            int o = f.payloadStart + prologueBytes;
            uint propCount = ReadU32(data, o);
            o += 4;
            f.storedPropCount = (int)propCount;

            if (propCount > 100000)
            {
                f.PartialReason = "implausible property count (" + propCount + ")";
                f.AddOpaque(Slice(data, o, payloadEnd), 0, 0, "implausible property count");
                f.opaquePropCount = (int)Math.Min(propCount, 1);
                return f;
            }

            f.ParsePayload(data, o, payloadEnd, (int)propCount);
            return f;
        }

        /// <summary>
        /// Walks the properties. When a type has no known layout the run is stored opaque and the
        /// parser scans forward for the next position from which the rest of the payload parses
        /// cleanly, so properties after the gap stay editable.
        /// </summary>
        void ParsePayload(byte[] d, int o, int end, int propCount)
        {
            int decoded = 0;
            int opaqueProps = 0;

            while (decoded + opaqueProps < propCount && o < end)
            {
                PropField parsed;
                string why;
                int next;

                if (TryReadField(d, o, end, out parsed, out next, out why))
                {
                    items.Add(new Item { Field = parsed });
                    fields.Add(parsed);
                    decoded++;
                    o = next;
                    continue;
                }

                // Unknown layout: find the first position after this one from which the rest of
                // the payload parses cleanly, so later properties stay editable.
                int remaining = propCount - (decoded + opaqueProps);
                int resumedAt = FindResync(d, o, end);

                if (resumedAt < 0)
                {
                    // Nothing after this point can be recovered; keep the rest verbatim.
                    ulong th = o + 8 <= end ? ReadU64(d, o) : 0UL;
                    AddOpaque(Slice(d, o, end), th, (uint)remaining, why);
                    opaqueProps += remaining;
                    o = end;
                    PartialReason = why + "; the rest of the payload is kept verbatim";
                    break;
                }

                // Count how many properties the resynchronised region actually holds, so the
                // stored property count still adds up.
                int visibleAfter = CountFields(d, resumedAt, end);

                ulong typeHash = o + 8 <= end ? ReadU64(d, o) : 0UL;
                AddOpaque(Slice(d, o, resumedAt), typeHash, (uint)Math.Max(1, remaining - visibleAfter), why);
                opaqueProps += Math.Max(1, remaining - visibleAfter);
                o = resumedAt;
                PartialReason = why + "; later properties were recovered by resynchronising";
            }

            if (o != end)
            {
                AddOpaque(Slice(d, o, end), 0, 0, "trailing payload");
                PartialReason = (end - o) + " trailing payload byte(s) kept verbatim";
            }

            opaquePropCount = opaqueProps;
            decodedAtLoad = fields.Count;
        }

        /// <summary>Counts decodable properties in [from, end), stopping at the first failure.</summary>
        static int CountFields(byte[] d, int from, int end)
        {
            int o = from, n = 0;
            while (o < end)
            {
                PropField tmp;
                string why;
                int next;
                if (!TryReadField(d, o, end, out tmp, out next, out why)) break;
                o = next;
                n++;
            }
            return n;
        }

        /// <summary>
        /// Returns the earliest offset after <paramref name="from"/> from which the remainder of
        /// the payload decodes as a sequence of properties ending exactly at <paramref name="end"/>.
        /// Returns -1 when no such offset exists.
        /// </summary>
        static int FindResync(byte[] d, int from, int end)
        {
            for (int cand = from + 1; cand < end; cand++)
            {
                if (ParseChainToEnd(d, cand, end)) return cand;
            }
            return -1;
        }

        static bool ParseChainToEnd(byte[] d, int from, int end)
        {
            int o = from;
            int n = 0;
            while (o < end)
            {
                PropField tmp;
                string why;
                int next;
                if (!TryReadField(d, o, end, out tmp, out next, out why)) return false;
                o = next;
                n++;
                if (n > 5000) return false;
            }
            return o == end && n > 0;
        }

        void AddOpaque(byte[] bytes, ulong typeHash, uint storedCount, string reason)
        {
            if (bytes == null || bytes.Length == 0) return;
            PropOpaque op = new PropOpaque();
            op.Bytes = bytes;
            op.TypeHash = typeHash;
            op.StoredCount = storedCount;
            op.Reason = reason;
            items.Add(new Item { Opaque = op });
            opaqueView.Add(op);
        }

        static bool TryReadField(byte[] d, int o, int end, out PropField field, out int next, out string why)
        {
            field = null;
            next = o;
            why = null;

            if (o + 12 > end) { why = "truncated property header"; return false; }

            ulong type = ReadU64(d, o);
            uint count = ReadU32(d, o + 8);
            PropKind kind = PropTypeNames.KindOf(type);

            if (kind == PropKind.Unknown)
            {
                why = "unknown type hash 0x" + type.ToString("X16") + " (" + count + " values)";
                return false;
            }
            if (count > 65536) { why = "implausible value count " + count; return false; }

            int p = o + 12;
            PropField f = new PropField();
            f.TypeHash = type;
            f.Kind = kind;
            int fixedSize = PropTypeNames.FixedSizeOf(type);

            for (int i = 0; i < count; i++)
            {
                if (p + 8 > end) { why = "truncated element key"; return false; }
                ulong key = ReadU64(d, p);
                p += 8;

                int n;
                if (kind == PropKind.Text)
                {
                    if (p + 4 > end) { why = "truncated string length"; return false; }
                    uint len = ReadU32(d, p);
                    p += 4;
                    if (len > 1 << 20 || p + len > end) { why = "string length " + len + " runs past the payload"; return false; }
                    n = (int)len;
                }
                else
                {
                    if (fixedSize < 0) { why = "no size for type"; return false; }
                    if (p + fixedSize > end) { why = "value runs past the payload"; return false; }
                    n = fixedSize;
                }

                f.Elements.Add(new PropElement(key, Slice(d, p, p + n)));
                p += n;
            }

            field = f;
            next = p;
            return true;
        }

        // ================================================================ editing
        /// <summary>Removes a property and its data. Count and size fields are repaired on save.</summary>
        public bool DeleteField(PropField field)
        {
            int at = IndexOfItem(field);
            if (at < 0) return false;
            items.RemoveAt(at);
            RebuildFieldView();
            return true;
        }

        public void AddField(PropField field)
        {
            if (field == null) return;
            items.Add(new Item { Field = field });
            RebuildFieldView();
        }

        /// <summary>Appends a copy of <paramref name="field"/> after <paramref name="index"/>.</summary>
        public void InsertField(PropField field, int index)
        {
            if (field == null) return;
            if (index < 0 || index >= fields.Count) { AddField(field); return; }

            PropField anchor = fields[index];
            int at = IndexOfItem(anchor);
            if (at < 0) { AddField(field); return; }
            items.Insert(at + 1, new Item { Field = field });
            RebuildFieldView();
        }

        public int IndexOf(PropField field)
        {
            for (int i = 0; i < fields.Count; i++) if (ReferenceEquals(fields[i], field)) return i;
            return -1;
        }

        /// <summary>Swaps <paramref name="oldField"/> for <paramref name="newField"/> in place.</summary>
        public bool ReplaceField(PropField oldField, PropField newField)
        {
            if (newField == null) return false;
            int at = IndexOfItem(oldField);
            if (at < 0) return false;
            items[at] = new Item { Field = newField };
            RebuildFieldView();
            return true;
        }

        public PropField FindBySignature(string signature)
        {
            for (int i = 0; i < fields.Count; i++)
                if (fields[i].Signature == signature) return fields[i];
            return null;
        }

        int IndexOfItem(PropField field)
        {
            for (int i = 0; i < items.Count; i++)
                if (!items[i].IsOpaque && ReferenceEquals(items[i].Field, field)) return i;
            return -1;
        }

        void RebuildFieldView()
        {
            fields.Clear();
            for (int i = 0; i < items.Count; i++) if (!items[i].IsOpaque) fields.Add(items[i].Field);
        }

        // ================================================================ saving
        public byte[] Serialize()
        {
            int opaqueBytes = 0;
            int opaqueProps = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (!items[i].IsOpaque) continue;
                opaqueBytes += items[i].Opaque.ByteLength;
                opaqueProps += (int)items[i].Opaque.StoredCount;
            }

            int payloadLen = prologue.Length + 4 + opaqueBytes;
            for (int i = 0; i < fields.Count; i++) payloadLen += fields[i].ByteLength;

            int total = headerEnd + 16 + payloadLen;
            byte[] o = new byte[total];

            Buffer.BlockCopy(original, 0, o, 0, headerEnd + 16);

            Put32(o, sizeOffset, (uint)(total - headerEnd));
            // The block field covers itself and the prologue-key count, so it is payload + 8.
            Put32(o, blockSizeOffset, (uint)(payloadLen + 8));

            int p = payloadStart;
            if (prologue.Length > 0) Buffer.BlockCopy(prologue, 0, o, p, prologue.Length);
            p += prologue.Length;

            Put32(o, p, (uint)(fields.Count + opaqueProps));
            p += 4;

            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].IsOpaque)
                {
                    byte[] raw = items[i].Opaque.Bytes;
                    Buffer.BlockCopy(raw, 0, o, p, raw.Length);
                    p += raw.Length;
                }
                else
                {
                    byte[] b = items[i].Field.Build();
                    Buffer.BlockCopy(b, 0, o, p, b.Length);
                    p += b.Length;
                }
            }

            return o;
        }

        static void Put32(byte[] a, int o, uint v) { PropField.PutU32(a, o, v); }

        /// <summary>Serialize, verify by re-parsing, then write. Throws (writing nothing) on failure.</summary>
        public void Save(string path)
        {
            byte[] o = Serialize();
            string problem = Verify(o);
            if (problem != null)
                throw new InvalidOperationException("Save aborted, the rebuilt file failed verification: " + problem);

            File.WriteAllBytes(path, o);

            PropFile fresh = Parse(o, path);
            AdoptFrom(fresh);
        }

        /// <summary>Returns null when the bytes re-parse to the same properties, else the reason.</summary>
        public string Verify(byte[] o)
        {
            PropFile check;
            try { check = Parse(o, SourcePath); }
            catch (PropParseException e) { return "re-parse failed: " + e.Message; }

            if (check.items.Count != items.Count)
                return "item count " + check.items.Count + " != " + items.Count;
            if (check.fields.Count != fields.Count)
                return "property count " + check.fields.Count + " != " + fields.Count;
            if (check.RawTailLength != RawTailLength)
                return "opaque byte count changed (" + RawTailLength + " -> " + check.RawTailLength + ")";

            for (int i = 0; i < fields.Count; i++)
            {
                PropField a = fields[i], b = check.fields[i];
                if (a.TypeHash != b.TypeHash) return "type hash changed at property " + (i + 1);
                if (a.Elements.Count != b.Elements.Count) return "value count changed at property " + (i + 1);
                for (int j = 0; j < a.Elements.Count; j++)
                {
                    if (a.Elements[j].KeyHash != b.Elements[j].KeyHash) return "key changed at property " + (i + 1);
                    if (a.Elements[j].Value.Length != b.Elements[j].Value.Length) return "value size changed at property " + (i + 1);
                }
            }
            return null;
        }

        void AdoptFrom(PropFile fresh)
        {
            original = fresh.original;
            items.Clear();
            items.AddRange(fresh.items);
            headerTable = fresh.headerTable;
            headerEnd = fresh.headerEnd;
            sizeOffset = fresh.sizeOffset;
            blockSizeOffset = fresh.blockSizeOffset;
            payloadStart = fresh.payloadStart;
            prologue = fresh.prologue;
            prologueKeys = fresh.prologueKeys;
            storedPropCount = fresh.storedPropCount;
            opaquePropCount = fresh.opaquePropCount;
            PartialReason = fresh.PartialReason;
            MagicText = fresh.MagicText;
            SourcePath = fresh.SourcePath;
            RebuildFieldView();
            opaqueView.Clear();
            for (int i = 0; i < items.Count; i++) if (items[i].IsOpaque) opaqueView.Add(items[i].Opaque);
        }

        // ================================================================ helpers
        static byte[] Slice(byte[] d, int from, int to)
        {
            if (to < from) to = from;
            byte[] b = new byte[to - from];
            Buffer.BlockCopy(d, from, b, 0, b.Length);
            return b;
        }

        internal static uint ReadU32(byte[] d, int o)
        {
            return (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
        }

        internal static ulong ReadU64(byte[] d, int o)
        {
            return ReadU32(d, o) | ((ulong)ReadU32(d, o + 4) << 32);
        }

        public static float ReadFloat(byte[] d, int o) { return BitConverter.ToSingle(d, o); }

        public static void WriteFloat(byte[] d, int o, float v)
        {
            byte[] b = BitConverter.GetBytes(v);
            Buffer.BlockCopy(b, 0, d, o, 4);
        }

        public static uint ReadUInt(byte[] d, int o) { return ReadU32(d, o); }
        public static void WriteUInt(byte[] d, int o, uint v) { PropField.PutU32(d, o, v); }
    }
}
