using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

public class TelltaleSklTool : EditorWindow
{
    string filePath = "", filePathB = "", hashDbPath = "";
    string oldName = "", newName = "";
    string output = "";
    Vector2 scroll;

    Dictionary<ulong, string> db = new Dictionary<ulong, string>();
    Dictionary<string, ulong> dbRev = new Dictionary<string, ulong>();
    string loadedDbPath = null;

    [MenuItem("Tools/Telltale SKL Tool")]
    static void Open() { GetWindow<TelltaleSklTool>("Telltale SKL"); }

    // ------------------------------------------------------------------ GUI
    void OnGUI()
    {
        PathField("File A (.skl or any)", ref filePath, "");
        PathField("File B (compare)", ref filePathB, "skl");
        PathField("BoneNames.HashDB", ref hashDbPath, "HashDB");

        EditorGUILayout.Space();
        if (GUILayout.Button("Dump bones of File A")) Run(DumpBones);
        if (GUILayout.Button("Scan File A for ALL known hashes (works on any file)")) Run(ScanFile);
        if (GUILayout.Button("Compare File A vs File B")) Run(Compare);
        if (GUILayout.Button("Verify CRC64 algorithm against HashDB")) Run(VerifyCrc);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Rename bone in File A (writes a NEW file, original untouched)", EditorStyles.boldLabel);
        oldName = EditorGUILayout.TextField("Old name or 0xHASH", oldName);
        newName = EditorGUILayout.TextField("New name or 0xHASH", newName);
        if (GUILayout.Button("Replace hash -> save *_edited.skl")) Run(ReplaceHash);

        EditorGUILayout.Space();
        if (GUILayout.Button("Save output to .txt"))
        {
            string p = EditorUtility.SaveFilePanel("Save output", "", "skl_dump.txt", "txt");
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

    void Run(Func<string> action)
    {
        try { LoadDb(); output = action(); }
        catch (Exception e) { output = "ERROR: " + e.Message + "\n" + e.StackTrace; }
        Debug.Log(output);
        GUI.FocusControl(null);
    }

    // ------------------------------------------------------------ HashDB
    void LoadDb()
    {
        if (string.IsNullOrEmpty(hashDbPath) || !File.Exists(hashDbPath)) { db.Clear(); dbRev.Clear(); loadedDbPath = null; return; }
        if (loadedDbPath == hashDbPath) return;
        db.Clear(); dbRev.Clear();
        using (var r = new BinaryReader(File.OpenRead(hashDbPath)))
        {
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                uint h2 = r.ReadUInt32();
                uint h1 = r.ReadUInt32();
                var sb = new StringBuilder();
                byte c;
                while ((c = r.ReadByte()) != 0) sb.Append((char)c);
                ulong h = ((ulong)h1 << 32) | h2;
                string name = sb.ToString();
                db[h] = name;
                dbRev[name.ToLowerInvariant()] = h;
            }
        }
        loadedDbPath = hashDbPath;
    }

    string NameOf(ulong h) { string n; return db.TryGetValue(h, out n) ? n : "<unknown>"; }

    // ------------------------------------------------------------ CRC64
    static readonly ulong[] CrcTable = BuildTable();
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
    public static ulong Crc64(string s)
    {
        ulong crc = 0;
        foreach (byte b in Encoding.ASCII.GetBytes(s.ToLowerInvariant()))
            crc = CrcTable[((crc >> 56) ^ b) & 0xFF] ^ (crc << 8);
        return crc;
    }

    static string Hex(ulong h) { return "0x" + h.ToString("X16"); }
    static string FileBytes(ulong h) { return BitConverter.ToString(BitConverter.GetBytes(h)).Replace("-", " "); }

    // ------------------------------------------------------ SKL parsing
    class Bone
    {
        public int Index, ParentIndex;
        public long NameOffset, ParentOffset;
        public ulong Hash, ParentHash;
        public string Name, ParentName; // legacy text format only
    }

    struct Layout
    {
        public string Label; public int Ver; public int Early; public bool Bttf;
        public Layout(string l, int v, int e, bool b) { Label = l; Ver = v; Early = e; Bttf = b; }
    }

    // One representative per branch of the MaxScript's version checks
    static readonly Layout[] Layouts =
    {
        new Layout("VerNum 13-17", 14, 0, false),
        new Layout("VerNum 18-54", 18, 0, false),
        new Layout("VerNum 55", 55, 0, false),
        new Layout("VerNum 17 BttF PS4", 17, 0, true),
        new Layout("VerNum 2-12", 5, 0, false),
        new Layout("VerNum 1", 1, 0, false),
        new Layout("VerNum 0, EarlyGameFix 4 (hashed)", 0, 4, false),
        new Layout("VerNum 0, EarlyGameFix 5-7", 0, 5, false),
        new Layout("VerNum 0, EarlyGameFix 8-9", 0, 8, false),
        new Layout("VerNum 0, EarlyGameFix 1-3 (text names)", 0, 1, false),
    };

    static List<Bone> TryParse(byte[] d, Layout L, out long leftover)
    {
        leftover = -1;
        try
        {
            var ms = new MemoryStream(d);
            var r = new BinaryReader(ms);

            int magic = r.ReadInt32();
            if (magic == 1297307189 || magic == 1297307190) // MSV5 / MSV6
            {
                r.ReadUInt32(); ms.Position += 8;
                uint pc = r.ReadUInt32(); if (pc > 4096) return null;
                ms.Position += 12L * pc;
            }
            else // MBIN / MTRE / unknown
            {
                uint pc = r.ReadUInt32(); if (pc > 4096) return null;
                uint chk = r.ReadUInt32(); ms.Position -= 4;
                if (chk > 0 && chk < 128)
                {
                    for (int i = 0; i < pc; i++)
                    {
                        uint len = r.ReadUInt32(); if (len > 512) return null;
                        ms.Position += len + 4;
                    }
                }
                else ms.Position += 12L * pc;
            }

            if (L.Ver == 0 && L.Early > 4 && L.Early < 8) ms.Position += 1;

            r.ReadUInt32(); // BoneFileSize
            int count = r.ReadInt32();
            if (count <= 0 || count > 4096) return null;

            bool hashed = L.Ver > 0 || L.Early > 3;
            var bones = new List<Bone>();

            for (int b = 0; b < count; b++)
            {
                var bone = new Bone { Index = b };
                if (hashed)
                {
                    bone.NameOffset = ms.Position; bone.Hash = r.ReadUInt64();
                    if (L.Ver < 13) ms.Position += 4;
                    bone.ParentOffset = ms.Position; bone.ParentHash = r.ReadUInt64();
                    if (L.Ver < 13) ms.Position += 4;
                }
                else
                {
                    r.ReadInt32(); int n = r.ReadInt32(); if (n < 0 || n > 512) return null;
                    bone.NameOffset = ms.Position; bone.Name = Encoding.ASCII.GetString(r.ReadBytes(n));
                    r.ReadInt32(); n = r.ReadInt32(); if (n < 0 || n > 512) return null;
                    bone.ParentOffset = ms.Position; bone.ParentName = Encoding.ASCII.GetString(r.ReadBytes(n));
                }

                bone.ParentIndex = r.ReadInt32();
                if (bone.ParentIndex < -1 || bone.ParentIndex >= count) return null;

                if (L.Ver >= 18 || L.Bttf) ms.Position += 12;
                ms.Position += 96; // translation, rotation, misc floats, 3x3 matrix

                r.ReadInt32(); // IKHeaderLength
                int ik = r.ReadInt32(); if (ik < 0 || ik > 128) return null;
                for (int i = 0; i < ik; i++)
                {
                    if (L.Ver == 55) ms.Position += 8;
                    else { int n = r.ReadInt32(); if (n < 0 || n > 512) return null; ms.Position += n; }
                    ms.Position += 4; // influence
                }

                if (L.Ver == 0 && L.Early > 4) ms.Position += 4;

                if (L.Ver > 1)
                {
                    r.ReadInt32();
                    int pi = r.ReadInt32(); if (pi < 0 || pi > 4096) return null;
                    ms.Position += 12L * pi + 4 + 28;
                }

                if (ms.Position > d.Length) return null;
                bones.Add(bone);
            }
            leftover = d.Length - ms.Position;
            return bones;
        }
        catch { return null; }
    }

    List<Bone> AutoParse(byte[] d, out string layoutLabel)
    {
        List<Bone> best = null; long bestLeft = long.MaxValue; int bestKnown = -1; layoutLabel = null;
        foreach (var L in Layouts)
        {
            long left;
            var bones = TryParse(d, L, out left);
            if (bones == null || left < 0) continue;
            int known = bones.Count(b => b.Name != null || db.ContainsKey(b.Hash));
            if (left < bestLeft || (left == bestLeft && known > bestKnown))
            { best = bones; bestLeft = left; bestKnown = known; layoutLabel = L.Label + " (leftover bytes: " + left + ")"; }
        }
        return best;
    }

    // ------------------------------------------------------------ Actions
    byte[] ReadA()
    {
        if (!File.Exists(filePath)) throw new Exception("File A not found.");
        return File.ReadAllBytes(filePath);
    }

    string DumpBones()
    {
        byte[] d = ReadA();
        string layout;
        var bones = AutoParse(d, out layout);
        var sb = new StringBuilder();
        sb.AppendLine("File: " + filePath);
        if (db.Count == 0) sb.AppendLine("WARNING: HashDB not loaded - names will show as <unknown>.");
        if (bones == null)
        {
            sb.AppendLine("Structured parse failed for every known layout. Falling back to raw scan:\n");
            sb.Append(ScanFile());
            return sb.ToString();
        }
        sb.AppendLine("Detected layout: " + layout);
        sb.AppendLine("Bone count: " + bones.Count + "\n");
        sb.AppendLine("Idx | Offset   | Name hash          | Bytes in file (HxD search) | Name | Parent idx | Parent name");
        foreach (var b in bones)
        {
            if (b.Name != null)
                sb.AppendLine(string.Format("{0,3} | 0x{1:X6} | (text)  {2} | parent {3} ({4})", b.Index, b.NameOffset, b.Name, b.ParentIndex, b.ParentName));
            else
                sb.AppendLine(string.Format("{0,3} | 0x{1:X6} | {2} | {3} | {4} | {5} | {6}",
                    b.Index, b.NameOffset, Hex(b.Hash), FileBytes(b.Hash), NameOf(b.Hash),
                    b.ParentIndex, b.ParentIndex < 0 ? "(root)" : NameOf(b.ParentHash)));
        }
        return sb.ToString();
    }

    string ScanFile()
    {
        byte[] d = ReadA();
        if (db.Count == 0) return "Raw scan needs the HashDB loaded.";
        var sb = new StringBuilder();
        var found = new Dictionary<ulong, List<long>>();
        for (int i = 0; i + 8 <= d.Length; i++)
        {
            ulong v = BitConverter.ToUInt64(d, i);
            if (v != 0 && db.ContainsKey(v))
            {
                List<long> l;
                if (!found.TryGetValue(v, out l)) found[v] = l = new List<long>();
                l.Add(i);
            }
        }
        sb.AppendLine("Known bone hashes found in " + Path.GetFileName(filePath) + ": " + found.Count + "\n");
        foreach (var kv in found.OrderBy(k => k.Value[0]))
            sb.AppendLine(string.Format("{0} | {1} | x{2} at {3}", Hex(kv.Key), db[kv.Key], kv.Value.Count,
                string.Join(", ", kv.Value.Select(o => "0x" + o.ToString("X")))));
        return sb.ToString();
    }

    string Compare()
    {
        if (!File.Exists(filePathB)) return "File B not found.";
        string la, lb;
        var A = AutoParse(ReadA(), out la);
        var B = AutoParse(File.ReadAllBytes(filePathB), out lb);
        if (A == null || B == null) return "Could not parse one of the files.";
        var ha = new HashSet<ulong>(A.Select(b => b.Name != null ? Crc64(b.Name) : b.Hash));
        var hb = new HashSet<ulong>(B.Select(b => b.Name != null ? Crc64(b.Name) : b.Hash));
        var sb = new StringBuilder();
        sb.AppendLine("A: " + Path.GetFileName(filePath) + " - " + A.Count + " bones, " + la);
        sb.AppendLine("B: " + Path.GetFileName(filePathB) + " - " + B.Count + " bones, " + lb + "\n");
        sb.AppendLine("=== In A but MISSING from B ===");
        foreach (var h in ha.Except(hb)) sb.AppendLine(Hex(h) + "  " + NameOf(h));
        sb.AppendLine("\n=== In B but not in A ===");
        foreach (var h in hb.Except(ha)) sb.AppendLine(Hex(h) + "  " + NameOf(h));
        return sb.ToString();
    }

    string VerifyCrc()
    {
        if (db.Count == 0) return "Load the HashDB first.";
        int ok = 0; var bad = new List<string>();
        foreach (var kv in db)
        {
            if (Crc64(kv.Value) == kv.Key) ok++;
            else if (bad.Count < 10) bad.Add(kv.Value + "  db=" + Hex(kv.Key) + "  crc=" + Hex(Crc64(kv.Value)));
        }
        return string.Format("CRC64 matches {0} / {1} entries ({2:F1}%).\n{3}\n\nFirst mismatches:\n{4}",
            ok, db.Count, 100.0 * ok / db.Count,
            ok > db.Count * 0.9 ? "Algorithm confirmed - you can rename to names NOT in the DB."
                                : "Algorithm NOT confirmed - only use names that exist in the HashDB, or 0x hashes.",
            string.Join("\n", bad));
    }

    ulong ResolveHash(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.Parse(s.Substring(2), NumberStyles.HexNumber);
        ulong h;
        if (dbRev.TryGetValue(s.ToLowerInvariant(), out h)) return h;
        return Crc64(s);
    }

    string ReplaceHash()
    {
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) return "Enter both names/hashes.";
        byte[] d = ReadA();
        ulong oldH = ResolveHash(oldName), newH = ResolveHash(newName);
        byte[] oldB = BitConverter.GetBytes(oldH), newB = BitConverter.GetBytes(newH);
        var sb = new StringBuilder();
        sb.AppendLine("Old: " + oldName + " = " + Hex(oldH) + "  [" + FileBytes(oldH) + "]");
        sb.AppendLine("New: " + newName + " = " + Hex(newH) + "  [" + FileBytes(newH) + "]");

        var hits = new List<int>(); bool newExists = false;
        for (int i = 0; i + 8 <= d.Length; i++)
        {
            ulong v = BitConverter.ToUInt64(d, i);
            if (v == oldH) hits.Add(i);
            if (v == newH) newExists = true;
        }
        if (hits.Count == 0) return sb + "\nOld hash NOT found in file. Nothing changed.";
        if (newExists) sb.AppendLine("WARNING: new hash already exists in this file - you'd get two bones with the same name!");

        foreach (int off in hits) Buffer.BlockCopy(newB, 0, d, off, 8);
        string outPath = Path.Combine(Path.GetDirectoryName(filePath),
            Path.GetFileNameWithoutExtension(filePath) + "_edited" + Path.GetExtension(filePath));
        File.WriteAllBytes(outPath, d);

        sb.AppendLine("\nReplaced " + hits.Count + " occurrence(s) at: " + string.Join(", ", hits.Select(o => "0x" + o.ToString("X"))));
        sb.AppendLine("(1 = the bone itself; extra hits = children's parent references / IK references)");
        sb.AppendLine("Saved: " + outPath);
        return sb.ToString();
    }
}