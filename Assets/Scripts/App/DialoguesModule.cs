using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TFM.Core;
using TFM.Core.Meta;
using UnityEngine;

namespace TFM.App
{
    /// <summary>
    /// Dialogue (.dlog) editor. A dialogue is folders of branches; every branch starts a chain of
    /// nodes. Left: folders and their branches, plus search. Middle: the chain of the selected
    /// branch as node cards, with buttons into sub-branches (choices, cases, sequence elements)
    /// and jump targets. Right: an inspector for the selected node or branch - text from the
    /// language file, chores (with Open), links, visibility conditions, Lua, rules, conditions.
    /// </summary>
    public class DialoguesModule : IFormatModule
    {
        public string Title { get { return "Dialogues"; } }

        const string DlogFilter = "Telltale dialogue (*.dlog)\0*.dlog\0All files (*.*)\0*.*\0";
        const string LandbFilter = "Telltale language database (*.landb)\0*.landb\0All files (*.*)\0*.*\0";
        const string PrefChoreFolder = "tfm.dlg.choreFolder";
        const string PrefLangFolder = "tfm.dlg.langFolder";

        AppContext _ctx;
        Rect _area;
        DlgDocument _doc;
        LanguageDb _lang;
        string _langNote = "";
        bool _dirty;
        string _path = "";
        string _choreFolder = "", _langFolder = "";

        // Navigation: the branch whose chain is shown, plus a back stack.
        MetaValue _branch;              // a DlgChild, or null for a chain started at _chainStart
        ulong _chainStart = DlgDocument.NoneId;
        readonly List<KeyValuePair<MetaValue, ulong>> _back = new List<KeyValuePair<MetaValue, ulong>>();
        MetaValue _selected;           // node or child shown in the inspector

        Vector2 _outlineScroll, _chainScroll, _inspScroll;
        string _search = "", _appliedSearch;
        readonly List<MetaValue> _searchHits = new List<MetaValue>();
        bool _showUnlinked;

        readonly MetaTreeView _tree = new MetaTreeView();
        HashPicker _picker;
        readonly ListPicker _nodePicker = new ListPicker();
        readonly ConfirmDialog _confirm = new ConfirmDialog();

        // Rule editor inputs
        readonly Dictionary<MetaValue, string> _newKeyName = new Dictionary<MetaValue, string>();
        readonly Dictionary<MetaValue, int> _newKeyType = new Dictionary<MetaValue, int>();
        readonly Dictionary<MetaValue, string> _newItemName = new Dictionary<MetaValue, string>();
        static readonly string[] KeyTypes = { "bool", "int", "float", "String" };

        // Node graph (main view) and its options
        readonly DlgGraphView _graph = new DlgGraphView();
        bool _showOutline;
        bool _showTags;
        string _find = "";
        int _findIndex = -1;
        List<MetaValue> _findHits;
        string _findFor;
        float _panelFrac = 0.36f;

        Dictionary<ulong, string> _choreIndex;
        string _choreIndexFor;

        // ================================================================ module
        public void Initialize(AppContext ctx)
        {
            _ctx = ctx;
            _picker = new HashPicker(ctx);
            _choreFolder = PlayerPrefs.GetString(PrefChoreFolder, "");
            _langFolder = PlayerPrefs.GetString(PrefLangFolder, "");
            _tree.SymbolName = Lookup;
            _graph.Describe = DescribeBox;
            _graph.ColorOf = BoxColor;
            _graph.OnSelect = o => { _selected = o; _inspScroll = Vector2.zero; GuiUtil.ResetBuffers(); };
            _graph.OnActivate = o =>
            {
                if (o.Type == "DlgNodeJump" && o["mhJumpToDlg"] != null && o["mhJumpToDlg"].Bits != 0) { OpenOtherDialogue(o["mhJumpToDlg"].Bits); return true; }
                return false;
            };
            _graph.FollowJumps = PlayerPrefs.GetInt("tfm.dlg.followJumps", 0) == 1;
            _tree.PickHash = v => _picker.Open("Choose a name for this value", null, v.Bits, (h, n) =>
            {
                v.Bits = h;
                if (_doc != null) _doc.RegisterName(n);
                MarkDirty("Value set to " + n + ".");
            });
        }

        public void Update() { }

        public bool HasUnsavedChanges { get { return _dirty || (_lang != null && _lang.Dirty); } }

        public bool SaveAll() { return Save(); }

        void SetStatus(string msg, bool error) { if (_ctx != null) _ctx.SetStatus(msg, error); }

        void MarkDirty(string msg)
        {
            _dirty = true;
            _graph.Invalidate();
            _findFor = null;
            _appliedSearch = null;
            if (msg != null) SetStatus(msg + " Save to write the file.", false);
        }

        string Lookup(ulong h)
        {
            if (h == 0) return null;
            string n = _doc != null ? _doc.LocalName(h) : null;
            if (n != null) return n;
            n = _ctx.PropNames.FileRef(h);
            if (n != null) return n;
            n = _ctx.PropNames.Key(h);
            if (n != null) return n;
            return MetaLayouts.Current.TypeName(h);
        }

        string HashText(ulong h)
        {
            if (h == 0) return "(none)";
            return Lookup(h) ?? "0x" + h.ToString("X16");
        }

        // ================================================================ text
        /// <summary>Speaker and text of a line, or a raw description when the language file is missing.</summary>
        string LineText(uint id)
        {
            if (id == 0) return "(no line)";
            if (_lang == null) return "[line " + id + " - no language file loaded]";
            if (!_lang.Has(id)) return "[line " + id + " - not in " + Path.GetFileName(_lang.Path) + "]";
            string sp = _lang.Speaker(id);
            string tx = _lang.Text(id) ?? "";
            return string.IsNullOrEmpty(sp) ? tx : sp + ": " + tx;
        }

        static string Clip(string s, int n)
        {
            if (s == null) return "";
            s = s.Replace('\n', ' ').Replace('\r', ' ');
            return s.Length <= n ? s : s.Substring(0, n - 3) + "...";
        }

        /// <summary>One-line description of a node for cards and pickers.</summary>
        string NodeSummary(MetaValue n)
        {
            string kind = DlgDocument.Kind(n);
            StringBuilder b = new StringBuilder();
            b.Append('[').Append(kind).Append("] ");
            switch (n.Type)
            {
                case "DlgNodeExchange":
                case "DlgNodeText":
                    {
                        List<uint> ids = DlgDocument.LineIds(n);
                        if (ids.Count > 0) b.Append(Clip(LineText(ids[0]), 90));
                        if (ids.Count > 1) b.Append("  (+" + (ids.Count - 1) + " lines)");
                        MetaValue ch = n["mhChore"];
                        if (ids.Count == 0 && ch != null) b.Append(HashText(ch.Bits));
                        break;
                    }
                case "DlgNodeScript":
                    b.Append(Clip(n["mScriptText"] != null ? n["mScriptText"].Text : "", 90));
                    break;
                case "DlgNodeJump":
                    b.Append("to ").Append(JumpTargetText(n));
                    break;
                case "DlgNodeIdle":
                    b.Append(HashText(n["mhIdle"] != null ? n["mhIdle"].Bits : 0));
                    break;
                case "DlgNodeChore":
                    b.Append(HashText(n["mChore"] != null ? n["mChore"].Bits : 0));
                    break;
                case "DlgNodeNotes":
                    b.Append(Clip(n["mNoteText"] != null ? n["mNoteText"].Text : "", 90));
                    break;
                case "DlgNodeLogic":
                    {
                        MetaValue r = n["mRule"];
                        string cond = RuleText(r);
                        string act = r != null && r.Kind != MetaKind.Raw ? GroupText(r["mActions"], true) : null;
                        if (cond != null) b.Append("if ").Append(Clip(cond, 60)).Append(' ');
                        if (act != null) b.Append(cond != null ? "then " : "").Append(Clip(act, 60));
                        if (cond == null && act == null) b.Append(RuleSummary(r));
                        break;
                    }
                default:
                    {
                        List<DlgDocument.ChildSetInfo> sets = DlgDocument.ChildSets(n);
                        int count = 0;
                        for (int i = 0; i < sets.Count; i++) count += sets[i].Children.Count;
                        if (sets.Count > 0) b.Append(count + " branch(es)");
                        break;
                    }
            }
            string ct = ConditionText(n);
            if (ct != null) b.Append("   {if ").Append(Clip(ct, 70)).Append('}');
            return b.ToString();
        }

        static bool HasVisibilityTest(MetaValue vis)
        {
            MetaValue s = vis["mScriptVisCond"];
            return (s != null && s.Bytes != null && s.Bytes.Length > 0) || vis["#rule"] != null;
        }

        string ChildSummary(MetaValue c)
        {
            StringBuilder b = new StringBuilder();
            uint t = DlgDocument.ChoiceTextId(c);
            if (t != 0) b.Append(Clip(LineText(t), 80));
            else
            {
                List<MetaValue> chain = _doc.ChainOfChild(c);
                MetaValue show = null;
                for (int i = 0; i < chain.Count && show == null; i++)
                    if (chain[i].Type != "DlgNodeStart" && chain[i].Type != "DlgNodeMarker" && chain[i].Type != "DlgNodeNotes") show = chain[i];
                if (show == null && chain.Count > 0) show = chain[0];
                if (show != null) b.Append(Clip(NodeSummary(show), 80));
                else b.Append("(empty branch)");
                if (chain.Count > 1) b.Append("  (" + chain.Count + " nodes)");
            }
            string ct = ConditionText(c);
            if (ct != null) b.Insert(0, "{if " + Clip(ct, 70) + "}  ");
            else if (c.Type == "DlgConditionalCase") b.Insert(0, "{otherwise}  ");
            return b.ToString();
        }

        string JumpTargetText(MetaValue jump)
        {
            MetaValue nm = jump["mJumpToName"];
            MetaValue dlg = jump["mhJumpToDlg"];
            MetaValue cls = jump["mJumpTargetClass"];
            int c = cls != null ? cls.AsInt : 1;
            if (c == 2) return "parent";
            if (c == 3) return "node after the parent's Wait";
            string other = dlg != null && dlg.Bits != 0 ? " in " + HashText(dlg.Bits) : "";
            ulong h = nm != null ? nm.Bits : 0;
            if (other.Length == 0)
            {
                List<MetaValue> t = _doc.ByName(h);
                if (t.Count > 0)
                {
                    MetaValue first = t[0];
                    return (Lookup(h) ?? DlgDocument.Kind(first) + " " + ShortId(DlgDocument.Id(first)));
                }
            }
            return HashText(h) + other;
        }

        static string ShortId(ulong id) { return "#" + (id & 0xFFFFFF).ToString("X6"); }

        static readonly string[] OpText = { "==", "<", ">", "<=", ">=" };
        static readonly string[] ActText = { "=", "+=", "-=" };

        string KeyName(ulong h)
        {
            string n = Lookup(h);
            return n ?? "unknown key 0x" + h.ToString("X16");
        }

        /// <summary>A rule group as an expression: "Drugstore - KennyHit == true and ...".</summary>
        string GroupText(MetaValue grp, bool actions)
        {
            MetaValue items = grp != null ? grp["mItems"] : null;
            if (items == null || items.Kind != MetaKind.Map || items.Items.Count == 0) return null;
            List<string> parts = new List<string>();
            for (int i = 0; i < items.Items.Count; i++)
            {
                MetaValue item = items.Items[i];
                MetaValue ps = item["Baseclass_PropertySet"];
                foreach (MetaValue g in MetaProps.Groups(ps))
                    for (int k = 0; k < g.Items.Count; k++)
                    {
                        ulong key = g.Keys[k].Bits;
                        string v = g.Items[k].Kind == MetaKind.String ? "\"" + g.Items[k].Text + "\"" : g.Items[k].Summary();
                        if (actions)
                        {
                            MetaValue a = MapGet(item["mKeyActionList"], key);
                            int ai = a != null ? a.AsInt : 0;
                            parts.Add(KeyName(key) + " " + (ai >= 0 && ai < ActText.Length ? ActText[ai] : "?") + " " + v);
                        }
                        else
                        {
                            MetaValue c = MapGet(item["mKeyComparisonList"], key);
                            MetaValue ng = MapGet(item["mKeyNegateList"], key);
                            int ci = c != null ? c.AsInt : 0;
                            parts.Add((ng != null && ng.AsBool ? "not " : "") + KeyName(key) + " " + (ci >= 0 && ci < OpText.Length ? OpText[ci] : "?") + " " + v);
                        }
                    }
            }
            if (parts.Count == 0) return null;
            MetaValue op = grp["mOperator"];
            return string.Join(op != null && op.AsInt == 2 ? " or " : " and ", parts.ToArray());
        }

        string RuleText(MetaValue rule)
        {
            if (rule == null || rule.Kind == MetaKind.Raw) return null;
            return GroupText(rule["mConditions"], false);
        }

        /// <summary>The test that decides whether a node or branch is used, as one line, or null.</summary>
        string ConditionText(MetaValue o) { return ConditionText(o, false); }

        /// <summary><paramref name="forBox"/>: leave out "on input" (a choice waiting for the player), which every choice has.</summary>
        string ConditionText(MetaValue o, bool forBox)
        {
            List<string> parts = new List<string>();
            MetaValue vis = DlgDocument.VisibilityConditions(o);
            if (vis != null)
            {
                MetaValue sc = vis["mScriptVisCond"];
                if (sc != null && sc.Bytes != null && sc.Bytes.Length > 0) parts.Add(sc.Text.Trim());
                string rt = RuleText(vis["#rule"]);
                if (!string.IsNullOrEmpty(rt)) parts.Add(rt);
            }
            MetaValue cs = DlgDocument.ConditionSet(o);
            if (cs != null)
                foreach (MetaValue c in cs["#items"].Items)
                {
                    if (c.Type == "DlgConditionRule") { string rt = RuleText(c["mRule"]); if (!string.IsNullOrEmpty(rt)) parts.Add(rt); }
                    else if (c.Type == "DlgConditionTime") parts.Add("after " + (c["mSeconds"] != null ? c["mSeconds"].Summary() : "?") + " s");
                    else if (c.Type == "DlgConditionInput" && !forBox) parts.Add("on input");
                }
            return parts.Count == 0 ? null : string.Join("; ", parts.ToArray());
        }

        static string RuleSummary(MetaValue rule)
        {
            if (rule == null) return "";
            StringBuilder b = new StringBuilder();
            int conds = rule.Path("mConditions.mItems") != null ? rule.Path("mConditions.mItems").Items.Count : 0;
            int acts = rule.Path("mActions.mItems") != null ? rule.Path("mActions.mItems").Items.Count : 0;
            b.Append(conds).Append(" test group(s), ").Append(acts).Append(" action group(s)");
            return b.ToString();
        }

        // ================================================================ GUI
        public void OnGUI(Rect area)
        {
            _area = area;
            Event e = Event.current;
            if (e.type == EventType.Layout || e.type == EventType.Repaint) GuiUtil.BeginFrame();
            GUILayout.BeginArea(area);
            DrawToolbar();
            if (_doc == null)
            {
                GuiUtil.Space(10);
                GUILayout.Label("No dialogue loaded. Use [Open .dlog...] or type a path and press [Load path].", GuiUtil.Note);
                GUILayout.Label("The matching language file (<name>_english.landb) is found automatically in the same folder or the"
                    + " language folder you set, and is used to show the lines. Chores are looked up in the same folder or the chore folder.", GuiUtil.Wrap);
            }
            else
            {
                DrawGraphToolbar();
                GUILayout.BeginHorizontal();
                if (_showOutline)
                {
                    GUILayout.BeginVertical(GUILayout.Width(Mathf.Clamp(_area.width * 0.2f, 200f, 360f)));
                    DrawOutline();
                    GUILayout.EndVertical();
                }
                // The graph takes whatever the properties panel leaves.
                float pw = Mathf.Clamp(_area.width * _panelFrac, 300f, 900f);
                Rect canvas = GUILayoutUtility.GetRect(100f, 100f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
                GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(pw), GUILayout.ExpandHeight(true));
                GUILayout.Label("Properties", GuiUtil.SubHeader);
                _inspScroll = GUILayout.BeginScrollView(_inspScroll, false, true, GUILayout.ExpandHeight(true));
                DrawInspector();
                GUILayout.EndScrollView();
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
                if (canvas.width > 20f && canvas.height > 20f && Event.current.type != EventType.Layout) _graph.OnGUI(canvas);
            }
            GUILayout.EndArea();

            Rect screen = _ctx.ScreenRect.width > 0 ? _ctx.ScreenRect : new Rect(0, 0, Screen.width, Screen.height);
            if (_picker.IsOpen) _picker.OnGUI(screen);
            if (_nodePicker.IsOpen) _nodePicker.OnGUI(screen);
            if (_confirm.IsOpen) _confirm.OnGUI(screen);
        }

        void DrawToolbar()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Open .dlog...", GuiUtil.W(110))) BrowseOpen();
            if (GUILayout.Button("Reload", GuiUtil.W(70))) Reload();
            GUI.enabled = _doc != null && HasUnsavedChanges;
            if (GUILayout.Button("Save", GuiUtil.W(70))) Save();
            GUI.enabled = true;
            _path = GUILayout.TextField(_path ?? "", GuiUtil.MinW(200));
            if (GUILayout.Button("Load path", GuiUtil.W(80)))
            {
                string p = (_path ?? "").Trim().Trim('"');
                if (p.Length > 0) _ctx.RunDeferred(() => LoadFile(p));
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Language file...", GuiUtil.W(120))) BrowseLanguage();
            if (GUILayout.Button("Language folder...", GuiUtil.W(130))) BrowseFolder(true);
            if (GUILayout.Button("Chore folder...", GuiUtil.W(110))) BrowseFolder(false);
            GUI.enabled = _doc != null;
            if (GUILayout.Button("Guess key names", GuiUtil.W(130))) _ctx.RunDeferred(GuessKeyNames);
            GUI.enabled = true;
            if (GUILayout.Button("Import names...", GuiUtil.W(120))) BrowseImportNames();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // One clipped status line: file, language file, chore folder.
            StringBuilder b = new StringBuilder();
            if (_doc != null)
            {
                b.Append(Path.GetFileName(_doc.File.SourcePath ?? "(unsaved)"));
                if (_dirty) b.Append(" *");
                b.Append("  |  ").Append(_doc.Nodes.Count).Append(" nodes");
                if (!_doc.File.FullyDecoded) b.Append("  |  ").Append(_doc.File.RawRegions).Append(" undecoded region(s) kept as-is");
                b.Append("  |  ");
            }
            b.Append(_lang != null ? "language " + Path.GetFileName(_lang.Path) + " (" + _lang.Count + " lines)" + (_lang.Dirty ? " *" : "") : "no language file");
            b.Append("  |  chores from ").Append(string.IsNullOrEmpty(_choreFolder) ? "the dialogue's folder" : _choreFolder);
            GUILayout.Label(b.ToString(), ClipRows.Note);
            if (_doc != null && _lang == null)
                GUILayout.Label(_langNote + " Lines are shown as raw ids; pick a language file to see the text.", GuiUtil.Warning);
        }

        // ================================================================ file I/O
        void BrowseOpen()
        {
            if (!NativeFileDialog.IsSupported) { SetStatus("The file dialog is only available in Windows builds. Type a path and press [Load path].", true); return; }
            _ctx.RunDeferred(() =>
            {
                string p = NativeFileDialog.OpenFile("Open Telltale dialogue", DlogFilter, Dir(_path));
                if (!string.IsNullOrEmpty(p)) LoadFile(p);
            });
        }

        static string Dir(string p)
        {
            try { if (!string.IsNullOrEmpty(p) && File.Exists(p)) return Path.GetDirectoryName(p); }
            catch { }
            return null;
        }

        void BrowseLanguage()
        {
            if (!NativeFileDialog.IsSupported) { SetStatus("The file dialog is only available in Windows builds.", true); return; }
            _ctx.RunDeferred(() =>
            {
                string p = NativeFileDialog.OpenFile("Choose the language file", LandbFilter, !string.IsNullOrEmpty(_langFolder) ? _langFolder : Dir(_path));
                if (!string.IsNullOrEmpty(p)) LoadLanguage(p);
            });
        }

        void BrowseFolder(bool language)
        {
            if (!NativeFileDialog.IsSupported) { SetStatus("The folder dialog is only available in Windows builds.", true); return; }
            _ctx.RunDeferred(() =>
            {
                string p = NativeFileDialog.OpenFolder(language ? "Folder with .landb language files" : "Folder with .chore files", Dir(_path));
                if (string.IsNullOrEmpty(p)) return;
                if (language)
                {
                    _langFolder = p;
                    PlayerPrefs.SetString(PrefLangFolder, p);
                    if (_doc != null && _lang == null) AutoLanguage();
                }
                else
                {
                    _choreFolder = p;
                    PlayerPrefs.SetString(PrefChoreFolder, p);
                    _choreIndex = null;
                }
                PlayerPrefs.Save();
            });
        }

        void LoadFile(string path)
        {
            if (HasUnsavedChanges && _doc != null && !string.Equals(_doc.File.SourcePath, path, StringComparison.OrdinalIgnoreCase))
            {
                _confirm.Open("Unsaved changes", "Discard unsaved changes to " + Path.GetFileName(_doc.File.SourcePath) + "?",
                    new[] { "Discard", "Cancel" }, c => { if (c == 0) { _dirty = false; if (_lang != null) _lang.Dirty = false; _ctx.RunDeferred(() => LoadFile(path)); } });
                return;
            }
            try
            {
                DlgDocument doc = DlgDocument.Load(path);
                _doc = doc;
                _graph.SetDocument(doc);
                _findHits = null;
                _findFor = null;
                _path = path;
                _dirty = false;
                _back.Clear();
                _branch = null;
                _chainStart = DlgDocument.NoneId;
                _selected = null;
                _tree.Clear();
                _appliedSearch = null;
                _search = "";
                _choreIndex = null;
                GuiUtil.ResetBuffers();
                _lang = null;
                _ctx.LineText = null;
                AutoLanguage();
                _graph.Invalidate();
                // Start on the first folder's first branch.
                for (int f = 0; f < doc.Folders.Count && _branch == null; f++)
                {
                    List<DlgDocument.ChildSetInfo> sets = DlgDocument.ChildSets(doc.Folders[f]);
                    if (sets.Count > 0 && sets[0].Children.Count > 0) ShowBranch(sets[0].Children[0], false);
                }
                string msg = "Loaded " + Path.GetFileName(path) + " - " + doc.Nodes.Count + " nodes";
                if (!doc.File.FullyDecoded) msg += ". " + doc.File.RawRegions + " region(s) could not be decoded and are kept as raw bytes";
                SetStatus(msg + ".", !doc.File.FullyDecoded);
            }
            catch (MetaParseException ex) { SetStatus("Could not read " + Path.GetFileName(path) + ": " + ex.Message, true); }
            catch (Exception ex) { SetStatus("Could not open file: " + ex.Message, true); }
        }

        void AutoLanguage()
        {
            List<string> extra = new List<string>();
            if (!string.IsNullOrEmpty(_langFolder)) extra.Add(_langFolder);
            List<string> cands = LanguageDb.CandidatesFor(_doc.File.SourcePath, extra);
            if (cands.Count == 0)
            {
                _langNote = "No language file found for " + Path.GetFileNameWithoutExtension(_doc.File.SourcePath)
                    + " (looked for " + Path.GetFileNameWithoutExtension(_doc.File.SourcePath) + "_english.landb next to the dialogue"
                    + (string.IsNullOrEmpty(_langFolder) ? "" : " and in " + _langFolder) + ").";
                return;
            }
            LoadLanguage(cands[0]);
        }

        void LoadLanguage(string path)
        {
            try
            {
                _lang = LanguageDb.Load(path);
                _langNote = "";
                _graph.Invalidate();
                LanguageDb shared = _lang;
                _ctx.LineText = id => shared.Has(id) ? (string.IsNullOrEmpty(shared.Speaker(id)) ? "" : shared.Speaker(id) + ": ") + shared.Text(id) : null;
                // Report lines the dialogue uses that this language file lacks.
                int missing = 0, total = 0;
                if (_doc != null)
                    for (int i = 0; i < _doc.Nodes.Count; i++)
                    {
                        List<uint> ids = DlgDocument.LineIds(_doc.Nodes[i]);
                        for (int k = 0; k < ids.Count; k++) { total++; if (!_lang.Has(ids[k])) missing++; }
                    }
                SetStatus("Language file " + Path.GetFileName(path) + " loaded" + (missing > 0 ? "; " + missing + " of " + total
                    + " lines are not in it (shown as raw ids). It may belong to another build or episode." : "."), missing > 0);
            }
            catch (Exception ex)
            {
                _lang = null;
                _langNote = "The language file " + Path.GetFileName(path) + " could not be read (" + ex.Message + ").";
                SetStatus(_langNote, true);
            }
        }

        void Reload()
        {
            if (_doc == null) { SetStatus("Nothing to reload.", true); return; }
            string p = _doc.File.SourcePath;
            if (HasUnsavedChanges)
            {
                _confirm.Open("Reload", "Discard unsaved changes and reload?", new[] { "Reload", "Cancel" }, c =>
                {
                    if (c != 0) return;
                    _dirty = false;
                    if (_lang != null) _lang.Dirty = false;
                    _ctx.RunDeferred(() => LoadFile(p));
                });
                return;
            }
            LoadFile(p);
        }

        bool Save()
        {
            if (_doc == null) return true;
            try
            {
                StringBuilder b = new StringBuilder();
                if (_dirty)
                {
                    _doc.File.Save(_doc.File.SourcePath);
                    _dirty = false;
                    b.Append("Saved ").Append(Path.GetFileName(_doc.File.SourcePath)).Append('.');
                }
                if (_lang != null && _lang.Dirty)
                {
                    _lang.Save();
                    b.Append(" Saved ").Append(Path.GetFileName(_lang.Path)).Append('.');
                }
                SetStatus(b.Length > 0 ? b.ToString().Trim() : "Nothing to save.", false);
                return true;
            }
            catch (Exception ex)
            {
                SetStatus("Save failed: " + ex.Message, true);
                return false;
            }
        }

        // ================================================================ outline
        void DrawOutline()
        {
            GUILayout.Label("Folders and branches", GuiUtil.SubHeader);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search", GuiUtil.W(50));
            _search = GUILayout.TextField(_search ?? "", GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
            _showUnlinked = GUILayout.Toggle(_showUnlinked, " Show nodes only reached by jumps");

            _outlineScroll = GUILayout.BeginScrollView(_outlineScroll, false, true, GUILayout.ExpandHeight(true));
            string q = (_search ?? "").Trim();
            if (q.Length > 0)
            {
                if (_appliedSearch != q) RunSearch(q);
                GUILayout.Label(_searchHits.Count + " match(es)", GuiUtil.Note);
                for (int i = 0; i < _searchHits.Count && i < 300; i++)
                    if (GUILayout.Button(Clip(NodeSummary(_searchHits[i]), 70), _searchHits[i] == _selected ? ClipRows.Selected : ClipRows.Row))
                        ShowNode(_searchHits[i]);
            }
            else if (_showUnlinked)
            {
                List<MetaValue> un = _doc.UnlinkedNodes();
                GUILayout.Label(un.Count + " node(s) not linked from any branch", GuiUtil.Note);
                for (int i = 0; i < un.Count && i < 500; i++)
                    if (GUILayout.Button(Clip(NodeSummary(un[i]), 70), un[i] == _selected ? ClipRows.Selected : ClipRows.Row))
                        ShowNode(un[i]);
            }
            else
            {
                for (int f = 0; f < _doc.Folders.Count; f++)
                {
                    MetaValue folder = _doc.Folders[f];
                    ulong fn = DlgDocument.NameHash(folder);
                    GUILayout.Label("Folder " + (f + 1) + (fn != 0 && Lookup(fn) != null ? ": " + Lookup(fn) : ""), GuiUtil.SubHeader);
                    List<DlgDocument.ChildSetInfo> sets = DlgDocument.ChildSets(folder);
                    for (int s = 0; s < sets.Count; s++)
                        for (int c = 0; c < sets[s].Children.Count; c++)
                        {
                            MetaValue ch = sets[s].Children[c];
                            bool sel = ReferenceEquals(ch, _branch);
                            if (GUILayout.Button(Clip(ChildSummary(ch), 70), sel ? ClipRows.Selected : ClipRows.Row))
                                ShowBranch(ch, true);
                        }
                }
            }
            GUILayout.EndScrollView();
        }

        void RunSearch(string q)
        {
            _appliedSearch = q;
            _searchHits.Clear();
            for (int i = 0; i < _doc.Nodes.Count; i++)
            {
                MetaValue n = _doc.Nodes[i];
                if (NodeSummary(n).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) { _searchHits.Add(n); continue; }
                // Also match every line of an exchange, and chore names.
                List<uint> ids = DlgDocument.LineIds(n);
                bool hit = false;
                for (int k = 0; k < ids.Count && !hit; k++) hit = LineText(ids[k]).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                List<KeyValuePair<string, MetaValue>> cr = DlgDocument.ChoreRefs(n);
                for (int k = 0; k < cr.Count && !hit; k++) hit = HashText(cr[k].Value.Bits).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                if (hit) _searchHits.Add(n);
            }
        }

        void ShowBranch(MetaValue child, bool remember)
        {
            _branch = child;
            _selected = child;
            _graph.Focus(child);
            _inspScroll = Vector2.zero;
            GuiUtil.ResetBuffers();
        }

        /// <summary>Selects a node and scrolls the graph to it.</summary>
        void ShowNode(MetaValue node)
        {
            _selected = node;
            _graph.Focus(node);
            _inspScroll = Vector2.zero;
            GuiUtil.ResetBuffers();
        }

        // ================================================================ graph
        void DrawGraphToolbar()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Fit", GuiUtil.W(44))) _graph.Fit();
            if (GUILayout.Button("+", GuiUtil.W(28))) _graph.ZoomBy(1.25f);
            if (GUILayout.Button("-", GuiUtil.W(28))) _graph.ZoomBy(0.8f);
            bool fj = GUILayout.Toggle(_graph.FollowJumps, " Follow jumps", GuiUtil.W(115));
            if (fj != _graph.FollowJumps)
            {
                _graph.FollowJumps = fj;
                PlayerPrefs.SetInt("tfm.dlg.followJumps", fj ? 1 : 0);
                _graph.Invalidate();
                _graph.Fit();
            }
            _graph.ShowJumpArrows = GUILayout.Toggle(_graph.ShowJumpArrows, " Jump arrows", GuiUtil.W(110));
            bool st = GUILayout.Toggle(_showTags, " Tags", GuiUtil.W(65));
            if (st != _showTags) { _showTags = st; _graph.Invalidate(); }
            _showOutline = GUILayout.Toggle(_showOutline, " List", GuiUtil.W(60));
            GUILayout.FlexibleSpace();
            GUILayout.Label("Find", GuiUtil.W(34));
            GUI.SetNextControlName("dlgFind");
            _find = GUILayout.TextField(_find ?? "", GuiUtil.MinW(100), GuiUtil.MaxW(200));
            bool enter = Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                         && GUI.GetNameOfFocusedControl() == "dlgFind";
            if (GUILayout.Button("Next", GuiUtil.W(50)) || enter)
            {
                if (enter) Event.current.Use();
                FindNext();
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("Drag to pan, wheel to zoom, click a box to edit it, double-click a jump to follow it. Red arrows are jumps."
                + " Follow jumps puts conversations reached by a jump under that jump.", ClipRows.Note);
        }

        void FindNext()
        {
            string q = (_find ?? "").Trim();
            if (q.Length == 0) return;
            if (_findFor != q || _findHits == null)
            {
                _findHits = _graph.Find(q);
                _findFor = q;
                _findIndex = -1;
            }
            if (_findHits.Count == 0) { SetStatus("Nothing in this dialogue matches \"" + q + "\".", true); return; }
            _findIndex = (_findIndex + 1) % _findHits.Count;
            ShowNode(_findHits[_findIndex]);
            SetStatus("Match " + (_findIndex + 1) + " of " + _findHits.Count + ".", false);
        }

        static readonly System.Text.RegularExpressions.Regex Tags = new System.Text.RegularExpressions.Regex(@"\{[^}]*\}|\[[^\]]*\]");

        /// <summary>A line as the player sees it: acting tags {..} and recording notes [..] removed.</summary>
        public static string Subtitle(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string t = Tags.Replace(text, " ");
            return System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();
        }

        string ShowText(string raw) { return _showTags ? (raw ?? "").Trim() : Subtitle(raw); }

        /// <summary>Speaker and line for a box. Raw id text when the language file is missing.</summary>
        string BoxLine(uint id, out string speaker)
        {
            speaker = null;
            if (_lang == null || !_lang.Has(id)) return LineText(id);
            speaker = _lang.Speaker(id);
            return ShowText(_lang.Text(id));
        }

        string ChoreText(MetaValue v)
        {
            if (v == null || v.Bits == 0) return null;
            return Lookup(v.Bits) ?? "0x" + v.Bits.ToString("X16");
        }

        static string FirstLines(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string[] parts = s.Replace("\r", "").Split('\n');
            List<string> keep = new List<string>();
            for (int i = 0; i < parts.Length && keep.Count < n; i++)
            {
                string t = parts[i].Trim();
                if (t.Length == 0 || t.StartsWith("--", StringComparison.Ordinal)) continue;
                keep.Add(t);
            }
            return string.Join("\n", keep.ToArray());
        }

        /// <summary>Title, body and condition shown in a graph box.</summary>
        string[] DescribeBox(MetaValue o, DlgGraphView.BoxKind kind)
        {
            string cond = kind == DlgGraphView.BoxKind.Folder ? null : ConditionText(o, true);
            if (cond != null) cond = "if " + cond;
            if (kind == DlgGraphView.BoxKind.Folder)
            {
                ulong fn = DlgDocument.NameHash(o);
                string nm = fn != 0 ? Lookup(fn) : null;
                return new[] { "Folder" + (nm != null ? ": " + nm : ""), "", null };
            }
            if (kind == DlgGraphView.BoxKind.Branch)
            {
                DlgDocument.ChildInfo ci = _doc.ChildInfoOf(o);
                int index = ci != null ? ci.Set.Children.IndexOf(o) + 1 : 0;
                string label;
                if (ci == null || _doc.FolderOfChild(o) != null) label = "Entry";
                else
                {
                    switch (ci.Set.FieldName)
                    {
                        case "mChoices": label = "Choice " + index; break;
                        case "mPreChoice": label = "Before the choices"; break;
                        case "mPostChoice": label = "After a choice"; break;
                        case "mCases": label = cond == null ? "Case " + index + ": otherwise" : "Case " + index; break;
                        case "mElements": label = "Element " + index; break;
                        case "mPElements": label = "Parallel " + index; break;
                        default: label = ci.Set.Label + " " + index; break;
                    }
                }
                ulong nmh = DlgDocument.NameHash(o);
                string nm = nmh != 0 ? Lookup(nmh) : null;
                if (nm != null) label += "  (" + nm + ")";
                uint tid = DlgDocument.ChoiceTextId(o);
                string body = "";
                if (tid != 0) { string sp; body = "\"" + BoxLine(tid, out sp) + "\""; }
                return new[] { label, body, cond };
            }

            string kindName = DlgDocument.Kind(o);
            switch (o.Type)
            {
                case "DlgNodeExchange":
                case "DlgNodeText":
                    {
                        List<uint> ids = DlgDocument.LineIds(o);
                        if (ids.Count > 0)
                        {
                            string sp;
                            string line = BoxLine(ids[0], out sp);
                            string more = ids.Count > 1 ? "\n(+" + (ids.Count - 1) + " more line" + (ids.Count > 2 ? "s" : "") + ")" : "";
                            string title = string.IsNullOrEmpty(sp) ? kindName : sp;
                            return new[] { title, line + more, cond };
                        }
                        string ch = ChoreText(o["mhChore"]);
                        return new[] { kindName + (ch != null ? " - chore" : ""), ch ?? "(no line, no chore)", cond };
                    }
                case "DlgNodeScript":
                    {
                        string lua = o["mScriptText"] != null ? FirstLines(o["mScriptText"].Text, 3) : "";
                        return new[] { "Lua script", lua.Length > 0 ? lua : "(empty)", cond };
                    }
                case "DlgNodeLogic":
                    {
                        MetaValue r = o["mRule"];
                        string c = RuleText(r);
                        string a = r != null && r.Kind != MetaKind.Raw ? GroupText(r["mActions"], true) : null;
                        string body = (c != null ? "if " + c : "") + (a != null ? (c != null ? "\nthen " : "") + a : "");
                        return new[] { "Logic", body.Length > 0 ? body : "(empty rule)", cond };
                    }
                case "DlgNodeJump":
                    {
                        MetaValue dlg = o["mhJumpToDlg"];
                        bool other = dlg != null && dlg.Bits != 0;
                        return new[] { other ? "Jump to another dialogue" : "Jump", "to " + JumpTargetText(o), cond };
                    }
                case "DlgNodeIdle": return new[] { "Idle", ChoreText(o["mhIdle"]) ?? "(no idle chore)", cond };
                case "DlgNodeChore": return new[] { "Chore", ChoreText(o["mChore"]) ?? "(no chore)", cond };
                case "DlgNodeNotes": return new[] { "Note", o["mNoteText"] != null ? FirstLines(o["mNoteText"].Text, 3) : "", cond };
                case "DlgNodeWait": return new[] { "Wait", "", cond ?? "until its conditions are met" };
                case "DlgNodeChoices":
                    {
                        List<DlgDocument.ChildSetInfo> sets = DlgDocument.ChildSets(o);
                        int n = sets.Count > 0 ? sets[0].Children.Count : 0;
                        return new[] { "Choices", n + " choice" + (n == 1 ? "" : "s") + " for the player", cond };
                    }
                case "DlgNodeConditional":
                    {
                        List<DlgDocument.ChildSetInfo> sets = DlgDocument.ChildSets(o);
                        int n = sets.Count > 0 ? sets[0].Children.Count : 0;
                        return new[] { "Conditional", "first case whose condition holds (" + n + " cases)", cond };
                    }
                case "DlgNodeSequence": return new[] { "Sequence", "plays one element each time", cond };
                case "DlgNodeParallel": return new[] { "Parallel", "plays its elements together", cond };
                case "DlgNodeStart": return new[] { "Start", NodeName(o), cond };
                case "DlgNodeMarker": return new[] { "Marker", NodeName(o), cond };
                case "DlgNodeExit": return new[] { "Exit", "the dialogue ends here", cond };
                case "DlgNodeCancelChoices": return new[] { "Cancel choices", "", cond };
                default: return new[] { kindName, "", cond };
            }
        }

        /// <summary>A Start/Marker node's name when known; otherwise whether jumps lead here.</summary>
        string NodeName(MetaValue o)
        {
            ulong h = DlgDocument.NameHash(o);
            if (h == 0) return "";
            string n = Lookup(h);
            if (n != null) return n;
            int jumps = 0;
            for (int i = 0; i < _doc.Nodes.Count; i++)
            {
                MetaValue j = _doc.Nodes[i];
                if (j.Type == "DlgNodeJump" && j["mJumpToName"] != null && j["mJumpToName"].Bits == h) jumps++;
            }
            return jumps > 0 ? "jump target (" + jumps + " jump" + (jumps == 1 ? "" : "s") + " lead here)" : "";
        }

        static Color BoxColor(MetaValue o)
        {
            switch (o.Type)
            {
                case "DlgNodeExchange":
                case "DlgNodeText": return new Color(0.25f, 0.45f, 0.75f);
                case "DlgNodeChoices": return new Color(0.55f, 0.35f, 0.75f);
                case "DlgChoice": return new Color(0.45f, 0.3f, 0.62f);
                case "DlgNodeConditional": return new Color(0.8f, 0.5f, 0.15f);
                case "DlgConditionalCase": return new Color(0.65f, 0.42f, 0.15f);
                case "DlgNodeLogic": return new Color(0.15f, 0.55f, 0.5f);
                case "DlgNodeScript": return new Color(0.35f, 0.5f, 0.25f);
                case "DlgNodeJump": return new Color(0.75f, 0.25f, 0.25f);
                case "DlgNodeStart": return new Color(0.2f, 0.6f, 0.3f);
                case "DlgNodeExit": return new Color(0.5f, 0.15f, 0.15f);
                case "DlgNodeChore":
                case "DlgNodeIdle": return new Color(0.5f, 0.3f, 0.55f);
                case "DlgNodeWait": return new Color(0.45f, 0.45f, 0.45f);
                case "DlgNodeSequence":
                case "DlgNodeParallel": return new Color(0.6f, 0.55f, 0.2f);
                case "DlgFolder": return new Color(0.3f, 0.35f, 0.4f);
                default: return new Color(0.4f, 0.4f, 0.45f);
            }
        }

        string FindDialoguePath(ulong hash)
        {
            string dir = Dir(_doc.File.SourcePath);
            if (dir == null) return null;
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*.dlog"))
                    if (HashDatabase.Crc64(Path.GetFileName(f)) == hash) return f;
            }
            catch { }
            return null;
        }

        void OpenOtherDialogue(ulong hash)
        {
            string p = FindDialoguePath(hash);
            if (p == null) { SetStatus(HashText(hash) + " is not in this dialogue's folder.", true); return; }
            _ctx.RunDeferred(() => LoadFile(p));
        }

        // ================================================================ inspector
        void DrawInspector()
        {
            if (_selected == null)
            {
                GUILayout.Label("Select a branch or a node.", GuiUtil.Note);
                return;
            }
            if (_doc.ChildInfoOf(_selected) != null) DrawChildInspector(_selected);
            else DrawNodeInspector(_selected);
        }

        void DrawIdLine(MetaValue o)
        {
            ulong id = DlgDocument.Id(o);
            ulong nm = DlgDocument.NameHash(o);
            GUILayout.Label("id 0x" + id.ToString("X16") + (nm != 0 ? "   |   name " + HashText(nm) : ""), GuiUtil.Note);
        }

        void DrawNodeInspector(MetaValue n)
        {
            GUILayout.Label(DlgDocument.Kind(n) + " node", GuiUtil.SubHeader);
            DrawIdLine(n);

            // Links
            ulong next = DlgDocument.LinkId(DlgDocument.NextLink(n));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Next: " + (next == DlgDocument.NoneId ? "(end of chain)" : _doc.NodeById(next) != null ? Clip(NodeSummary(_doc.NodeById(next)), 70) : "missing node"), GuiUtil.Wrap);
            if (GUILayout.Button("Change...", GuiUtil.W(80)))
                PickNode("Choose the node that follows", n, id => { string r = _doc.SetNext(n, id); _doc.Reindex(); MarkDirty("Next link changed (" + r + ")."); });
            GUILayout.EndHorizontal();

            GuiUtil.Space(4);
            switch (n.Type)
            {
                case "DlgNodeExchange":
                    DrawChoreField(n, "mhChore", "Chore");
                    DrawLines(n);
                    break;
                case "DlgNodeText":
                    DrawLines(n);
                    break;
                case "DlgNodeIdle":
                    DrawChoreField(n, "mhIdle", "Idle chore");
                    break;
                case "DlgNodeChore":
                    DrawChoreField(n, "mChore", "Chore");
                    break;
                case "DlgNodeScript":
                    {
                        GUILayout.Label("Lua script", GuiUtil.SubHeader);
                        MetaValue s = n["mScriptText"];
                        string t = GUILayout.TextArea(s.Text, GuiUtil.Mono, GUILayout.MinHeight(GuiUtil.S(60f)));
                        if (t != s.Text) { s.Text = t; MarkDirty(null); }
                        DrawBoolField(n, "mbBlocking", "Wait for the script to finish");
                        DrawBoolField(n, "mbExecuteOnInstanceRetire", "Run when the node instance retires");
                        break;
                    }
                case "DlgNodeNotes":
                    {
                        MetaValue s = n["mNoteText"];
                        string t = GUILayout.TextArea(s.Text, GUILayout.MinHeight(GuiUtil.S(60f)));
                        if (t != s.Text) { s.Text = t; MarkDirty(null); }
                        break;
                    }
                case "DlgNodeLogic":
                    GUILayout.Label("Rule (tests and sets game logic)", GuiUtil.SubHeader);
                    DrawRule(n["mRule"]);
                    break;
                case "DlgNodeJump":
                    DrawJump(n);
                    break;
                case "DlgNodeWait":
                    DrawConditionSet(n, "The node waits until these conditions are met");
                    break;
                case "DlgNodeSequence":
                    DrawEnum(n["mPlaybackMode"], "Playback", new[] { "", "Sequential", "Shuffle" });
                    DrawEnum(n["mLifetimeMode"], "Lifetime", new[] { "", "Looping", "Single sequence", "Single sequence, repeat final" });
                    break;
            }

            List<DlgDocument.ChildSetInfo> sets = DlgDocument.ChildSets(n);
            for (int s = 0; s < sets.Count; s++)
            {
                GUILayout.Label(sets[s].Label + " (" + sets[s].Children.Count + ")", GuiUtil.SubHeader);
                for (int c = 0; c < sets[s].Children.Count; c++)
                {
                    MetaValue ch = sets[s].Children[c];
                    if (GUILayout.Button((c + 1) + ". " + Clip(ChildSummary(ch), 90), ClipRows.Row)) ShowBranch(ch, true);
                }
            }

            DrawVisibility(n);

            GuiUtil.Space(6);
            if (_tree.Draw("All fields", n, 0)) MarkDirty(null);

            GuiUtil.Space(8);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Duplicate after", GuiUtil.W(130)))
            {
                string why;
                MetaValue copy = _doc.DuplicateAfter(n, out why);
                if (copy == null) SetStatus(why, true);
                else { _selected = copy; MarkDirty("Node duplicated after itself."); }
            }
            if (GUILayout.Button("Delete node", GuiUtil.W(110)))
            {
                _confirm.Open("Delete node", "Remove this " + DlgDocument.Kind(n) + " node and join the nodes around it?", new[] { "Delete", "Cancel" }, c =>
                {
                    if (c != 0) return;
                    int jumps = CountJumpsTo(n);
                    string r = _doc.DeleteNode(n);
                    if (r != null) { SetStatus(r, true); return; }
                    _selected = _branch;
                    MarkDirty("Node deleted." + (jumps > 0 ? " Warning: " + jumps + " jump(s) targeted it by name and now go nowhere." : ""));
                });
            }
            GUILayout.EndHorizontal();
        }

        int CountJumpsTo(MetaValue n)
        {
            ulong nm = DlgDocument.NameHash(n);
            if (nm == 0) return 0;
            int k = 0;
            for (int i = 0; i < _doc.Nodes.Count; i++)
            {
                MetaValue j = _doc.Nodes[i];
                if (j.Type == "DlgNodeJump" && j["mJumpToName"] != null && j["mJumpToName"].Bits == nm) k++;
            }
            return k;
        }

        void DrawChildInspector(MetaValue c)
        {
            DlgDocument.ChildInfo ci = _doc.ChildInfoOf(c);
            GUILayout.Label("Branch (" + DlgDocument.Kind(c) + ")", GuiUtil.SubHeader);
            DrawIdLine(c);
            if (ci != null && _doc.FolderOfChild(c) == null)
                GUILayout.Label(ci.Set.Label + " of " + Clip(NodeSummary(ci.Owner), 70), GuiUtil.Note);

            uint tid = DlgDocument.ChoiceTextId(c);
            if (tid != 0)
            {
                GUILayout.Label("Choice text", GuiUtil.SubHeader);
                DrawLineEditor(tid);
            }

            ulong first = DlgDocument.LinkId(DlgDocument.ChildLink(c));
            GUILayout.BeginHorizontal();
            GUILayout.Label("First node: " + (first == DlgDocument.NoneId ? "(none)" : _doc.NodeById(first) != null ? Clip(NodeSummary(_doc.NodeById(first)), 70) : "missing"), GuiUtil.Wrap);
            if (GUILayout.Button("Change...", GuiUtil.W(80)))
                PickNode("Choose the first node of this branch", null, id => { string r = _doc.SetChildLink(c, id); _doc.Reindex(); MarkDirty("Branch relinked (" + r + ")."); });
            GUILayout.EndHorizontal();

            if (DlgDocument.ConditionSet(c) != null) DrawConditionSet(c, "The branch is available only when these conditions are met");
            DrawVisibility(c);

            MetaValue up = DlgDocument.ObjectProps(c, 1);
            if (up != null && _tree.Draw("User properties", up, 0)) MarkDirty(null);
            GuiUtil.Space(6);
            if (_tree.Draw("All fields", c, 0)) MarkDirty(null);
        }

        // ------------------------------------------------------------ pieces
        void DrawBoolField(MetaValue o, string field, string label)
        {
            MetaValue v = o[field];
            if (v == null) return;
            bool b = GUILayout.Toggle(v.AsBool, " " + label);
            if (b != v.AsBool) { v.AsBool = b; MarkDirty(null); }
        }

        void DrawEnum(MetaValue v, string label, string[] names)
        {
            if (v == null) return;
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GuiUtil.W(120));
            int cur = v.AsInt;
            for (int i = 1; i < names.Length; i++)
            {
                if (GUILayout.Toggle(cur == i, names[i], GUI.skin.button) && cur != i) { v.AsInt = i; MarkDirty(null); }
            }
            if (cur < 1 || cur >= names.Length) GUILayout.Label("(value " + cur + ")", GuiUtil.Note);
            GUILayout.EndHorizontal();
        }

        void DrawChoreField(MetaValue n, string field, string label)
        {
            MetaValue v = n[field];
            if (v == null) return;
            string name = v.Bits == 0 ? null : Lookup(v.Bits);
            GUILayout.Label(label, GuiUtil.SubHeader);
            GUILayout.Label(v.Bits == 0 ? "(none)" : name ?? "0x" + v.Bits.ToString("X16") + " (name not known)", GuiUtil.Wrap);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Replace...", GuiUtil.W(90)))
                _picker.Open("Choose the chore to play", ".chore", v.Bits, (h, nn) => { v.Bits = h; MarkDirty(label + " set to " + nn + "."); });
            if (v.Bits != 0 && GUILayout.Button("Clear", GuiUtil.W(60))) { v.Bits = 0; MarkDirty(label + " cleared."); }
            GUI.enabled = v.Bits != 0;
            if (GUILayout.Button("Open chore", GuiUtil.W(100))) OpenChore(v.Bits);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        void OpenChore(ulong hash)
        {
            string path = FindChore(hash);
            if (path == null)
            {
                string n = Lookup(hash);
                SetStatus((n ?? "0x" + hash.ToString("X16")) + " was not found in the dialogue folder"
                    + (string.IsNullOrEmpty(_choreFolder) ? "" : " or " + _choreFolder)
                    + ". Extract it from its archive there, or set the chore folder.", true);
                return;
            }
            if (_ctx.OpenInModule == null || !_ctx.OpenInModule(ChoresModule.ModuleTitle, path))
                SetStatus("Could not open " + Path.GetFileName(path) + " in the chore editor.", true);
        }

        string FindChore(ulong hash)
        {
            string dir = Dir(_doc.File.SourcePath);
            string key = (dir ?? "") + "|" + (_choreFolder ?? "");
            if (_choreIndex == null || _choreIndexFor != key)
            {
                _choreIndex = new Dictionary<ulong, string>();
                _choreIndexFor = key;
                IndexChores(dir);
                IndexChores(_choreFolder);
            }
            string p;
            return _choreIndex.TryGetValue(hash, out p) ? p : null;
        }

        void IndexChores(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            try
            {
                foreach (string f in Directory.GetFiles(folder, "*.chore", SearchOption.AllDirectories))
                {
                    ulong h = HashDatabase.Crc64(Path.GetFileName(f));
                    if (!_choreIndex.ContainsKey(h)) _choreIndex[h] = f;
                }
            }
            catch (Exception e) { SetStatus("Could not scan " + folder + ": " + e.Message, true); }
        }

        void DrawLines(MetaValue n)
        {
            List<uint> ids = DlgDocument.LineIds(n);
            GUILayout.Label("Lines (" + ids.Count + ")", GuiUtil.SubHeader);
            if (_lang == null) GUILayout.Label("No language file is loaded, so only the raw line ids are shown.", GuiUtil.Warning);
            for (int i = 0; i < ids.Count; i++) DrawLineEditor(ids[i]);
        }

        /// <summary>Speaker and text of one line, editable when the language file has it; raw id otherwise.</summary>
        void DrawLineEditor(uint id)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            if (_lang == null || !_lang.Has(id))
            {
                GUILayout.Label(LineText(id), GuiUtil.Warning);
                GUILayout.EndVertical();
                return;
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("line " + id, GuiUtil.Note, GuiUtil.W(110));
            string sp = _lang.Speaker(id) ?? "";
            string nsp = GUILayout.TextField(sp, GuiUtil.W(140));
            if (nsp != sp) { _lang.SetSpeaker(id, nsp); SetStatus("Speaker changed in the language file. Save to write it.", false); }
            ulong voice = _lang.VoiceHash(id);
            if (voice != 0) GUILayout.Label(HashText(voice), GuiUtil.Note);
            GUILayout.EndHorizontal();
            string tx = _lang.Text(id) ?? "";
            string ntx = GUILayout.TextArea(tx, GUILayout.ExpandWidth(true));
            if (ntx != tx) { _lang.SetText(id, ntx); _graph.Invalidate(); SetStatus("Line text changed in the language file. Save to write it.", false); }
            GUILayout.Label("Subtitle: " + Subtitle(ntx), GuiUtil.Note);
            GUILayout.EndVertical();
        }

        void DrawJump(MetaValue n)
        {
            GUILayout.Label("Jump", GuiUtil.SubHeader);
            DrawEnum(n["mJumpTargetClass"], "Target", new[] { "", "Named node", "Parent", "Node after parent's Wait" });
            DrawEnum(n["mJumpBehavior"], "Behaviour", new[] { "", "Jump and run", "Run and return", "Return" });
            DrawEnum(n["mVisibilityBehavior"], "Visibility", new[] { "", "Ignore", "Obey" });
            GUILayout.BeginHorizontal();
            GUILayout.Label("Target: " + JumpTargetText(n), GuiUtil.Wrap);
            MetaValue jt = n["mJumpToName"] != null && (n["mhJumpToDlg"] == null || n["mhJumpToDlg"].Bits == 0) ? FirstByName(n["mJumpToName"].Bits) : null;
            if (jt != null && GUILayout.Button("Go to target", GuiUtil.W(100))) ShowNode(jt);
            if (GUILayout.Button("Change...", GuiUtil.W(80)))
            {
                // Targets are named objects in this dialogue.
                List<MetaValue> named = new List<MetaValue>();
                List<string> labels = new List<string>();
                for (int i = 0; i < _doc.Nodes.Count; i++)
                    if (DlgDocument.NameHash(_doc.Nodes[i]) != 0) { named.Add(_doc.Nodes[i]); labels.Add(Clip(NodeSummary(_doc.Nodes[i]), 100) + "   " + ShortId(DlgDocument.Id(_doc.Nodes[i]))); }
                _nodePicker.Open("Choose the node to jump to (nodes with a name)", labels, -1, null, i =>
                {
                    n["mJumpToName"].Bits = DlgDocument.NameHash(named[i]);
                    if (n["mhJumpToDlg"] != null) n["mhJumpToDlg"].Bits = 0;
                    if (n["mJumpTargetClass"] != null) n["mJumpTargetClass"].AsInt = 1;
                    MarkDirty("Jump target changed.");
                });
            }
            GUILayout.EndHorizontal();
            MetaValue dlg = n["mhJumpToDlg"];
            if (dlg != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Other dialogue: " + (dlg.Bits == 0 ? "(this one)" : HashText(dlg.Bits)), GuiUtil.Wrap);
                if (GUILayout.Button("Choose .dlog...", GuiUtil.W(110)))
                    _picker.Open("Dialogue to jump into", ".dlog", dlg.Bits, (h, nn) => { dlg.Bits = h; MarkDirty("Jump now goes to " + nn + "."); });
                if (dlg.Bits != 0 && GUILayout.Button("This dialogue", GuiUtil.W(100))) { dlg.Bits = 0; MarkDirty(null); }
                if (dlg.Bits != 0 && GUILayout.Button("Open it", GuiUtil.W(70))) OpenOtherDialogue(dlg.Bits);
                GUILayout.EndHorizontal();
            }
        }

        MetaValue FirstByName(ulong h)
        {
            List<MetaValue> t = _doc.ByName(h);
            return t.Count > 0 ? t[0] : null;
        }

        void PickNode(string title, MetaValue exclude, Action<ulong> onPick)
        {
            List<MetaValue> nodes = new List<MetaValue>();
            List<string> labels = new List<string> { "(none - end here)" };
            nodes.Add(null);
            for (int i = 0; i < _doc.Nodes.Count; i++)
            {
                if (ReferenceEquals(_doc.Nodes[i], exclude)) continue;
                nodes.Add(_doc.Nodes[i]);
                labels.Add(Clip(NodeSummary(_doc.Nodes[i]), 100) + "   " + ShortId(DlgDocument.Id(_doc.Nodes[i])));
            }
            _nodePicker.Open(title, labels, -1, null, i => onPick(nodes[i] == null ? DlgDocument.NoneId : DlgDocument.Id(nodes[i])));
        }

        // ------------------------------------------------------------ conditions
        void DrawVisibility(MetaValue o)
        {
            MetaValue vis = DlgDocument.VisibilityConditions(o);
            if (vis == null) return;
            GUILayout.Label("Visibility conditions", GuiUtil.SubHeader);
            GUILayout.Label("Lua expression that must be true for this to be used (empty = always):", GuiUtil.Note);
            MetaValue s = vis["mScriptVisCond"];
            if (s != null)
            {
                string t = GUILayout.TextArea(s.Text, GuiUtil.Mono, GUILayout.MinHeight(GuiUtil.S(36f)));
                if (t != s.Text) { s.Text = t; MarkDirty(null); }
            }
            DrawBoolField(vis, "mbDiesOff", "Dies off (stops being available once used)");
            bool hasRule = vis["#rule"] != null;
            bool nr = GUILayout.Toggle(hasRule, " Use a rule as well");
            if (nr != hasRule) { _doc.SetVisibilityRule(vis, nr); MarkDirty(nr ? "Visibility rule added." : "Visibility rule removed."); }
            if (vis["#rule"] != null) DrawRule(vis["#rule"]);
        }

        static readonly string[] DurationNames = { "", "Timed", "Indefinitely" };

        void DrawConditionSet(MetaValue o, string caption)
        {
            MetaValue cs = DlgDocument.ConditionSet(o);
            if (cs == null) return;
            List<MetaValue> items = cs["#items"].Items;
            GUILayout.Label("Conditions (" + items.Count + ")", GuiUtil.SubHeader);
            GUILayout.Label(caption + ".", GuiUtil.Note);
            for (int i = 0; i < items.Count; i++)
            {
                MetaValue c = items[i];
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                GUILayout.Label((i + 1) + ". " + DlgDocument.Kind(c), GuiUtil.SubHeader);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Remove", GuiUtil.W(70)))
                {
                    items.RemoveAt(i);
                    MarkDirty("Condition removed.");
                    GUILayout.EndHorizontal();
                    GUILayout.EndVertical();
                    break;
                }
                GUILayout.EndHorizontal();
                switch (c.Type)
                {
                    case "DlgConditionInput":
                        GUILayout.Label("Met when the player gives input.", GuiUtil.Note);
                        break;
                    case "DlgConditionTime":
                        DrawEnum(c["mDurationClass"], "Duration", DurationNames);
                        GUILayout.BeginHorizontal();
                        GUILayout.Label("Seconds", GuiUtil.W(120));
                        if (_tree.DrawLeaf(c["mSeconds"])) MarkDirty(null);
                        GUILayout.EndHorizontal();
                        break;
                    case "DlgConditionRule":
                        DrawRule(c["mRule"]);
                        break;
                    default:
                        if (_tree.Draw("Fields", c, 0)) MarkDirty(null);
                        break;
                }
                GUILayout.EndVertical();
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("Add:", GuiUtil.W(40));
            if (GUILayout.Button("Input", GuiUtil.W(70))) { _doc.AddCondition(cs, "DlgConditionInput"); MarkDirty("Input condition added."); }
            if (GUILayout.Button("Time", GuiUtil.W(70))) { _doc.AddCondition(cs, "DlgConditionTime"); MarkDirty("Time condition added."); }
            if (GUILayout.Button("Rule", GuiUtil.W(70))) { _doc.AddCondition(cs, "DlgConditionRule"); MarkDirty("Rule condition added."); }
            GUILayout.EndHorizontal();
        }

        static readonly string[] RuleGroups = { "mConditions", "mActions", "mElse" };
        static readonly string[] RuleGroupLabels = { "If (tests)", "Then (actions)", "Else (actions)" };
        static readonly string[] CompareNames = { "==", "<", ">", "<=", ">=" };
        static readonly string[] ActionNames = { "set", "add", "subtract" };

        /// <summary>
        /// Rule editor: each group holds named items; an item is a set of keys with values, each
        /// tested (with an optional comparison and negation) or set (with an action).
        /// </summary>
        void DrawRule(MetaValue rule)
        {
            if (rule == null) return;
            if (rule.Kind == MetaKind.Raw) { GUILayout.Label("This rule could not be decoded; it is kept as raw bytes.", GuiUtil.Warning); return; }
            GUILayout.BeginVertical(GUI.skin.box);
            MetaValue fl = rule["mFlags"];
            if (fl != null)
            {
                uint f = fl.FlagsValue;
                GUILayout.BeginHorizontal();
                bool or = (f & 16) != 0;
                bool nor = GUILayout.Toggle(or, " Groups are OR'ed (otherwise AND)", GuiUtil.W(260));
                if (nor != or) { fl.FlagsValue = nor ? f | 16u : f & ~16u; MarkDirty(null); }
                GUILayout.Label("flags " + f, GuiUtil.Note);
                GUILayout.EndHorizontal();
            }
            for (int g = 0; g < RuleGroups.Length; g++)
            {
                MetaValue grp = rule[RuleGroups[g]];
                if (grp == null) continue;
                DrawLogicGroup(grp, RuleGroupLabels[g], g > 0);
            }
            GUILayout.EndVertical();
        }

        void DrawLogicGroup(MetaValue grp, string label, bool actions)
        {
            MetaValue items = grp["mItems"];
            int n = items != null ? items.Items.Count : 0;
            string expr = GroupText(grp, actions);
            if (expr != null) GUILayout.Label(label + ":  " + expr, GuiUtil.Wrap);
            GUILayout.BeginHorizontal();
            GUILayout.Label(label + " - " + n + " item(s)", GuiUtil.SubHeader);
            MetaValue op = grp["mOperator"];
            if (op != null && !actions)
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Toggle(op.AsInt != 2, "AND", GUI.skin.button, GuiUtil.W(50)) && op.AsInt == 2) { op.AsInt = 1; MarkDirty(null); }
                if (GUILayout.Toggle(op.AsInt == 2, "OR", GUI.skin.button, GuiUtil.W(50)) && op.AsInt != 2) { op.AsInt = 2; MarkDirty(null); }
            }
            GUILayout.EndHorizontal();
            if (items == null) return;
            for (int i = 0; i < items.Items.Count; i++)
            {
                MetaValue item = items.Items[i];
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                GUILayout.Label(items.Keys[i].Text, GuiUtil.Note);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Remove item", GuiUtil.W(100)))
                {
                    items.Keys.RemoveAt(i);
                    items.Items.RemoveAt(i);
                    MarkDirty("Rule item removed.");
                    GUILayout.EndHorizontal();
                    GUILayout.EndVertical();
                    break;
                }
                GUILayout.EndHorizontal();
                DrawLogicItem(item, actions);
                GUILayout.EndVertical();
            }
            GUILayout.BeginHorizontal();
            string nm;
            if (!_newItemName.TryGetValue(grp, out nm)) nm = "";
            nm = GUILayout.TextField(nm, GuiUtil.W(180));
            _newItemName[grp] = nm;
            GUI.enabled = nm.Trim().Length > 0;
            if (GUILayout.Button("Add item", GuiUtil.W(80)))
            {
                MetaValue it = _doc.NewLogicItem(nm.Trim());
                items.Keys.Add(MetaValue.NewString(nm.Trim()));
                items.Items.Add(it);
                _newItemName[grp] = "";
                MarkDirty("Rule item added.");
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            MetaValue sub = grp["mLogicGroups"];
            if (sub != null && sub.Items.Count > 0 && _tree.Draw("Nested groups", sub, 0)) MarkDirty(null);
        }

        void DrawLogicItem(MetaValue item, bool actions)
        {
            MetaValue ps = item["Baseclass_PropertySet"];
            if (ps == null || MetaProps.IsRaw(ps)) { GUILayout.Label("Keys could not be decoded.", GuiUtil.Warning); return; }
            MetaValue neg = item["mKeyNegateList"], cmp = item["mKeyComparisonList"], act = item["mKeyActionList"];
            List<MetaValue> groups = MetaProps.Groups(ps);
            for (int g = 0; g < groups.Count; g++)
            {
                MetaValue grp = groups[g];
                for (int i = 0; i < grp.Items.Count; i++)
                {
                    ulong key = grp.Keys[i].Bits;
                    GUILayout.BeginHorizontal();
                    bool unknown = Lookup(key) == null;
                    GUILayout.Label(KeyName(key), unknown ? GuiUtil.Warning : GuiUtil.Wrap, GuiUtil.MinW(160));
                    if (unknown && GUILayout.Button("Name...", GuiUtil.W(64))) { _namingKey = _namingKey == key ? 0 : key; _namingText = ""; }
                    if (GUILayout.Button("Copy", GuiUtil.W(48)))
                    {
                        GUIUtility.systemCopyBuffer = "0x" + key.ToString("X16");
                        SetStatus("Copied key 0x" + key.ToString("X16") + (unknown ? "" : " (" + Lookup(key) + ")") + ".", false);
                    }
                    if (GUILayout.Button("Paste", GuiUtil.W(50)))
                    {
                        ulong nk;
                        string why = ParseKey(GUIUtility.systemCopyBuffer, out nk);
                        if (why != null) SetStatus(why, true);
                        else if (nk != key)
                        {
                            ReplaceKey(item, key, nk);
                            MarkDirty("Key replaced with " + KeyName(nk) + ".");
                            GUILayout.EndHorizontal();
                            return;
                        }
                    }
                    if (!actions)
                    {
                        MetaValue nv = MapGet(neg, key);
                        bool isNeg = nv != null && nv.AsBool;
                        bool nn = GUILayout.Toggle(isNeg, " not", GuiUtil.W(50));
                        if (nn != isNeg) { MapSet(neg, key, MetaValue.NewBool(nn)); MarkDirty(null); }
                        MetaValue cv = MapGet(cmp, key);
                        int c = cv != null ? cv.AsInt : 0;
                        if (GUILayout.Button(c >= 0 && c < CompareNames.Length ? CompareNames[c] : c.ToString(), GuiUtil.W(40)))
                        {
                            MapSet(cmp, key, MetaValue.NewInt("int", (c + 1) % CompareNames.Length));
                            MarkDirty(null);
                        }
                    }
                    else
                    {
                        MetaValue av = MapGet(act, key);
                        int a = av != null ? av.AsInt : 0;
                        if (GUILayout.Button(a >= 0 && a < ActionNames.Length ? ActionNames[a] : a.ToString(), GuiUtil.W(70)))
                        {
                            MapSet(act, key, MetaValue.NewInt("int", (a + 1) % ActionNames.Length));
                            MarkDirty(null);
                        }
                    }
                    if (_tree.DrawLeaf(grp.Items[i])) MarkDirty(null);
                    bool remove = GUILayout.Button("x", GuiUtil.W(24));
                    GUILayout.EndHorizontal();
                    if (_namingKey == key) DrawKeyNamer(key);
                    GUILayout.BeginHorizontal();
                    if (remove)
                    {
                        MetaProps.Remove(ps, key);
                        MapRemove(neg, key);
                        MapRemove(cmp, key);
                        MapRemove(act, key);
                        MarkDirty("Key removed.");
                        GUILayout.EndHorizontal();
                        return;
                    }
                    GUILayout.EndHorizontal();
                }
            }

            // Add a key
            GUILayout.BeginHorizontal();
            string kn;
            if (!_newKeyName.TryGetValue(item, out kn)) kn = "";
            kn = GUILayout.TextField(kn, GuiUtil.MinW(160));
            _newKeyName[item] = kn;
            int kt;
            if (!_newKeyType.TryGetValue(item, out kt)) kt = 0;
            if (GUILayout.Button(KeyTypes[kt], GuiUtil.W(60))) _newKeyType[item] = (kt + 1) % KeyTypes.Length;
            GUI.enabled = kn.Trim().Length > 0;
            if (GUILayout.Button(actions ? "Add key to set" : "Add key to test", GuiUtil.W(120)))
            {
                string name = kn.Trim();
                ulong h = 0;
                bool byHash = name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && HashDatabase.TryParseHex(name, out h);
                if (!byHash) h = HashDatabase.Crc64(name);
                MetaValue v;
                switch (KeyTypes[kt])
                {
                    case "int": v = MetaValue.NewInt("int", 0); break;
                    case "float": v = MetaValue.NewFloat(0f); break;
                    case "String": v = MetaValue.NewString(""); break;
                    default: v = MetaValue.NewBool(true); break;
                }
                MetaProps.Set(ps, h, KeyTypes[kt], v);
                if (!byHash)
                {
                    _doc.RegisterName(name);
                    MetaValue refs = item["mReferenceKeyList"];
                    if (refs != null && refs.Kind == MetaKind.Array) refs.Items.Add(MetaValue.NewString(name));
                }
                _newKeyName[item] = "";
                MarkDirty("Key " + name + " added.");
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        ulong _namingKey;
        string _namingText = "";

        /// <summary>Names an unknown key. Only a name whose hash matches is accepted; it is saved to CustomNames.</summary>
        void DrawKeyNamer(ulong key)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("The file stores only a hash of this key's name. Type the exact name (for example"
                + " \"Drugstore - KennyHit\"); it is accepted only if its hash is 0x" + key.ToString("X16") + ".", GuiUtil.Wrap);
            GUILayout.BeginHorizontal();
            _namingText = GUILayout.TextField(_namingText ?? "", GuiUtil.MinW(240));
            string clean, why;
            bool valid = CustomNameStore.Validate(_namingText, out clean, out why);
            bool match = valid && HashDatabase.Crc64(clean) == key;
            GUI.enabled = match;
            if (GUILayout.Button("Save name", GuiUtil.W(90)))
            {
                SaveName(key, clean);
                _namingKey = 0;
                SetStatus("Key named " + clean + " (saved to " + CustomNameStore.FolderName + ").", false);
            }
            GUI.enabled = true;
            if (GUILayout.Button("Cancel", GuiUtil.W(60))) _namingKey = 0;
            GUILayout.EndHorizontal();
            if (valid && !match) GUILayout.Label("No match: that name hashes to 0x" + HashDatabase.Crc64(clean).ToString("X16") + ".", GuiUtil.Note);
            GUILayout.EndVertical();
        }

        void SaveName(ulong hash, string name)
        {
            if (_doc != null) _doc.RegisterName(name);
            if (_ctx.PropNames.FileRef(hash) != null || _ctx.CustomNames == null) return;
            try
            {
                _ctx.CustomNames.Append(name);
                _ctx.PropNames.AddCustomFileName(hash, name);
            }
            catch (Exception e) { SetStatus("Could not save the name to CustomNames: " + e.Message, true); }
        }

        /// <summary>
        /// Searches names for the dialogue's unnamed rule keys with the "Area - Words" pattern.
        /// Hits are exact hash matches and are saved to CustomNames.
        /// </summary>
        void GuessKeyNames()
        {
            if (_doc == null) return;
            List<ulong> keys = LogicKeyGuesser.RuleKeys(_doc);
            List<ulong> targets = new List<ulong>();
            HashSet<string> areas = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Game" };
            HashSet<string> words = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < LogicKeyGuesser.CommonWords.Length; i++) words.Add(LogicKeyGuesser.CommonWords[i]);
            for (int i = 0; i < keys.Count; i++)
            {
                string n = Lookup(keys[i]);
                if (n == null) { targets.Add(keys[i]); continue; }
                string a = LogicKeyGuesser.AreaOf(n);
                if (a != null) { areas.Add(a); LogicKeyGuesser.AddWords(words, n.Substring(a.Length + 3)); }
            }
            if (targets.Count == 0) { SetStatus("Every rule key in this dialogue already has a name.", false); return; }
            // The dialogue's own name usually starts with its area ("drugstore_carley").
            string stem = Path.GetFileNameWithoutExtension(_doc.File.SourcePath ?? "");
            int us = stem.IndexOf('_');
            string first = us > 0 ? stem.Substring(0, us) : stem;
            if (first.Length > 1) areas.Add(char.ToUpperInvariant(first[0]) + first.Substring(1));
            HashSet<string> subjects = new HashSet<string>(StringComparer.Ordinal);
            _doc.Root.Walk(v => { if (v.Kind == MetaKind.String && v.Bytes != null && v.Bytes.Length < 300) LogicKeyGuesser.AddWords(words, v.Text); });
            if (_lang != null)
            {
                foreach (string t in _lang.AllText()) LogicKeyGuesser.AddWords(words, t);
                foreach (uint id in AllLineIds())
                {
                    string sp = _lang.Speaker(id);
                    if (!string.IsNullOrEmpty(sp) && sp.Length < 20) subjects.Add(char.ToUpperInvariant(sp[0]) + sp.Substring(1).ToLowerInvariant());
                }
            }
            // Very long vocabularies make the two-word search slow; keep the shorter words.
            List<string> wl = new List<string>(words);
            wl.Sort((x, y) => x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y));
            if (wl.Count > 2500) wl.RemoveRange(2500, wl.Count - 2500);
            Dictionary<ulong, string> hits = LogicKeyGuesser.Guess(targets, areas, wl, subjects);
            foreach (KeyValuePair<ulong, string> h in hits) SaveName(h.Key, h.Value);
            List<string> names = new List<string>(hits.Values);
            SetStatus("Key names found: " + hits.Count + " of " + targets.Count + " unnamed rule keys"
                + (names.Count > 0 ? " (" + Clip(string.Join(", ", names.ToArray()), 160) + "), saved to " + CustomNameStore.FolderName : "")
                + ". Areas tried: " + string.Join(", ", new List<string>(areas).ToArray()) + ".", false);
        }

        const string ImportFileName = "imported_strings.txt";

        void BrowseImportNames()
        {
            if (!NativeFileDialog.IsSupported) { SetStatus("The folder dialog is only available in Windows builds.", true); return; }
            _ctx.RunDeferred(() =>
            {
                string p = NativeFileDialog.OpenFolder("Folder with Lua scripts or text files to take names from", Dir(_path));
                if (!string.IsNullOrEmpty(p)) ImportNames(p);
            });
        }

        /// <summary>
        /// Collects every quoted string from .lua and .txt files under a folder (decompiled scripts
        /// keep their string constants) and saves the new ones to CustomNames/imported_strings.txt.
        /// Logic, rule and property keys are hashes of such strings, so they resolve afterwards.
        /// </summary>
        public int ImportNames(string folder)
        {
            if (_ctx.CustomNames == null) { SetStatus("The CustomNames folder is not available.", true); return 0; }
            List<string> files = new List<string>();
            try
            {
                files.AddRange(Directory.GetFiles(folder, "*.lua", SearchOption.AllDirectories));
                files.AddRange(Directory.GetFiles(folder, "*.txt", SearchOption.AllDirectories));
            }
            catch (Exception e) { SetStatus("Could not read " + folder + ": " + e.Message, true); return 0; }
            System.Text.RegularExpressions.Regex q = new System.Text.RegularExpressions.Regex("\"((?:[^\"\\\\\\n]|\\\\.){2,120})\"|'((?:[^'\\\\\\n]|\\\\.){2,120})'");
            HashSet<ulong> have = new HashSet<ulong>();
            List<string> fresh = new List<string>();
            for (int f = 0; f < files.Count; f++)
            {
                string text;
                try { text = File.ReadAllText(files[f]); }
                catch { continue; }
                foreach (System.Text.RegularExpressions.Match m in q.Matches(text))
                {
                    string s = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
                    string clean, why;
                    if (!CustomNameStore.Validate(s, out clean, out why)) continue;
                    ulong h = HashDatabase.Crc64(clean);
                    if (!have.Add(h) || Lookup(h) != null) continue;
                    fresh.Add(clean);
                }
            }
            if (fresh.Count == 0) { SetStatus("No new names in " + files.Count + " file(s) under " + folder + ".", false); return 0; }
            try
            {
                string path = Path.Combine(_ctx.CustomNames.Folder, ImportFileName);
                Directory.CreateDirectory(_ctx.CustomNames.Folder);
                File.AppendAllText(path, string.Join("\r\n", fresh.ToArray()) + "\r\n", new UTF8Encoding(false));
            }
            catch (Exception e) { SetStatus("Could not save imported names: " + e.Message, true); return 0; }
            _ctx.ReloadCustomNames();
            int named = 0, total = 0;
            if (_doc != null)
                foreach (ulong k in LogicKeyGuesser.RuleKeys(_doc)) { total++; if (Lookup(k) != null) named++; }
            SetStatus("Imported " + fresh.Count + " new names from " + files.Count + " file(s) into " + CustomNameStore.FolderName + "/" + ImportFileName
                + (_doc != null ? ". Rule keys named in this dialogue: " + named + " of " + total + "." : "."), false);
            return fresh.Count;
        }

        IEnumerable<uint> AllLineIds()
        {
            for (int i = 0; i < _doc.Nodes.Count; i++)
                foreach (uint id in DlgDocument.LineIds(_doc.Nodes[i])) yield return id;
        }

        /// <summary>Reads a key from pasted text: "0x..." (or 16 hex digits) is taken as the hash, anything else as a name.</summary>
        static string ParseKey(string text, out ulong key)
        {
            key = 0;
            string t = (text ?? "").Trim().Trim('"');
            if (t.Length == 0) return "The clipboard is empty. Copy a key first.";
            bool hex = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                       || (t.Length == 16 && System.Text.RegularExpressions.Regex.IsMatch(t, "^[0-9A-Fa-f]{16}$"));
            if (hex) return HashDatabase.TryParseHex(t, out key) ? null : "The clipboard does not hold a valid hash: " + t;
            string clean, why;
            if (!CustomNameStore.Validate(t, out clean, out why)) return why;
            key = HashDatabase.Crc64(clean);
            return null;
        }

        /// <summary>Swaps a tested/set key for another, keeping its value, negation, comparison and action.</summary>
        void ReplaceKey(MetaValue item, ulong oldKey, ulong newKey)
        {
            MetaValue ps = item["Baseclass_PropertySet"];
            foreach (MetaValue g in MetaProps.Groups(ps))
                for (int i = 0; i < g.Keys.Count; i++)
                    if (g.Keys[i].Bits == oldKey) g.Keys[i].Bits = newKey;
            string[] maps = { "mKeyNegateList", "mKeyComparisonList", "mKeyActionList" };
            for (int m = 0; m < maps.Length; m++)
            {
                MetaValue map = item[maps[m]];
                if (map == null || map.Kind != MetaKind.Map) continue;
                for (int i = 0; i < map.Keys.Count; i++)
                    if (map.Keys[i].Bits == oldKey) map.Keys[i].Bits = newKey;
            }
        }

        static MetaValue MapGet(MetaValue map, ulong key)
        {
            if (map == null || map.Kind != MetaKind.Map) return null;
            for (int i = 0; i < map.Keys.Count; i++) if (map.Keys[i].Bits == key) return map.Items[i];
            return null;
        }

        static void MapSet(MetaValue map, ulong key, MetaValue v)
        {
            if (map == null || map.Kind != MetaKind.Map) return;
            for (int i = 0; i < map.Keys.Count; i++)
                if (map.Keys[i].Bits == key) { map.Items[i] = v; return; }
            map.Keys.Add(MetaValue.NewHash("Symbol", key));
            map.Items.Add(v);
        }

        static void MapRemove(MetaValue map, ulong key)
        {
            if (map == null || map.Kind != MetaKind.Map) return;
            for (int i = 0; i < map.Keys.Count; i++)
                if (map.Keys[i].Bits == key) { map.Keys.RemoveAt(i); map.Items.RemoveAt(i); return; }
        }
    }
}
