using System;
using System.Collections.Generic;
using System.IO;

namespace TFM.Core.Meta
{
    /// <summary>
    /// A .landb language database: dialogue lines keyed by id. Each LanguageRes holds the speaker
    /// (mPrefix) and the text (mText, with {emotion} tags) in mResolvedLocalData.
    /// </summary>
    public sealed class LanguageDb
    {
        public readonly MetaFile File;
        readonly Dictionary<uint, MetaValue> _byId = new Dictionary<uint, MetaValue>();
        public bool Dirty;

        public LanguageDb(MetaFile file)
        {
            File = file;
            Index();
        }

        public static LanguageDb Load(string path) { return new LanguageDb(MetaFile.Load(path, "LanguageDB")); }

        public string Path { get { return File.SourcePath; } }

        public int Count { get { return _byId.Count; } }

        MetaValue Map { get { return File.Root["mLanguageResources"]; } }

        void Index()
        {
            _byId.Clear();
            MetaValue m = Map;
            if (m == null || m.Kind != MetaKind.Map) return;
            for (int i = 0; i < m.Items.Count; i++)
            {
                uint id = (uint)m.Keys[i].Bits;
                if (!_byId.ContainsKey(id)) _byId[id] = m.Items[i];
                MetaValue alias = m.Items[i]["mIDAlias"];
                // Lines can be referenced through an alias id as well.
                if (alias != null && alias.Bits != 0 && !_byId.ContainsKey((uint)alias.Bits)) _byId[(uint)alias.Bits] = m.Items[i];
            }
        }

        public bool Has(uint id) { return _byId.ContainsKey(id); }

        /// <summary>Every line's speaker and text (for word lists and searches).</summary>
        public IEnumerable<string> AllText()
        {
            foreach (MetaValue res in _byId.Values)
            {
                MetaValue l = Local(res);
                if (l == null) continue;
                if (l["mPrefix"] != null) yield return l["mPrefix"].Text;
                if (l["mText"] != null) yield return l["mText"].Text;
            }
        }

        public MetaValue Line(uint id)
        {
            MetaValue v;
            return _byId.TryGetValue(id, out v) ? v : null;
        }

        static MetaValue Local(MetaValue res) { return res != null ? res["mResolvedLocalData"] : null; }

        public string Speaker(uint id)
        {
            MetaValue l = Local(Line(id));
            return l != null && l["mPrefix"] != null ? l["mPrefix"].Text : null;
        }

        public string Text(uint id)
        {
            MetaValue l = Local(Line(id));
            return l != null && l["mText"] != null ? l["mText"].Text : null;
        }

        /// <summary>Changes a line's text. Returns false when the id is not in this database.</summary>
        public bool SetText(uint id, string text)
        {
            MetaValue l = Local(Line(id));
            if (l == null || l["mText"] == null) return false;
            if (l["mText"].Text == text) return true;
            l["mText"].Text = text;
            Dirty = true;
            return true;
        }

        public bool SetSpeaker(uint id, string speaker)
        {
            MetaValue l = Local(Line(id));
            if (l == null || l["mPrefix"] == null) return false;
            if (l["mPrefix"].Text == speaker) return true;
            l["mPrefix"].Text = speaker;
            Dirty = true;
            return true;
        }

        /// <summary>The voice file handle of a line (CRC64 of the .wav name), or 0.</summary>
        public ulong VoiceHash(uint id)
        {
            MetaValue r = Line(id);
            MetaValue v = r != null ? r["mhVoiceData"] : null;
            return v != null ? v.Bits : 0;
        }

        public void Save()
        {
            File.Save(File.SourcePath);
            Dirty = false;
        }

        /// <summary>
        /// Language files that belong to a dialogue: "&lt;dlog name&gt;_&lt;language&gt;.landb" in each
        /// folder, English first. Returns existing paths only.
        /// </summary>
        public static List<string> CandidatesFor(string dlogPath, IList<string> extraFolders)
        {
            List<string> found = new List<string>();
            if (string.IsNullOrEmpty(dlogPath)) return found;
            string stem = System.IO.Path.GetFileNameWithoutExtension(dlogPath);
            List<string> folders = new List<string>();
            string dir = System.IO.Path.GetDirectoryName(dlogPath);
            if (!string.IsNullOrEmpty(dir)) folders.Add(dir);
            if (extraFolders != null) for (int i = 0; i < extraFolders.Count; i++) if (!string.IsNullOrEmpty(extraFolders[i])) folders.Add(extraFolders[i]);
            for (int f = 0; f < folders.Count; f++)
            {
                if (!Directory.Exists(folders[f])) continue;
                string english = System.IO.Path.Combine(folders[f], stem + "_english.landb");
                if (System.IO.File.Exists(english) && !Contains(found, english)) found.Add(english);
                string[] any;
                try { any = Directory.GetFiles(folders[f], stem + "_*.landb"); }
                catch { continue; }
                for (int i = 0; i < any.Length; i++) if (!Contains(found, any[i])) found.Add(any[i]);
            }
            return found;
        }

        static bool Contains(List<string> list, string p)
        {
            for (int i = 0; i < list.Count; i++) if (string.Equals(list[i], p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
