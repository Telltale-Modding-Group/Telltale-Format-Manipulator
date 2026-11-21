using System;
using System.Collections.Generic;
using System.IO;

namespace TFM.Core.Meta
{
    public sealed class MetaParseException : Exception
    {
        public MetaParseException(string message) : base(message) { }
    }

    /// <summary>One entry of the MSV6 header's type table.</summary>
    public sealed class MetaHeaderType
    {
        public ulong Hash;
        public uint Crc;
        /// <summary>Resolved type name, or null when the hash is unknown.</summary>
        public string Name;
    }

    /// <summary>
    /// A MetaStream version 6 file ("6VSM"): header, a decoded value tree for the main section, and
    /// the debug and async sections kept as raw bytes. Every class is read through its layout; the
    /// few classes the engine serializes by hand are handled in <see cref="MetaCodec"/>. Members that
    /// sit behind a size block and cannot be decoded are kept as raw bytes, so a file always saves
    /// back exactly unless something was edited.
    /// </summary>
    public sealed class MetaFile
    {
        public const uint MagicMsv6 = 0x4D535636; // bytes '6','V','S','M'

        public string SourcePath;
        public string RootType;
        public string Game = MetaLayouts.DefaultGame;
        public readonly List<MetaHeaderType> Types = new List<MetaHeaderType>();
        public MetaValue Root;
        public byte[] DebugSection = new byte[0];
        public byte[] AsyncSection = new byte[0];

        /// <summary>Problems found while reading: raw fallbacks, inexact layouts. Empty for a clean decode.</summary>
        public readonly List<string> Warnings = new List<string>();

        /// <summary>Number of regions kept as raw bytes because they could not be decoded.</summary>
        public int RawRegions;

        internal MetaLayouts Layouts;

        /// <summary>Classes met while reading. Some files leave a few of them out of the header
        /// (for example Flags in chores); only classes that were not in the file before get added.</summary>
        internal HashSet<string> ReadClasses = new HashSet<string>(StringComparer.Ordinal);

        public bool FullyDecoded { get { return RawRegions == 0; } }

        /// <summary>The root class for a file name, or null when the extension is not a known MetaStream format.</summary>
        public static string RootTypeForPath(string path)
        {
            string n = (path ?? "").ToLowerInvariant();
            if (n.EndsWith(".chore")) return "Chore";
            if (n.EndsWith(".dlog")) return "Dlg";
            if (n.EndsWith(".landb")) return "LanguageDB";
            if (n.EndsWith(".preloadpackagertd")) return "PreloadPackage::RuntimeDataDialog";
            if (n.EndsWith(".preloadpackagerts")) return "PreloadPackage::RuntimeDataScene";
            if (n.EndsWith(".aam")) return "ActorAgentMapper";
            return null;
        }

        public static MetaFile Load(string path, string rootType)
        {
            MetaFile f = Parse(File.ReadAllBytes(path), rootType ?? RootTypeForPath(path), MetaLayouts.Current);
            f.SourcePath = path;
            return f;
        }

        public static MetaFile Parse(byte[] data, string rootType, MetaLayouts layouts)
        {
            if (layouts == null || layouts.IsEmpty)
                throw new MetaParseException("The MetaStream layout table (Assets/Data/MetaLayouts.bytes) is not loaded.");
            if (string.IsNullOrEmpty(rootType)) throw new MetaParseException("Unknown file type.");
            if (data == null || data.Length < 20) throw new MetaParseException("The file is too short to be a MetaStream file.");
            uint magic = BitConverter.ToUInt32(data, 0);
            if (magic != MagicMsv6)
                throw new MetaParseException("Not a MetaStream 6 file: it starts with 0x" + magic.ToString("X8")
                    + ". Only Definitive Edition style (6VSM) files are supported so far.");

            MetaFile f = new MetaFile();
            f.Layouts = layouts;
            f.RootType = rootType;
            uint mainSize = BitConverter.ToUInt32(data, 4);
            uint debugSize = BitConverter.ToUInt32(data, 8);
            uint asyncSize = BitConverter.ToUInt32(data, 12);
            uint count = BitConverter.ToUInt32(data, 16);
            if (count > 100000 || 20L + count * 12L > data.Length) throw new MetaParseException("Corrupt type table (" + count + " entries).");
            int p = 20;
            for (int i = 0; i < count; i++)
            {
                MetaHeaderType t = new MetaHeaderType();
                t.Hash = BitConverter.ToUInt64(data, p);
                t.Crc = BitConverter.ToUInt32(data, p + 8);
                t.Name = layouts.TypeName(t.Hash);
                f.Types.Add(t);
                p += 12;
            }
            long end = (long)p + mainSize + debugSize + asyncSize;
            if (end != data.Length)
                throw new MetaParseException("Section sizes (" + mainSize + " + " + debugSize + " + " + asyncSize
                    + ") do not match the file length.");

            MetaCodec codec = new MetaCodec(f, data, p, p + (int)mainSize);
            f.Root = codec.ReadRoot(rootType);
            for (int i = 0; i < codec.ClassesWritten.Count; i++) f.ReadClasses.Add(codec.ClassesWritten[i]);
            f.DebugSection = Slice(data, p + (int)mainSize, (int)debugSize);
            f.AsyncSection = Slice(data, p + (int)mainSize + (int)debugSize, (int)asyncSize);
            return f;
        }

        static byte[] Slice(byte[] d, int start, int len)
        {
            byte[] b = new byte[len];
            Buffer.BlockCopy(d, start, b, 0, len);
            return b;
        }

        /// <summary>Version CRC of a type as listed in this file's header, or null.</summary>
        public uint? CrcOf(string type)
        {
            for (int i = 0; i < Types.Count; i++) if (Types[i].Name == type) return Types[i].Crc;
            return null;
        }

        Dictionary<string, uint> _crcCache;
        int _crcCacheCount = -1;

        /// <summary>Cached form of <see cref="CrcOf"/> for the hot read/write path.</summary>
        internal bool TryCrc(string type, out uint crc)
        {
            if (_crcCache == null || _crcCacheCount != Types.Count)
            {
                _crcCache = new Dictionary<string, uint>(StringComparer.Ordinal);
                for (int i = 0; i < Types.Count; i++)
                    if (Types[i].Name != null && !_crcCache.ContainsKey(Types[i].Name)) _crcCache[Types[i].Name] = Types[i].Crc;
                _crcCacheCount = Types.Count;
            }
            return _crcCache.TryGetValue(type, out crc);
        }

        public bool HasHeaderType(string type) { return CrcOf(type).HasValue; }

        /// <summary>
        /// Adds a type to the header table when it is missing. The version comes from <paramref name="crc"/>,
        /// else the game's default. Returns true when the table changed.
        /// </summary>
        public bool EnsureHeaderType(string type, uint? crc)
        {
            if (HasHeaderType(type)) return false;
            uint c;
            if (crc.HasValue) c = crc.Value;
            else if (!Layouts.TryDefaultCrc(Game, type, out c)) return false;
            MetaHeaderType t = new MetaHeaderType();
            t.Hash = HashDatabase.Crc64(type);
            t.Crc = c;
            t.Name = type;
            Types.Add(t);
            return true;
        }

        public byte[] Serialize()
        {
            MetaCodec codec = new MetaCodec(this, null, 0, 0);
            byte[] main = codec.WriteRoot(Root, RootType);
            // Classes written for the first time (moved or newly created data) get header entries.
            for (int i = 0; i < codec.ClassesWritten.Count; i++)
            {
                string c = codec.ClassesWritten[i];
                if (ReadClasses.Contains(c)) continue;
                EnsureHeaderType(c, null);
                ReadClasses.Add(c);
            }

            MemoryStream ms = new MemoryStream(20 + Types.Count * 12 + main.Length + DebugSection.Length + AsyncSection.Length);
            BinaryWriter w = new BinaryWriter(ms);
            w.Write(MagicMsv6);
            w.Write((uint)main.Length);
            w.Write((uint)DebugSection.Length);
            w.Write((uint)AsyncSection.Length);
            w.Write((uint)Types.Count);
            for (int i = 0; i < Types.Count; i++)
            {
                w.Write(Types[i].Hash);
                w.Write(Types[i].Crc);
            }
            w.Write(main);
            w.Write(DebugSection);
            w.Write(AsyncSection);
            w.Flush();
            return ms.ToArray();
        }

        public void Save(string path)
        {
            byte[] bytes = Serialize();
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            SourcePath = path;
        }

        /// <summary>Builds a value of <paramref name="type"/> with every member at its zero value.</summary>
        public MetaValue CreateDefault(string type)
        {
            return new MetaCodec(this, null, 0, 0).CreateDefault(type, 0);
        }
    }

    /// <summary>Reads and writes MetaValue trees. One instance per read or write pass.</summary>
    public sealed class MetaCodec
    {
        readonly MetaFile _file;
        readonly MetaLayouts _L;
        readonly byte[] _d;
        int _p;
        int _limit;

        MemoryStream _ms;
        BinaryWriter _w;
        readonly HashSet<string> _classSeen = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Classes written in this pass, in first-use order.</summary>
        public readonly List<string> ClassesWritten = new List<string>();

        public MetaCodec(MetaFile file, byte[] data, int start, int end)
        {
            _file = file;
            _L = file.Layouts ?? MetaLayouts.Current;
            _d = data;
            _p = start;
            _limit = end;
        }

        // ================================================================ reading
        public MetaValue ReadRoot(string type)
        {
            MetaValue v = Read(type);
            if (_p != _limit)
                throw new MetaParseException("The main section has " + (_limit - _p) + " bytes left after the " + type + " was read.");
            return v;
        }

        void Need(int n)
        {
            if (n < 0 || _p + n > _limit) throw new MetaParseException("Unexpected end of data at 0x" + _p.ToString("X"));
        }

        uint U32() { Need(4); uint v = BitConverter.ToUInt32(_d, _p); _p += 4; return v; }

        ulong U64() { Need(8); ulong v = BitConverter.ToUInt64(_d, _p); _p += 8; return v; }

        ushort U16() { Need(2); ushort v = BitConverter.ToUInt16(_d, _p); _p += 2; return v; }

        byte[] Take(int n)
        {
            Need(n);
            byte[] b = new byte[n];
            Buffer.BlockCopy(_d, _p, b, 0, n);
            _p += n;
            return b;
        }

        int Count()
        {
            uint n = U32();
            if (n > (uint)(_limit - _p) && n > 0) throw new MetaParseException("Implausible element count " + n + " at 0x" + (_p - 4).ToString("X"));
            return (int)n;
        }

        string TypeNameOf(ulong hash)
        {
            string n = _L.TypeName(hash);
            if (n != null) return n;
            for (int i = 0; i < _file.Types.Count; i++) if (_file.Types[i].Hash == hash && _file.Types[i].Name != null) return _file.Types[i].Name;
            return null;
        }

        public MetaValue Read(string t)
        {
            if (t == "bool")
            {
                Need(1);
                byte b = _d[_p];
                if (b != (byte)'0' && b != (byte)'1') throw new MetaParseException("Bad bool byte 0x" + b.ToString("X2") + " at 0x" + _p.ToString("X"));
                _p++;
                return MetaValue.NewBool(b == (byte)'1');
            }
            if (MetaTypes.IsInt(t))
            {
                int w = MetaTypes.IntWidth(t);
                Need(w);
                ulong v = w == 1 ? _d[_p] : w == 2 ? BitConverter.ToUInt16(_d, _p) : w == 4 ? BitConverter.ToUInt32(_d, _p) : BitConverter.ToUInt64(_d, _p);
                _p += w;
                MetaValue m = new MetaValue(t, MetaKind.Int);
                m.Bits = v;
                return m;
            }
            if (t == "float")
            {
                MetaValue m = new MetaValue(t, MetaKind.Float);
                m.Bits = U32();
                return m;
            }
            if (t == "double")
            {
                MetaValue m = new MetaValue(t, MetaKind.Double);
                m.Bits = U64();
                return m;
            }
            if (t == "Symbol") return MetaValue.NewHash(t, U64());
            if (t == "String")
            {
                int n = Count();
                MetaValue m = new MetaValue(t, MetaKind.String);
                m.Bytes = Take(n);
                return m;
            }
            if (t == "HandleBase")
            {
                MetaValue m = new MetaValue(t, MetaKind.HandleBase);
                m.Bits = U64();
                if (m.Bits != 0) m.Bits2 = U64();
                return m;
            }
            if (MetaTypes.IsHandle(t)) return MetaValue.NewHash(t, U64());

            List<string> args;
            string baseName = MetaTypes.SplitTemplate(t, out args);
            if (args.Count > 0)
            {
                if (baseName == "DCArray" || baseName == "List" || baseName == "Set" || baseName == "Deque")
                {
                    int n = Count();
                    MetaValue arr = new MetaValue(t, MetaKind.Array);
                    for (int i = 0; i < n; i++) arr.Items.Add(Read(args[0]));
                    return arr;
                }
                if (baseName == "SArray" && args.Count >= 2)
                {
                    int n;
                    if (!int.TryParse(args[1], out n)) throw new MetaParseException("Bad SArray size in " + t);
                    MetaValue arr = new MetaValue(t, MetaKind.Array);
                    for (int i = 0; i < n; i++) arr.Items.Add(Read(args[0]));
                    return arr;
                }
                if (baseName == "Map" && args.Count >= 2)
                {
                    int n = Count();
                    MetaValue map = new MetaValue(t, MetaKind.Map);
                    for (int i = 0; i < n; i++)
                    {
                        map.Keys.Add(Read(args[0]));
                        map.Items.Add(Read(args[1]));
                    }
                    return map;
                }
                if (baseName == "BitSetBase")
                {
                    int n;
                    if (!int.TryParse(args[0], out n)) throw new MetaParseException("Bad BitSetBase size in " + t);
                    MetaValue arr = new MetaValue(t, MetaKind.Array);
                    for (int i = 0; i < n; i++)
                    {
                        MetaValue word = new MetaValue("uint32", MetaKind.Int);
                        word.Bits = U32();
                        arr.Items.Add(word);
                    }
                    return arr;
                }
            }

            switch (t)
            {
                case "PropertySet": return ReadPropertySet();
                case "Dlg": return ReadDlg();
                case "DlgChildSet":
                case "DlgConditionSet": return ReadPolySet(t);
                case "DependencyLoader<1>": return ReadDependencyLoader(t);
                case "DlgObjectProps": return ReadObjectProps();
                case "DlgVisibilityConditions": return ReadVisibilityConditions();
                case "DlgNodeExchange": return ReadExchange();
                case "ToolProps": return ReadToolProps();
                case "JiraRecordManager": NoteClass(t); return new MetaValue(t, MetaKind.Struct);
                case "Chore": return ReadChore();
                case "ChoreResource": return ReadChoreResource();
                case "Animation": return ReadAnimation();
            }
            return ReadStruct(t);
        }

        void NoteClass(string t)
        {
            if (_classSeen.Add(t)) ClassesWritten.Add(t);
        }

        readonly Dictionary<string, MetaClassLayout> _layoutCache = new Dictionary<string, MetaClassLayout>(StringComparer.Ordinal);

        MetaClassLayout LayoutOf(string t)
        {
            MetaClassLayout cached;
            if (_layoutCache.TryGetValue(t, out cached)) return cached;
            bool exact;
            uint crc;
            uint? fileCrc = null;
            if (_file.TryCrc(t, out crc)) fileCrc = crc;
            MetaClassLayout l = _L.Resolve(t, fileCrc, _file.Game, out exact);
            if (l == null) throw new MetaParseException("No layout is known for type " + t + ".");
            if (!exact && _d != null && _classSeen.Add("~inexact:" + t))
                _file.Warnings.Add("No layout for the exact version of " + t + "; used another known version.");
            _layoutCache[t] = l;
            return l;
        }

        MetaValue ReadStruct(string t)
        {
            MetaClassLayout l = LayoutOf(t);
            NoteClass(t);
            MetaValue v = new MetaValue(t, MetaKind.Struct);
            for (int i = 0; i < l.Members.Count; i++)
            {
                MetaMemberLayout m = l.Members[i];
                if ((m.Flags & 1) != 0 || MetaTypes.IsNeverSerialized(m.Type)) continue;
                v.Fields.Add(new MetaField(m.Name, m.Blocked ? ReadBlocked(m.Type, t + "." + m.Name) : Read(m.Type)));
            }
            return v;
        }

        /// <summary>Reads a value behind a u32 size block. Anything that fails inside is kept as raw bytes.</summary>
        MetaValue ReadBlocked(string type, string where)
        {
            int start = _p;
            uint size = U32();
            if (size < 4 || start + (long)size > _limit)
                throw new MetaParseException("Bad size block " + size + " for " + where + " at 0x" + start.ToString("X"));
            int end = start + (int)size;
            int savedLimit = _limit;
            _limit = end;
            try
            {
                MetaValue v = Read(type);
                if (_p != end) throw new MetaParseException("read " + (_p - start) + " of " + size + " bytes");
                return v;
            }
            catch (MetaParseException e)
            {
                _p = start + 4;
                MetaValue raw = new MetaValue(type, MetaKind.Raw);
                raw.Bytes = Take(end - _p);
                raw.Note = e.Message;
                _file.RawRegions++;
                _file.Warnings.Add(where + " (" + type + ", " + (size - 4) + " bytes) kept as raw bytes: " + e.Message);
                return raw;
            }
            finally
            {
                _limit = savedLimit;
                _p = end;
            }
        }

        MetaValue ReadPoly()
        {
            int at = _p;
            ulong h = U64();
            string t = TypeNameOf(h);
            if (t == null) throw new MetaParseException("Unknown object type 0x" + h.ToString("X16") + " at 0x" + at.ToString("X"));
            return Read(t);
        }

        MetaValue IntValue(string type, ulong bits)
        {
            MetaValue m = new MetaValue(type, MetaKind.Int);
            m.Bits = bits;
            return m;
        }

        MetaValue ReadPropertySet()
        {
            NoteClass("PropertySet");
            MetaValue v = new MetaValue("PropertySet", MetaKind.Struct);
            uint ver = U32();
            uint flags = U32();
            v.Fields.Add(new MetaField("mPropVersion", IntValue("int32", ver)));
            v.Fields.Add(new MetaField("mPropertyFlags", IntValue("uint32", flags)));

            int start = _p;
            uint size = U32();
            if (size < 4 || start + (long)size > _limit) throw new MetaParseException("Bad PropertySet block at 0x" + start.ToString("X"));
            int end = start + (int)size;
            int savedLimit = _limit;
            _limit = end;
            try
            {
                if (ver != 2) throw new MetaParseException("PropertySet version " + ver + " is not supported");
                if ((flags & 0x4000000) != 0) throw new MetaParseException("PropertySet flag 0x4000000 is not supported");
                MetaValue parents = new MetaValue("DCArray<Handle<PropertySet>>", MetaKind.Array);
                int np = Count();
                for (int i = 0; i < np; i++) parents.Items.Add(MetaValue.NewHash("Handle<PropertySet>", U64()));
                v.Fields.Add(new MetaField("#parents", parents));

                MetaValue groups = new MetaValue("#groups", MetaKind.Array);
                int ng = Count();
                for (int g = 0; g < ng; g++)
                {
                    ulong th = U64();
                    string tn = TypeNameOf(th);
                    if (tn == null) throw new MetaParseException("unknown property value type 0x" + th.ToString("X16"));
                    // A group is a map from key symbol to value; its Type is the value type.
                    MetaValue grp = new MetaValue(tn, MetaKind.Map);
                    int n = Count();
                    for (int i = 0; i < n; i++)
                    {
                        grp.Keys.Add(MetaValue.NewHash("Symbol", U64()));
                        grp.Items.Add(Read(tn));
                    }
                    groups.Items.Add(grp);
                }
                v.Fields.Add(new MetaField("#groups", groups));
                if ((flags & 1024) != 0) v.Fields.Add(new MetaField("#embedded", ReadPropertySet()));
                if (_p != end) throw new MetaParseException("read " + (_p - start) + " of " + size + " bytes");
            }
            catch (MetaParseException e)
            {
                // Keep the whole body verbatim; the version and flags stay readable.
                v.Fields.RemoveAll(f => f.Name.StartsWith("#", StringComparison.Ordinal));
                _p = start + 4;
                MetaValue raw = new MetaValue("#body", MetaKind.Raw);
                raw.Bytes = Take(end - _p);
                raw.Note = e.Message;
                v.Fields.Add(new MetaField("#raw", raw));
                _file.RawRegions++;
                _file.Warnings.Add("A PropertySet (" + (size - 4) + " bytes) was kept as raw bytes: " + e.Message);
            }
            finally
            {
                _limit = savedLimit;
                _p = end;
            }
            return v;
        }

        MetaValue ReadDlg()
        {
            MetaValue v = ReadStruct("Dlg");
            MetaValue folders = new MetaValue("#folders", MetaKind.Array);
            int nf = Count();
            for (int i = 0; i < nf; i++) folders.Items.Add(Read("DlgFolder"));
            v.Fields.Add(new MetaField("#folders", folders));
            MetaValue nodes = new MetaValue("#nodes", MetaKind.Array);
            nodes.Polymorphic = true;
            int nn = Count();
            for (int i = 0; i < nn; i++) nodes.Items.Add(ReadPoly());
            v.Fields.Add(new MetaField("#nodes", nodes));
            v.Fields.Add(new MetaField("#tail", Read("bool")));
            return v;
        }

        MetaValue ReadPolySet(string t)
        {
            NoteClass(t);
            MetaValue v = new MetaValue(t, MetaKind.Struct);
            MetaValue items = new MetaValue("#items", MetaKind.Array);
            items.Polymorphic = true;
            int n = Count();
            for (int i = 0; i < n; i++) items.Items.Add(ReadPoly());
            v.Fields.Add(new MetaField("#items", items));
            return v;
        }

        MetaValue ReadDependencyLoader(string t)
        {
            NoteClass(t);
            MetaValue v = new MetaValue(t, MetaKind.Struct);
            MetaValue has = Read("bool");
            v.Fields.Add(new MetaField("#has", has));
            if (has.AsBool)
            {
                v.Fields.Add(new MetaField("#arrayType", MetaValue.NewHash("Symbol", U64())));
                v.Fields.Add(new MetaField("#names", Read("DCArray<String>")));
            }
            return v;
        }

        static readonly uint[] ObjectPropBits = { 1, 2, 4 };

        MetaValue ReadObjectProps()
        {
            MetaValue v = ReadStruct("DlgObjectProps");
            uint fl = v["mFlags"] != null ? v["mFlags"].FlagsValue : 0;
            for (int i = 0; i < ObjectPropBits.Length; i++)
                if ((fl & ObjectPropBits[i]) != 0) v.Fields.Add(new MetaField("#props" + ObjectPropBits[i], ReadPropertySet()));
            return v;
        }

        MetaValue ReadVisibilityConditions()
        {
            MetaValue v = ReadStruct("DlgVisibilityConditions");
            uint fl = v["mFlags"] != null ? v["mFlags"].FlagsValue : 0;
            if ((fl & 1) != 0) v.Fields.Add(new MetaField("#rule", Read("Rule")));
            return v;
        }

        MetaValue ReadExchange()
        {
            MetaValue v = ReadStruct("DlgNodeExchange");
            MetaValue f = v.Path("Baseclass_DlgNode.mFlags");
            uint fl = f != null ? f.FlagsValue : 0;
            if ((fl & 1) != 0)
            {
                if (!_L.HasLayout("NoteCollection")) throw new MetaParseException("Exchange node with a note collection (no layout known)");
                v.Fields.Add(new MetaField("#notes", Read("NoteCollection")));
            }
            if ((fl & 2) != 0) v.Fields.Add(new MetaField("#lines", Read("DlgLineCollection")));
            return v;
        }

        MetaValue ReadToolProps()
        {
            NoteClass("ToolProps");
            MetaValue v = new MetaValue("ToolProps", MetaKind.Struct);
            MetaValue has = Read("bool");
            v.Fields.Add(new MetaField("#has", has));
            if (has.AsBool) v.Fields.Add(new MetaField("#props", ReadPropertySet()));
            return v;
        }

        MetaValue ReadChore()
        {
            MetaValue v = ReadStruct("Chore");
            int nr = v["mNumResources"] != null ? v["mNumResources"].AsInt : 0;
            int na = v["mNumAgents"] != null ? v["mNumAgents"].AsInt : 0;
            if (nr < 0 || na < 0 || nr > 1000000 || na > 1000000) throw new MetaParseException("Implausible resource/agent counts");
            MetaValue res = new MetaValue("#resources", MetaKind.Array);
            for (int i = 0; i < nr; i++) res.Items.Add(Read("ChoreResource"));
            MetaValue ag = new MetaValue("#agents", MetaKind.Array);
            for (int i = 0; i < na; i++) ag.Items.Add(Read("ChoreAgent"));
            v.Fields.Add(new MetaField("#resources", res));
            v.Fields.Add(new MetaField("#agents", ag));
            return v;
        }

        MetaValue ReadChoreResource()
        {
            MetaValue v = ReadStruct("ChoreResource");
            MetaValue emb = v["mbEmbedded"];
            if (emb != null && emb.AsBool)
            {
                ulong h1 = U64();
                ulong h2 = U64();
                string et = TypeNameOf(h1);
                if (et == null) throw new MetaParseException("Unknown embedded object type 0x" + h1.ToString("X16"));
                v.Fields.Add(new MetaField("#embedType", MetaValue.NewHash("Symbol", h1)));
                v.Fields.Add(new MetaField("#embedType2", MetaValue.NewHash("Symbol", h2)));
                v.Fields.Add(new MetaField("#embedded", Read(et)));
            }
            return v;
        }

        MetaValue ReadAnimation()
        {
            MetaValue v = ReadStruct("Animation");
            int start = _p;
            uint size = U32();
            if (size < 4 || start + (long)size > _limit) throw new MetaParseException("Bad Animation value block");
            int end = start + (int)size;
            int savedLimit = _limit;
            _limit = end;
            MetaValue vals = new MetaValue("#values", MetaKind.Struct);
            try
            {
                int total = (int)U32();
                uint bufSize = U32();
                int ntypes = (int)U32();
                if (total < 0 || ntypes < 0 || ntypes > 100000) throw new MetaParseException("Bad animation value counts");
                MetaValue descs = new MetaValue("#types", MetaKind.Array);
                List<string> typeNames = new List<string>();
                List<int> counts = new List<int>();
                for (int i = 0; i < ntypes; i++)
                {
                    ulong h = U64();
                    ushort cnt = U16();
                    ushort ver = U16();
                    MetaValue d = new MetaValue("#type", MetaKind.Struct);
                    d.Fields.Add(new MetaField("hash", MetaValue.NewHash("Symbol", h)));
                    d.Fields.Add(new MetaField("count", IntValue("int16", cnt)));
                    d.Fields.Add(new MetaField("version", IntValue("int16", ver)));
                    descs.Items.Add(d);
                    string tn = TypeNameOf(h);
                    if (tn == null) throw new MetaParseException("Unknown animation value type 0x" + h.ToString("X16"));
                    typeNames.Add(tn);
                    counts.Add(cnt);
                }
                MetaValue list = new MetaValue("#list", MetaKind.Array);
                for (int i = 0; i < typeNames.Count; i++)
                    for (int k = 0; k < counts[i]; k++) list.Items.Add(Read(typeNames[i]));
                if (list.Items.Count != total) throw new MetaParseException("Animation value count mismatch");
                MetaValue flags = new MetaValue("#flags", MetaKind.Array);
                for (int i = 0; i < total; i++) flags.Items.Add(IntValue("int32", U32()));
                ushort noNames = U16();
                MetaValue names = new MetaValue("#names", MetaKind.Array);
                if (noNames == 0) for (int i = 0; i < total; i++) names.Items.Add(MetaValue.NewHash("Symbol", U64()));
                if (_p != end) throw new MetaParseException("Animation value block: read " + (_p - start) + " of " + size + " bytes");

                // The trailing flag and name arrays repeat each value's own mFlags / mName. When they
                // do, they are rebuilt from the values on save so edits stay consistent.
                bool derived = true;
                for (int i = 0; i < total && derived; i++)
                {
                    MetaValue nm = list.Items[i].FindField("mName");
                    MetaValue fl = list.Items[i].FindField("mFlags");
                    if (nm == null || fl == null || fl.Bits != flags.Items[i].Bits) derived = false;
                    else if (noNames == 0 && nm.Bits != names.Items[i].Bits) derived = false;
                }

                vals.Fields.Add(new MetaField("bufferSize", IntValue("uint32", bufSize)));
                vals.Fields.Add(new MetaField("types", descs));
                vals.Fields.Add(new MetaField("values", list));
                vals.Fields.Add(new MetaField("flags", flags));
                vals.Fields.Add(new MetaField("noNames", IntValue("uint16", noNames)));
                vals.Fields.Add(new MetaField("names", names));
                vals.Fields.Add(new MetaField("derived", MetaValue.NewBool(derived)));
            }
            catch (MetaParseException e)
            {
                vals.Fields.Clear();
                _p = start + 4;
                MetaValue raw = new MetaValue("#body", MetaKind.Raw);
                raw.Bytes = Take(end - _p);
                raw.Note = e.Message;
                vals.Fields.Add(new MetaField("#raw", raw));
                _file.RawRegions++;
                _file.Warnings.Add("Animation values (" + (size - 4) + " bytes) kept as raw bytes: " + e.Message);
            }
            finally
            {
                _limit = savedLimit;
                _p = end;
            }
            v.Fields.Add(new MetaField("#values", vals));
            return v;
        }

        // ================================================================ writing
        public byte[] WriteRoot(MetaValue root, string type)
        {
            _ms = new MemoryStream();
            _w = new BinaryWriter(_ms);
            Write(root, type);
            _w.Flush();
            return _ms.ToArray();
        }

        int OpenBlock()
        {
            int at = (int)_ms.Position;
            _w.Write(0u);
            return at;
        }

        void CloseBlock(int at)
        {
            int end = (int)_ms.Position;
            _ms.Position = at;
            _w.Write((uint)(end - at));
            _ms.Position = end;
        }

        void WriteRaw(MetaValue v)
        {
            if (v.Bytes != null) _w.Write(v.Bytes);
        }

        /// <summary>Writes <paramref name="v"/> as <paramref name="t"/> (normally v.Type; differs for property groups).</summary>
        public void Write(MetaValue v, string t)
        {
            if (v.Kind == MetaKind.Raw) { WriteRaw(v); return; }
            switch (v.Kind)
            {
                case MetaKind.Bool: _w.Write((byte)(v.Bits != 0 ? '1' : '0')); return;
                case MetaKind.Int:
                    {
                        int w = MetaTypes.IntWidth(v.Type);
                        if (w == 1) _w.Write((byte)v.Bits);
                        else if (w == 2) _w.Write((ushort)v.Bits);
                        else if (w == 4) _w.Write((uint)v.Bits);
                        else _w.Write(v.Bits);
                        return;
                    }
                case MetaKind.Float: _w.Write((uint)v.Bits); return;
                case MetaKind.Double: _w.Write(v.Bits); return;
                case MetaKind.Hash: _w.Write(v.Bits); return;
                case MetaKind.String:
                    _w.Write((uint)(v.Bytes != null ? v.Bytes.Length : 0));
                    if (v.Bytes != null) _w.Write(v.Bytes);
                    return;
                case MetaKind.HandleBase:
                    _w.Write(v.Bits);
                    if (v.Bits != 0) _w.Write(v.Bits2);
                    return;
                case MetaKind.Array:
                    {
                        List<string> args;
                        string baseName = MetaTypes.SplitTemplate(t, out args);
                        bool counted = !(baseName == "SArray" || baseName == "BitSetBase");
                        if (counted) _w.Write((uint)v.Items.Count);
                        string elem = args.Count > 0 ? args[0] : null;
                        for (int i = 0; i < v.Items.Count; i++)
                        {
                            MetaValue e = v.Items[i];
                            if (v.Polymorphic) _w.Write(HashDatabase.Crc64(e.Type));
                            Write(e, baseName == "BitSetBase" || elem == null || v.Polymorphic ? e.Type : elem);
                        }
                        return;
                    }
                case MetaKind.Map:
                    {
                        List<string> args;
                        MetaTypes.SplitTemplate(t, out args);
                        _w.Write((uint)v.Items.Count);
                        for (int i = 0; i < v.Items.Count; i++)
                        {
                            Write(v.Keys[i], args.Count > 0 ? args[0] : v.Keys[i].Type);
                            Write(v.Items[i], args.Count > 1 ? args[1] : v.Items[i].Type);
                        }
                        return;
                    }
            }

            switch (t)
            {
                case "PropertySet": WritePropertySet(v); return;
                case "Dlg":
                    WriteStruct(v, t);
                    WriteCountedList(v["#folders"]);
                    WriteCountedList(v["#nodes"]);
                    Write(v["#tail"], "bool");
                    return;
                case "DlgChildSet":
                case "DlgConditionSet":
                    NoteClass(t);
                    WriteCountedList(v["#items"]);
                    return;
                case "DependencyLoader<1>":
                    NoteClass(t);
                    Write(v["#has"], "bool");
                    if (v["#has"].AsBool)
                    {
                        _w.Write(v["#arrayType"].Bits);
                        Write(v["#names"], "DCArray<String>");
                    }
                    return;
                case "DlgObjectProps":
                    WriteStruct(v, t);
                    for (int i = 0; i < ObjectPropBits.Length; i++)
                    {
                        MetaValue s = v["#props" + ObjectPropBits[i]];
                        if (s != null) WritePropertySet(s);
                    }
                    return;
                case "DlgVisibilityConditions":
                    WriteStruct(v, t);
                    if (v["#rule"] != null) Write(v["#rule"], "Rule");
                    return;
                case "DlgNodeExchange":
                    WriteStruct(v, t);
                    if (v["#notes"] != null) Write(v["#notes"], "NoteCollection");
                    if (v["#lines"] != null) Write(v["#lines"], "DlgLineCollection");
                    return;
                case "ToolProps":
                    NoteClass(t);
                    Write(v["#has"], "bool");
                    if (v["#has"].AsBool && v["#props"] != null) WritePropertySet(v["#props"]);
                    return;
                case "JiraRecordManager":
                    NoteClass(t);
                    return;
                case "Chore":
                    {
                        MetaValue res = v["#resources"], ag = v["#agents"];
                        if (v["mNumResources"] != null) v["mNumResources"].AsInt = res.Items.Count;
                        if (v["mNumAgents"] != null) v["mNumAgents"].AsInt = ag.Items.Count;
                        WriteStruct(v, t);
                        for (int i = 0; i < res.Items.Count; i++) Write(res.Items[i], "ChoreResource");
                        for (int i = 0; i < ag.Items.Count; i++) Write(ag.Items[i], "ChoreAgent");
                        return;
                    }
                case "ChoreResource":
                    WriteStruct(v, t);
                    if (v["#embedded"] != null)
                    {
                        _w.Write(v["#embedType"].Bits);
                        _w.Write(v["#embedType2"].Bits);
                        Write(v["#embedded"], v["#embedded"].Type);
                    }
                    return;
                case "Animation":
                    WriteStruct(v, t);
                    WriteAnimationValues(v["#values"]);
                    return;
            }
            WriteStruct(v, t);
        }

        void WriteCountedList(MetaValue list)
        {
            _w.Write((uint)list.Items.Count);
            for (int i = 0; i < list.Items.Count; i++)
            {
                MetaValue e = list.Items[i];
                if (list.Polymorphic) _w.Write(HashDatabase.Crc64(e.Type));
                Write(e, e.Type);
            }
        }

        void WriteStruct(MetaValue v, string t)
        {
            MetaClassLayout l = LayoutOf(t);
            NoteClass(t);
            for (int i = 0; i < l.Members.Count; i++)
            {
                MetaMemberLayout m = l.Members[i];
                if ((m.Flags & 1) != 0 || MetaTypes.IsNeverSerialized(m.Type)) continue;
                MetaValue fv = v[m.Name];
                if (fv == null) fv = CreateDefault(m.Type, 0);
                if (m.Blocked)
                {
                    int at = OpenBlock();
                    Write(fv, m.Type);
                    CloseBlock(at);
                }
                else
                {
                    Write(fv, m.Type);
                }
            }
        }

        void WritePropertySet(MetaValue v)
        {
            NoteClass("PropertySet");
            _w.Write((uint)v["mPropVersion"].Bits);
            _w.Write((uint)v["mPropertyFlags"].Bits);
            int at = OpenBlock();
            MetaValue raw = v["#raw"];
            if (raw != null)
            {
                WriteRaw(raw);
            }
            else
            {
                MetaValue parents = v["#parents"];
                _w.Write((uint)parents.Items.Count);
                for (int i = 0; i < parents.Items.Count; i++) _w.Write(parents.Items[i].Bits);
                MetaValue groups = v["#groups"];
                // Empty groups are dropped: the engine never writes a type with no values.
                int n = 0;
                for (int g = 0; g < groups.Items.Count; g++) if (groups.Items[g].Items.Count > 0) n++;
                _w.Write((uint)n);
                for (int g = 0; g < groups.Items.Count; g++)
                {
                    MetaValue grp = groups.Items[g];
                    if (grp.Items.Count == 0) continue;
                    _w.Write(HashDatabase.Crc64(grp.Type));
                    _w.Write((uint)grp.Items.Count);
                    for (int i = 0; i < grp.Items.Count; i++)
                    {
                        _w.Write(grp.Keys[i].Bits);
                        Write(grp.Items[i], grp.Type);
                    }
                }
                MetaValue emb = v["#embedded"];
                if (emb != null) WritePropertySet(emb);
            }
            CloseBlock(at);
        }

        void WriteAnimationValues(MetaValue vals)
        {
            int at = OpenBlock();
            MetaValue raw = vals["#raw"];
            if (raw != null)
            {
                WriteRaw(raw);
                CloseBlock(at);
                return;
            }
            MetaValue list = vals["values"];
            MetaValue oldTypes = vals["types"];
            bool derived = vals["derived"] != null && vals["derived"].AsBool;
            bool noNames = vals["noNames"].Bits != 0;

            // Values are grouped by type in the order the groups first appear.
            List<string> order = new List<string>();
            Dictionary<string, List<MetaValue>> byType = new Dictionary<string, List<MetaValue>>(StringComparer.Ordinal);
            for (int i = 0; i < list.Items.Count; i++)
            {
                MetaValue e = list.Items[i];
                List<MetaValue> g;
                if (!byType.TryGetValue(e.Type, out g))
                {
                    g = new List<MetaValue>();
                    byType[e.Type] = g;
                    order.Add(e.Type);
                }
                g.Add(e);
            }
            List<MetaValue> ordered = new List<MetaValue>(list.Items.Count);
            for (int i = 0; i < order.Count; i++) ordered.AddRange(byType[order[i]]);
            bool reordered = false;
            for (int i = 0; i < ordered.Count; i++) if (!ReferenceEquals(ordered[i], list.Items[i])) { reordered = true; break; }
            if (reordered)
            {
                list.Items.Clear();
                list.Items.AddRange(ordered);
                derived = true;
            }

            _w.Write((uint)list.Items.Count);
            _w.Write((uint)vals["bufferSize"].Bits);
            _w.Write((uint)order.Count);
            for (int i = 0; i < order.Count; i++)
            {
                ulong h = HashDatabase.Crc64(order[i]);
                ushort ver = 0;
                for (int k = 0; k < oldTypes.Items.Count; k++)
                    if (oldTypes.Items[k]["hash"].Bits == h) { ver = (ushort)oldTypes.Items[k]["version"].Bits; break; }
                _w.Write(h);
                _w.Write((ushort)byType[order[i]].Count);
                _w.Write(ver);
            }
            for (int i = 0; i < list.Items.Count; i++) Write(list.Items[i], list.Items[i].Type);

            MetaValue flags = vals["flags"], names = vals["names"];
            bool useStored = !derived && flags.Items.Count == list.Items.Count && (noNames || names.Items.Count == list.Items.Count);
            for (int i = 0; i < list.Items.Count; i++)
            {
                if (useStored) _w.Write((uint)flags.Items[i].Bits);
                else
                {
                    MetaValue f = list.Items[i].FindField("mFlags");
                    _w.Write(f != null ? (uint)f.Bits : 0u);
                }
            }
            _w.Write((ushort)(noNames ? 1 : 0));
            if (!noNames)
            {
                for (int i = 0; i < list.Items.Count; i++)
                {
                    if (useStored) _w.Write(names.Items[i].Bits);
                    else
                    {
                        MetaValue n = list.Items[i].FindField("mName");
                        _w.Write(n != null ? n.Bits : 0UL);
                    }
                }
            }
            CloseBlock(at);
        }

        // ================================================================ defaults
        /// <summary>A zero value of <paramref name="t"/>, shaped like what the reader would produce.</summary>
        public MetaValue CreateDefault(string t, int depth)
        {
            if (depth > 40) throw new MetaParseException("Type nesting too deep for " + t);
            if (t == "bool") return MetaValue.NewBool(false);
            if (MetaTypes.IsInt(t)) return IntValue(t, 0);
            if (t == "float") return MetaValue.NewFloat(0f);
            if (t == "double") return new MetaValue(t, MetaKind.Double);
            if (t == "Symbol" || MetaTypes.IsHandle(t)) return MetaValue.NewHash(t, 0);
            if (t == "String") { MetaValue s = new MetaValue(t, MetaKind.String); s.Bytes = new byte[0]; return s; }
            if (t == "HandleBase") return new MetaValue(t, MetaKind.HandleBase);
            List<string> args;
            string baseName = MetaTypes.SplitTemplate(t, out args);
            if (args.Count > 0)
            {
                if (baseName == "DCArray" || baseName == "List" || baseName == "Set" || baseName == "Deque") return new MetaValue(t, MetaKind.Array);
                if (baseName == "Map") return new MetaValue(t, MetaKind.Map);
                if (baseName == "SArray" || baseName == "BitSetBase")
                {
                    MetaValue a = new MetaValue(t, MetaKind.Array);
                    int n;
                    int.TryParse(baseName == "SArray" && args.Count > 1 ? args[1] : args[0], out n);
                    for (int i = 0; i < n; i++) a.Items.Add(baseName == "SArray" ? CreateDefault(args[0], depth + 1) : IntValue("uint32", 0));
                    return a;
                }
            }
            switch (t)
            {
                case "PropertySet":
                    {
                        MetaValue p = new MetaValue(t, MetaKind.Struct);
                        p.Fields.Add(new MetaField("mPropVersion", IntValue("int32", 2)));
                        p.Fields.Add(new MetaField("mPropertyFlags", IntValue("uint32", 0)));
                        p.Fields.Add(new MetaField("#parents", new MetaValue("DCArray<Handle<PropertySet>>", MetaKind.Array)));
                        p.Fields.Add(new MetaField("#groups", new MetaValue("#groups", MetaKind.Array)));
                        return p;
                    }
                case "DlgChildSet":
                case "DlgConditionSet":
                    {
                        MetaValue s = new MetaValue(t, MetaKind.Struct);
                        MetaValue items = new MetaValue("#items", MetaKind.Array);
                        items.Polymorphic = true;
                        s.Fields.Add(new MetaField("#items", items));
                        return s;
                    }
                case "DependencyLoader<1>":
                case "ToolProps":
                    {
                        MetaValue s = new MetaValue(t, MetaKind.Struct);
                        s.Fields.Add(new MetaField("#has", MetaValue.NewBool(false)));
                        return s;
                    }
                case "JiraRecordManager":
                    return new MetaValue(t, MetaKind.Struct);
            }
            bool exact;
            MetaClassLayout l = _L.Resolve(t, _file.CrcOf(t), _file.Game, out exact);
            MetaValue v = new MetaValue(t, MetaKind.Struct);
            if (l == null) return v;
            for (int i = 0; i < l.Members.Count; i++)
            {
                MetaMemberLayout m = l.Members[i];
                if ((m.Flags & 1) != 0 || MetaTypes.IsNeverSerialized(m.Type)) continue;
                v.Fields.Add(new MetaField(m.Name, CreateDefault(m.Type, depth + 1)));
            }
            if (t == "Animation")
            {
                MetaValue vals = new MetaValue("#values", MetaKind.Struct);
                vals.Fields.Add(new MetaField("bufferSize", IntValue("uint32", 0)));
                vals.Fields.Add(new MetaField("types", new MetaValue("#types", MetaKind.Array)));
                vals.Fields.Add(new MetaField("values", new MetaValue("#list", MetaKind.Array)));
                vals.Fields.Add(new MetaField("flags", new MetaValue("#flags", MetaKind.Array)));
                vals.Fields.Add(new MetaField("noNames", IntValue("uint16", 0)));
                vals.Fields.Add(new MetaField("names", new MetaValue("#names", MetaKind.Array)));
                vals.Fields.Add(new MetaField("derived", MetaValue.NewBool(true)));
                v.Fields.Add(new MetaField("#values", vals));
            }
            else if (t == "Chore")
            {
                v.Fields.Add(new MetaField("#resources", new MetaValue("#resources", MetaKind.Array)));
                v.Fields.Add(new MetaField("#agents", new MetaValue("#agents", MetaKind.Array)));
            }
            return v;
        }
    }
}
