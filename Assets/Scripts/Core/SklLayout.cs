using System.Collections.Generic;

namespace TFM.Core
{
    /// <summary>
    /// One structural variant of the .skl bone list. The file itself doesn't say which variant it is
    /// (the version lives in the matching .d3dmesh), so the parser tries each one and keeps the best fit.
    /// Rules follow TelltaleGames_D3DMesh.ms.
    /// </summary>
    public sealed class SklLayout
    {
        public string Label;
        public int Ver;
        public int Early;
        public bool Bttf;

        /// <summary>Bone names stored as 64-bit hashes (otherwise length-prefixed text).</summary>
        public bool Hashed { get { return Ver > 0 || Early > 3; } }
        /// <summary>Mirror bone hash + index present (VerNum 18+, or Back to the Future PS4).</summary>
        public bool HasMirror { get { return Ver >= 18 || Bttf; } }
        /// <summary>Constraint block present after the resource group list (VerNum 2+).</summary>
        public bool HasConstraints { get { return Ver > 1; } }
        /// <summary>Resource group entries are hash+weight (VerNum 55) instead of string+weight.</summary>
        public bool ResourceGroupsHashed { get { return Ver == 55; } }
        /// <summary>4 bytes of padding after each hash (VerNum below 13).</summary>
        public bool HashPadding { get { return Hashed && Ver < 13; } }
        /// <summary>Single byte before BoneFileSize (VerNum 0, EarlyGameFix 5-7).</summary>
        public bool PreSizeByte { get { return Ver == 0 && Early > 4 && Early < 8; } }
        /// <summary>4 bytes of padding after the resource group list (VerNum 0, EarlyGameFix 5+).</summary>
        public bool PostGroupPadding { get { return Ver == 0 && Early > 4; } }

        public SklLayout(string label, int ver, int early, bool bttf)
        {
            Label = label; Ver = ver; Early = early; Bttf = bttf;
        }

        public override string ToString() { return Label; }

        static readonly SklLayout[] all =
        {
            new SklLayout("VerNum 13-17", 14, 0, false),
            new SklLayout("VerNum 18-54", 18, 0, false),
            new SklLayout("VerNum 55", 55, 0, false),
            new SklLayout("VerNum 17 BttF PS4", 17, 0, true),
            new SklLayout("VerNum 2-12", 5, 0, false),
            new SklLayout("VerNum 1", 1, 0, false),
            new SklLayout("VerNum 0, EarlyGameFix 4 (hashed)", 0, 4, false),
            new SklLayout("VerNum 0, EarlyGameFix 5-7", 0, 5, false),
            new SklLayout("VerNum 0, EarlyGameFix 8-9", 0, 8, false),
            new SklLayout("VerNum 0, EarlyGameFix 1-3 (text names)", 0, 1, false),
        };

        public static IReadOnlyList<SklLayout> All { get { return all; } }

        public static SklLayout FindByLabel(string label)
        {
            foreach (var l in all) if (l.Label == label) return l;
            return null;
        }
    }
}
