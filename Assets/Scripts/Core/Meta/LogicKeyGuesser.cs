using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace TFM.Core.Meta
{
    /// <summary>
    /// Recovers game-logic key names from their CRC64. TWD keys follow "&lt;Area&gt; - &lt;Words&gt;"
    /// ("Drugstore - GlennNeedsHelp", "Drugstore - KennyHit"), with the words in CamelCase or
    /// separated by spaces. Areas come from names already known; words from the dialogue text,
    /// the language file and a list of common verbs. Every hit is exact (a 64-bit hash match).
    /// </summary>
    public static class LogicKeyGuesser
    {
        public static readonly string[] CommonWords =
        {
            "Talked", "Talk", "Asked", "Ask", "Met", "Saved", "Save", "Helped", "Help", "Needs", "Gave", "Give", "Knows", "Know", "Told",
            "Trust", "Trusts", "Likes", "Choice", "Choices", "Convo", "Done", "Finished", "Seen", "Saw", "Looked", "With", "To", "About",
            "Has", "Had", "Is", "Was", "Got", "Found", "Find", "Hit", "Stop", "Stopped", "Back", "From", "Playing", "Story", "Count",
            "Times", "Visited", "First", "Again", "Already", "Promise", "Sided", "Side", "Agree", "Angry", "Scared", "Option", "Topic",
            "Greeted", "Intro", "State", "Phase", "Step", "Progress", "Open", "Opened", "Closed", "Broken", "On", "Off", "Lock", "Locked",
            "Chopped", "Remaining", "Used", "Took", "Taken", "Picked", "Up", "Gone", "Left", "Dead", "Alive", "Bitten", "Killed", "Shot",
            "Ate", "Fed", "Food", "Water", "Mode", "Combat", "Ready", "Active", "Started", "Complete", "Completed", "Fail", "Failed",
        };

        static readonly Regex Word = new Regex("[A-Z]?[a-z]{2,14}|[A-Z]{2,}(?=[A-Z][a-z]|$)");

        /// <summary>Splits text and CamelCase names into capitalized words.</summary>
        public static void AddWords(HashSet<string> into, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (Match m in Word.Matches(text))
            {
                string w = m.Value;
                if (w.Length < 2) continue;
                // "KENNY" (speaker tags) -> "Kenny"; "needs" -> "Needs". Hashes ignore case, but the
                // saved name should read the way the game's authors wrote it.
                bool allCaps = w.ToUpperInvariant() == w;
                string rest = allCaps ? w.Substring(1).ToLowerInvariant() : w.Substring(1);
                into.Add(char.ToUpperInvariant(w[0]) + rest);
            }
        }

        /// <summary>"Drugstore - GlennNeedsHelp" -> area "Drugstore".</summary>
        public static string AreaOf(string keyName)
        {
            int i = keyName.IndexOf(" - ", StringComparison.Ordinal);
            return i > 0 ? keyName.Substring(0, i) : null;
        }

        /// <summary>
        /// Tries area + " - " + one or two words (CamelCase or space-joined), and area + subject +
        /// two words. Returns hash -> name for every target that matched.
        /// </summary>
        public static Dictionary<ulong, string> Guess(ICollection<ulong> targets, ICollection<string> areas,
                                                      ICollection<string> words, ICollection<string> subjects)
        {
            Dictionary<ulong, string> hits = new Dictionary<ulong, string>();
            HashSet<ulong> want = new HashSet<ulong>(targets);
            if (want.Count == 0) return hits;
            List<string> w = new List<string>(words);
            foreach (string area in areas)
            {
                if (want.Count == 0) break;
                ulong pa = HashDatabase.Crc64(area + " - ");
                for (int i = 0; i < w.Count && want.Count > 0; i++)
                {
                    ulong h1 = HashDatabase.Crc64Continue(pa, w[i]);
                    Check(want, hits, h1, area + " - " + w[i]);
                    ulong h1s = HashDatabase.Crc64Continue(h1, " ");
                    for (int j = 0; j < w.Count; j++)
                    {
                        Check(want, hits, HashDatabase.Crc64Continue(h1, w[j]), area + " - " + w[i] + w[j]);
                        Check(want, hits, HashDatabase.Crc64Continue(h1s, w[j]), area + " - " + w[i] + " " + w[j]);
                    }
                }
                foreach (string subj in subjects)
                {
                    ulong h0 = HashDatabase.Crc64Continue(pa, subj);
                    for (int i = 0; i < w.Count && want.Count > 0; i++)
                    {
                        ulong h1 = HashDatabase.Crc64Continue(h0, w[i]);
                        for (int j = 0; j < w.Count; j++)
                            Check(want, hits, HashDatabase.Crc64Continue(h1, w[j]), area + " - " + subj + w[i] + w[j]);
                    }
                }
            }
            return hits;
        }

        static void Check(HashSet<ulong> want, Dictionary<ulong, string> hits, ulong h, string name)
        {
            if (!want.Contains(h)) return;
            want.Remove(h);
            hits[h] = name;
        }

        /// <summary>Every key tested or set by a rule anywhere in the dialogue.</summary>
        public static List<ulong> RuleKeys(DlgDocument doc)
        {
            HashSet<ulong> seen = new HashSet<ulong>();
            List<ulong> list = new List<ulong>();
            doc.Root.Walk(v =>
            {
                if (v.Type != "LogicGroup") return;
                MetaValue items = v["mItems"];
                if (items == null || items.Kind != MetaKind.Map) return;
                for (int i = 0; i < items.Items.Count; i++)
                    foreach (MetaValue g in MetaProps.Groups(items.Items[i]["Baseclass_PropertySet"]))
                        foreach (MetaValue k in g.Keys) if (seen.Add(k.Bits)) list.Add(k.Bits);
            });
            return list;
        }
    }
}
