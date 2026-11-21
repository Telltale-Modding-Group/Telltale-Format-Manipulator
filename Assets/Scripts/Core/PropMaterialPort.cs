using System;
using System.Collections.Generic;
using System.Text;

namespace TFM.Core
{
    public sealed class MaterialTransplantResult
    {
        public bool Success;
        public int ValuesRemoved;
        public int ValuesAdded;
        public int HeaderTypesAdded;
        public bool MaterialReplaced;   // false when the target had no material and one was added
        public string Log = "";
    }

    /// <summary>
    /// Finds the material in a property file and moves whole materials between files.
    ///
    /// A material is more than its T3MaterialData block: the block names the properties it
    /// reads, and the values (textures, colours, floats, switches) are ordinary properties of
    /// the same file. A transplant therefore swaps the block and the values together, so the
    /// result is as self-consistent as the donor.
    /// </summary>
    public static class MaterialPort
    {
        /// <summary>Prefix of the engine's material property names, e.g. "Material - Diffuse Texture".</summary>
        public const string MaterialKeyPrefix = "Material - ";

        /// <summary>The property holding the material block, or null when the file has none.</summary>
        public static PropField FindMaterialField(PropFile file)
        {
            if (file == null) return null;
            for (int i = 0; i < file.Fields.Count; i++)
                if (file.Fields[i].Kind == PropKind.Material && file.Fields[i].Elements.Count > 0)
                    return file.Fields[i];
            return null;
        }

        /// <summary>Decodes the file's material, or returns null with a reason.</summary>
        public static PropMaterial ReadMaterial(PropFile file, out PropField field, out string why)
        {
            field = FindMaterialField(file);
            if (field == null) { why = "this file has no material"; return null; }
            return PropMaterial.TryParse(field.Elements[0].Value, out why);
        }

        /// <summary>
        /// Keys whose values belong to the material: everything the block references, plus
        /// properties named "Material - ..." (when names are available).
        /// </summary>
        public static HashSet<ulong> OwnedKeys(PropFile file, PropMaterial material, Func<ulong, string> keyName)
        {
            HashSet<ulong> keys = material != null ? material.ReferencedKeys() : new HashSet<ulong>();
            if (keyName == null || file == null) return keys;
            for (int i = 0; i < file.Fields.Count; i++)
            {
                PropField f = file.Fields[i];
                if (f.Kind == PropKind.Material) continue;
                for (int j = 0; j < f.Elements.Count; j++)
                {
                    string n = keyName(f.Elements[j].KeyHash);
                    if (n != null && n.StartsWith(MaterialKeyPrefix, StringComparison.Ordinal))
                        keys.Add(f.Elements[j].KeyHash);
                }
            }
            return keys;
        }

        /// <summary>Finds the ordinary property value stored under a key, or null.</summary>
        public static PropElement FindValue(PropFile file, ulong key, out PropField owner)
        {
            owner = null;
            if (file == null) return null;
            for (int i = 0; i < file.Fields.Count; i++)
            {
                PropField f = file.Fields[i];
                if (f.Kind == PropKind.Material) continue;
                for (int j = 0; j < f.Elements.Count; j++)
                    if (f.Elements[j].KeyHash == key) { owner = f; return f.Elements[j]; }
            }
            return null;
        }

        /// <summary>
        /// Replaces the target's material with the donor's: the T3MaterialData block, every value
        /// the material owns, and the header types needed to read them. Values that are not part
        /// of either material are left alone. The target is only changed when the donor's
        /// material decodes.
        /// </summary>
        public static MaterialTransplantResult Transplant(PropFile target, PropFile donor, Func<ulong, string> keyName)
        {
            MaterialTransplantResult res = new MaterialTransplantResult();
            if (target == null || donor == null) { res.Log = "Both files must be loaded."; return res; }

            PropField donorField;
            string why;
            PropMaterial donorMat = ReadMaterial(donor, out donorField, out why);
            if (donorMat == null) { res.Log = "The donor's material cannot be used: " + why + "."; return res; }

            PropField targetField;
            PropMaterial targetMat = ReadMaterial(target, out targetField, out why);
            if (targetField != null && targetMat == null)
            {
                res.Log = "The open file's material does not decode (" + why + "), so it is left untouched.";
                return res;
            }

            HashSet<ulong> owned = OwnedKeys(target, targetMat, keyName);
            owned.UnionWith(OwnedKeys(donor, donorMat, keyName));

            StringBuilder log = new StringBuilder();

            // 1. Drop the values that belong to either material, so nothing stale survives.
            List<PropField> emptied = new List<PropField>();
            for (int i = 0; i < target.Fields.Count; i++)
            {
                PropField f = target.Fields[i];
                if (f.Kind == PropKind.Material) continue;
                int before = f.Elements.Count;
                f.Elements.RemoveAll(e => owned.Contains(e.KeyHash));
                res.ValuesRemoved += before - f.Elements.Count;
                if (before > 0 && f.Elements.Count == 0) emptied.Add(f);
            }
            for (int i = 0; i < emptied.Count; i++) target.DeleteField(emptied[i]);

            // 2. Bring in the donor's values, grouped into properties of the same type.
            HashSet<ulong> movedTypes = new HashSet<ulong>();
            for (int i = 0; i < donor.Fields.Count; i++)
            {
                PropField df = donor.Fields[i];
                if (df.Kind == PropKind.Material) continue;
                for (int j = 0; j < df.Elements.Count; j++)
                {
                    PropElement e = df.Elements[j];
                    if (!owned.Contains(e.KeyHash)) continue;

                    PropField into = FirstFieldOfType(target, df.TypeHash);
                    if (into == null)
                    {
                        into = new PropField();
                        into.TypeHash = df.TypeHash;
                        into.Kind = df.Kind;
                        target.AddField(into);
                    }
                    into.Elements.Add(e.Clone());
                    movedTypes.Add(df.TypeHash);
                    res.ValuesAdded++;
                }
            }

            // 3. Swap the block itself.
            PropField newField = donorField.Clone();
            if (targetField != null)
            {
                target.ReplaceField(targetField, newField);
                res.MaterialReplaced = true;
            }
            else
            {
                target.AddField(newField);
            }

            // 4. List any types the target's header is missing, with the donor's version words.
            movedTypes.Add(PropMaterial.TypeHash);
            for (int i = 0; i < PropMaterial.HeaderTypeNames.Length; i++)
                movedTypes.Add(HashDatabase.Crc64(PropMaterial.HeaderTypeNames[i]));
            foreach (ulong t in movedTypes)
            {
                uint version;
                if (donor.TryGetHeaderVersion(t, out version) && target.AddHeaderType(t, version))
                    res.HeaderTypesAdded++;
            }

            res.Success = true;
            log.AppendLine(res.MaterialReplaced
                ? "Replaced the material block with the donor's."
                : "The open file had no material; added the donor's.");
            log.AppendLine("Removed " + res.ValuesRemoved + " material value(s) from the open file.");
            log.AppendLine("Copied " + res.ValuesAdded + " material value(s) from the donor.");
            if (res.HeaderTypesAdded > 0)
                log.AppendLine("Added " + res.HeaderTypesAdded + " type(s) to the header table.");
            log.AppendLine("Properties unrelated to the material were not touched.");
            res.Log = log.ToString();
            return res;
        }

        static PropField FirstFieldOfType(PropFile file, ulong typeHash)
        {
            for (int i = 0; i < file.Fields.Count; i++)
                if (file.Fields[i].TypeHash == typeHash) return file.Fields[i];
            return null;
        }
    }
}
