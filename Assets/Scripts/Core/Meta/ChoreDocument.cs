using System;
using System.Collections.Generic;

namespace TFM.Core.Meta
{
    /// <summary>
    /// A .chore file: agents that each own a list of resources (animations, sounds, nested chores,
    /// dialogue lines, or keyframed agent properties). Resource timing is a list of blocks on the
    /// chore's timeline. Wraps the decoded MetaFile tree; every edit goes straight into that tree.
    /// </summary>
    public sealed class ChoreDocument
    {
        public readonly MetaFile File;

        public ChoreDocument(MetaFile file)
        {
            if (file == null || file.Root == null || file.RootType != "Chore") throw new ArgumentException("Not a chore file.");
            File = file;
        }

        public static ChoreDocument Load(string path) { return new ChoreDocument(MetaFile.Load(path, "Chore")); }

        public static ChoreDocument Parse(byte[] data) { return new ChoreDocument(MetaFile.Parse(data, "Chore", MetaLayouts.Current)); }

        public MetaValue Root { get { return File.Root; } }

        public List<MetaValue> Resources { get { return Root["#resources"].Items; } }

        public List<MetaValue> Agents { get { return Root["#agents"].Items; } }

        public string Name
        {
            get { MetaValue v = Root["mName"]; return v != null ? v.Text : ""; }
            set { MetaValue v = Root["mName"]; if (v != null) v.Text = value; }
        }

        public float Length
        {
            get { MetaValue v = Root["mLength"]; return v != null ? v.AsFloat : 0f; }
            set { MetaValue v = Root["mLength"]; if (v != null) v.AsFloat = value; }
        }

        public string SceneFile
        {
            get { MetaValue v = Root["mChoreSceneFile"]; return v != null ? v.Text : ""; }
            set { MetaValue v = Root["mChoreSceneFile"]; if (v != null) v.Text = value; }
        }

        /// <summary>Plain-text dependency names (for example the .dlog that plays this chore).</summary>
        public List<string> Dependencies()
        {
            List<string> list = new List<string>();
            MetaValue dl = Root["mDependencies"];
            MetaValue names = dl != null ? dl["#names"] : null;
            if (names != null && names.Items != null) for (int i = 0; i < names.Items.Count; i++) list.Add(names.Items[i].Text);
            return list;
        }

        // ============================================================ resources
        public static ulong ResName(MetaValue res)
        {
            MetaValue v = res["mResName"];
            return v != null ? v.Bits : 0;
        }

        /// <summary>The file the resource plays (its CRC64 name), or 0 for property keyframes.</summary>
        public static ulong ObjectName(MetaValue res)
        {
            MetaValue h = res["mhObject"];
            return h != null && h.Kind == MetaKind.HandleBase ? h.Bits : 0;
        }

        /// <summary>CRC64 of the resource's object type name (Animation, Chore, SoundData...), or 0.</summary>
        public static ulong ObjectType(MetaValue res)
        {
            MetaValue h = res["mhObject"];
            return h != null && h.Kind == MetaKind.HandleBase && h.Bits != 0 ? h.Bits2 : 0;
        }

        /// <summary>Readable resource kind: the object type, "Embedded ..." or "Properties" for agent keyframes.</summary>
        public static string KindName(MetaValue res)
        {
            MetaValue emb = res["#embedded"];
            if (emb != null) return "Embedded " + emb.Type;
            ulong t = ObjectType(res);
            if (t == 0) return "Properties";
            string n = MetaLayouts.Current.TypeName(t);
            return n ?? ("type 0x" + t.ToString("X16"));
        }

        /// <summary>The usual file extension for a resource kind, used to filter name pickers.</summary>
        public static string ExtensionForKind(string kind)
        {
            switch (kind)
            {
                case "Animation": return ".anm";
                case "Chore": return ".chore";
                case "SoundData": return ".wav";
                case "LanguageRes": return ".lang";
                case "PropertySet": return ".prop";
                case "Scene": return ".scene";
                case "D3DMesh": return ".d3dmesh";
                case "T3Texture": return ".d3dtx";
                case "Dlg": return ".dlog";
                case "PhonemeTable": return ".ptable";
                default: return null;
            }
        }

        public static bool IsFileResource(MetaValue res) { return ObjectName(res) != 0; }

        /// <summary>
        /// Points a file resource at another file. The resource name and the object handle hold the
        /// same CRC64 in every game file, so both change together. The object type stays unless a new one is given.
        /// </summary>
        public static void SetReference(MetaValue res, ulong nameHash, ulong typeHash)
        {
            MetaValue rn = res["mResName"];
            if (rn != null) rn.Bits = nameHash;
            MetaValue h = res["mhObject"];
            if (h != null && h.Kind == MetaKind.HandleBase)
            {
                h.Bits = nameHash;
                if (typeHash != 0) h.Bits2 = typeHash;
            }
        }

        /// <summary>Renames a property-keyframe resource (its name is not a file).</summary>
        public static void SetResName(MetaValue res, ulong nameHash)
        {
            MetaValue rn = res["mResName"];
            if (rn != null) rn.Bits = nameHash;
        }

        public static float ResLength(MetaValue res)
        {
            MetaValue v = res["mResLength"];
            return v != null ? v.AsFloat : 0f;
        }

        public static bool GetBool(MetaValue res, string field)
        {
            MetaValue v = res[field];
            return v != null && v.AsBool;
        }

        public static void SetBool(MetaValue res, string field, bool value)
        {
            MetaValue v = res[field];
            if (v != null) v.AsBool = value;
        }

        public static List<MetaValue> Blocks(MetaValue res)
        {
            MetaValue b = res["mBlocks"];
            return b != null && b.Kind == MetaKind.Array ? b.Items : new List<MetaValue>();
        }

        public static float BlockStart(MetaValue block) { return block["mStartTime"].AsFloat; }

        public static float BlockEnd(MetaValue block) { return block["mEndTime"].AsFloat; }

        public static void SetBlock(MetaValue block, float start, float end)
        {
            block["mStartTime"].AsFloat = start;
            block["mEndTime"].AsFloat = end;
        }

        /// <summary>Adds a block to a resource. Returns the new block.</summary>
        public MetaValue AddBlock(MetaValue res, float start, float end)
        {
            MetaValue b = res["mBlocks"];
            if (b == null || b.Kind != MetaKind.Array) throw new InvalidOperationException("This resource has no block list.");
            MetaValue nb = File.CreateDefault("ChoreResource::Block");
            nb["mStartTime"].AsFloat = start;
            nb["mEndTime"].AsFloat = end;
            nb["mScale"].AsFloat = 1f;
            b.Items.Add(nb);
            return nb;
        }

        /// <summary>The keyframed values inside the resource's control animation.</summary>
        public static List<MetaValue> ControlValues(MetaValue res)
        {
            MetaValue a = res["mControlAnimation"];
            MetaValue vals = a != null ? a["#values"] : null;
            MetaValue list = vals != null ? vals["values"] : null;
            return list != null ? list.Items : new List<MetaValue>();
        }

        /// <summary>Times of every keyframe sample in the resource's control animation.</summary>
        public static List<float> KeyTimes(MetaValue res)
        {
            List<float> times = new List<float>();
            List<MetaValue> vals = ControlValues(res);
            for (int i = 0; i < vals.Count; i++)
            {
                MetaValue s = vals[i]["mSamples"];
                if (s == null || s.Items == null) continue;
                for (int k = 0; k < s.Items.Count; k++)
                {
                    MetaValue t = s.Items[k]["mTime"];
                    if (t != null) times.Add(t.AsFloat);
                }
            }
            return times;
        }

        // ============================================================ agents
        public static string AgentName(MetaValue agent)
        {
            MetaValue v = agent["mAgentName"];
            return v != null ? v.Text : "";
        }

        public static List<MetaValue> AgentResourceIndexValues(MetaValue agent)
        {
            MetaValue r = agent["mResources"];
            return r != null && r.Items != null ? r.Items : new List<MetaValue>();
        }

        public static List<int> AgentResourceIndices(MetaValue agent)
        {
            List<int> list = new List<int>();
            List<MetaValue> v = AgentResourceIndexValues(agent);
            for (int i = 0; i < v.Count; i++) list.Add(v[i].AsInt);
            return list;
        }

        /// <summary>Indices of the agents that own resource <paramref name="resourceIndex"/>.</summary>
        public List<int> OwnersOf(int resourceIndex)
        {
            List<int> owners = new List<int>();
            for (int a = 0; a < Agents.Count; a++)
                if (AgentResourceIndices(Agents[a]).Contains(resourceIndex)) owners.Add(a);
            return owners;
        }

        public static readonly string[] AgentResourceSuffixes = { "", " Blocking", " POI Blocking", " path blocking" };

        /// <summary>
        /// Renames an agent. Property resources named after the agent ("Name", "Name Blocking", ...)
        /// store a hash of that name, so they are renamed with it. Returns how many resources changed.
        /// </summary>
        public int RenameAgent(MetaValue agent, string newName)
        {
            string old = AgentName(agent);
            agent["mAgentName"].Text = newName;
            int changed = 0;
            List<int> idx = AgentResourceIndices(agent);
            for (int i = 0; i < idx.Count; i++)
            {
                if (idx[i] < 0 || idx[i] >= Resources.Count) continue;
                MetaValue res = Resources[idx[i]];
                if (IsFileResource(res)) continue;
                ulong h = ResName(res);
                for (int s = 0; s < AgentResourceSuffixes.Length; s++)
                {
                    if (HashDatabase.Crc64(old + AgentResourceSuffixes[s]) != h) continue;
                    SetResName(res, HashDatabase.Crc64(newName + AgentResourceSuffixes[s]));
                    changed++;
                    break;
                }
            }
            return changed;
        }

        /// <summary>Removes a resource and renumbers every agent's resource indices.</summary>
        public void DeleteResource(int index)
        {
            if (index < 0 || index >= Resources.Count) return;
            Resources.RemoveAt(index);
            for (int a = 0; a < Agents.Count; a++)
            {
                List<MetaValue> v = AgentResourceIndexValues(Agents[a]);
                for (int i = v.Count - 1; i >= 0; i--)
                {
                    int x = v[i].AsInt;
                    if (x == index) v.RemoveAt(i);
                    else if (x > index) v[i].AsInt = x - 1;
                }
            }
        }

        /// <summary>Removes an agent; its resources go too unless another agent also uses them.</summary>
        public void DeleteAgent(int agentIndex)
        {
            if (agentIndex < 0 || agentIndex >= Agents.Count) return;
            List<int> owned = AgentResourceIndices(Agents[agentIndex]);
            Agents.RemoveAt(agentIndex);
            owned.Sort();
            for (int i = owned.Count - 1; i >= 0; i--)
            {
                if (OwnersOf(owned[i]).Count == 0) DeleteResource(owned[i]);
            }
        }

        public int FindAgent(string name)
        {
            for (int i = 0; i < Agents.Count; i++)
                if (string.Equals(AgentName(Agents[i]), name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        // ============================================================ transplant
        public sealed class TransplantResult
        {
            public int AgentIndex = -1;
            public int ResourcesAdded;
            public readonly List<string> HeaderTypesAdded = new List<string>();
            public readonly List<string> Warnings = new List<string>();
        }

        /// <summary>
        /// Copies an agent and all of its resources from another chore into this one. Resource
        /// indices are remapped; resources shared by several donor agents are copied once per call.
        /// Header types the copied data needs are added with the donor's versions.
        /// </summary>
        public TransplantResult TransplantAgent(ChoreDocument donor, int donorAgentIndex, string newName)
        {
            TransplantResult r = new TransplantResult();
            MetaValue src = donor.Agents[donorAgentIndex];
            string name = string.IsNullOrEmpty(newName) ? AgentName(src) : newName;
            if (FindAgent(name) >= 0)
                r.Warnings.Add("This chore already has an agent named " + name + "; the game may treat them as one agent.");

            MetaValue copy = src.Clone();
            List<MetaValue> idx = AgentResourceIndexValues(copy);
            for (int i = 0; i < idx.Count; i++)
            {
                int di = idx[i].AsInt;
                if (di < 0 || di >= donor.Resources.Count) continue;
                MetaValue res = donor.Resources[di].Clone();
                Resources.Add(res);
                idx[i].AsInt = Resources.Count - 1;
                r.ResourcesAdded++;
            }
            Agents.Add(copy);
            r.AgentIndex = Agents.Count - 1;
            if (name != AgentName(src)) RenameAgent(copy, name);

            // Header types: everything the donor listed that this file lacks, with the donor's version.
            for (int i = 0; i < donor.File.Types.Count; i++)
            {
                MetaHeaderType t = donor.File.Types[i];
                if (t.Name == null || File.HasHeaderType(t.Name)) continue;
                File.Types.Add(new MetaHeaderType { Hash = t.Hash, Crc = t.Crc, Name = t.Name });
                r.HeaderTypesAdded.Add(t.Name);
            }

            r.Warnings.Add("The agent must exist in this chore's scene (" + SceneFile + ") for the game to play it.");
            float maxEnd = 0f;
            for (int i = 0; i < idx.Count; i++)
            {
                int ri = idx[i].AsInt;
                if (ri < 0 || ri >= Resources.Count) continue;
                List<MetaValue> bl = Blocks(Resources[ri]);
                for (int b = 0; b < bl.Count; b++) maxEnd = Math.Max(maxEnd, BlockEnd(bl[b]));
            }
            if (maxEnd > Length + 0.001f)
                r.Warnings.Add("Copied blocks run to " + maxEnd.ToString("0.###") + " s, past this chore's length of "
                               + Length.ToString("0.###") + " s.");
            return r;
        }

        /// <summary>Copies one resource from another chore and gives it to <paramref name="agentIndex"/>.</summary>
        public int TransplantResource(ChoreDocument donor, int donorResourceIndex, int agentIndex)
        {
            MetaValue res = donor.Resources[donorResourceIndex].Clone();
            Resources.Add(res);
            int ni = Resources.Count - 1;
            MetaValue idx = Agents[agentIndex]["mResources"];
            idx.Items.Add(MetaValue.NewInt("int32", ni));
            for (int i = 0; i < donor.File.Types.Count; i++)
            {
                MetaHeaderType t = donor.File.Types[i];
                if (t.Name == null || File.HasHeaderType(t.Name)) continue;
                File.Types.Add(new MetaHeaderType { Hash = t.Hash, Crc = t.Crc, Name = t.Name });
            }
            return ni;
        }

        /// <summary>Earliest and latest time on the timeline: 0..length, widened by any block outside it.</summary>
        public void TimelineRange(out float start, out float end)
        {
            start = 0f;
            end = Math.Max(Length, 0.001f);
            for (int i = 0; i < Resources.Count; i++)
            {
                List<MetaValue> bl = Blocks(Resources[i]);
                for (int b = 0; b < bl.Count; b++)
                {
                    start = Math.Min(start, BlockStart(bl[b]));
                    end = Math.Max(end, BlockEnd(bl[b]));
                }
            }
        }

        /// <summary>Every String value in the file, used to guess resource names.</summary>
        public HashSet<string> AllStrings()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
            Root.Walk(v => { if (v.Kind == MetaKind.String && v.Bytes != null && v.Bytes.Length > 0 && v.Bytes.Length < 256) set.Add(v.Text); });
            return set;
        }
    }
}
