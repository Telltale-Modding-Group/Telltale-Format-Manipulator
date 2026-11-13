using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TFM.Core
{
    public sealed class HashHit
    {
        public ulong Hash;
        public string Name;
        public List<long> Offsets = new List<long>();
    }

    public sealed class CompareResult
    {
        public List<ulong> OnlyInA = new List<ulong>();
        public List<ulong> OnlyInB = new List<ulong>();
        public List<ulong> InBoth = new List<ulong>();
    }

    /// <summary>Read-only reports over skeletons and arbitrary files.</summary>
    public static class SklAnalysis
    {
        public static string Describe(SklFile f, HashDatabase db)
        {
            db = db ?? HashDatabase.Empty;
            var sb = new StringBuilder();
            sb.AppendLine("File: " + (f.SourcePath ?? "(memory)"));
            sb.AppendLine("Header: " + f.MagicText + "   Layout: " + f.Layout.Label + "   Leftover bytes: " + f.LeftoverBytes);
            sb.AppendLine("Size: " + f.OriginalSize + " bytes   Bones: " + f.Bones.Count);
            if (db.Count == 0) sb.AppendLine("WARNING: hash database not loaded - names show as hashes.");
            sb.AppendLine();
            sb.AppendLine("Idx | Name hash          | Name | Parent idx | Parent name" + (f.Layout.HasMirror ? " | Mirror" : ""));
            for (int i = 0; i < f.Bones.Count; i++)
            {
                var b = f.Bones[i];
                string parent = b.ParentIndex < 0 ? "(root)" : f.BoneName(b.ParentIndex, db);
                string mirror = "";
                if (b.HasMirror)
                    mirror = " | " + (b.MirrorIndex >= 0 ? f.BoneName(b.MirrorIndex, db) + " (" + b.MirrorIndex + ")" : db.NameOrHex(b.MirrorHash) + " (not in file)");
                sb.AppendLine(string.Format("{0,3} | {1} | {2} | {3} | {4}{5}",
                    i, f.IsTextLayout ? "(text)            " : HashDatabase.Hex(b.Hash), f.BoneName(i, db), b.ParentIndex, parent, mirror));
            }
            return sb.ToString();
        }

        /// <summary>Finds every 8-byte little-endian value in the data that is a known bone hash. Works on any file type.</summary>
        public static List<HashHit> ScanForKnownHashes(byte[] data, HashDatabase db)
        {
            var found = new Dictionary<ulong, HashHit>();
            if (data == null || db == null || db.Count == 0) return new List<HashHit>();
            for (int i = 0; i + 8 <= data.Length; i++)
            {
                ulong v = BitConverter.ToUInt64(data, i);
                if (v == 0) continue;
                string name;
                if (!db.TryGetName(v, out name)) continue;
                HashHit hit;
                if (!found.TryGetValue(v, out hit)) found[v] = hit = new HashHit { Hash = v, Name = name };
                hit.Offsets.Add(i);
            }
            return found.Values.OrderBy(h => h.Offsets[0]).ToList();
        }

        public static CompareResult Compare(SklFile a, SklFile b)
        {
            var ha = new HashSet<ulong>(a.Bones.Select(x => x.Hash));
            var hb = new HashSet<ulong>(b.Bones.Select(x => x.Hash));
            var r = new CompareResult();
            foreach (var x in a.Bones) { if (hb.Contains(x.Hash)) r.InBoth.Add(x.Hash); else r.OnlyInA.Add(x.Hash); }
            foreach (var x in b.Bones) if (!ha.Contains(x.Hash)) r.OnlyInB.Add(x.Hash);
            return r;
        }

        /// <summary>Indices of donor bones whose names are not present in the target.</summary>
        public static List<int> MissingBones(SklFile donor, SklFile target)
        {
            var have = new HashSet<ulong>(target.Bones.Select(x => x.Hash));
            var list = new List<int>();
            for (int i = 0; i < donor.Bones.Count; i++) if (!have.Contains(donor.Bones[i].Hash)) list.Add(i);
            return list;
        }

        public static string VerifyCrc(HashDatabase db)
        {
            if (db == null || db.Count == 0) return "Hash database is not loaded.";
            int ok = 0;
            var bad = new List<string>();
            foreach (string n in db.SortedNames)
            {
                ulong h;
                db.TryGetHash(n, out h);
                if (HashDatabase.Crc64(n) == h) ok++;
                else if (bad.Count < 10) bad.Add(n + "  db=" + HashDatabase.Hex(h) + "  crc=" + HashDatabase.Hex(HashDatabase.Crc64(n)));
            }
            int total = db.SortedNames.Count;
            var sb = new StringBuilder();
            sb.AppendLine(string.Format("CRC64 matches {0} / {1} names ({2:F2}%).", ok, total, 100.0 * ok / Math.Max(1, total)));
            sb.AppendLine("Records repaired while loading: " + db.RepairedRecords);
            if (bad.Count > 0) { sb.AppendLine("First mismatches:"); foreach (var s in bad) sb.AppendLine("  " + s); }
            return sb.ToString();
        }
    }
}
