using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

public class TelltaleSklBonePorter : EditorWindow
{
    string donorPath = "", targetPath = "", hashDbPath = "", boneFilter = "";
    bool remapMirror = true;
    string output = "";
    Vector2 scroll;

    Dictionary<ulong, string> db = new Dictionary<ulong, string>();
    Dictionary<string, ulong> dbRev = new Dictionary<string, ulong>();
    string loadedDb = null;

    [MenuItem("Tools/Telltale SKL Bone Porter")]
    static void Open() { GetWindow<TelltaleSklBonePorter>("SKL Bone Porter"); }

    // ================================================================ GUI
    void OnGUI()
    {
        PathField("Donor SKL (has the bones)", ref donorPath, "skl");
        PathField("Target SKL (missing bones)", ref targetPath, "skl");
        PathField("BoneNames.HashDB", ref hashDbPath, "HashDB");

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Bones to port (comma separated names or 0xHASH). Empty = ALL missing bones.", EditorStyles.wordWrappedMiniLabel);
        boneFilter = EditorGUILayout.TextField("Bones", boneFilter);
        remapMirror = EditorGUILayout.Toggle("Remap mirror index (v18+)", remapMirror);

        EditorGUILayout.Space();
        if (GUILayout.Button("Preview (no file written)")) Run(() => Port(false));
        if (GUILayout.Button("PORT BONES -> save *_ported.skl")) Run(() => Port(true));
        if (GUILayout.Button("Save output to .txt"))
        {
            string p = EditorUtility.SaveFilePanel("Save log", "", "port_log.txt", "txt");
            if (!string.IsNullOrEmpty(p)) File.WriteAllText(p, output);
        }

        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.TextArea(output, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
    }

    void PathField(string label, ref string path, string ext)
    {
        EditorGUILayout.BeginHorizontal();
        path = EditorGUILayout.TextField(label, path);
        if (GUILayout.Button("...", GUILayout.Width(30)))
        {
            string p = EditorUtility.OpenFilePanel(label, "", ext);
            if (!string.IsNullOrEmpty(p)) path = p;
        }
        EditorGUILayout.EndHorizontal();
    }

    void Run(Func<string> a)
    {
        try { LoadDb(); output = a(); }
        catch (Exception e) { output = "ERROR: " + e.Message + "\n" + e.StackTrace; }
        Debug.Log(output);
        GUI.FocusControl(null);
    }

    // ============================================================ HashDB
    void LoadDb()
    {
        if (string.IsNullOrEmpty(hashDbPath) || !File.Exists(hashDbPath)) { db.Clear(); dbRev.Clear(); loadedDb = null; return; }
        if (loadedDb == hashDbPath) return;
        db.Clear(); dbRev.Clear();
        using (var r = new BinaryReader(File.OpenRead(hashDbPath)))
        {
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                uint h2 = r.ReadUInt32(), h1 = r.ReadUInt32();
                var sb = new StringBuilder(); byte c;
                while ((c = r.ReadByte()) != 0) sb.Append((char)c);
                ulong h = ((ulong)h1 << 32) | h2;
                db[h] = sb.ToString();
                dbRev[sb.ToString().ToLowerInvariant()] = h;
            }
        }
        loadedDb = hashDbPath;
    }

    // ============================================================= CRC64
    static readonly ulong[] T64 = BuildTable();
    static ulong[] BuildTable()
    {
        const ulong poly = 0x42F0E1EBA9EA3693UL;
        var t = new ulong[256];
        for (int i = 0; i < 256; i++)
        {
            ulong c = (ulong)i << 56;
            for (int k = 0; k < 8; k++) c = (c & 0x8000000000000000UL) != 0 ? (c << 1) ^ poly : c << 1;
            t[i] = c;
        }
        return t;
    }
    static ulong Crc64(string s)
    {
        ulong crc = 0;
        foreach (byte b in Encoding.ASCII.GetBytes(s.ToLowerInvariant()))
            crc = T64[((crc >> 56) ^ b) & 0xFF] ^ (crc << 8);
        return crc;
    }
    static string Hex(ulong h) { return "0x" + h.ToString("X16"); }

    // ======================================================= Data model
    class Bone
    {
        public int Index, ParentIndex;
        public long Start, End, ParentIdxOffset, MirrorOffset = -1;
        public ulong Key, ParentKey;
        public string Name; // legacy text format only
    }

    class Skel
    {
        public byte[] Data; public Layout L;
        public long HeaderSizeOffset = -1, HeaderEnd, SizeOffset, CountOffset, Leftover;
        public uint BlockSize; public int Count;
        public List<Bone> Bones = new List<Bone>();
    }

    struct Layout
    {
        public string Label; public int Ver, Early; public bool Bttf;
        public Layout(string l, int v, int e, bool b) { Label = l; Ver = v; Early = e; Bttf = b; }
    }

    static readonly Layout[] Layouts =
    {
        new Layout("VerNum 13-17", 14, 0, false),
        new Layout("VerNum 18-54", 18, 0, false),
        new Layout("VerNum 55", 55, 0, false),
        new Layout("VerNum 17 BttF PS4", 17, 0, true),
        new Layout("VerNum 2-12", 5, 0, false),
        new Layout("VerNum 1", 1, 0, false),
        new Layout("VerNum 0, EarlyGameFix 4", 0, 4, false),
        new Layout("VerNum 0, EarlyGameFix 5-7", 0, 5, false),
        new Layout("VerNum 0, EarlyGameFix 8-9", 0, 8, false),
        new Layout("VerNum 0, EarlyGameFix 1-3 (text)", 0, 1, false),
    };

    // =========================================================== Parser
    static Skel TryParse(byte[] d, Layout L)
    {
        try
        {
            var s = new Skel { Data = d, L = L };
            var ms = new MemoryStream(d);
            var r = new BinaryReader(ms);

            int magic = r.ReadInt32();
            if (magic == 1297307189 || magic == 1297307190) // MSV5 / MSV6
            {
                s.HeaderSizeOffset = ms.Position; r.ReadUInt32();
                ms.Position += 8;
                uint pc = r.ReadUInt32(); if (pc > 4096) return null;
                ms.Position += 12L * pc;
            }
            else // MBIN / MTRE / raw
            {
                uint pc = r.ReadUInt32(); if (pc > 4096) return null;
                uint chk = r.ReadUInt32(); ms.Position -= 4;
                if (chk > 0 && chk < 128)
                    for (int i = 0; i < pc; i++)
                    {
                        uint len = r.ReadUInt32(); if (len > 512) return null;
                        ms.Position += len + 4;
                    }
                else ms.Position += 12L * pc;
            }
            s.HeaderEnd = ms.Position;

            if (L.Ver == 0 && L.Early > 4 && L.Early < 8) ms.Position += 1;

            s.SizeOffset = ms.Position;  s.BlockSize = r.ReadUInt32();
            s.CountOffset = ms.Position; s.Count = r.ReadInt32();
            if (s.Count <= 0 || s.Count > 4096) return null;

            bool hashed = L.Ver > 0 || L.Early > 3;

            for (int b = 0; b < s.Count; b++)
            {
                var bone = new Bone { Index = b, Start = ms.Position };

                if (hashed)
                {
                    bone.Key = r.ReadUInt64();       if (L.Ver < 13) ms.Position += 4;
                    bone.ParentKey = r.ReadUInt64(); if (L.Ver < 13) ms.Position += 4;
                }
                else
                {
                    r.ReadInt32(); int n = r.ReadInt32(); if (n < 0 || n > 512) return null;
                    bone.Name = Encoding.ASCII.GetString(r.ReadBytes(n)); bone.Key = Crc64(bone.Name);
                    r.ReadInt32(); n = r.ReadInt32(); if (n < 0 || n > 512) return null;
                    bone.ParentKey = Crc64(Encoding.ASCII.GetString(r.ReadBytes(n)));
                }

                bone.ParentIdxOffset = ms.Position;
                bone.ParentIndex = r.ReadInt32();
                if (bone.ParentIndex < -1 || bone.ParentIndex >= s.Count) return null;

                if (L.Ver >= 18 || L.Bttf) { bone.MirrorOffset = ms.Position; ms.Position += 12; }

                ms.Position += 96; // pos, quat, rest transform block, 3x vec3 scales

                r.ReadInt32();
                int ik = r.ReadInt32(); if (ik < 0 || ik > 128) return null;
                for (int i = 0; i < ik; i++)
                {
                    if (L.Ver == 55) ms.Position += 8;
                    else { int n = r.ReadInt32(); if (n < 0 || n > 512) return null; ms.Position += n; }
                    ms.Position += 4;
                }

                if (L.Ver == 0 && L.Early > 4) ms.Position += 4;

                if (L.Ver > 1)
                {
                    r.ReadInt32();
                    int pi = r.ReadInt32(); if (pi < 0 || pi > 4096) return null;
                    ms.Position += 12L * pi + 4 + 28;
                }

                if (ms.Position > d.Length) return null;
                bone.End = ms.Position;
                s.Bones.Add(bone);
            }
            s.Leftover = d.Length - ms.Position;
            return s;
        }
        catch { return null; }
    }

    bool ParseBoth(out Skel D, out Skel T)
    {
        byte[] dd = File.ReadAllBytes(donorPath), td = File.ReadAllBytes(targetPath);
        D = T = null; long best = long.MaxValue; int bestKnown = -1;
        foreach (var L in Layouts)
        {
            var a = TryParse(dd, L); var b = TryParse(td, L);
            if (a == null || b == null) continue;
            long left = a.Leftover + b.Leftover;
            int known = a.Bones.Count(x => x.Name != null || db.ContainsKey(x.Key))
                      + b.Bones.Count(x => x.Name != null || db.ContainsKey(x.Key));
            if (left < best || (left == best && known > bestKnown)) { D = a; T = b; best = left; bestKnown = known; }
        }
        return D != null;
    }

    string NameOf(Bone b)
    {
        if (b.Name != null) return b.Name;
        string n; return db.TryGetValue(b.Key, out n) ? n : Hex(b.Key);
    }

    ulong Resolve(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.Parse(s.Substring(2), NumberStyles.HexNumber);
        ulong h; return dbRev.TryGetValue(s.ToLowerInvariant(), out h) ? h : Crc64(s);
    }

    static void WriteI32(byte[] a, long off, int v) { Buffer.BlockCopy(BitConverter.GetBytes(v), 0, a, (int)off, 4); }
    static void WriteU32(byte[] a, long off, uint v) { Buffer.BlockCopy(BitConverter.GetBytes(v), 0, a, (int)off, 4); }

    // ============================================================= Port
    string Port(bool write)
    {
        if (!File.Exists(donorPath) || !File.Exists(targetPath)) return "Set both donor and target SKL paths.";
        Skel D, T;
        if (!ParseBoth(out D, out T))
            return "No layout parses BOTH files. They are probably from different game versions - bones cannot be byte-copied between them.";

        var sb = new StringBuilder();
        sb.AppendLine("Layout: " + T.L.Label + "   (donor leftover " + D.Leftover + " B, target leftover " + T.Leftover + " B)");
        sb.AppendLine("Donor bones: " + D.Count + "   Target bones: " + T.Count + "\n");

        // --- target lookup
        var map = new Dictionary<ulong, int>();
        foreach (var b in T.Bones) if (!map.ContainsKey(b.Key)) map[b.Key] = b.Index;

        // --- choose bones to port
        var want = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(boneFilter))
        {
            foreach (var b in D.Bones) if (!map.ContainsKey(b.Key)) want.Add(b.Index);
        }
        else
        {
            foreach (var tok in boneFilter.Split(',').Where(t => t.Trim().Length > 0))
            {
                ulong k = Resolve(tok);
                var db_ = D.Bones.FirstOrDefault(b => b.Key == k);
                if (db_ == null) { sb.AppendLine("Not in donor: " + tok.Trim()); continue; }
                if (map.ContainsKey(k)) { sb.AppendLine("Already in target, skipped: " + tok.Trim()); continue; }
                // add bone + every missing ancestor
                var cur = db_;
                while (cur != null && !map.ContainsKey(cur.Key))
                {
                    want.Add(cur.Index);
                    cur = cur.ParentIndex >= 0 ? D.Bones[cur.ParentIndex] : null;
                }
            }
        }
        if (want.Count == 0) return sb + "Nothing to port - target already has every requested bone.";

        // --- order so parents always come before children
        var order = new List<Bone>();
        var placed = new HashSet<ulong>(map.Keys);
        var pending = want.OrderBy(i => i).Select(i => D.Bones[i]).ToList();
        while (pending.Count > 0)
        {
            var ready = pending.Where(b => b.ParentIndex < 0 || placed.Contains(D.Bones[b.ParentIndex].Key)).ToList();
            if (ready.Count == 0) throw new Exception("Could not resolve parent order (cyclic hierarchy?).");
            foreach (var b in ready) { order.Add(b); placed.Add(b.Key); pending.Remove(b); }
        }

        // --- assign new indices first (mirror bones may reference later ones)
        int next = T.Count;
        foreach (var b in order) map[b.Key] = next++;

        // --- build patched copies
        var chunks = new List<byte[]>();
        int mirrorUnrecognized = 0, mirrorCleared = 0;
        sb.AppendLine("New idx | Bone | Parent (new idx) | Bytes");
        foreach (var b in order)
        {
            byte[] c = new byte[b.End - b.Start];
            Buffer.BlockCopy(D.Data, (int)b.Start, c, 0, c.Length);

            int newParent = -1;
            if (b.ParentIndex >= 0) newParent = map[D.Bones[b.ParentIndex].Key];
            else sb.AppendLine("WARNING: " + NameOf(b) + " is a ROOT bone in the donor -> target will have 2 roots.");
            WriteI32(c, b.ParentIdxOffset - b.Start, newParent);

            if (remapMirror && b.MirrorOffset >= 0)
            {
                int rel = (int)(b.MirrorOffset - b.Start);
                ulong mk = BitConverter.ToUInt64(c, rel);
                int mi = BitConverter.ToInt32(c, rel + 8);
                bool isMirror = mi == -1 || (mi >= 0 && mi < D.Count && D.Bones[mi].Key == mk);
                if (!isMirror) mirrorUnrecognized++;
                else if (mi >= 0)
                {
                    int ni;
                    if (map.TryGetValue(mk, out ni)) WriteI32(c, rel + 8, ni);
                    else { WriteI32(c, rel + 8, -1); mirrorCleared++; }
                }
            }

            chunks.Add(c);
            string pName = b.ParentIndex >= 0 ? NameOf(D.Bones[b.ParentIndex]) : "(root)";
            sb.AppendLine(string.Format("{0,7} | {1} | {2} ({3}) | {4}", map[b.Key], NameOf(b), pName, newParent, c.Length));
        }
        if (mirrorUnrecognized > 0) sb.AppendLine("\nNote: mirror field not recognized on " + mirrorUnrecognized + " bone(s) - left byte-identical.");
        if (mirrorCleared > 0) sb.AppendLine("Note: " + mirrorCleared + " bone(s) had a mirror bone not present in target -> mirror index set to -1.");

        // --- assemble new file
        uint added = (uint)chunks.Sum(c => c.Length);
        long insertAt = T.Bones[T.Bones.Count - 1].End;
        var o = new MemoryStream();
        o.Write(T.Data, 0, (int)insertAt);
        foreach (var c in chunks) o.Write(c, 0, c.Length);
        o.Write(T.Data, (int)insertAt, T.Data.Length - (int)insertAt);
        byte[] nd = o.ToArray();

        WriteI32(nd, T.CountOffset, T.Count + order.Count);

        long spanToEnd = insertAt - T.SizeOffset;
        WriteU32(nd, T.SizeOffset, T.BlockSize + added);
        sb.AppendLine("\nBoneCount:    " + T.Count + " -> " + (T.Count + order.Count));
        sb.AppendLine("BoneFileSize: " + T.BlockSize + " -> " + (T.BlockSize + added) +
            (T.BlockSize == spanToEnd ? "   (matched bone-list size exactly - good)"
                                      : "   (differed from bone-list span by " + ((long)T.BlockSize - spanToEnd) + " B; same delta applied)"));

        if (T.HeaderSizeOffset >= 0)
        {
            uint hs = BitConverter.ToUInt32(T.Data, (int)T.HeaderSizeOffset);
            long body = T.Data.Length - T.HeaderEnd;
            WriteU32(nd, T.HeaderSizeOffset, hs + added);
            sb.AppendLine("Header size:  " + hs + " -> " + (hs + added) +
                (hs == body ? "   (matched file body - good)" : "   (body is " + body + " B; same delta applied)"));
        }

        // --- verify
        var check = TryParse(nd, T.L);
        bool ok = check != null && check.Count == T.Count + order.Count && check.Leftover == T.Leftover
                  && check.Bones.All(b => b.ParentIndex < b.Index);
        sb.AppendLine("\nVerification re-parse: " + (ok ? "OK" : "FAILED - do not use this file"));
        sb.AppendLine("Size: " + T.Data.Length + " -> " + nd.Length + " bytes (+" + added + ")");

        if (write && ok)
        {
            string outPath = Path.Combine(Path.GetDirectoryName(targetPath),
                Path.GetFileNameWithoutExtension(targetPath) + "_ported" + Path.GetExtension(targetPath));
            File.WriteAllBytes(outPath, nd);
            sb.AppendLine("Saved: " + outPath);
        }
        else if (!write) sb.AppendLine("(Preview only - nothing written)");
        return sb.ToString();
    }
}