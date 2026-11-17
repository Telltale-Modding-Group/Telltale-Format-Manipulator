using System;
using System.Collections.Generic;
using System.Text;

namespace TFM.Core
{
    /// <summary>One side of a field comparison between two property files.</summary>
    public sealed class PropMatch
    {
        public PropField Target;      // field in the open file, or null when missing
        public PropField Donor;       // field in the other file, or null when absent there
        public bool SameType;
        public bool SameValueCount;
        public bool Identical;

        public string Signature
        {
            get
            {
                if (Target != null) return Target.Signature;
                return Donor != null ? Donor.Signature : "";
            }
        }

        public bool OnlyInTarget { get { return Donor == null; } }
        public bool OnlyInDonor { get { return Target == null; } }
    }

    public sealed class PropCompareResult
    {
        public List<PropMatch> Matches = new List<PropMatch>();

        public int IdenticalCount, DifferentCount, OnlyInTargetCount, OnlyInDonorCount;

        public void Recount()
        {
            IdenticalCount = DifferentCount = OnlyInTargetCount = OnlyInDonorCount = 0;
            for (int i = 0; i < Matches.Count; i++)
            {
                PropMatch m = Matches[i];
                if (m.OnlyInTarget) OnlyInTargetCount++;
                else if (m.OnlyInDonor) OnlyInDonorCount++;
                else if (m.Identical) IdenticalCount++;
                else DifferentCount++;
            }
        }
    }

    public sealed class PropPortResult
    {
        public int Added;
        public int Replaced;
        public int Skipped;
        public bool Success;
        public string Log = "";
    }

    /// <summary>Compares property files by type hash plus first element key, and ports fields between them.</summary>
    public static class PropPort
    {
        public static string SignatureOf(ulong typeHash, ulong firstKey)
        {
            return typeHash.ToString("X16") + ":" + firstKey.ToString("X16");
        }

        public static PropCompareResult Compare(PropFile target, PropFile donor)
        {
            PropCompareResult r = new PropCompareResult();
            if (target == null) throw new ArgumentNullException("target");

            if (donor == null)
            {
                for (int i = 0; i < target.Fields.Count; i++)
                {
                    PropMatch m = new PropMatch();
                    m.Target = target.Fields[i];
                    r.Matches.Add(m);
                }
                r.Recount();
                return r;
            }

            // Index the donor by signature, first occurrence wins.
            var dmap = new Dictionary<string, PropField>();
            for (int i = 0; i < donor.Fields.Count; i++)
            {
                string k = donor.Fields[i].Signature;
                if (!dmap.ContainsKey(k)) dmap[k] = donor.Fields[i];
            }

            var used = new HashSet<string>();
            for (int i = 0; i < target.Fields.Count; i++)
            {
                PropField t = target.Fields[i];
                PropMatch m = new PropMatch();
                m.Target = t;

                PropField d;
                if (dmap.TryGetValue(t.Signature, out d) && used.Add(t.Signature))
                {
                    m.Donor = d;
                    m.SameType = t.TypeHash == d.TypeHash;
                    m.SameValueCount = t.Elements.Count == d.Elements.Count;
                    m.Identical = m.SameType && m.SameValueCount && SameValues(t, d);
                }
                r.Matches.Add(m);
            }

            for (int i = 0; i < donor.Fields.Count; i++)
            {
                PropField d = donor.Fields[i];
                if (used.Contains(d.Signature)) continue;
                if (ContainsSignature(r.Matches, d.Signature)) continue;
                PropMatch m = new PropMatch();
                m.Donor = d;
                m.SameType = true;
                m.SameValueCount = true;
                r.Matches.Add(m);
            }

            r.Recount();
            return r;
        }

        static bool ContainsSignature(List<PropMatch> list, string sig)
        {
            for (int i = 0; i < list.Count; i++) if (list[i].Signature == sig) return true;
            return false;
        }

        public static bool SameValues(PropField a, PropField b)
        {
            if (a.TypeHash != b.TypeHash) return false;
            if (a.Elements.Count != b.Elements.Count) return false;
            for (int i = 0; i < a.Elements.Count; i++)
            {
                PropElement x = a.Elements[i], y = b.Elements[i];
                if (x.KeyHash != y.KeyHash) return false;
                if (x.Value.Length != y.Value.Length) return false;
                for (int k = 0; k < x.Value.Length; k++)
                    if (x.Value[k] != y.Value[k]) return false;
            }
            return true;
        }

        /// <summary>Adds every field present in the donor but not in the target, appended at the end.</summary>
        public static PropPortResult AddMissing(PropFile target, PropFile donor)
        {
            PropPortResult res = new PropPortResult();
            if (target == null || donor == null) { res.Log = "Both files must be loaded."; return res; }

            var sb = new StringBuilder();
            for (int i = 0; i < donor.Fields.Count; i++)
            {
                PropField d = donor.Fields[i];
                if (target.FindBySignature(d.Signature) != null) { res.Skipped++; continue; }
                target.AddField(d.Clone());
                res.Added++;
                sb.AppendLine("  + " + d.TypeName + "  (" + d.Elements.Count + " values)");
            }

            res.Success = true;
            res.Log = res.Added == 0
                ? "Nothing to add: every field in the donor is already in this file."
                : "Added " + res.Added + " field(s):\n" + sb;
            return res;
        }

        /// <summary>Overwrites matching fields in the target with the donor's values.</summary>
        public static PropPortResult ReplaceMatching(PropFile target, PropFile donor)
        {
            PropPortResult res = new PropPortResult();
            if (target == null || donor == null) { res.Log = "Both files must be loaded."; return res; }

            var sb = new StringBuilder();
            for (int i = 0; i < target.Fields.Count; i++)
            {
                PropField t = target.Fields[i];
                PropField d = donor.FindBySignature(t.Signature);
                if (d == null) { res.Skipped++; continue; }
                if (SameValues(t, d)) { res.Skipped++; continue; }
                if (t.TypeHash != d.TypeHash) { res.Skipped++; continue; }

                target.ReplaceField(t, d.Clone());
                res.Replaced++;
                sb.AppendLine("  ~ " + d.TypeName + "  (" + d.Elements.Count + " values)");
            }

            res.Success = true;
            res.Log = res.Replaced == 0
                ? "Nothing to replace: no matching field differs from the donor."
                : "Replaced " + res.Replaced + " field(s):\n" + sb;
            return res;
        }

        /// <summary>Moves one donor field into the target, replacing a same-signature field if present.</summary>
        public static PropPortResult MoveOne(PropFile target, PropField donorField, bool replaceExisting, bool removeFromDonor, PropFile donor)
        {
            PropPortResult res = new PropPortResult();
            if (target == null || donorField == null) { res.Log = "No field selected."; return res; }

            string sig = donorField.Signature;
            PropField existing = target.FindBySignature(sig);

            if (existing != null)
            {
                if (!replaceExisting)
                {
                    res.Skipped = 1;
                    res.Success = true;
                    res.Log = "This field is already in the open file (" + existing.TypeName + "). "
                              + "Tick [Replace existing] to overwrite it.";
                    return res;
                }
                int at = target.IndexOf(existing);
                if (at < 0) { target.AddField(donorField.Clone()); res.Added = 1; }
                else { target.ReplaceField(existing, donorField.Clone()); res.Replaced = 1; }
            }
            else
            {
                target.AddField(donorField.Clone());
                res.Added = 1;
            }

            if (removeFromDonor && donor != null)
            {
                donor.DeleteField(donorField);
            }

            res.Success = true;
            res.Log = (res.Replaced > 0
                    ? "Replaced " + donorField.TypeName + " in the open file."
                    : "Added " + donorField.TypeName + " to the open file.")
                + (removeFromDonor ? " Removed it from the donor file." : "");
            return res;
        }
    }
}
