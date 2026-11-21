using System;
using System.Collections.Generic;
using System.Globalization;

namespace TFM.Core.Meta
{
    /// <summary>
    /// Turns chore resource hashes back into names. Chores store only CRC64 names, and the names
    /// follow several patterns, so each hash is tried against (in order):
    ///   1. file names from the archive dumps and the CustomNames folder (.anm, .chore, .wav, ...);
    ///   2. "&lt;id&gt;.lang" for dialogue-line resources, searching near the IDs that did resolve;
    ///   3. "Procedural Look At" for embedded look-at resources;
    ///   4. agent names and every string in the file, alone and with " Blocking", " POI Blocking"
    ///      or " path blocking" (agent property and transform resources), including neighbouring
    ///      numbered or lettered names, since copied agents keep the name they were created with.
    /// Anything left shows as a hash and can be named by the user.
    /// </summary>
    public sealed class ChoreNameResolver
    {
        public enum Source { None, FileName, LanguageLine, Builtin, AgentName, FileString, Guess }

        readonly Func<ulong, string> _files;
        readonly Dictionary<ulong, string> _local = new Dictionary<ulong, string>();
        readonly Dictionary<ulong, Source> _localSource = new Dictionary<ulong, Source>();

        static Dictionary<ulong, string> _langNames;
        static int _langSeedCount = -1;

        /// <summary>
        /// Indexes "&lt;n&gt;.lang" for every numeric file stem in the name dumps (voice files are named
        /// after their line id). Cheap to call repeatedly: it only rebuilds when the list size changes.
        /// </summary>
        public static void SeedLanguageNames(IList<string> fileNames)
        {
            if (fileNames == null || fileNames.Count == _langSeedCount) return;
            Dictionary<ulong, string> d = new Dictionary<ulong, string>();
            for (int i = 0; i < fileNames.Count; i++)
            {
                string n = fileNames[i];
                int dot = n.LastIndexOf('.');
                if (dot <= 0) continue;
                bool digits = true;
                for (int k = 0; k < dot; k++) if (!char.IsDigit(n[k])) { digits = false; break; }
                if (!digits) continue;
                string lang = n.Substring(0, dot) + ".lang";
                ulong h = HashDatabase.Crc64(lang);
                if (!d.ContainsKey(h)) d[h] = lang;
            }
            _langNames = d;
            _langSeedCount = fileNames.Count;

            // Distinct stems (names without extension) for the last-resort pass.
            HashSet<string> stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < fileNames.Count; i++)
            {
                string n = fileNames[i];
                int dot = n.LastIndexOf('.');
                stems.Add(dot > 0 ? n.Substring(0, dot) : n);
            }
            _stems = new List<string>(stems);
        }

        static List<string> _stems;

        static readonly string[] StemSuffixes = { "", " Blocking", " POI Blocking", " path blocking", ".chore", ".anm", ".wav", ".prop", ".scene" };

        /// <summary>
        /// Last resort: every file stem in the dumps with each agent suffix and common extension
        /// (a resource can be named after a file that is not referenced elsewhere, or after an
        /// agent that only exists in another scene). Uses incremental CRCs, so it stays fast.
        /// </summary>
        void StemPass(HashSet<ulong> wanted)
        {
            if (_stems == null) return;
            HashSet<ulong> open = new HashSet<ulong>();
            foreach (ulong h in wanted) if (!_local.ContainsKey(h) && FileOrLang(h) == null) open.Add(h);
            if (open.Count == 0) return;
            for (int i = 0; i < _stems.Count && open.Count > 0; i++)
            {
                ulong c = HashDatabase.Crc64(_stems[i]);
                for (int k = 0; k < StemSuffixes.Length; k++)
                {
                    ulong h = k == 0 ? c : HashDatabase.Crc64Continue(c, StemSuffixes[k]);
                    if (!open.Remove(h)) continue;
                    _local[h] = _stems[i] + StemSuffixes[k];
                    _localSource[h] = Source.Guess;
                }
            }
        }

        string FileOrLang(ulong h)
        {
            string n = _files != null ? _files(h) : null;
            if (n == null && _langNames != null) _langNames.TryGetValue(h, out n);
            return n;
        }

        public ChoreNameResolver(ChoreDocument doc, Func<ulong, string> fileNames)
        {
            _files = fileNames;
            Build(doc);
        }

        public string Resolve(ulong hash)
        {
            Source s;
            return Resolve(hash, out s);
        }

        public string Resolve(ulong hash, out Source source)
        {
            source = Source.None;
            if (hash == 0) return null;
            string n;
            if (_local.TryGetValue(hash, out n))
            {
                source = _localSource[hash];
                return n;
            }
            n = _files != null ? _files(hash) : null;
            if (n != null) { source = Source.FileName; return n; }
            if (_langNames != null && _langNames.TryGetValue(hash, out n)) source = Source.LanguageLine;
            return n;
        }

        public static string SourceLabel(Source s)
        {
            switch (s)
            {
                case Source.FileName: return "archive / custom names";
                case Source.LanguageLine: return "dialogue line id";
                case Source.Builtin: return "engine name";
                case Source.AgentName: return "agent name";
                case Source.FileString: return "text in this file";
                case Source.Guess: return "guessed from a similar agent name";
                default: return "unresolved";
            }
        }

        void Add(string name, Source s)
        {
            ulong h = HashDatabase.Crc64(name);
            if (_local.ContainsKey(h)) return;
            _local[h] = name;
            _localSource[h] = s;
        }

        void Build(ChoreDocument doc)
        {
            Add("Procedural Look At", Source.Builtin);

            HashSet<ulong> wanted = new HashSet<ulong>();
            List<ulong> langWanted = new List<ulong>();
            List<long> langKnown = new List<long>();
            for (int i = 0; i < doc.Resources.Count; i++)
            {
                MetaValue r = doc.Resources[i];
                ulong h = ChoreDocument.ResName(r);
                if (h == 0) continue;
                string kind = ChoreDocument.KindName(r);
                string f = FileOrLang(h);
                if (kind == "LanguageRes")
                {
                    if (f != null) { long id; if (TryLangId(f, out id)) langKnown.Add(id); }
                    else langWanted.Add(h);
                }
                if (f == null) wanted.Add(h);
            }

            // Agent names and file strings, with the suffixes the engine appends.
            List<string> agents = new List<string>();
            for (int a = 0; a < doc.Agents.Count; a++) agents.Add(ChoreDocument.AgentName(doc.Agents[a]));
            for (int a = 0; a < agents.Count; a++)
                for (int s = 0; s < ChoreDocument.AgentResourceSuffixes.Length; s++)
                    Add(agents[a] + ChoreDocument.AgentResourceSuffixes[s], Source.AgentName);

            foreach (string str in doc.AllStrings())
            {
                // Strings can be "chore.chore:agent" references; try the part after the colon too.
                TryCandidate(str, wanted, Source.FileString);
                int colon = str.LastIndexOf(':');
                if (colon > 0 && colon < str.Length - 1) TryCandidate(str.Substring(colon + 1), wanted, Source.FileString);
            }

            // Copied agents keep the name they were made with: try nearby letters and numbers.
            if (CountUnresolved(wanted) > 0)
                for (int a = 0; a < agents.Count; a++)
                    foreach (string v in Neighbours(agents[a])) TryCandidate(v, wanted, Source.Guess);

            StemPass(wanted);

            // Dialogue lines: "<id>.lang". Lines without a voice file are missing from the dumps,
            // but their IDs sit next to the ones that resolved.
            if (langWanted.Count > 0 && langKnown.Count > 0)
            {
                HashSet<ulong> targets = new HashSet<ulong>(langWanted);
                HashSet<long> tried = new HashSet<long>();
                for (int k = 0; k < langKnown.Count && targets.Count > 0; k++)
                {
                    for (long id = langKnown[k] - 4096; id <= langKnown[k] + 4096 && targets.Count > 0; id++)
                    {
                        if (id < 0 || !tried.Add(id)) continue;
                        string n = id.ToString(CultureInfo.InvariantCulture) + ".lang";
                        ulong h = HashDatabase.Crc64(n);
                        if (!targets.Remove(h)) continue;
                        _local[h] = n;
                        _localSource[h] = Source.LanguageLine;
                    }
                }
            }
        }

        int CountUnresolved(HashSet<ulong> wanted)
        {
            int n = 0;
            foreach (ulong h in wanted) if (!_local.ContainsKey(h)) n++;
            return n;
        }

        void TryCandidate(string baseName, HashSet<ulong> wanted, Source s)
        {
            for (int i = 0; i < ChoreDocument.AgentResourceSuffixes.Length; i++)
            {
                string n = baseName + ChoreDocument.AgentResourceSuffixes[i];
                ulong h = HashDatabase.Crc64(n);
                if (wanted.Contains(h) && !_local.ContainsKey(h))
                {
                    _local[h] = n;
                    _localSource[h] = s;
                }
            }
        }

        /// <summary>"DeadBodyF" -> DeadBodyA..Z; "Zombie2" -> Zombie0..Zombie20, Zombie00..; plus case variants of the last letter.</summary>
        static IEnumerable<string> Neighbours(string name)
        {
            if (string.IsNullOrEmpty(name)) yield break;
            int end = name.Length;
            int ds = end;
            while (ds > 0 && char.IsDigit(name[ds - 1])) ds--;
            if (ds < end)
            {
                string stem = name.Substring(0, ds);
                for (int i = 0; i <= 20; i++)
                {
                    yield return stem + i.ToString(CultureInfo.InvariantCulture);
                    yield return stem + i.ToString("00", CultureInfo.InvariantCulture);
                    yield return stem + "_" + i.ToString(CultureInfo.InvariantCulture);
                }
                yield return stem;
            }
            char last = name[end - 1];
            if (char.IsLetter(last))
            {
                string stem = name.Substring(0, end - 1);
                for (char c = 'A'; c <= 'Z'; c++)
                {
                    yield return stem + c;
                    yield return stem + char.ToLowerInvariant(c);
                }
                yield return stem;
            }
            yield return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        static bool TryLangId(string name, out long id)
        {
            id = 0;
            if (!name.EndsWith(".lang", StringComparison.OrdinalIgnoreCase)) return false;
            return long.TryParse(name.Substring(0, name.Length - 5), NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
        }
    }
}
