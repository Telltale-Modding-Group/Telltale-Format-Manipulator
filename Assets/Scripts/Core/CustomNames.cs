using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TFM.Core
{
    /// <summary>
    /// User-supplied file names kept in a CustomNames folder next to the application.
    ///
    /// Property files store names only as CRC-64 hashes, so a name that is in none of the
    /// tool's dumps cannot be shown again after saving. Any .txt (one name per line) or .bin
    /// (TFMNAMES dump) placed in the folder is read at startup, and names typed into the editor
    /// are appended to custom_names.txt, so they stay readable from then on.
    /// </summary>
    public sealed class CustomNameStore
    {
        public const string FolderName = "CustomNames";
        public const string DefaultFileName = "custom_names.txt";

        /// <summary>Longest name accepted from the editor; real Telltale names are far shorter.</summary>
        public const int MaxNameLength = 260;

        public string Folder { get; private set; }
        public string DefaultFile { get { return Path.Combine(Folder, DefaultFileName); } }

        /// <summary>Files read by the last Load call.</summary>
        public int FilesRead { get; private set; }

        public CustomNameStore(string folder)
        {
            Folder = folder ?? "";
        }

        /// <summary>
        /// Creates the folder and an empty custom_names.txt when either is missing. Returns null
        /// on success, else the reason (e.g. the application folder is read-only).
        /// </summary>
        public string EnsureExists()
        {
            try
            {
                if (!Directory.Exists(Folder)) Directory.CreateDirectory(Folder);
                if (!File.Exists(DefaultFile)) File.WriteAllText(DefaultFile, "", new UTF8Encoding(false));
                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        /// <summary>Reads every .txt and .bin under the folder, recursively, keyed by CRC-64.</summary>
        public Dictionary<ulong, string> Load()
        {
            Dictionary<ulong, string> all = new Dictionary<ulong, string>();
            FilesRead = 0;
            if (!Directory.Exists(Folder)) return all;

            string[] files;
            try { files = Directory.GetFiles(Folder, "*", SearchOption.AllDirectories); }
            catch { return all; }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < files.Length; i++)
            {
                string ext = Path.GetExtension(files[i]);
                try
                {
                    if (string.Equals(ext, ".bin", StringComparison.OrdinalIgnoreCase))
                    {
                        FileNameDumps.LoadInto(all, File.ReadAllBytes(files[i]));
                        FilesRead++;
                    }
                    else if (string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase))
                    {
                        FileNameDumps.LoadTextInto(all, File.ReadAllText(files[i]));
                        FilesRead++;
                    }
                }
                catch { } // an unreadable file is skipped; the others still load
            }
            return all;
        }

        /// <summary>
        /// Checks a typed name. Names are hashed as lower-case ASCII, so anything outside
        /// printable ASCII would hash differently from what the game expects.
        /// </summary>
        public static bool Validate(string input, out string name, out string why)
        {
            name = (input ?? "").Trim();
            why = null;
            if (name.Length == 0) { why = "Enter a name."; return false; }
            if (name.Length > MaxNameLength) { why = "The name is longer than " + MaxNameLength + " characters."; return false; }
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c < 0x20 || c > 0x7E) { why = "Only plain ASCII characters can be used (found '" + c + "')."; return false; }
            }
            return true;
        }

        /// <summary>
        /// Appends a validated name to custom_names.txt, creating the file if needed, and returns
        /// its hash. Throws when the file cannot be written.
        /// </summary>
        public ulong Append(string name)
        {
            string why;
            string clean;
            if (!Validate(name, out clean, out why)) throw new ArgumentException(why);

            if (!Directory.Exists(Folder)) Directory.CreateDirectory(Folder);
            string path = DefaultFile;

            // Start on a fresh line even if the user edited the file and left no final newline.
            string prefix = "";
            if (File.Exists(path))
            {
                byte[] existing = File.ReadAllBytes(path);
                if (existing.Length > 0 && existing[existing.Length - 1] != (byte)'\n') prefix = "\r\n";
            }
            File.AppendAllText(path, prefix + clean + "\r\n", new UTF8Encoding(false));
            return HashDatabase.Crc64(clean);
        }
    }
}
