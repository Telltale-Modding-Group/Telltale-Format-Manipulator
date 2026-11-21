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
    /// Chore editor. A chore is a list of agents, each owning resources (animations, sounds,
    /// nested chores, dialogue lines, keyframed agent properties) that play in blocks on the
    /// chore's timeline. Tabs: agents and resources with an inspector, a draggable timeline,
    /// copying agents/resources from another chore, and a raw view of every field.
    /// </summary>
    public class ChoresModule : IFormatModule, IFileOpener
    {
        public const string ModuleTitle = "Chores";
        public string Title { get { return ModuleTitle; } }

        const string ChoreFilter = "Telltale chore (*.chore)\0*.chore\0All files (*.*)\0*.*\0";

        enum Tab { Resources, Timeline, Transplant, Info }

        AppContext _ctx;
        Rect _area;
        Tab _tab;

        ChoreDocument _doc;
        ChoreNameResolver _names;
        bool _dirty;
        string _path = "";

        // Selection: an agent row or a resource row.
        int _selAgent = -1;
        int _selRes = -1;
        Vector2 _listScroll, _inspScroll, _infoScroll, _tlScroll, _tlInspScroll, _trScroll;

        readonly MetaTreeView _tree = new MetaTreeView();
        HashPicker _picker;
        readonly ConfirmDialog _confirm = new ConfirmDialog();

        string _renameBuf = "";
        object _renameFor;
        bool _showProps, _showKeys, _showAll;

        // Transplant
        ChoreDocument _donor;
        ChoreNameResolver _donorNames;
        string _donorPath = "";
        int _donorAgent = -1;
        string _donorNewName = "";
        string _transplantLog = "";

        // Timeline
        float _pxPerSec = 80f;
        bool _snap = true;
        int _dragRes = -1, _dragBlock = -1, _dragMode;
        float _dragMouse0, _dragStart0, _dragEnd0;

        sealed class Row
        {
            public int Agent = -1;
            public int Res = -1;
            public bool Header;
            public string Text;
        }

        readonly List<Row> _rows = new List<Row>();
        bool _rowsDirty = true;

        // ================================================================ module
        public void Initialize(AppContext ctx)
        {
            _ctx = ctx;
            _picker = new HashPicker(ctx);
            _tree.SymbolName = Lookup;
            _tree.PickHash = v => _picker.Open("Choose a name for this value", null, v.Bits, (h, n) =>
            {
                v.Bits = h;
                MarkDirty("Value set to " + n + ".");
            });
        }

        public void Update() { }

        public bool HasUnsavedChanges { get { return _dirty; } }

        public bool SaveAll() { return !_dirty || Save(); }

        void SetStatus(string msg, bool error) { if (_ctx != null) _ctx.SetStatus(msg, error); }

        void MarkDirty(string msg)
        {
            _dirty = true;
            _rowsDirty = true;
            if (msg != null) SetStatus(msg + " Save to write the file.", false);
        }

        string Lookup(ulong h)
        {
            if (h == 0) return null;
            string n = _names != null ? _names.Resolve(h) : _ctx.PropNames.FileRef(h);
            if (n != null) return n;
            n = _ctx.PropNames.Key(h);
            if (n != null) return n;
            n = MetaLayouts.Current.TypeName(h);
            if (n != null) return n;
            string bone;
            if (_ctx.BoneNames != null && _ctx.BoneNames.TryGetName(h, out bone)) return bone;
            return null;
        }

        string ResLabel(MetaValue res)
        {
            ulong h = ChoreDocument.ResName(res);
            string n = Lookup(h) ?? ("0x" + h.ToString("X16"));
            string k = ShortKind(ChoreDocument.KindName(res));
            string off = ChoreDocument.GetBool(res, "mbEnabled") ? "" : "  (off)";
            return "[" + k + "] " + n + off;
        }

        static string ShortKind(string kind)
        {
            switch (kind)
            {
                case "Animation": return "anim";
                case "Chore": return "chore";
                case "SoundData": return "sound";
                case "LanguageRes": return "line";
                case "SoundEventData": return "event";
                case "Properties": return "props";
                default: return kind.StartsWith("Embedded", StringComparison.Ordinal) ? "embedded" : kind;
            }
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
                GUILayout.Label("No chore loaded. Use [Open .chore...] or type a path and press [Load path].", GuiUtil.Note);
                GUILayout.Label("Chores are the timelines the game plays for actions and cutscenes: agents (characters, cameras,"
                    + " objects) with the animations, sounds, dialogue lines, nested chores and property keyframes they play.", GuiUtil.Wrap);
                GUILayout.EndArea();
                DrawPopups();
                return;
            }
            if (_rowsDirty) BuildRows();
            DrawTabs();
            switch (_tab)
            {
                case Tab.Resources: DrawResourcesTab(); break;
                case Tab.Timeline: DrawTimelineTab(); break;
                case Tab.Transplant: DrawTransplantTab(); break;
                case Tab.Info: DrawInfoTab(); break;
            }
            GUILayout.EndArea();
            DrawPopups();
        }

        void DrawPopups()
        {
            Rect screen = _ctx.ScreenRect.width > 0 ? _ctx.ScreenRect : new Rect(0, 0, Screen.width, Screen.height);
            if (_picker.IsOpen) _picker.OnGUI(screen);
            if (_confirm.IsOpen) _confirm.OnGUI(screen);
        }

        void DrawToolbar()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Open .chore...", GuiUtil.W(110))) BrowseOpen();
            if (GUILayout.Button("Reload", GuiUtil.W(70))) Reload();
            GUI.enabled = _doc != null && _dirty;
            if (GUILayout.Button("Save", GuiUtil.W(70))) Save();
            GUI.enabled = _doc != null;
            if (GUILayout.Button("Save as...", GuiUtil.W(80))) SaveAs();
            GUI.enabled = true;
            _path = GUILayout.TextField(_path ?? "", GuiUtil.MinW(200));
            if (GUILayout.Button("Load path", GuiUtil.W(80)))
            {
                string p = (_path ?? "").Trim().Trim('"');
                if (p.Length > 0) _ctx.RunDeferred(() => LoadFile(p));
            }
            GUILayout.EndHorizontal();

            if (_doc != null)
            {
                StringBuilder b = new StringBuilder();
                b.Append(Path.GetFileName(_doc.File.SourcePath ?? "(unsaved)"));
                if (_dirty) b.Append(" *");
                b.Append("   |   ").Append(_doc.Agents.Count).Append(" agents, ").Append(_doc.Resources.Count).Append(" resources");
                b.Append("   |   length ").Append(GuiUtil.Fmt(_doc.Length)).Append(" s");
                b.Append("   |   scene ").Append(_doc.SceneFile);
                if (!_doc.File.FullyDecoded) b.Append("   |   ").Append(_doc.File.RawRegions).Append(" undecoded region(s), kept as-is");
                GUILayout.Label(b.ToString(), GuiUtil.Note);
            }
        }

        void DrawTabs()
        {
            GUILayout.BeginHorizontal();
            string[] tabs = { "Agents & resources", "Timeline", "Copy from another chore", "Info / all fields" };
            for (int i = 0; i < tabs.Length; i++)
            {
                bool active = (int)_tab == i;
                if (GUILayout.Toggle(active, tabs[i], GUI.skin.button, GUILayout.ExpandWidth(true)) && !active) _tab = (Tab)i;
            }
            GUILayout.EndHorizontal();
        }

        // ================================================================ file I/O
        void BrowseOpen()
        {
            if (!NativeFileDialog.IsSupported)
            {
                SetStatus("The file dialog is only available in Windows builds. Type a path and press [Load path].", true);
                return;
            }
            _ctx.RunDeferred(() =>
            {
                string p = NativeFileDialog.OpenFile("Open Telltale chore", ChoreFilter, CurrentDirectory());
                if (!string.IsNullOrEmpty(p)) LoadFile(p);
            });
        }

        string CurrentDirectory()
        {
            try { if (!string.IsNullOrEmpty(_path) && File.Exists(_path)) return Path.GetDirectoryName(_path); }
            catch { }
            return null;
        }

        public bool OpenFile(string path)
        {
            if (_dirty)
            {
                _confirm.Open("Unsaved changes", "The open chore has unsaved changes. Open " + Path.GetFileName(path) + " anyway?",
                    new[] { "Save, then open", "Discard changes", "Cancel" }, c =>
                    {
                        if (c == 0 && Save()) _ctx.RunDeferred(() => LoadFile(path));
                        else if (c == 1) _ctx.RunDeferred(() => LoadFile(path));
                    });
                return true;
            }
            _ctx.RunDeferred(() => LoadFile(path));
            return true;
        }

        void LoadFile(string path)
        {
            try
            {
                ChoreDocument doc = ChoreDocument.Load(path);
                ChoreNameResolver.SeedLanguageNames(ToList(_ctx.PropNames.FileNameList));
                _doc = doc;
                _names = new ChoreNameResolver(doc, h => _ctx.PropNames.FileRef(h));
                _path = path;
                _dirty = false;
                _selAgent = doc.Agents.Count > 0 ? 0 : -1;
                _selRes = -1;
                _rowsDirty = true;
                _tree.Clear();
                _listScroll = _inspScroll = _infoScroll = _tlScroll = Vector2.zero;
                GuiUtil.ResetBuffers();
                int unresolved = 0;
                for (int i = 0; i < doc.Resources.Count; i++) if (_names.Resolve(ChoreDocument.ResName(doc.Resources[i])) == null) unresolved++;
                string msg = "Loaded " + Path.GetFileName(path) + " - " + doc.Agents.Count + " agents, " + doc.Resources.Count + " resources";
                if (unresolved > 0) msg += ", " + unresolved + " resource name(s) unresolved";
                if (!doc.File.FullyDecoded) msg += ". " + doc.File.RawRegions + " region(s) could not be decoded and are kept as raw bytes";
                SetStatus(msg + ".", !doc.File.FullyDecoded);
            }
            catch (MetaParseException ex) { SetStatus("Could not read " + Path.GetFileName(path) + ": " + ex.Message, true); }
            catch (Exception ex) { SetStatus("Could not open file: " + ex.Message, true); }
        }

        static IList<string> ToList(IReadOnlyList<string> l)
        {
            IList<string> asList = l as IList<string>;
            if (asList != null) return asList;
            List<string> r = new List<string>(l.Count);
            for (int i = 0; i < l.Count; i++) r.Add(l[i]);
            return r;
        }

        void Reload()
        {
            if (_doc == null || string.IsNullOrEmpty(_doc.File.SourcePath)) { SetStatus("Nothing to reload.", true); return; }
            if (_dirty)
            {
                string p = _doc.File.SourcePath;
                _confirm.Open("Reload", "Discard unsaved changes and reload " + Path.GetFileName(p) + "?", new[] { "Reload", "Cancel" },
                    c => { if (c == 0) _ctx.RunDeferred(() => LoadFile(p)); });
                return;
            }
            LoadFile(_doc.File.SourcePath);
        }

        bool Save()
        {
            if (_doc == null) return true;
            if (string.IsNullOrEmpty(_doc.File.SourcePath)) { SetStatus("This chore has no path; use Save as.", true); return false; }
            return SaveTo(_doc.File.SourcePath);
        }

        void SaveAs()
        {
            if (!NativeFileDialog.IsSupported) { SetStatus("The file dialog is only available in Windows builds.", true); return; }
            _ctx.RunDeferred(() =>
            {
                string p = NativeFileDialog.SaveFile("Save chore as", ChoreFilter, CurrentDirectory(),
                    Path.GetFileName(_doc.File.SourcePath ?? "chore.chore"), "chore");
                if (!string.IsNullOrEmpty(p)) SaveTo(p);
            });
        }

        bool SaveTo(string path)
        {
            try
            {
                // The chore stores its own file name; keep it in step when saving under a new name.
                string oldName = _doc.Name;
                string newName = Path.GetFileName(path);
                bool renamed = !string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase)
                               && oldName.EndsWith(".chore", StringComparison.OrdinalIgnoreCase);
                if (renamed) _doc.Name = newName;
                _doc.File.Save(path);
                _path = path;
                _dirty = false;
                SetStatus("Saved " + newName + (renamed ? " (its internal name was updated to match)" : "") + ".", false);
                return true;
            }
            catch (Exception ex)
            {
                SetStatus("Save failed: " + ex.Message, true);
                return false;
            }
        }

        // ================================================================ rows
        void BuildRows()
        {
            _rows.Clear();
            if (_doc == null) return;
            bool[] owned = new bool[_doc.Resources.Count];
            for (int a = 0; a < _doc.Agents.Count; a++)
            {
                List<int> idx = ChoreDocument.AgentResourceIndices(_doc.Agents[a]);
                _rows.Add(new Row { Agent = a, Text = ChoreDocument.AgentName(_doc.Agents[a]) + "   (" + idx.Count + ")" });
                for (int i = 0; i < idx.Count; i++)
                {
                    int r = idx[i];
                    if (r < 0 || r >= _doc.Resources.Count)
                    {
                        _rows.Add(new Row { Agent = a, Res = -1, Text = "    (bad resource index " + r + ")" });
                        continue;
                    }
                    owned[r] = true;
                    _rows.Add(new Row { Agent = a, Res = r, Text = "    " + ResLabel(_doc.Resources[r]) });
                }
            }
            bool header = false;
            for (int r = 0; r < owned.Length; r++)
            {
                if (owned[r]) continue;
                if (!header) { _rows.Add(new Row { Header = true, Text = "Resources no agent uses" }); header = true; }
                _rows.Add(new Row { Res = r, Text = "    " + ResLabel(_doc.Resources[r]) });
            }
            _rowsDirty = false;
        }

        // ================================================================ resources tab
        void DrawResourcesTab()
        {
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(Mathf.Clamp(_area.width * 0.38f, 260f, 560f)));
            GUILayout.Label("Agents and their resources", GuiUtil.SubHeader);
            float listH = Mathf.Max(120f, _area.height - GuiUtil.S(120f));
            _listScroll = GuiUtil.VirtualList(_listScroll, listH, _rows.Count, GuiUtil.RowHeight, (i, r) =>
            {
                Row row = _rows[i];
                bool sel = !row.Header && row.Res == _selRes && (row.Res >= 0 ? (row.Agent == _selAgent || row.Agent < 0) : row.Agent == _selAgent);
                GUIStyle st = row.Header ? ClipRows.Note : sel ? ClipRows.Selected : ClipRows.Row;
                if (GUI.Button(r, row.Text, st) && !row.Header)
                {
                    _selAgent = row.Agent;
                    _selRes = row.Res;
                    _inspScroll = Vector2.zero;
                    _renameFor = null;
                    GuiUtil.ResetBuffers();
                }
            });
            GUILayout.EndVertical();

            GUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandWidth(true));
            _inspScroll = GUILayout.BeginScrollView(_inspScroll, false, true, GUILayout.ExpandHeight(true));
            if (_selRes >= 0 && _selRes < _doc.Resources.Count) DrawResourceInspector(_selRes, true);
            else if (_selAgent >= 0 && _selAgent < _doc.Agents.Count) DrawAgentInspector(_selAgent);
            else DrawChoreInspector();
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        void DrawChoreInspector()
        {
            GUILayout.Label("Chore", GuiUtil.SubHeader);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Length (s)", GuiUtil.W(140));
            float len = _doc.Length;
            if (GuiUtil.FloatField("chore.len", ref len, GuiUtil.W(100)) && len >= 0f) { _doc.Length = len; MarkDirty("Chore length set."); }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Scene file", GuiUtil.W(140));
            string sc = GUILayout.TextField(_doc.SceneFile, GuiUtil.MinW(200));
            if (sc != _doc.SceneFile) { _doc.SceneFile = sc; MarkDirty(null); }
            GUILayout.EndHorizontal();
            List<string> deps = _doc.Dependencies();
            if (deps.Count > 0) GUILayout.Label("Depends on: " + string.Join(", ", deps.ToArray()), GuiUtil.Note);
            GuiUtil.Space(6);
            GUILayout.Label("Select an agent or a resource on the left.", GuiUtil.Note);
        }

        void DrawAgentInspector(int ai)
        {
            MetaValue agent = _doc.Agents[ai];
            string name = ChoreDocument.AgentName(agent);
            GUILayout.Label("Agent: " + name, GuiUtil.SubHeader);

            if (!ReferenceEquals(_renameFor, agent)) { _renameFor = agent; _renameBuf = name; }
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", GuiUtil.W(140));
            _renameBuf = GUILayout.TextField(_renameBuf ?? "", GuiUtil.MinW(200));
            GUI.enabled = !string.IsNullOrEmpty(_renameBuf) && _renameBuf != name;
            if (GUILayout.Button("Rename", GuiUtil.W(80)))
            {
                int n = _doc.RenameAgent(agent, _renameBuf.Trim());
                _names = new ChoreNameResolver(_doc, h => _ctx.PropNames.FileRef(h));
                MarkDirty("Renamed agent to " + _renameBuf.Trim() + (n > 0 ? "; " + n + " resource(s) named after it were renamed too." : "."));
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label("The agent must exist in the scene the chore plays in. Resources named after the agent"
                + " (\"" + name + "\", \"" + name + " Blocking\") are renamed with it.", GuiUtil.Note);

            MetaValue actor = agent.Path("mAABinding.mActorName");
            if (actor != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Actor binding", GuiUtil.W(140));
                string s = GUILayout.TextField(actor.Text, GuiUtil.MinW(200));
                if (s != actor.Text) { actor.Text = s; MarkDirty(null); }
                GUILayout.EndHorizontal();
            }

            GuiUtil.Space(6);
            GUILayout.Label("Resources", GuiUtil.SubHeader);
            List<int> idx = ChoreDocument.AgentResourceIndices(agent);
            for (int i = 0; i < idx.Count; i++)
            {
                int r = idx[i];
                if (r < 0 || r >= _doc.Resources.Count) { GUILayout.Label("bad index " + r, GuiUtil.Error); continue; }
                if (GUILayout.Button(ResLabel(_doc.Resources[r]), ClipRows.Row)) { _selRes = r; _selAgent = ai; GuiUtil.ResetBuffers(); }
            }

            GuiUtil.Space(6);
            if (_tree.Draw("Attachment", agent["mAttachment"], 0)) MarkDirty(null);
            if (_tree.Draw("Enabled rule", agent["mAgentEnabledRule"], 0)) MarkDirty(null);
            if (_tree.Draw("All agent fields", agent, 0)) MarkDirty(null);

            GuiUtil.Space(10);
            if (GUILayout.Button("Delete this agent", GuiUtil.W(160)))
            {
                _confirm.Open("Delete agent", "Delete " + name + " and the resources only it uses?", new[] { "Delete", "Cancel" }, c =>
                {
                    if (c != 0) return;
                    _doc.DeleteAgent(ai);
                    _selAgent = -1;
                    _selRes = -1;
                    MarkDirty("Deleted agent " + name + ".");
                });
            }
        }

        /// <summary>Inspector for one resource; <paramref name="full"/> adds the raw views.</summary>
        void DrawResourceInspector(int ri, bool full)
        {
            MetaValue res = _doc.Resources[ri];
            string kind = ChoreDocument.KindName(res);
            ulong h = ChoreDocument.ResName(res);
            ChoreNameResolver.Source src;
            string name = _names.Resolve(h, out src);
            GUILayout.Label("Resource " + ri + "  (" + kind + ")", GuiUtil.SubHeader);
            GUILayout.Label(name != null ? name : "Name not known", name != null ? GuiUtil.Wrap : GuiUtil.Warning);
            GUILayout.Label("hash 0x" + h.ToString("X16") + "   |   name from: " + ChoreNameResolver.SourceLabel(src), GuiUtil.Note);
            if (kind == "LanguageRes" && name != null && name.EndsWith(".lang", StringComparison.OrdinalIgnoreCase))
            {
                uint lineId;
                string said = uint.TryParse(name.Substring(0, name.Length - 5), out lineId) && _ctx.LineText != null ? _ctx.LineText(lineId) : null;
                GUILayout.Label(said != null ? "Line: " + said : "Line text: open the dialogue and its language file on the Dialogues tab to see it here.",
                    said != null ? GuiUtil.Wrap : GuiUtil.Note);
            }
            if (kind == "SoundEventData" && name == null)
                GUILayout.Label("Sound event names come from the FMOD banks (Master Bank.strings.bank / *.soundeventbankmap),"
                    + " which are not loaded yet.", GuiUtil.Note);

            List<int> owners = _doc.OwnersOf(ri);
            List<string> on = new List<string>();
            for (int i = 0; i < owners.Count; i++) on.Add(ChoreDocument.AgentName(_doc.Agents[owners[i]]));
            GUILayout.Label("Used by: " + (on.Count == 0 ? "no agent" : string.Join(", ", on.ToArray())), GuiUtil.Note);

            GUILayout.BeginHorizontal();
            bool isFile = ChoreDocument.IsFileResource(res);
            if (isFile)
            {
                if (GUILayout.Button("Replace reference...", GuiUtil.W(170)))
                {
                    string ext = ChoreDocument.ExtensionForKind(kind);
                    _picker.Open("Choose the " + kind + " this resource should play" + (ext != null ? " (" + ext + ")" : ""), ext, h, (nh, nn) =>
                    {
                        ChoreDocument.SetReference(res, nh, 0);
                        MarkDirty("Resource now plays " + nn + ".");
                    });
                }
            }
            else if (GUILayout.Button("Rename...", GuiUtil.W(110)))
            {
                _picker.Open("Name for this resource (usually the agent name, or \"<agent> Blocking\")", null, h, (nh, nn) =>
                {
                    ChoreDocument.SetResName(res, nh);
                    MarkDirty("Resource renamed to " + nn + ".");
                });
            }
            if (GUILayout.Button("Copy hash", GuiUtil.W(90))) { GUIUtility.systemCopyBuffer = "0x" + h.ToString("X16"); SetStatus("Copied.", false); }
            if (kind == "Chore" && name != null && GUILayout.Button("Open this chore", GuiUtil.W(130))) OpenNested(name);
            GUILayout.EndHorizontal();

            GuiUtil.Space(4);
            GUILayout.BeginHorizontal();
            bool en = ChoreDocument.GetBool(res, "mbEnabled");
            bool nen = GUILayout.Toggle(en, " Enabled", GuiUtil.W(100));
            if (nen != en) { ChoreDocument.SetBool(res, "mbEnabled", nen); MarkDirty(nen ? "Resource enabled." : "Resource disabled."); }
            bool np = ChoreDocument.GetBool(res, "mbNoPose");
            bool nnp = GUILayout.Toggle(np, " No pose", GuiUtil.W(100));
            if (nnp != np) { ChoreDocument.SetBool(res, "mbNoPose", nnp); MarkDirty(null); }
            GUILayout.Label("Priority", GuiUtil.W(60));
            if (_tree.DrawLeaf(res["mPriority"])) MarkDirty(null);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Resource length (s)", GuiUtil.W(140));
            if (_tree.DrawLeaf(res["mResLength"])) MarkDirty(null);
            GUILayout.EndHorizontal();

            GuiUtil.Space(6);
            GUILayout.Label("Blocks (when it plays on the chore timeline)", GuiUtil.SubHeader);
            List<MetaValue> bl = ChoreDocument.Blocks(res);
            if (bl.Count == 0) GUILayout.Label(isFile ? "No blocks: this resource never plays." : "No blocks: property keyframes apply over the whole chore.", GuiUtil.Note);
            for (int b = 0; b < bl.Count; b++)
            {
                MetaValue blk = bl[b];
                GUILayout.BeginHorizontal();
                GUILayout.Label("#" + b + "  start", GuiUtil.W(70));
                float s = ChoreDocument.BlockStart(blk), e = ChoreDocument.BlockEnd(blk);
                bool c = GuiUtil.FloatField("blk" + ri + "s" + b, ref s, GuiUtil.W(80));
                GUILayout.Label("end", GuiUtil.W(30));
                c |= GuiUtil.FloatField("blk" + ri + "e" + b, ref e, GuiUtil.W(80));
                if (c) { ChoreDocument.SetBlock(blk, s, e); MarkDirty(null); }
                GUILayout.Label("scale", GuiUtil.W(40));
                if (_tree.DrawLeaf(blk["mScale"])) MarkDirty(null);
                MetaValue loop = blk["mbLoopingBlock"];
                bool lp = GUILayout.Toggle(loop.AsBool, " loop", GuiUtil.W(60));
                if (lp != loop.AsBool) { loop.AsBool = lp; MarkDirty(null); }
                if (GUILayout.Button("Remove", GuiUtil.W(70))) { bl.RemoveAt(b); MarkDirty("Block removed."); GUILayout.EndHorizontal(); break; }
                GUILayout.EndHorizontal();
            }
            if (GUILayout.Button("Add block", GuiUtil.W(100)))
            {
                float st = bl.Count > 0 ? ChoreDocument.BlockEnd(bl[bl.Count - 1]) : 0f;
                float len = Math.Max(0.1f, ChoreDocument.ResLength(res));
                _doc.AddBlock(res, st, st + len);
                MarkDirty("Block added.");
            }

            if (!full) return;
            GuiUtil.Space(8);
            if (_tree.Draw("Properties (" + MetaProps.Count(res["mResourceProperties"]) + ")", res["mResourceProperties"], 0)) MarkDirty(null);
            MetaValue ctrl = res["mControlAnimation"];
            if (_tree.Draw("Keyframes (" + ChoreDocument.ControlValues(res).Count + " curves)", ctrl, 0)) MarkDirty(null);
            if (res["#embedded"] != null && _tree.Draw("Embedded " + res["#embedded"].Type, res["#embedded"], 0)) MarkDirty(null);
            if (_tree.Draw("Body-part weights", res["mResourceGroupInclude"], 0)) MarkDirty(null);
            if (_tree.Draw("All resource fields", res, 0)) MarkDirty(null);

            GuiUtil.Space(10);
            if (GUILayout.Button("Delete this resource", GuiUtil.W(170)))
            {
                _confirm.Open("Delete resource", "Delete resource " + ri + " (" + (name ?? "unnamed") + ")? Agents that use it lose it.",
                    new[] { "Delete", "Cancel" }, c =>
                    {
                        if (c != 0) return;
                        _doc.DeleteResource(ri);
                        _selRes = -1;
                        MarkDirty("Resource deleted.");
                    });
            }
        }

        void OpenNested(string choreName)
        {
            string dir = _doc.File.SourcePath != null ? Path.GetDirectoryName(_doc.File.SourcePath) : null;
            string p = dir != null ? Path.Combine(dir, choreName) : null;
            if (p == null || !File.Exists(p))
            {
                SetStatus(choreName + " is not in the same folder as this chore. Extract it from the archive next to this file to open it.", true);
                return;
            }
            OpenFile(p);
        }

        // ================================================================ timeline
        static readonly Color ColAnim = new Color(0.35f, 0.6f, 0.95f);
        static readonly Color ColChore = new Color(0.65f, 0.45f, 0.9f);
        static readonly Color ColSound = new Color(0.35f, 0.8f, 0.5f);
        static readonly Color ColLine = new Color(0.95f, 0.75f, 0.3f);
        static readonly Color ColOther = new Color(0.6f, 0.6f, 0.6f);
        static readonly Color ColKey = new Color(0.95f, 0.5f, 0.35f);

        static Color KindColor(string kind)
        {
            switch (kind)
            {
                case "Animation": return ColAnim;
                case "Chore": return ColChore;
                case "SoundData":
                case "SoundEventData": return ColSound;
                case "LanguageRes": return ColLine;
                default: return ColOther;
            }
        }

        static void Fill(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        void DrawTimelineTab()
        {
            float t0, t1;
            _doc.TimelineRange(out t0, out t1);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Zoom", GuiUtil.W(40));
            _pxPerSec = GUILayout.HorizontalSlider(_pxPerSec, 10f, 600f, GuiUtil.W(200));
            GUILayout.Label(Mathf.RoundToInt(_pxPerSec) + " px/s", GuiUtil.W(70));
            _snap = GUILayout.Toggle(_snap, " Snap to 1/30 s", GuiUtil.W(130));
            GUILayout.FlexibleSpace();
            GUILayout.Label("Drag a bar to move it, drag its ends to trim. Orange marks are property keyframes.", GuiUtil.Note);
            GUILayout.EndHorizontal();

            float labelW = GuiUtil.S(240f);
            float rowH = GuiUtil.S(20f);
            float rulerH = GuiUtil.S(20f);
            float trackW = Mathf.Max(200f, (t1 - t0) * _pxPerSec * GuiUtil.Scale + GuiUtil.S(40f));
            float viewH = Mathf.Max(150f, (_area.height - GuiUtil.S(110f)) * 0.62f);
            Rect view = GUILayoutUtility.GetRect(10f, viewH, GUILayout.ExpandWidth(true));
            Rect content = new Rect(0, 0, labelW + trackW, rulerH + _rows.Count * rowH + 4f);
            _tlScroll = GUI.BeginScrollView(view, _tlScroll, content);

            float scale = _pxPerSec * GuiUtil.Scale;
            Func<float, float> X = t => labelW + (t - t0) * scale;

            // Ruler
            Fill(new Rect(labelW, 0, trackW, rulerH), new Color(0.12f, 0.12f, 0.14f));
            float step = NiceStep(60f / scale);
            for (float t = Mathf.Ceil(t0 / step) * step; t <= t1 + 0.0001f; t += step)
            {
                float x = X(t);
                Fill(new Rect(x, rulerH - 6f, 1f, 6f), Color.gray);
                GUI.Label(new Rect(x + 2f, 0, 80f, rulerH), GuiUtil.Fmt((float)Math.Round(t, 3)), GuiUtil.Note);
            }

            Event e = Event.current;
            for (int i = 0; i < _rows.Count; i++)
            {
                Row row = _rows[i];
                float y = rulerH + i * rowH;
                Rect lab = new Rect(_tlScroll.x, y, labelW - 4f, rowH);
                if (row.Res < 0) Fill(new Rect(0, y, labelW + trackW, rowH), new Color(0.2f, 0.2f, 0.23f));
                bool sel = row.Res >= 0 && row.Res == _selRes;
                if (GUI.Button(lab, row.Text, row.Header ? ClipRows.Note : sel ? ClipRows.Selected : ClipRows.Row) && !row.Header)
                {
                    _selRes = row.Res;
                    _selAgent = row.Agent;
                    GuiUtil.ResetBuffers();
                }
                if (row.Res < 0) continue;
                MetaValue res = _doc.Resources[row.Res];
                string kind = ChoreDocument.KindName(res);
                Color col = KindColor(kind);
                if (!ChoreDocument.GetBool(res, "mbEnabled")) col *= 0.5f;
                List<MetaValue> bl = ChoreDocument.Blocks(res);
                for (int b = 0; b < bl.Count; b++)
                {
                    float s = ChoreDocument.BlockStart(bl[b]), en = ChoreDocument.BlockEnd(bl[b]);
                    Rect bar = new Rect(X(s), y + 2f, Mathf.Max(3f, (en - s) * scale), rowH - 4f);
                    Fill(bar, sel ? Color.Lerp(col, Color.white, 0.35f) : col);
                    HandleBarInput(e, bar, row.Res, b, s, en);
                }
                if (bl.Count == 0)
                {
                    List<float> keys = ChoreDocument.KeyTimes(res);
                    for (int k = 0; k < keys.Count && k < 2000; k++)
                        Fill(new Rect(X(keys[k]) - 2f, y + rowH * 0.5f - 3f, 5f, 6f), ColKey);
                }
            }

            // Chore length marker
            float lx = X(_doc.Length);
            Fill(new Rect(lx, 0, 2f, content.height), new Color(1f, 0.3f, 0.3f, 0.8f));
            if (t0 < 0f) Fill(new Rect(X(0f), 0, 1f, content.height), new Color(1f, 1f, 1f, 0.4f));

            if (e.type == EventType.MouseDrag && _dragRes >= 0) { UpdateDrag(e.mousePosition.x, scale); e.Use(); }
            if (e.rawType == EventType.MouseUp && _dragRes >= 0)
            {
                _dragRes = -1;
                MarkDirty("Block moved.");
            }
            GUI.EndScrollView();

            GuiUtil.Space(4);
            _tlInspScroll = GUILayout.BeginScrollView(_tlInspScroll, GUI.skin.box, GUILayout.ExpandHeight(true));
            if (_selRes >= 0 && _selRes < _doc.Resources.Count) DrawResourceInspector(_selRes, false);
            else GUILayout.Label("Click a bar or a row to inspect a resource.", GuiUtil.Note);
            GUILayout.EndScrollView();
        }

        void HandleBarInput(Event e, Rect bar, int res, int block, float s, float en)
        {
            if (e.type != EventType.MouseDown || e.button != 0 || !bar.Contains(e.mousePosition)) return;
            float edge = Mathf.Min(GuiUtil.S(6f), bar.width / 3f);
            _dragMode = e.mousePosition.x < bar.x + edge ? 1 : e.mousePosition.x > bar.xMax - edge ? 2 : 0;
            _dragRes = res;
            _dragBlock = block;
            _dragMouse0 = e.mousePosition.x;
            _dragStart0 = s;
            _dragEnd0 = en;
            _selRes = res;
            List<int> owners = _doc.OwnersOf(res);
            _selAgent = owners.Count > 0 ? owners[0] : -1;
            GuiUtil.ResetBuffers();
            e.Use();
        }

        void UpdateDrag(float mouseX, float scale)
        {
            if (_dragRes < 0 || _dragRes >= _doc.Resources.Count) { _dragRes = -1; return; }
            List<MetaValue> bl = ChoreDocument.Blocks(_doc.Resources[_dragRes]);
            if (_dragBlock < 0 || _dragBlock >= bl.Count) { _dragRes = -1; return; }
            float dt = (mouseX - _dragMouse0) / scale;
            float s = _dragStart0, en = _dragEnd0;
            if (_dragMode == 0) { s += dt; en += dt; }
            else if (_dragMode == 1) s = Mathf.Min(s + dt, en - 0.001f);
            else en = Mathf.Max(en + dt, s + 0.001f);
            if (_snap)
            {
                float q = 1f / 30f;
                if (_dragMode == 0) { float len = en - s; s = Mathf.Round(s / q) * q; en = s + len; }
                else if (_dragMode == 1) s = Mathf.Min(Mathf.Round(s / q) * q, en - 0.001f);
                else en = Mathf.Max(Mathf.Round(en / q) * q, s + 0.001f);
            }
            ChoreDocument.SetBlock(bl[_dragBlock], s, en);
            _dirty = true;
        }

        static float NiceStep(float minStep)
        {
            float[] steps = { 0.01f, 0.02f, 0.05f, 0.1f, 0.2f, 0.25f, 0.5f, 1f, 2f, 5f, 10f, 15f, 30f, 60f };
            for (int i = 0; i < steps.Length; i++) if (steps[i] >= minStep) return steps[i];
            return 120f;
        }

        // ================================================================ transplant
        void DrawTransplantTab()
        {
            GUILayout.Label("Copy agents or resources from another chore into this one", GuiUtil.SubHeader);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Choose donor chore...", GuiUtil.W(170)))
            {
                if (!NativeFileDialog.IsSupported) SetStatus("The file dialog is only available in Windows builds. Type a path.", true);
                else _ctx.RunDeferred(() =>
                {
                    string p = NativeFileDialog.OpenFile("Choose donor chore", ChoreFilter, CurrentDirectory());
                    if (!string.IsNullOrEmpty(p)) LoadDonor(p);
                });
            }
            _donorPath = GUILayout.TextField(_donorPath ?? "", GuiUtil.MinW(200));
            if (GUILayout.Button("Load", GuiUtil.W(60))) { string p = _donorPath.Trim().Trim('"'); _ctx.RunDeferred(() => LoadDonor(p)); }
            GUILayout.EndHorizontal();

            if (_donor == null)
            {
                GUILayout.Label("Pick a donor chore. Its agents can be copied here with all of their resources (blocks, keyframes,"
                    + " properties); header types the copied data needs are added with the donor's versions.", GuiUtil.Note);
                return;
            }

            GUILayout.Label("Donor: " + Path.GetFileName(_donor.File.SourcePath) + "   |   " + _donor.Agents.Count + " agents, "
                + _donor.Resources.Count + " resources   |   scene " + _donor.SceneFile, GuiUtil.Note);
            if (!string.Equals(_donor.SceneFile, _doc.SceneFile, StringComparison.OrdinalIgnoreCase))
                GUILayout.Label("The donor plays in a different scene (" + _donor.SceneFile + " vs " + _doc.SceneFile
                    + "). Copied agents must exist in this chore's scene.", GuiUtil.Warning);

            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(Mathf.Clamp(_area.width * 0.4f, 260f, 520f)));
            _trScroll = GUILayout.BeginScrollView(_trScroll, GUI.skin.box, GUILayout.ExpandHeight(true));
            for (int a = 0; a < _donor.Agents.Count; a++)
            {
                MetaValue ag = _donor.Agents[a];
                string label = ChoreDocument.AgentName(ag) + "   (" + ChoreDocument.AgentResourceIndices(ag).Count + " resources)";
                if (GUILayout.Button(label, a == _donorAgent ? ClipRows.Selected : ClipRows.Row))
                {
                    _donorAgent = a;
                    _donorNewName = ChoreDocument.AgentName(ag);
                }
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            GUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandWidth(true));
            if (_donorAgent >= 0 && _donorAgent < _donor.Agents.Count)
            {
                MetaValue ag = _donor.Agents[_donorAgent];
                GUILayout.Label("Agent " + ChoreDocument.AgentName(ag), GuiUtil.SubHeader);
                List<int> idx = ChoreDocument.AgentResourceIndices(ag);
                for (int i = 0; i < idx.Count && i < 60; i++)
                {
                    int r = idx[i];
                    if (r < 0 || r >= _donor.Resources.Count) continue;
                    MetaValue res = _donor.Resources[r];
                    GUILayout.BeginHorizontal();
                    ulong h = ChoreDocument.ResName(res);
                    GUILayout.Label("[" + ShortKind(ChoreDocument.KindName(res)) + "] " + (_donorNames.Resolve(h) ?? "0x" + h.ToString("X16")), GuiUtil.Wrap);
                    GUI.enabled = _selAgent >= 0 && _selAgent < _doc.Agents.Count;
                    if (GUILayout.Button("Copy to selected agent", GuiUtil.W(170)))
                    {
                        int ni = _doc.TransplantResource(_donor, r, _selAgent);
                        _transplantLog = "Copied resource " + r + " to " + ChoreDocument.AgentName(_doc.Agents[_selAgent]) + " as resource " + ni + ".";
                        RefreshNames();
                        MarkDirty(_transplantLog);
                    }
                    GUI.enabled = true;
                    GUILayout.EndHorizontal();
                }
                if (idx.Count > 60) GUILayout.Label("... and " + (idx.Count - 60) + " more", GuiUtil.Note);
                GUILayout.Label(_selAgent >= 0 && _selAgent < _doc.Agents.Count
                    ? "Selected agent in this chore: " + ChoreDocument.AgentName(_doc.Agents[_selAgent])
                    : "Select an agent on the first tab to copy single resources to it.", GuiUtil.Note);

                GuiUtil.Space(6);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Name in this chore", GuiUtil.W(140));
                _donorNewName = GUILayout.TextField(_donorNewName ?? "", GuiUtil.MinW(160));
                GUILayout.EndHorizontal();
                if (GUILayout.Button("Copy agent with all its resources", GuiUtil.W(250)))
                {
                    var r = _doc.TransplantAgent(_donor, _donorAgent, (_donorNewName ?? "").Trim());
                    StringBuilder b = new StringBuilder();
                    b.Append("Copied agent with ").Append(r.ResourcesAdded).Append(" resource(s).");
                    if (r.HeaderTypesAdded.Count > 0) b.Append(" Added header types: ").Append(string.Join(", ", r.HeaderTypesAdded.ToArray())).Append('.');
                    for (int i = 0; i < r.Warnings.Count; i++) b.Append("\n- ").Append(r.Warnings[i]);
                    _transplantLog = b.ToString();
                    _selAgent = r.AgentIndex;
                    _selRes = -1;
                    RefreshNames();
                    MarkDirty("Copied agent " + _donorNewName + ".");
                }
            }
            else GUILayout.Label("Select a donor agent.", GuiUtil.Note);
            if (!string.IsNullOrEmpty(_transplantLog)) GUILayout.Label(_transplantLog, GuiUtil.Wrap);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        void RefreshNames() { _names = new ChoreNameResolver(_doc, h => _ctx.PropNames.FileRef(h)); }

        void LoadDonor(string path)
        {
            try
            {
                _donor = ChoreDocument.Load(path);
                _donorNames = new ChoreNameResolver(_donor, h => _ctx.PropNames.FileRef(h));
                _donorPath = path;
                _donorAgent = -1;
                _transplantLog = "";
                SetStatus("Donor loaded: " + Path.GetFileName(path) + ".", false);
            }
            catch (Exception ex) { SetStatus("Could not open donor: " + ex.Message, true); }
        }

        // ================================================================ info
        void DrawInfoTab()
        {
            _infoScroll = GUILayout.BeginScrollView(_infoScroll, false, true, GUILayout.ExpandHeight(true));
            GUILayout.Label("File", GuiUtil.SubHeader);
            GUILayout.Label("Internal name: " + _doc.Name, GuiUtil.Note);
            GUILayout.Label("Header types (" + _doc.File.Types.Count + "):", GuiUtil.Note);
            StringBuilder b = new StringBuilder();
            for (int i = 0; i < _doc.File.Types.Count; i++)
            {
                MetaHeaderType t = _doc.File.Types[i];
                b.Append(t.Name ?? ("0x" + t.Hash.ToString("X16"))).Append("  version 0x").Append(t.Crc.ToString("X8")).Append('\n');
            }
            GuiUtil.ReadOnlyText(b.ToString());
            if (_doc.File.Warnings.Count > 0)
            {
                GUILayout.Label("Reading notes", GuiUtil.SubHeader);
                for (int i = 0; i < _doc.File.Warnings.Count && i < 50; i++) GUILayout.Label(_doc.File.Warnings[i], GuiUtil.Warning);
            }
            GuiUtil.Space(6);
            GUILayout.Label("All fields (every value in the file can be edited here)", GuiUtil.SubHeader);
            if (_tree.Draw("Chore", _doc.Root, 0)) MarkDirty(null);
            GUILayout.EndScrollView();
        }
    }
}
