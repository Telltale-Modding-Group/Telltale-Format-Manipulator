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
                    b.Append(RuleSummary(n["mRule"]));
                    break;
                default:
                    {
                        List<DlgDocument.ChildSetInfo> sets = DlgDocument.ChildSets(n);
                        int count = 0;
                        for (int i = 0; i < sets.Count; i++) count += sets[i].Children.Count;
                        if (sets.Count > 0) b.Append(count + " branch(es)");
                        break;
                    }
            }
            MetaValue vis = DlgDocument.VisibilityConditions(n);
            if (vis != null && HasVisibilityTest(vis)) b.Append("   {conditional}");
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
            MetaValue cs = DlgDocument.ConditionSet(c);
            int nc = cs != null ? cs["#items"].Items.Count : 0;
            MetaValue vis = DlgDocument.VisibilityConditions(c);
            if (nc > 0 || (vis != null && HasVisibilityTest(vis))) b.Append("   {" + (nc > 0 ? nc + " condition(s)" : "conditional") + "}");
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
                GUILayout.BeginHorizontal();
                float lw = Mathf.Clamp(_area.width * 0.22f, 200f, 400f);
                float cw = Mathf.Clamp(_area.width * 0.33f, 260f, 660f);
                GUILayout.BeginVertical(GUILayout.Width(lw));
                DrawOutline();
                GUILayout.EndVertical();
                GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(cw));
                DrawChain();
                GUILayout.EndVertical();
                GUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandWidth(true));
                _inspScroll = GUILayout.BeginScrollView(_inspScroll, false, true, GUILayout.ExpandHeight(true));
                DrawInspector();
                GUILayout.EndScrollView();
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
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
            GUILayout.Label(_lang != null ? "Language: " + Path.GetFileName(_lang.Path) + " (" + _lang.Count + " lines)" + (_lang.Dirty ? " *" : "")
                : "Language: none", _lang != null ? GuiUtil.Note : GuiUtil.Warning, GUILayout.ExpandWidth(true));
            GUILayout.Label("Chores: " + (string.IsNullOrEmpty(_choreFolder) ? "dialogue folder" : _choreFolder), GuiUtil.Note, GuiUtil.MaxW(360));
            GUILayout.EndHorizontal();
            if (_doc != null && _lang == null)
                GUILayout.Label(_langNote + " Lines are shown as raw ids; pick a language file to see the text.", GuiUtil.Warning);

            if (_doc != null)
            {
                StringBuilder b = new StringBuilder();
                b.Append(Path.GetFileName(_doc.File.SourcePath ?? "(unsaved)"));
                if (_dirty) b.Append(" *");
                b.Append("   |   ").Append(_doc.Folders.Count).Append(" folders, ").Append(_doc.Nodes.Count).Append(" nodes");
                b.Append("   |   project ").Append(_doc.ProjectId);
                if (!_doc.File.FullyDecoded) b.Append("   |   ").Append(_doc.File.RawRegions).Append(" undecoded region(s), kept as-is");
                GUILayout.Label(b.ToString(), GuiUtil.Note);
            }
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

        void PushBack()
        {
            _back.Add(new KeyValuePair<MetaValue, ulong>(_branch, _chainStart));
            if (_back.Count > 100) _back.RemoveAt(0);
        }

        void ShowBranch(MetaValue child, bool remember)
        {
            if (remember) PushBack();
            _branch = child;
            _chainStart = DlgDocument.LinkId(DlgDocument.ChildLink(child));
            _selected = child;
            _chainScroll = Vector2.zero;
            GuiUtil.ResetBuffers();
        }

        /// <summary>Shows the chain a node is in (from its branch when it has one) and selects the node.</summary>
        void ShowNode(MetaValue node)
        {
            PushBack();
            DlgDocument.ChildInfo head = _doc.ChainHead(node);
            if (head != null)
            {
                _branch = head.Child;
                _chainStart = DlgDocument.LinkId(DlgDocument.ChildLink(head.Child));
            }
            else
            {
                _branch = null;
                // Walk back to the first node of this loose chain.
                MetaValue cur = node;
                HashSet<ulong> seen = new HashSet<ulong>();
                while (seen.Add(DlgDocument.Id(cur)))
                {
                    MetaValue p = _doc.NodeById(DlgDocument.LinkId(DlgDocument.PrevLink(cur)));
                    if (p == null) break;
                    cur = p;
                }
                _chainStart = DlgDocument.Id(cur);
            }
            _selected = node;
            GuiUtil.ResetBuffers();
        }

        // ================================================================ chain
        void DrawChain()
        {
            GUILayout.BeginHorizontal();
            GUI.enabled = _back.Count > 0;
            if (GUILayout.Button("< Back", GuiUtil.W(70)))
            {
                KeyValuePair<MetaValue, ulong> b = _back[_back.Count - 1];
                _back.RemoveAt(_back.Count - 1);
                _branch = b.Key;
                _chainStart = b.Value;
                _selected = _branch;
                GuiUtil.ResetBuffers();
            }
            GUI.enabled = true;
            string where = "Chain";
            if (_branch != null)
            {
                DlgDocument.ChildInfo ci = _doc.ChildInfoOf(_branch);
                if (ci != null)
                {
                    MetaValue folder = _doc.FolderOfChild(_branch);
                    where = folder != null ? "Folder branch" : ci.Set.Label + " of " + DlgDocument.Kind(ci.Owner) + " " + ShortId(DlgDocument.Id(ci.Owner));
                }
            }
            GUILayout.Label(where, GuiUtil.SubHeader);
            if (_branch != null)
            {
                DlgDocument.ChildInfo ci = _doc.ChildInfoOf(_branch);
                if (ci != null && _doc.FolderOfChild(_branch) == null && GUILayout.Button("Up to parent", GuiUtil.W(100)))
                    ShowNode(ci.Owner);
            }
            GUILayout.EndHorizontal();

            _chainScroll = GUILayout.BeginScrollView(_chainScroll, false, true, GUILayout.ExpandHeight(true));
            if (_branch != null)
            {
                if (GUILayout.Button("Branch: " + Clip(ChildSummary(_branch), 80), ReferenceEquals(_selected, _branch) ? ClipRows.Selected : ClipRows.Row))
                    _selected = _branch;
            }
            List<MetaValue> chain = _doc.Chain(_chainStart);
            if (chain.Count == 0) GUILayout.Label("This branch has no nodes.", GuiUtil.Note);
            for (int i = 0; i < chain.Count; i++)
            {
                MetaValue n = chain[i];
                GUILayout.BeginVertical(GUI.skin.box);
                if (GUILayout.Button(Clip(NodeSummary(n), 110), ReferenceEquals(n, _selected) ? ClipRows.Selected : ClipRows.Row))
                {
                    _selected = n;
                    _inspScroll = Vector2.zero;
                    GuiUtil.ResetBuffers();
                }
                // Branches of this node open their own chains.
                List<DlgDocument.ChildSetInfo> sets = DlgDocument.ChildSets(n);
                for (int s = 0; s < sets.Count; s++)
                {
                    if (sets[s].Children.Count == 0) continue;
                    GUILayout.Label("  " + sets[s].Label + ":", GuiUtil.Note);
                    for (int c = 0; c < sets[s].Children.Count; c++)
                    {
                        MetaValue ch = sets[s].Children[c];
                        if (GUILayout.Button("    " + (c + 1) + ". " + Clip(ChildSummary(ch), 90) + "  >", ClipRows.Row))
                            ShowBranch(ch, true);
                    }
                }
                if (n.Type == "DlgNodeJump")
                {
                    List<MetaValue> t = _doc.ByName(n["mJumpToName"] != null ? n["mJumpToName"].Bits : 0);
                    MetaValue dlg = n["mhJumpToDlg"];
                    bool external = dlg != null && dlg.Bits != 0;
                    if (!external && t.Count > 0 && GUILayout.Button("    Go to jump target  >", ClipRows.Row))
                    {
                        MetaValue target = t[0];
                        if (_doc.ChildInfoOf(target) != null) ShowBranch(target, true);
                        else ShowNode(target);
                    }
                }
                GUILayout.EndVertical();
            }
            if (chain.Count > 0)
            {
                ulong end = DlgDocument.LinkId(DlgDocument.NextLink(chain[chain.Count - 1]));
                if (end != DlgDocument.NoneId) GUILayout.Label("The chain continues to a node that is missing or already shown (loop).", GuiUtil.Warning);
            }
            GUILayout.EndScrollView();
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
                PickNode("Choose the first node of this branch", null, id => { string r = _doc.SetChildLink(c, id); _doc.Reindex(); _chainStart = DlgDocument.LinkId(DlgDocument.ChildLink(c)); MarkDirty("Branch relinked (" + r + ")."); });
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
            if (ntx != tx) { _lang.SetText(id, ntx); SetStatus("Line text changed in the language file. Save to write it.", false); }
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
                GUILayout.EndHorizontal();
            }
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
                    GUILayout.Label(HashText(key), GuiUtil.Wrap, GuiUtil.MinW(160));
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
                    if (GUILayout.Button("x", GuiUtil.W(24)))
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
                ulong h = HashDatabase.Crc64(name);
                MetaValue v;
                switch (KeyTypes[kt])
                {
                    case "int": v = MetaValue.NewInt("int", 0); break;
                    case "float": v = MetaValue.NewFloat(0f); break;
                    case "String": v = MetaValue.NewString(""); break;
                    default: v = MetaValue.NewBool(true); break;
                }
                MetaProps.Set(ps, h, KeyTypes[kt], v);
                _doc.RegisterName(name);
                MetaValue refs = item["mReferenceKeyList"];
                if (refs != null && refs.Kind == MetaKind.Array) refs.Items.Add(MetaValue.NewString(name));
                _newKeyName[item] = "";
                MarkDirty("Key " + name + " added.");
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
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
