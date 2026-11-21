using System;
using System.Collections.Generic;
using System.Text;

namespace TFM.Core.Meta
{
    public enum MetaKind
    {
        /// <summary>bool, written as ASCII '0' / '1'.</summary>
        Bool,
        /// <summary>Signed or unsigned integer of the width its type name implies; value in Bits.</summary>
        Int,
        /// <summary>32-bit float, raw bits in Bits so round trips are exact.</summary>
        Float,
        /// <summary>64-bit float, raw bits in Bits.</summary>
        Double,
        /// <summary>Length-prefixed byte string; bytes in Bytes.</summary>
        String,
        /// <summary>u64 CRC64 (Symbol, Handle&lt;T&gt;, HandleLock&lt;T&gt;); value in Bits.</summary>
        Hash,
        /// <summary>HandleBase: u64 name, then a u64 type hash (Bits2) only when the name is not 0.</summary>
        HandleBase,
        /// <summary>A class: ordered fields. Extra data written by a custom serializer uses names starting with '#'.</summary>
        Struct,
        /// <summary>DCArray / List / Set / Deque / SArray / BitSetBase and synthetic lists; elements in Items.</summary>
        Array,
        /// <summary>Map: Keys[i] -> Items[i].</summary>
        Map,
        /// <summary>Bytes that could not be decoded, kept verbatim.</summary>
        Raw,
    }

    public sealed class MetaField
    {
        public string Name;
        public MetaValue Value;

        public MetaField(string name, MetaValue value)
        {
            Name = name;
            Value = value;
        }
    }

    /// <summary>
    /// A decoded MetaStream value. One class covers every shape so trees can be walked, edited,
    /// cloned and moved between files without knowing their types in advance.
    /// </summary>
    public sealed class MetaValue
    {
        public string Type;
        public MetaKind Kind;
        public ulong Bits;
        public ulong Bits2;
        public byte[] Bytes;
        public List<MetaField> Fields;
        public List<MetaValue> Items;
        public List<MetaValue> Keys;
        /// <summary>Array elements are written as (u64 type hash, value): polymorphic lists.</summary>
        public bool Polymorphic;
        /// <summary>For Raw values: why decoding failed.</summary>
        public string Note;

        public MetaValue(string type, MetaKind kind)
        {
            Type = type;
            Kind = kind;
            if (kind == MetaKind.Struct) Fields = new List<MetaField>();
            else if (kind == MetaKind.Array) Items = new List<MetaValue>();
            else if (kind == MetaKind.Map)
            {
                Items = new List<MetaValue>();
                Keys = new List<MetaValue>();
            }
        }

        // ------------------------------------------------------------ constructors
        public static MetaValue NewBool(bool v) { MetaValue m = new MetaValue("bool", MetaKind.Bool); m.Bits = v ? 1UL : 0UL; return m; }

        public static MetaValue NewInt(string type, long v) { MetaValue m = new MetaValue(type, MetaKind.Int); m.Bits = (ulong)v; return m; }

        public static MetaValue NewFloat(float v)
        {
            MetaValue m = new MetaValue("float", MetaKind.Float);
            m.Bits = BitConverter.ToUInt32(BitConverter.GetBytes(v), 0);
            return m;
        }

        public static MetaValue NewHash(string type, ulong v) { MetaValue m = new MetaValue(type, MetaKind.Hash); m.Bits = v; return m; }

        public static MetaValue NewString(string text)
        {
            MetaValue m = new MetaValue("String", MetaKind.String);
            m.Bytes = EncodeText(text);
            return m;
        }

        // ------------------------------------------------------------ accessors
        public bool AsBool { get { return Bits != 0; } set { Bits = value ? 1UL : 0UL; } }

        public long AsLong { get { return (long)Bits; } set { Bits = (ulong)value; } }

        public int AsInt { get { return unchecked((int)(uint)Bits); } set { Bits = (ulong)(uint)value; } }

        public float AsFloat
        {
            get
            {
                if (Kind == MetaKind.Double) return (float)BitConverter.Int64BitsToDouble((long)Bits);
                return BitConverter.ToSingle(BitConverter.GetBytes((uint)Bits), 0);
            }
            set
            {
                if (Kind == MetaKind.Double) Bits = (ulong)BitConverter.DoubleToInt64Bits(value);
                else Bits = BitConverter.ToUInt32(BitConverter.GetBytes(value), 0);
            }
        }

        public string Text
        {
            get { return Bytes == null ? "" : DecodeText(Bytes); }
            set { Bytes = EncodeText(value ?? ""); }
        }

        /// <summary>Strings are shown as UTF-8 when valid, else Latin-1, and written back the same way.</summary>
        public static string DecodeText(byte[] b)
        {
            try
            {
                return new UTF8Encoding(false, true).GetString(b);
            }
            catch (ArgumentException)
            {
                char[] c = new char[b.Length];
                for (int i = 0; i < b.Length; i++) c[i] = (char)b[i];
                return new string(c);
            }
        }

        public static byte[] EncodeText(string s)
        {
            bool latin = true;
            for (int i = 0; i < s.Length; i++) if (s[i] > 0x7F) { latin = false; break; }
            if (latin)
            {
                byte[] b = new byte[s.Length];
                for (int i = 0; i < s.Length; i++) b[i] = (byte)s[i];
                return b;
            }
            return new UTF8Encoding(false).GetBytes(s);
        }

        /// <summary>The field with this name, or null.</summary>
        public MetaValue this[string name]
        {
            get
            {
                if (Fields == null) return null;
                for (int i = 0; i < Fields.Count; i++) if (Fields[i].Name == name) return Fields[i].Value;
                return null;
            }
        }

        public MetaField FieldEntry(string name)
        {
            if (Fields == null) return null;
            for (int i = 0; i < Fields.Count; i++) if (Fields[i].Name == name) return Fields[i];
            return null;
        }

        public void SetField(string name, MetaValue v)
        {
            MetaField f = FieldEntry(name);
            if (f != null) f.Value = v;
            else Fields.Add(new MetaField(name, v));
        }

        /// <summary>Follows a dotted path of field names ("Baseclass_DlgNode.mFlags.mFlags"). Null when any step is missing.</summary>
        public MetaValue Path(string path)
        {
            MetaValue cur = this;
            int start = 0;
            while (cur != null && start <= path.Length)
            {
                int dot = path.IndexOf('.', start);
                if (dot < 0) dot = path.Length;
                cur = cur[path.Substring(start, dot - start)];
                start = dot + 1;
                if (dot == path.Length) break;
            }
            return cur;
        }

        /// <summary>
        /// Searches this value and its base classes (fields named Baseclass_*) for a field.
        /// Lets callers ask for "mName" without knowing where in the hierarchy it was declared.
        /// </summary>
        public MetaValue FindField(string name)
        {
            if (Fields == null) return null;
            MetaValue direct = this[name];
            if (direct != null) return direct;
            for (int i = 0; i < Fields.Count; i++)
            {
                if (!Fields[i].Name.StartsWith("Baseclass_", StringComparison.Ordinal)) continue;
                MetaValue v = Fields[i].Value.FindField(name);
                if (v != null) return v;
            }
            return null;
        }

        /// <summary>Unwraps a Flags struct (or a plain integer) to its integer value.</summary>
        public uint FlagsValue
        {
            get
            {
                if (Kind == MetaKind.Int) return (uint)Bits;
                MetaValue f = this["mFlags"];
                return f != null && f.Kind == MetaKind.Int ? (uint)f.Bits : 0u;
            }
            set
            {
                if (Kind == MetaKind.Int) { Bits = value; return; }
                MetaValue f = this["mFlags"];
                if (f != null) f.Bits = value;
            }
        }

        public MetaValue Clone()
        {
            MetaValue c = new MetaValue(Type, MetaKind.Raw);
            c.Kind = Kind;
            c.Bits = Bits;
            c.Bits2 = Bits2;
            c.Polymorphic = Polymorphic;
            c.Note = Note;
            if (Bytes != null) c.Bytes = (byte[])Bytes.Clone();
            if (Fields != null)
            {
                c.Fields = new List<MetaField>(Fields.Count);
                for (int i = 0; i < Fields.Count; i++) c.Fields.Add(new MetaField(Fields[i].Name, Fields[i].Value.Clone()));
            }
            if (Items != null)
            {
                c.Items = new List<MetaValue>(Items.Count);
                for (int i = 0; i < Items.Count; i++) c.Items.Add(Items[i].Clone());
            }
            if (Keys != null)
            {
                c.Keys = new List<MetaValue>(Keys.Count);
                for (int i = 0; i < Keys.Count; i++) c.Keys.Add(Keys[i].Clone());
            }
            return c;
        }

        /// <summary>Calls <paramref name="visit"/> for this value and every value under it.</summary>
        public void Walk(Action<MetaValue> visit)
        {
            visit(this);
            if (Fields != null) for (int i = 0; i < Fields.Count; i++) Fields[i].Value.Walk(visit);
            if (Keys != null) for (int i = 0; i < Keys.Count; i++) Keys[i].Walk(visit);
            if (Items != null) for (int i = 0; i < Items.Count; i++) Items[i].Walk(visit);
        }

        /// <summary>A one-line summary for lists and tree rows.</summary>
        public string Summary()
        {
            switch (Kind)
            {
                case MetaKind.Bool: return AsBool ? "true" : "false";
                case MetaKind.Int: return MetaTypes.IsUnsigned(Type) ? Bits.ToString() : AsSignedString();
                case MetaKind.Float:
                case MetaKind.Double: return AsFloat.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
                case MetaKind.String: return "\"" + Text + "\"";
                case MetaKind.Hash: return "0x" + Bits.ToString("X16");
                case MetaKind.HandleBase: return Bits == 0 ? "(empty)" : "0x" + Bits.ToString("X16");
                case MetaKind.Array: return "[" + Items.Count + "]";
                case MetaKind.Map: return "{" + Items.Count + "}";
                case MetaKind.Raw: return "(" + (Bytes != null ? Bytes.Length : 0) + " raw bytes)";
                default: return "";
            }
        }

        string AsSignedString()
        {
            int w = MetaTypes.IntWidth(Type);
            if (w == 1) return ((sbyte)(byte)Bits).ToString();
            if (w == 2) return ((short)(ushort)Bits).ToString();
            if (w == 4) return ((int)(uint)Bits).ToString();
            return ((long)Bits).ToString();
        }

        public override string ToString() { return Type + " " + Summary(); }
    }
}
