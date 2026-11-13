using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TFM.Core
{
    public sealed class SklParseException : Exception
    {
        public SklParseException(string message) : base(message) { }
    }

    public sealed class PortResult
    {
        public bool Success;
        public int AddedCount;
        public string Log;
    }

    /// <summary>
    /// A loaded Telltale .skl file. Header and trailing bytes are kept verbatim; bones are kept as
    /// raw chunks with known field offsets, so saving an unmodified file reproduces it byte for byte.
    /// </summary>
    public sealed class SklFile
    {
        public const int MagicMSV5 = 1297307189;
        public const int MagicMSV6 = 1297307190;
        public const int MagicMBIN = 1296189774;
        public const int MagicMTRE = 1297371717;
        const int BttfCheckValue = -1339414801;

        public string SourcePath { get; private set; }
        public SklLayout Layout { get; private set; }
        public string MagicText { get; private set; }
        public long LeftoverBytes { get; private set; }
        public int OriginalSize { get { return data.Length; } }
        public IReadOnlyList<SklBone> Bones { get { return bones; } }
        public bool IsTextLayout { get { return !Layout.Hashed; } }

        /// <summary>Number of bones whose names resolved through the hash database at load time.</summary>
        public int KnownNameCount { get; private set; }

        byte[] data;
        List<SklBone> bones = new List<SklBone>();
        int headerSizeOffset = -1;   // MSV5/6 "file size" field
        int sizeOffset;              // BoneFileSize
        int countOffset;             // BoneCount
        int bonesStart, bonesEnd;
        uint blockSize;
        bool parentsBeforeChildren;  // whether the original file kept parents ahead of children

        SklFile() { }

        // ================================================================ loading
        public static SklFile Load(string path, HashDatabase db)
        {
            if (!File.Exists(path)) throw new SklParseException("File not found: " + path);
            return Parse(File.ReadAllBytes(path), db, path);
        }

        public static SklFile Parse(byte[] data, HashDatabase db, string sourcePath = null)
        {
            if (data == null || data.Length < 16) throw new SklParseException("File is too small to be a skeleton.");
            if (db == null) db = HashDatabase.Empty;

            bool bttfHint = data.Length >= 0x2C && BitConverter.ToInt32(data, 0x28) == BttfCheckValue;
            SklFile best = null;
            foreach (var layout in SklLayout.All)
            {
                SklFile f = TryParse(data, layout, db);
                if (f == null) continue;
                if (best == null || Better(f, best, bttfHint)) best = f;
            }
            if (best == null)
                throw new SklParseException("Could not parse the bone list with any known .skl layout. The file may be damaged or from an unsupported game.");
            best.SourcePath = sourcePath;
            return best;
        }

        static bool Better(SklFile a, SklFile b, bool bttfHint)
        {
            if (a.LeftoverBytes != b.LeftoverBytes) return a.LeftoverBytes < b.LeftoverBytes;
            if (a.KnownNameCount != b.KnownNameCount) return a.KnownNameCount > b.KnownNameCount;
            if (a.Layout.Bttf != b.Layout.Bttf) return a.Layout.Bttf == bttfHint;
            return false;
        }

        public static SklFile Parse(byte[] data, HashDatabase db, SklLayout layout, string sourcePath = null)
        {
            if (data == null) throw new SklParseException("No data.");
            if (layout == null) throw new ArgumentNullException("layout");
            var f = TryParse(data, layout, db ?? HashDatabase.Empty);
            if (f == null) throw new SklParseException("File does not match layout \"" + layout.Label + "\".");
            f.SourcePath = sourcePath;
            return f;
        }

        static SklFile TryParse(byte[] d, SklLayout L, HashDatabase db)
        {
            try { return ParseCore(d, L, db); }
            catch (SklParseException) { return null; }
            catch (ArgumentException) { return null; }
            catch (IndexOutOfRangeException) { return null; }
        }

        sealed class Reader
        {
            readonly byte[] d; public int Pos;
            public Reader(byte[] d) { this.d = d; }
            void Need(int n) { if (Pos < 0 || Pos + n > d.Length) throw new SklParseException("eof"); }
            public int I32() { Need(4); int v = BitConverter.ToInt32(d, Pos); Pos += 4; return v; }
            public uint U32() { Need(4); uint v = BitConverter.ToUInt32(d, Pos); Pos += 4; return v; }
            public ulong U64() { Need(8); ulong v = BitConverter.ToUInt64(d, Pos); Pos += 8; return v; }
            public float F() { Need(4); float v = BitConverter.ToSingle(d, Pos); Pos += 4; return v; }
            public Vec3 V3() { return new Vec3(F(), F(), F()); }
            public Quat Q() { return new Quat(F(), F(), F(), F()); }
            public void Skip(long n) { if (n < 0) throw new SklParseException("neg"); Need((int)Math.Min(n, int.MaxValue)); Pos += (int)n; }
            public string Str(int n) { Need(n); string s = Encoding.ASCII.GetString(d, Pos, n); Pos += n; return s; }
        }

        static SklFile ParseCore(byte[] d, SklLayout L, HashDatabase db)
        {
            var f = new SklFile { data = d, Layout = L };
            var r = new Reader(d);

            int magic = r.I32();
            if (magic == MagicMSV5 || magic == MagicMSV6)
            {
                f.MagicText = magic == MagicMSV5 ? "MSV5" : "MSV6";
                f.headerSizeOffset = r.Pos; r.U32();
                r.Skip(8);
                uint pc = r.U32(); if (pc > 4096) return null;
                r.Skip(12L * pc);
            }
            else
            {
                f.MagicText = magic == MagicMBIN ? "MBIN" : magic == MagicMTRE ? "MTRE" : "unknown (0x" + ((uint)magic).ToString("X8") + ")";
                uint pc = r.U32(); if (pc > 4096) return null;
                int save = r.Pos;
                uint chk = pc > 0 ? r.U32() : 0; r.Pos = save;
                if (chk > 0 && chk < 128)
                {
                    for (int i = 0; i < pc; i++)
                    {
                        uint len = r.U32(); if (len > 512) return null;
                        r.Skip(len + 4);
                    }
                }
                else r.Skip(12L * pc);
            }

            if (L.PreSizeByte) r.Skip(1);

            f.sizeOffset = r.Pos; f.blockSize = r.U32();
            f.countOffset = r.Pos; int count = r.I32();
            if (count <= 0 || count > 4096) return null;
            f.bonesStart = r.Pos;

            bool ordered = true;
            for (int b = 0; b < count; b++)
            {
                int start = r.Pos;
                var bone = new SklBone { Index = b, textLayout = !L.Hashed };

                if (L.Hashed)
                {
                    bone.oHash = r.Pos - start; bone.Hash = r.U64(); if (L.HashPadding) r.Skip(4);
                    bone.oParentHash = r.Pos - start; bone.ParentHash = r.U64(); if (L.HashPadding) r.Skip(4);
                }
                else
                {
                    bone.oNameSeg = r.Pos - start;
                    int sect = r.I32(); int n = r.I32(); if (n < 0 || n > 512) return null;
                    bone.nameSegLen = n; bone.nameSectDelta = sect - n;
                    bone.TextName = r.Str(n);
                    bone.Hash = HashDatabase.Crc64(bone.TextName);

                    bone.oParentSeg = r.Pos - start;
                    sect = r.I32(); n = r.I32(); if (n < 0 || n > 512) return null;
                    bone.parentSegLen = n; bone.parentSectDelta = sect - n;
                    bone.TextParentName = r.Str(n);
                    bone.ParentHash = HashDatabase.Crc64(bone.TextParentName);
                }

                bone.oParentIndex = r.Pos - start; bone.ParentIndex = r.I32();
                if (bone.ParentIndex < -1 || bone.ParentIndex >= count) return null;
                if (bone.ParentIndex >= b) ordered = false;

                if (L.HasMirror)
                {
                    bone.HasMirror = true;
                    bone.oMirror = r.Pos - start;
                    bone.MirrorHash = r.U64();
                    bone.MirrorIndex = r.I32();
                }

                bone.oLocalPos = r.Pos - start; bone.LocalPosition = r.V3();
                bone.oLocalRot = r.Pos - start; bone.LocalRotation = r.Q();
                bone.oRestSize = r.Pos - start; bone.RestTransformBlockSize = r.I32();
                bone.oRestRot = r.Pos - start; bone.RestRotation = r.Q();
                bone.oRestPos = r.Pos - start; bone.RestPosition = r.V3();
                bone.oScales = r.Pos - start;
                bone.GlobalTranslationScale = r.V3();
                bone.LocalTranslationScale = r.V3();
                bone.AnimTranslationScale = r.V3();

                r.I32(); // resource group block size
                int groups = r.I32(); if (groups < 0 || groups > 128) return null;
                for (int i = 0; i < groups; i++)
                {
                    var g = new SklResourceGroup { IsHashed = L.ResourceGroupsHashed };
                    g.Offset = r.Pos - start;
                    if (g.IsHashed) g.Hash = r.U64();
                    else
                    {
                        int n = r.I32(); if (n < 0 || n > 512) return null;
                        g.Name = r.Str(n);
                    }
                    g.WeightOffset = r.Pos - start; g.Weight = r.F();
                    bone.ResourceGroups.Add(g);
                }

                if (L.PostGroupPadding) r.Skip(4);

                if (L.HasConstraints)
                {
                    bone.HasConstraints = true;
                    r.I32(); // constraint block size
                    int axes = r.I32(); if (axes < 0 || axes > 4096) return null;
                    bone.oAxes = r.Pos - start;
                    for (int i = 0; i < axes; i++) bone.ConstraintAxes.Add(r.V3());
                    r.I32(); // range block size
                    bone.oRanges = r.Pos - start;
                    for (int i = 0; i < 6; i++) bone.ConstraintRanges[i] = r.F();
                    bone.oExtra = r.Pos - start; bone.ConstraintExtra = r.F();
                }

                bone.raw = new byte[r.Pos - start];
                Buffer.BlockCopy(d, start, bone.raw, 0, bone.raw.Length);
                f.bones.Add(bone);
                if (bone.TextName != null || db.Contains(bone.Hash)) f.KnownNameCount++;
            }

            f.bonesEnd = r.Pos;
            f.LeftoverBytes = d.Length - r.Pos;
            f.parentsBeforeChildren = ordered;
            return f;
        }

        // ================================================================ saving
        public byte[] Serialize()
        {
            var chunks = new List<byte[]>(bones.Count);
            long total = 0;
            foreach (var b in bones) { byte[] c = b.Build(); chunks.Add(c); total += c.Length; }
            long delta = total - (bonesEnd - bonesStart);

            var ms = new MemoryStream((int)(data.Length + Math.Max(0, delta)));
            ms.Write(data, 0, bonesStart);
            foreach (var c in chunks) ms.Write(c, 0, c.Length);
            ms.Write(data, bonesEnd, data.Length - bonesEnd);
            byte[] o = ms.ToArray();

            SklBone.PutI32(o, countOffset, bones.Count);
            PutU32(o, sizeOffset, (uint)(blockSize + delta));
            if (headerSizeOffset >= 0)
            {
                uint hs = BitConverter.ToUInt32(data, headerSizeOffset);
                PutU32(o, headerSizeOffset, (uint)(hs + delta));
            }
            return o;
        }

        static void PutU32(byte[] a, int o, uint v) { Buffer.BlockCopy(BitConverter.GetBytes(v), 0, a, o, 4); }

        /// <summary>Serialize, verify by re-parsing, then write. Throws (and writes nothing) if verification fails.</summary>
        public void Save(string path)
        {
            byte[] o = Serialize();
            string problem = Verify(o);
            if (problem != null) throw new InvalidOperationException("Save aborted, the rebuilt file failed verification: " + problem);
            File.WriteAllBytes(path, o);
            // Adopt the written bytes as the new baseline.
            var fresh = ParseCore(o, Layout, HashDatabase.Empty);
            AdoptFrom(fresh);
            SourcePath = path;
        }

        /// <summary>Returns null if the bytes re-parse consistently with this file, else a description of the problem.</summary>
        public string Verify(byte[] o)
        {
            SklFile check;
            try { check = ParseCore(o, Layout, HashDatabase.Empty); }
            catch (Exception e) { check = null; if (!(e is SklParseException)) return "re-parse threw " + e.Message; }
            if (check == null) return "re-parse failed";
            if (check.bones.Count != bones.Count) return "bone count " + check.bones.Count + " != " + bones.Count;
            if (check.LeftoverBytes != LeftoverBytes) return "trailing bytes changed (" + LeftoverBytes + " -> " + check.LeftoverBytes + ")";
            for (int i = 0; i < check.bones.Count; i++)
            {
                var a = check.bones[i];
                if (a.ParentIndex < -1 || a.ParentIndex >= check.bones.Count) return "bone " + i + " has invalid parent";
                if (parentsBeforeChildren && a.ParentIndex >= i) return "bone " + i + " has a parent that comes after it";
                if (a.Hash != bones[i].Hash) return "bone " + i + " name mismatch after re-parse";
            }
            return null;
        }

        void AdoptFrom(SklFile f)
        {
            data = f.data; bones = f.bones; headerSizeOffset = f.headerSizeOffset;
            sizeOffset = f.sizeOffset; countOffset = f.countOffset;
            bonesStart = f.bonesStart; bonesEnd = f.bonesEnd; blockSize = f.blockSize;
            LeftoverBytes = f.LeftoverBytes; parentsBeforeChildren = f.parentsBeforeChildren;
        }

        // ================================================================ queries
        public string BoneName(int index, HashDatabase db)
        {
            var b = bones[index];
            if (b.TextName != null) return b.TextName;
            return (db ?? HashDatabase.Empty).NameOrHex(b.Hash);
        }

        public int FindBone(ulong hash)
        {
            for (int i = 0; i < bones.Count; i++) if (bones[i].Hash == hash) return i;
            return -1;
        }

        // ================================================================ edits
        public void RenameBone(int index, string newName, HashDatabase db)
        {
            CheckIndex(index);
            if (string.IsNullOrWhiteSpace(newName)) throw new InvalidOperationException("Bone name cannot be empty.");
            newName = newName.Trim();

            if (IsTextLayout)
            {
                if (Encoding.ASCII.GetByteCount(newName) != newName.Length || newName.Any(ch => ch > 127))
                    throw new InvalidOperationException("Bone names must be plain ASCII.");
                ulong h = HashDatabase.Crc64(newName);
                int other = FindBone(h);
                if (other >= 0 && other != index) throw new InvalidOperationException("Bone #" + other + " is already named \"" + bones[other].TextName + "\".");
                var b = bones[index];
                b.TextName = newName; b.Hash = h;
                foreach (var c in bones)
                    if (c.ParentIndex == index) { c.TextParentName = newName; c.ParentHash = h; }
                return;
            }

            ulong hash;
            if (db == null || !db.TryGetHash(newName, out hash)) hash = HashDatabase.Crc64(newName);
            RenameBoneByHash(index, hash);
        }

        public void RenameBoneByHash(int index, ulong newHash)
        {
            CheckIndex(index);
            if (IsTextLayout) throw new InvalidOperationException("This skeleton stores bone names as text; rename by name instead.");
            var b = bones[index];
            ulong old = b.Hash;
            if (old == newHash) return;
            int other = FindBone(newHash);
            if (other >= 0 && other != index) throw new InvalidOperationException("Bone #" + other + " already uses that name/hash.");

            b.Hash = newHash;
            foreach (var c in bones)
            {
                if (c.ParentIndex == index) c.ParentHash = newHash;
                if (c.HasMirror && c.MirrorHash == old) c.MirrorHash = newHash;
            }
        }

        public void SetParent(int index, int newParent)
        {
            CheckIndex(index);
            var b = bones[index];
            if (newParent == b.ParentIndex) return;
            if (newParent < -1 || newParent >= bones.Count) throw new InvalidOperationException("Parent index out of range.");
            if (newParent == index) throw new InvalidOperationException("A bone cannot be its own parent.");
            if (newParent >= index) throw new InvalidOperationException("The parent must come before the bone in the list (index lower than " + index + ").");

            if (newParent == -1)
            {
                // Roots carry a parent hash/name too; reuse the existing root's value when there is one.
                var root = bones.FirstOrDefault(x => x.ParentIndex == -1);
                if (root != null) { b.ParentHash = root.ParentHash; b.TextParentName = root.TextParentName; }
            }
            else
            {
                b.ParentHash = bones[newParent].Hash;
                if (IsTextLayout) b.TextParentName = bones[newParent].TextName;
            }
            b.ParentIndex = newParent;
        }

        /// <summary>Sets the mirror bone. -1 clears the index but keeps the stored mirror hash (as in shipped files).</summary>
        public void SetMirror(int index, int mirrorIndex)
        {
            CheckIndex(index);
            var b = bones[index];
            if (!b.HasMirror) throw new InvalidOperationException("This skeleton version has no mirror bone field.");
            if (mirrorIndex < -1 || mirrorIndex >= bones.Count) throw new InvalidOperationException("Mirror index out of range.");
            b.MirrorIndex = mirrorIndex;
            if (mirrorIndex >= 0) b.MirrorHash = bones[mirrorIndex].Hash;
        }

        void CheckIndex(int i)
        {
            if (i < 0 || i >= bones.Count) throw new ArgumentOutOfRangeException("index");
        }

        // ================================================================ porting
        public PortResult PortBones(SklFile donor, IList<int> donorBoneIndices, bool remapMirror, HashDatabase db, bool dryRun)
        {
            db = db ?? HashDatabase.Empty;
            var res = new PortResult();
            var sb = new StringBuilder();

            if (donor == null) { res.Log = "No donor skeleton."; return res; }
            if (donor.Layout.Label != Layout.Label)
            {
                res.Log = "The donor uses a different skeleton layout (" + donor.Layout.Label + ") than this file (" + Layout.Label +
                          "). Bones can only be copied between skeletons of the same game version.";
                return res;
            }

            sb.AppendLine("Layout: " + Layout.Label);
            sb.AppendLine("Donor bones: " + donor.bones.Count + "   Target bones: " + bones.Count);
            sb.AppendLine();

            var map = new Dictionary<ulong, int>();
            foreach (var b in bones) if (!map.ContainsKey(b.Hash)) map[b.Hash] = b.Index;

            var want = new HashSet<int>();
            if (donorBoneIndices == null)
            {
                foreach (var b in donor.bones) if (!map.ContainsKey(b.Hash)) want.Add(b.Index);
            }
            else
            {
                foreach (int di in donorBoneIndices)
                {
                    if (di < 0 || di >= donor.bones.Count) { sb.AppendLine("Ignored invalid donor index " + di); continue; }
                    var cur = donor.bones[di];
                    if (map.ContainsKey(cur.Hash)) { sb.AppendLine("Already in target, skipped: " + donor.BoneName(di, db)); continue; }
                    while (cur != null && !map.ContainsKey(cur.Hash))
                    {
                        if (want.Add(cur.Index) && cur.Index != di) sb.AppendLine("Also adding missing parent: " + donor.BoneName(cur.Index, db));
                        cur = cur.ParentIndex >= 0 ? donor.bones[cur.ParentIndex] : null;
                    }
                }
            }

            if (want.Count == 0)
            {
                sb.AppendLine("Nothing to port - the target already has every requested bone.");
                res.Success = true; res.Log = sb.ToString(); return res;
            }

            // Parents before children.
            var order = new List<SklBone>();
            var placed = new HashSet<ulong>(map.Keys);
            var pending = want.OrderBy(i => i).Select(i => donor.bones[i]).ToList();
            while (pending.Count > 0)
            {
                var ready = pending.Where(b => b.ParentIndex < 0 || placed.Contains(donor.bones[b.ParentIndex].Hash)).ToList();
                if (ready.Count == 0) { sb.AppendLine("ERROR: could not resolve parent order (cyclic hierarchy?)."); res.Log = sb.ToString(); return res; }
                foreach (var b in ready) { order.Add(b); placed.Add(b.Hash); pending.Remove(b); }
            }

            var newMap = new Dictionary<ulong, int>(map);
            int next = bones.Count;
            foreach (var b in order) newMap[b.Hash] = next++;

            var added = new List<SklBone>();
            int mirrorCleared = 0;
            sb.AppendLine("New idx | Bone | Parent (new idx) | Bytes");
            foreach (var src in order)
            {
                var c = src.Clone();
                c.Index = newMap[src.Hash];
                if (src.ParentIndex >= 0)
                {
                    c.ParentIndex = newMap[donor.bones[src.ParentIndex].Hash];
                }
                else
                {
                    c.ParentIndex = -1;
                    sb.AppendLine("WARNING: " + donor.BoneName(src.Index, db) + " is a ROOT bone in the donor - the target will have more than one root.");
                }

                if (remapMirror && c.HasMirror && c.MirrorIndex >= 0)
                {
                    int ni;
                    if (newMap.TryGetValue(c.MirrorHash, out ni)) c.MirrorIndex = ni;
                    else { c.MirrorIndex = -1; mirrorCleared++; }
                }

                added.Add(c);
                string pName = src.ParentIndex >= 0 ? donor.BoneName(src.ParentIndex, db) : "(root)";
                sb.AppendLine(string.Format("{0,7} | {1} | {2} ({3}) | {4}", c.Index, donor.BoneName(src.Index, db), pName, c.ParentIndex, c.RawLength));
            }

            // Existing target bones whose mirror bone was missing until now.
            int mirrorLinked = 0;
            if (remapMirror)
            {
                foreach (var b in bones)
                {
                    int ni;
                    if (b.HasMirror && b.MirrorIndex == -1 && newMap.TryGetValue(b.MirrorHash, out ni) && ni >= bones.Count)
                    {
                        if (!dryRun) b.MirrorIndex = ni;
                        mirrorLinked++;
                    }
                }
            }

            if (mirrorCleared > 0) sb.AppendLine("\nNote: " + mirrorCleared + " ported bone(s) mirror a bone that is not in the target - mirror index set to -1.");
            if (mirrorLinked > 0) sb.AppendLine("Note: " + mirrorLinked + " existing bone(s) now have their mirror bone and were linked to it.");

            long addedBytes = added.Sum(b => (long)b.RawLength);
            sb.AppendLine();
            sb.AppendLine("BoneCount: " + bones.Count + " -> " + (bones.Count + added.Count));
            sb.AppendLine("Size: " + data.Length + " -> " + (data.Length + addedBytes) + " bytes (+" + addedBytes + ")");

            if (!dryRun)
            {
                bones.AddRange(added);
                sb.AppendLine("Bones added in memory - save the file to write them.");
            }
            else sb.AppendLine("(Preview only - nothing changed)");

            res.Success = true; res.AddedCount = added.Count; res.Log = sb.ToString();
            return res;
        }
    }
}
