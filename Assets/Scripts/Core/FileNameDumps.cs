using System;
using System.Collections.Generic;
using System.IO;

namespace TFM.Core
{
    /// <summary>
    /// Loads the name dumps written by the Archives module (Dump Binary: "TFMNAMES", u32 version,
    /// u32 count, then u64 hash, u16 length, name bytes) and merges every dump under a folder into
    /// one lookup table. Each dump holds one archive's file names; the union is what props need.
    /// </summary>
    public static class FileNameDumps
    {
        const string Magic = "TFMNAMES";

        /// <summary>Reads one dump into <paramref name="into"/>. Returns how many entries were new.</summary>
        public static int LoadInto(Dictionary<ulong, string> into, byte[] data)
        {
            if (into == null || data == null || data.Length < 16) return 0;
            char[] m = new char[8];
            for (int i = 0; i < 8; i++) m[i] = (char)data[i];
            if (new string(m) != Magic) return 0;

            uint count = BitConverter.ToUInt32(data, 12);
            int p = 16, added = 0;
            for (uint i = 0; i < count; i++)
            {
                if (p + 10 > data.Length) break;
                ulong hash = BitConverter.ToUInt64(data, p);
                int len = BitConverter.ToUInt16(data, p + 8);
                p += 10;
                if (p + len > data.Length) break;

                char[] chars = new char[len];
                for (int k = 0; k < len; k++) chars[k] = (char)data[p + k];
                p += len;

                if (len == 0 || into.ContainsKey(hash)) continue;
                into[hash] = new string(chars);
                added++;
            }
            return added;
        }

        /// <summary>
        /// Reads every name dump under the first of <paramref name="folders"/> that exists and
        /// contains dumps. Files that are not dumps are skipped.
        /// </summary>
        public static NameTable LoadFolderAt(string[] folders)
        {
            if (folders == null) return NameTable.Empty;
            for (int i = 0; i < folders.Length; i++)
            {
                NameTable t = LoadFolder(folders[i]);
                if (t.Count > 0) return t;
            }
            return NameTable.Empty;
        }

        /// <summary>
        /// Reads every name dump under <paramref name="folder"/>, recursively. Files that are not
        /// dumps are skipped. Returns a table keyed by the CRC-64 stored in each dump.
        /// </summary>
        public static NameTable LoadFolder(string folder)
        {
            NameTable table = NameTable.Empty;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return table;

            Dictionary<ulong, string> all = new Dictionary<ulong, string>();
            string[] files;
            try { files = Directory.GetFiles(folder, "*.bin", SearchOption.AllDirectories); }
            catch { return table; }

            for (int i = 0; i < files.Length; i++)
            {
                try { LoadInto(all, File.ReadAllBytes(files[i])); }
                catch { } // a dump that cannot be read is skipped; the rest still load
            }

            if (all.Count == 0) return table;
            return NameTable.FromDictionary(all, folder);
        }
    }
}
