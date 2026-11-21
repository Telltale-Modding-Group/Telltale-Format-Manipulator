using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace TFM.Core.Meta
{
    /// <summary>
    /// A .dlog dialogue. Folders hold children; each child (and each branch of a Choices,
    /// Conditional, Sequence or Parallel node) starts a chain of nodes joined by mPrev/mNext links.
    /// Every folder, node, child, line and condition has a u64 id; <see cref="NoneId"/> means "no link".
    /// Jumps target nodes by name hash (mJumpToName), optionally in another dialogue.
    /// </summary>
    public sealed class DlgDocument
    {
        /// <summary>The id written for empty links (and for most conditions).</summary>
        public const ulong NoneId = 0x988D0903F713877BUL;

        public readonly MetaFile File;

        public sealed class ChildInfo
        {
            public MetaValue Child;
            /// <summary>The node or folder that owns the child set.</summary>
            public MetaValue Owner;
            public ChildSetInfo Set;
        }

        public sealed class ChildSetInfo
        {
            public string Label;
            public string FieldName;
            public MetaValue Set;
            public List<MetaValue> Children { get { return Set["#items"].Items; } }
        }

        readonly Dictionary<ulong, MetaValue> _nodes = new Dictionary<ulong, MetaValue>();
        readonly Dictionary<ulong, ChildInfo> _children = new Dictionary<ulong, ChildInfo>();
        readonly Dictionary<ulong, List<MetaValue>> _byName = new Dictionary<ulong, List<MetaValue>>();
        readonly Dictionary<MetaValue, MetaValue> _folderOf = new Dictionary<MetaValue, MetaValue>();
        readonly Dictionary<ulong, string> _localNames = new Dictionary<ulong, string>();

        public DlgDocument(MetaFile file)
        {
            if (file == null || file.Root == null || file.RootType != "Dlg") throw new ArgumentException("Not a dialogue file.");
            File = file;
            Reindex();
            CollectNames();
        }

        public static DlgDocument Load(string path) { return new DlgDocument(MetaFile.Load(path, "Dlg")); }

        public MetaValue Root { get { return File.Root; } }

        public List<MetaValue> Folders { get { return Root["#folders"].Items; } }

        public List<MetaValue> Nodes { get { return Root["#nodes"].Items; } }

        public string Name { get { MetaValue v = Root["mName"]; return v != null ? v.Text : ""; } }

        public uint ProjectId { get { MetaValue v = Root["mProjectID"]; return v != null ? (uint)v.Bits : 0; } }

        // ============================================================ ids and links
        public static ulong Id(MetaValue o)
        {
            MetaValue d = o != null ? o.FindField("mDlgObjID") : null;
            MetaValue id = d != null ? d["mID"] : null;
            return id != null ? id.Bits : NoneId;
        }

        public static void SetId(MetaValue o, ulong id)
        {
            MetaValue d = o.FindField("mDlgObjID");
            if (d != null && d["mID"] != null) d["mID"].Bits = id;
        }

        public static ulong LinkId(MetaValue link) { return link == null ? NoneId : Id(link); }

        public static void SetLinkId(MetaValue link, ulong id) { if (link != null) SetId(link, id); }

        public static MetaValue PrevLink(MetaValue node) { return node.FindField("mPrev"); }

        public static MetaValue NextLink(MetaValue node) { return node.FindField("mNext"); }

        /// <summary>The link from a child to the first node of its chain.</summary>
        public static MetaValue ChildLink(MetaValue child) { return child.FindField("mLink"); }

        public static ulong NameHash(MetaValue o)
        {
            MetaValue n = o != null ? o.FindField("mName") : null;
            return n != null && n.Kind == MetaKind.Hash ? n.Bits : 0;
        }

        public static uint NodeFlags(MetaValue node)
        {
            MetaValue f = node.FindField("mFlags");
            return f != null ? f.FlagsValue : 0;
        }

        /// <summary>"Exchange", "Choices", "Logic"... from DlgNodeExchange etc.</summary>
        public static string Kind(MetaValue node)
        {
            string t = node.Type;
            if (t.StartsWith("DlgNode", StringComparison.Ordinal) && t.Length > 7) return t.Substring(7);
            if (t.StartsWith("Dlg", StringComparison.Ordinal) && t.Length > 3) return t.Substring(3);
            return t;
        }

        public void Reindex()
        {
            _nodes.Clear();
            _children.Clear();
            _byName.Clear();
            _folderOf.Clear();
            for (int i = 0; i < Nodes.Count; i++)
            {
                MetaValue n = Nodes[i];
                ulong id = Id(n);
                if (!_nodes.ContainsKey(id)) _nodes[id] = n;
                AddName(n);
                List<ChildSetInfo> sets = ChildSets(n);
                for (int s = 0; s < sets.Count; s++) IndexChildren(n, sets[s]);
            }
            for (int f = 0; f < Folders.Count; f++)
            {
                List<ChildSetInfo> sets = ChildSets(Folders[f]);
                for (int s = 0; s < sets.Count; s++)
                {
                    IndexChildren(Folders[f], sets[s]);
                    List<MetaValue> ch = sets[s].Children;
                    for (int c = 0; c < ch.Count; c++) _folderOf[ch[c]] = Folders[f];
                }
            }
        }

        void IndexChildren(MetaValue owner, ChildSetInfo set)
        {
            List<MetaValue> ch = set.Children;
            for (int c = 0; c < ch.Count; c++)
            {
                ulong id = Id(ch[c]);
                if (!_children.ContainsKey(id)) _children[id] = new ChildInfo { Child = ch[c], Owner = owner, Set = set };
                AddName(ch[c]);
            }
        }

        void AddName(MetaValue o)
        {
            ulong h = NameHash(o);
            if (h == 0) return;
            List<MetaValue> l;
            if (!_byName.TryGetValue(h, out l)) { l = new List<MetaValue>(); _byName[h] = l; }
            l.Add(o);
        }

        public MetaValue NodeById(ulong id)
        {
            MetaValue n;
            return _nodes.TryGetValue(id, out n) ? n : null;
        }

        public ChildInfo ChildById(ulong id)
        {
            ChildInfo c;
            return _children.TryGetValue(id, out c) ? c : null;
        }

        public ChildInfo ChildInfoOf(MetaValue child) { return ChildById(Id(child)); }

        /// <summary>Nodes and children whose mName is <paramref name="nameHash"/> (jump targets).</summary>
        public List<MetaValue> ByName(ulong nameHash)
        {
            List<MetaValue> l;
            return _byName.TryGetValue(nameHash, out l) ? l : new List<MetaValue>();
        }

        public MetaValue FolderOfChild(MetaValue child)
        {
            MetaValue f;
            return _folderOf.TryGetValue(child, out f) ? f : null;
        }

        static string SetLabel(string field)
        {
            switch (field)
            {
                case "mChoices": return "Choices";
                case "mPreChoice": return "Before the choices";
                case "mPostChoice": return "After a choice";
                case "mCases": return "Cases";
                case "mElements": return "Elements";
                case "mPElements": return "Parallel elements";
                case "Baseclass_DlgChildSet": return "Children";
                default: return field;
            }
        }

        /// <summary>The child sets of a node or folder, in file order.</summary>
        public static List<ChildSetInfo> ChildSets(MetaValue owner)
        {
            List<ChildSetInfo> list = new List<ChildSetInfo>();
            if (owner == null || owner.Fields == null) return list;
            CollectSets(owner, null, list, 0);
            return list;
        }

        static void CollectSets(MetaValue v, string topField, List<ChildSetInfo> list, int depth)
        {
            if (v.Fields == null || depth > 6) return;
            for (int i = 0; i < v.Fields.Count; i++)
            {
                MetaField f = v.Fields[i];
                MetaValue fv = f.Value;
                if (fv.Kind != MetaKind.Struct) continue;
                if (fv.Type == "DlgChildSet")
                {
                    string field = topField ?? f.Name;
                    list.Add(new ChildSetInfo { FieldName = field, Label = SetLabel(field), Set = fv });
                    continue;
                }
                // Look inside wrappers (DlgChildSetChoice -> Baseclass_DlgChildSet) and base classes,
                // but not into links, conditions or props.
                if (fv.Type.StartsWith("DlgChildSet", StringComparison.Ordinal) || fv.Type.EndsWith("::DlgChildSetElement", StringComparison.Ordinal))
                    CollectSets(fv, topField ?? f.Name, list, depth + 1);
                else if (f.Name == "Baseclass_DlgNode")
                    CollectSets(fv, topField, list, depth + 1);
            }
        }

        // ============================================================ chains
        /// <summary>The nodes from <paramref name="firstNodeId"/> along mNext, stopping at an empty link, a missing node or a loop.</summary>
        public List<MetaValue> Chain(ulong firstNodeId)
        {
            List<MetaValue> chain = new List<MetaValue>();
            HashSet<ulong> seen = new HashSet<ulong>();
            ulong id = firstNodeId;
            while (id != NoneId && seen.Add(id))
            {
                MetaValue n = NodeById(id);
                if (n == null) break;
                chain.Add(n);
                id = LinkId(NextLink(n));
            }
            return chain;
        }

        public List<MetaValue> ChainOfChild(MetaValue child) { return Chain(LinkId(ChildLink(child))); }

        /// <summary>Walks mPrev back to the child that starts this node's chain, or null.</summary>
        public ChildInfo ChainHead(MetaValue node)
        {
            HashSet<ulong> seen = new HashSet<ulong>();
            MetaValue cur = node;
            while (cur != null && seen.Add(Id(cur)))
            {
                ulong prev = LinkId(PrevLink(cur));
                if (prev == NoneId) return null;
                ChildInfo c = ChildById(prev);
                if (c != null) return c;
                cur = NodeById(prev);
            }
            return null;
        }

        /// <summary>Nodes that no child, node or folder links to (often reached only through jumps).</summary>
        public List<MetaValue> UnlinkedNodes()
        {
            HashSet<ulong> linked = new HashSet<ulong>();
            for (int i = 0; i < Nodes.Count; i++) linked.Add(LinkId(NextLink(Nodes[i])));
            foreach (ChildInfo c in _children.Values) linked.Add(LinkId(ChildLink(c.Child)));
            List<MetaValue> list = new List<MetaValue>();
            for (int i = 0; i < Nodes.Count; i++) if (!linked.Contains(Id(Nodes[i]))) list.Add(Nodes[i]);
            return list;
        }

        // ============================================================ content
        /// <summary>LanguageResProxy ids of the lines a node shows (Exchange lines, Text nodes).</summary>
        public static List<uint> LineIds(MetaValue node)
        {
            List<uint> ids = new List<uint>();
            MetaValue lines = node["#lines"];
            MetaValue map = lines != null ? lines["mLines"] : null;
            if (map != null && map.Kind == MetaKind.Map)
            {
                // Show lines in the order the entry list plays them, then any not listed.
                MetaValue entries = node["mEntries"];
                List<int> order = new List<int>();
                if (entries != null && entries.Items != null)
                    for (int i = 0; i < entries.Items.Count; i++)
                    {
                        MetaValue id = entries.Items[i]["mID"];
                        if (id != null) order.Add(id.AsInt);
                    }
                HashSet<int> used = new HashSet<int>();
                for (int o = 0; o < order.Count; o++)
                    for (int i = 0; i < map.Keys.Count; i++)
                        if (map.Keys[i].AsInt == order[o] && used.Add(i)) ids.Add(ProxyId(map.Items[i]["mLangResProxy"]));
                for (int i = 0; i < map.Keys.Count; i++) if (!used.Contains(i)) ids.Add(ProxyId(map.Items[i]["mLangResProxy"]));
            }
            MetaValue text = node["mLangResProxy"];
            if (text != null) ids.Add(ProxyId(text));
            return ids;
        }

        public static uint ProxyId(MetaValue proxy)
        {
            if (proxy == null) return 0;
            if (proxy.Kind == MetaKind.Int) return (uint)proxy.Bits;
            MetaValue id = proxy["mID"];
            return id != null ? (uint)id.Bits : 0;
        }

        /// <summary>The object's property sets by bit: 1 user, 2 production, 4 tool. Null when not present.</summary>
        public static MetaValue ObjectProps(MetaValue o, int bit)
        {
            MetaValue op = o.FindField("mDlgObjectProps");
            return op != null ? op["#props" + bit] : null;
        }

        public const string ChoiceTextKey = "Choice Display Text";

        /// <summary>The line id of a choice's button text ("Choice Display Text" in its user props), or 0.</summary>
        public static uint ChoiceTextId(MetaValue child)
        {
            MetaValue ps = ObjectProps(child, 1);
            if (ps == null) return 0;
            return ProxyId(MetaProps.Get(ps, ChoiceTextKey));
        }

        /// <summary>Chore handles a node plays: (field label, value).</summary>
        public static List<KeyValuePair<string, MetaValue>> ChoreRefs(MetaValue node)
        {
            List<KeyValuePair<string, MetaValue>> list = new List<KeyValuePair<string, MetaValue>>();
            string[] fields = { "mhChore", "mhIdle", "mChore" };
            for (int i = 0; i < fields.Length; i++)
            {
                MetaValue v = node.FindField(fields[i]);
                if (v != null && v.Kind == MetaKind.Hash) list.Add(new KeyValuePair<string, MetaValue>(fields[i], v));
            }
            return list;
        }

        public static MetaValue VisibilityConditions(MetaValue o) { return o.FindField("mVisCond"); }

        /// <summary>The condition list of a choice or Wait node, or null.</summary>
        public static MetaValue ConditionSet(MetaValue o)
        {
            if (o.Fields == null) return null;
            for (int i = 0; i < o.Fields.Count; i++)
            {
                MetaValue v = o.Fields[i].Value;
                if (v.Type == "DlgConditionSet") return v;
            }
            return null;
        }

        // ============================================================ names
        void CollectNames()
        {
            // Key and name strings used in this file: rule item names, reference key lists, and the
            // quoted strings inside Lua (Logic["Drugstore - GlennNeedsHelp"]).
            Regex quoted = new Regex("\"([^\"\\r\\n]{1,200})\"");
            Root.Walk(v =>
            {
                if (v.Kind != MetaKind.String || v.Bytes == null || v.Bytes.Length == 0) return;
                string s = v.Text;
                if (s.Length <= 200) AddLocalName(s);
                if (s.IndexOf('"') >= 0)
                    foreach (Match m in quoted.Matches(s)) AddLocalName(m.Groups[1].Value);
            });
            AddLocalName(ChoiceTextKey);
        }

        void AddLocalName(string s)
        {
            ulong h = HashDatabase.Crc64(s);
            if (!_localNames.ContainsKey(h)) _localNames[h] = s;
        }

        /// <summary>A name for a symbol hash found among this file's own strings, or null.</summary>
        public string LocalName(ulong hash)
        {
            string n;
            return _localNames.TryGetValue(hash, out n) ? n : null;
        }

        public void RegisterName(string s) { AddLocalName(s); }

        // ============================================================ editing
        readonly Random _rng = new Random();

        /// <summary>A fresh id that no object in this file uses.</summary>
        public ulong NewId()
        {
            HashSet<ulong> used = new HashSet<ulong>();
            Root.Walk(v => { if (v.Type == "DlgObjID" && v["mID"] != null) used.Add(v["mID"].Bits); });
            byte[] b = new byte[8];
            while (true)
            {
                _rng.NextBytes(b);
                ulong id = BitConverter.ToUInt64(b, 0);
                if (id != 0 && id != NoneId && !used.Contains(id)) return id;
            }
        }

        /// <summary>
        /// Points <paramref name="node"/>'s next link at <paramref name="targetId"/> (NoneId to end the chain),
        /// keeping both directions consistent. The old next node loses its back link; a node that
        /// previously led into the target loses its forward link. Returns a description of the
        /// side effects for the status bar.
        /// </summary>
        public string SetNext(MetaValue node, ulong targetId)
        {
            ulong me = Id(node);
            ulong old = LinkId(NextLink(node));
            List<string> notes = new List<string>();
            if (old == targetId) return "unchanged";
            MetaValue oldNode = NodeById(old);
            if (oldNode != null && LinkId(PrevLink(oldNode)) == me)
            {
                SetLinkId(PrevLink(oldNode), NoneId);
                notes.Add("the old next node is now unlinked");
            }
            SetLinkId(NextLink(node), targetId);
            MetaValue target = NodeById(targetId);
            if (target != null) StealPrev(target, me, notes);
            return notes.Count == 0 ? "linked" : string.Join("; ", notes.ToArray());
        }

        /// <summary>Points a child (choice, case, folder entry...) at the first node of its chain.</summary>
        public string SetChildLink(MetaValue child, ulong targetId)
        {
            ulong me = Id(child);
            ulong old = LinkId(ChildLink(child));
            List<string> notes = new List<string>();
            if (old == targetId) return "unchanged";
            MetaValue oldNode = NodeById(old);
            if (oldNode != null && LinkId(PrevLink(oldNode)) == me)
            {
                SetLinkId(PrevLink(oldNode), NoneId);
                notes.Add("the old first node is now unlinked");
            }
            SetLinkId(ChildLink(child), targetId);
            MetaValue target = NodeById(targetId);
            if (target != null) StealPrev(target, me, notes);
            return notes.Count == 0 ? "linked" : string.Join("; ", notes.ToArray());
        }

        void StealPrev(MetaValue target, ulong newPrev, List<string> notes)
        {
            ulong was = LinkId(PrevLink(target));
            if (was != NoneId && was != newPrev)
            {
                MetaValue pn = NodeById(was);
                if (pn != null && LinkId(NextLink(pn)) == Id(target))
                {
                    SetLinkId(NextLink(pn), NoneId);
                    notes.Add("the node that led here before now ends its chain");
                }
                ChildInfo pc = ChildById(was);
                if (pc != null && LinkId(ChildLink(pc.Child)) == Id(target))
                {
                    SetLinkId(ChildLink(pc.Child), NoneId);
                    notes.Add("the branch that led here before is now empty");
                }
            }
            SetLinkId(PrevLink(target), newPrev);
        }

        /// <summary>
        /// Removes a node from its chain and from the file. Nodes with branches are refused (their
        /// chains would be orphaned). Returns null on success, else the reason.
        /// </summary>
        public string DeleteNode(MetaValue node)
        {
            List<ChildSetInfo> sets = ChildSets(node);
            for (int i = 0; i < sets.Count; i++)
                if (sets[i].Children.Count > 0) return "This node has branches. Remove or relink its branches first.";
            ulong me = Id(node);
            ulong prev = LinkId(PrevLink(node)), next = LinkId(NextLink(node));
            MetaValue pn = NodeById(prev);
            if (pn != null && LinkId(NextLink(pn)) == me) SetLinkId(NextLink(pn), next);
            ChildInfo pc = ChildById(prev);
            if (pc != null && LinkId(ChildLink(pc.Child)) == me) SetLinkId(ChildLink(pc.Child), next);
            MetaValue nn = NodeById(next);
            if (nn != null && LinkId(PrevLink(nn)) == me) SetLinkId(PrevLink(nn), prev);
            Nodes.Remove(node);
            Reindex();
            return null;
        }

        /// <summary>
        /// Copies a node (without branches) and inserts the copy right after it in the chain.
        /// The copy and every object id inside it get fresh ids. Returns the copy, or null with a reason.
        /// </summary>
        public MetaValue DuplicateAfter(MetaValue node, out string why)
        {
            why = null;
            List<ChildSetInfo> sets = ChildSets(node);
            for (int i = 0; i < sets.Count; i++)
                if (sets[i].Children.Count > 0) { why = "Nodes with branches cannot be duplicated yet."; return null; }
            MetaValue copy = node.Clone();
            ulong oldId = Id(node);
            // Fresh ids for the node and its own objects; links keep pointing where they did.
            ulong newId = NewId();
            SetId(copy, newId);
            MetaValue lines = copy["#lines"];
            if (lines != null)
                lines.Walk(v => { if (v.Type == "DlgObjIDOwner" && v["mDlgObjID"] != null && v["mDlgObjID"]["mID"].Bits != NoneId) v["mDlgObjID"]["mID"].Bits = NewId(); });
            ulong next = LinkId(NextLink(node));
            SetLinkId(PrevLink(copy), oldId);
            SetLinkId(NextLink(copy), next);
            SetLinkId(NextLink(node), newId);
            MetaValue nn = NodeById(next);
            if (nn != null && LinkId(PrevLink(nn)) == oldId) SetLinkId(PrevLink(nn), newId);
            int at = Nodes.IndexOf(node);
            Nodes.Insert(at + 1, copy);
            Reindex();
            return copy;
        }

        public static readonly string[] ConditionTypes = { "DlgConditionInput", "DlgConditionTime", "DlgConditionRule" };

        /// <summary>Adds a condition of <paramref name="type"/> to a condition set. Conditions carry the none id, like the game's own.</summary>
        public MetaValue AddCondition(MetaValue conditionSet, string type)
        {
            MetaValue c = File.CreateDefault(type);
            SetId(c, NoneId);
            if (type == "DlgConditionTime" && c["mSeconds"] != null) c["mSeconds"].AsFloat = 1f;
            conditionSet["#items"].Items.Add(c);
            File.EnsureHeaderType(type, null);
            return c;
        }

        /// <summary>Gives visibility conditions a Rule (sets flag 1), or removes it.</summary>
        public void SetVisibilityRule(MetaValue visCond, bool on)
        {
            MetaValue flags = visCond["mFlags"];
            if (on)
            {
                if (visCond["#rule"] == null) visCond.Fields.Add(new MetaField("#rule", File.CreateDefault("Rule")));
                if (flags != null) flags.FlagsValue = flags.FlagsValue | 1u;
                File.EnsureHeaderType("Rule", null);
                File.EnsureHeaderType("LogicGroup", null);
            }
            else
            {
                visCond.Fields.RemoveAll(f => f.Name == "#rule");
                if (flags != null) flags.FlagsValue = flags.FlagsValue & ~1u;
            }
        }

        /// <summary>A new LogicItem (one tested or set key group) for a LogicGroup's item map.</summary>
        public MetaValue NewLogicItem(string name)
        {
            MetaValue item = File.CreateDefault("LogicGroup::LogicItem");
            if (item["mName"] != null) item["mName"].Text = name ?? "";
            File.EnsureHeaderType("LogicGroup::LogicItem", null);
            return item;
        }
    }
}
