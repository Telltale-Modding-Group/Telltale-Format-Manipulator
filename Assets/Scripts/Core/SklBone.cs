using System;
using System.Collections.Generic;
using System.Text;

namespace TFM.Core
{
    /// <summary>One entry of a bone's resource group membership list (name/hash + weight).</summary>
    public sealed class SklResourceGroup
    {
        public bool IsHashed;
        public ulong Hash;       // VerNum 55 only
        public string Name;      // other versions (read-only: changing its length would resize the bone)
        public float Weight;

        internal int Offset;     // offset of the hash or string length within the raw chunk
        internal int WeightOffset;
    }

    /// <summary>
    /// One skeleton entry. Every editable field is patched back into a copy of the bone's
    /// original bytes on save, so unknown data and padding survive untouched.
    /// </summary>
    public sealed class SklBone
    {
        public int Index { get; internal set; }

        public ulong Hash; public string TextName;
        public ulong ParentHash; public string TextParentName;
        public int ParentIndex;

        public bool HasMirror; public ulong MirrorHash; public int MirrorIndex;

        public Vec3 LocalPosition; public Quat LocalRotation;

        public int RestTransformBlockSize;
        public Quat RestRotation; public Vec3 RestPosition;

        public Vec3 GlobalTranslationScale, LocalTranslationScale, AnimTranslationScale;

        public List<SklResourceGroup> ResourceGroups = new List<SklResourceGroup>();

        public bool HasConstraints;
        public List<Vec3> ConstraintAxes = new List<Vec3>();
        public float[] ConstraintRanges = new float[6];
        public float ConstraintExtra;

        public int RawLength { get { return raw == null ? 0 : raw.Length; } }

        // ---------------------------------------------------------- internal layout
        internal byte[] raw;
        internal bool textLayout;
        // offsets within raw (-1 = not present)
        internal int oHash = -1, oParentHash = -1;
        internal int oNameSeg = -1, nameSegLen, nameSectDelta;       // text layouts: [sectLen][len][bytes]
        internal int oParentSeg = -1, parentSegLen, parentSectDelta;
        internal int oParentIndex, oMirror = -1, oLocalPos, oLocalRot;
        internal int oRestSize, oRestRot, oRestPos, oScales;
        internal int oAxes = -1, oRanges = -1, oExtra = -1;

        internal SklBone Clone()
        {
            var b = (SklBone)MemberwiseClone();
            b.raw = (byte[])raw.Clone();
            b.ResourceGroups = new List<SklResourceGroup>();
            foreach (var g in ResourceGroups)
                b.ResourceGroups.Add(new SklResourceGroup { IsHashed = g.IsHashed, Hash = g.Hash, Name = g.Name, Weight = g.Weight, Offset = g.Offset, WeightOffset = g.WeightOffset });
            b.ConstraintAxes = new List<Vec3>(ConstraintAxes);
            b.ConstraintRanges = (float[])ConstraintRanges.Clone();
            return b;
        }

        /// <summary>Builds the bone's bytes from the current field values.</summary>
        internal byte[] Build()
        {
            byte[] c = (byte[])raw.Clone();
            int shift = 0;

            if (textLayout)
            {
                // Rebuild name segments only if the text changed (keeps original bytes otherwise).
                string origName = Encoding.ASCII.GetString(raw, oNameSeg + 8, nameSegLen);
                string origParent = Encoding.ASCII.GetString(raw, oParentSeg + 8, parentSegLen);
                bool nameChanged = (TextName ?? "") != origName;
                bool parentChanged = (TextParentName ?? "") != origParent;
                if (nameChanged || parentChanged)
                {
                    var ms = new System.IO.MemoryStream();
                    WriteSeg(ms, TextName ?? "", nameSectDelta);
                    WriteSeg(ms, TextParentName ?? "", parentSectDelta);
                    int oldEnd = oParentSeg + 8 + parentSegLen;
                    ms.Write(raw, oldEnd, raw.Length - oldEnd);
                    byte[] head = new byte[oNameSeg];
                    Buffer.BlockCopy(raw, 0, head, 0, oNameSeg);
                    byte[] tail = ms.ToArray();
                    c = new byte[head.Length + tail.Length];
                    Buffer.BlockCopy(head, 0, c, 0, head.Length);
                    Buffer.BlockCopy(tail, 0, c, head.Length, tail.Length);
                    shift = c.Length - raw.Length; // everything after the name segments moves by this
                }
            }
            else
            {
                PutU64(c, oHash, Hash);
                PutU64(c, oParentHash, ParentHash);
            }

            PutI32(c, oParentIndex + shift, ParentIndex);
            if (oMirror >= 0)
            {
                PutU64(c, oMirror + shift, MirrorHash);
                PutI32(c, oMirror + shift + 8, MirrorIndex);
            }
            PutVec(c, oLocalPos + shift, LocalPosition);
            PutQuat(c, oLocalRot + shift, LocalRotation);
            PutQuat(c, oRestRot + shift, RestRotation);
            PutVec(c, oRestPos + shift, RestPosition);
            PutVec(c, oScales + shift, GlobalTranslationScale);
            PutVec(c, oScales + shift + 12, LocalTranslationScale);
            PutVec(c, oScales + shift + 24, AnimTranslationScale);

            foreach (var g in ResourceGroups)
            {
                if (g.IsHashed) PutU64(c, g.Offset + shift, g.Hash);
                PutF(c, g.WeightOffset + shift, g.Weight);
            }

            if (oAxes >= 0)
            {
                for (int i = 0; i < ConstraintAxes.Count; i++) PutVec(c, oAxes + shift + 12 * i, ConstraintAxes[i]);
                for (int i = 0; i < 6; i++) PutF(c, oRanges + shift + 4 * i, ConstraintRanges[i]);
                PutF(c, oExtra + shift, ConstraintExtra);
            }
            return c;
        }

        static void WriteSeg(System.IO.Stream s, string text, int sectDelta)
        {
            byte[] b = Encoding.ASCII.GetBytes(text);
            s.Write(BitConverter.GetBytes(b.Length + sectDelta), 0, 4);
            s.Write(BitConverter.GetBytes(b.Length), 0, 4);
            s.Write(b, 0, b.Length);
        }

        static void PutU64(byte[] a, int o, ulong v) { Buffer.BlockCopy(BitConverter.GetBytes(v), 0, a, o, 8); }
        internal static void PutI32(byte[] a, int o, int v) { Buffer.BlockCopy(BitConverter.GetBytes(v), 0, a, o, 4); }
        static void PutF(byte[] a, int o, float v) { Buffer.BlockCopy(BitConverter.GetBytes(v), 0, a, o, 4); }
        static void PutVec(byte[] a, int o, Vec3 v) { PutF(a, o, v.X); PutF(a, o + 4, v.Y); PutF(a, o + 8, v.Z); }
        static void PutQuat(byte[] a, int o, Quat q) { PutF(a, o, q.X); PutF(a, o + 4, q.Y); PutF(a, o + 8, q.Z); PutF(a, o + 12, q.W); }
    }
}
