using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TFM.Core;
using UnityEngine;

namespace TFM.App
{
    /// <summary>
    /// .skl bone editor: open, inspect, edit, port bones between skeletons and save in place.
    /// </summary>
    public class BonesModule : IFormatModule
    {
        public string Title { get { return "Bones"; } }

        enum Tab { Bones, Port, Scan, Info }

        AppContext _ctx;
        Rect _area;
        SklFile _file;
        bool _dirty;
        string _path = "";
        string _statusText = "";
        bool _statusError;

        Tab _tab = Tab.Bones;
        int _selected = -1;
        string _filter = "";
        string _appliedFilter = null;
        readonly List<int> _visible = new List<int>();
        Vector2 _treeScroll, _inspectorScroll, _infoScroll, _logScroll;
        int _scrollToRow = -1;

        // Depth caching for hierarchy indentation
        int[] _depth = new int[0];

        // Port tab state
        string _donorPath = "";
        SklFile _donor;
        readonly List<int> _missing = new List<int>();
        readonly List<string> _missingNames = new List<string>();
        readonly List<bool> _missingChecked = new List<bool>();
        string _missingFilter = "";
        string _appliedMissingFilter = null;
        readonly List<int> _missingVisible = new List<int>();
        Vector2 _missingListScroll;
        bool _remapMirror = true;
        string _portLog = "";

        // Scan tab state
        string _scanResult = "";
        string _scanSourceName = "";
        Vector2 _scanScroll;

        // Scanned-file editing: the raw bytes of the last scanned file, its discovered hits and
        // the renames queued against them. Lets non-.skl files (meshes, archives, ...) be searched
        // for bone name references and have those references rewritten, like the old tool's scan.
        byte[] _scanBytes;
        string _scanPath;
        List<HashHit> _scanHits = new List<HashHit>();
        readonly Dictionary<ulong, ulong> _scanRenames = new Dictionary<ulong, ulong>();
        Vector2 _scanHitsScroll;
        readonly ListPicker _scanPicker = new ListPicker();
        ulong _scanRenameOld;

        readonly ListPicker _namePicker = new ListPicker();
        readonly ListPicker _parentPicker = new ListPicker();
        readonly ListPicker _mirrorPicker = new ListPicker();

        // ------------------------------------------------------------- lifecycle
        public void Initialize(AppContext ctx)
        {
            _ctx = ctx;
        }

        public bool HasUnsavedChanges { get { return _dirty; } }

        public bool SaveAll()
        {
            return Save();
        }

        public void Update() { }

        // ---------------------------------------------------------------- layout
        public void OnGUI(Rect area)
        {
            Event e = Event.current;
            if (e.type == EventType.Layout || e.type == EventType.Repaint) GuiUtil.BeginFrame();

            _area = area;
            GUILayout.BeginArea(area);

            DrawToolbar();

            GUILayout.BeginHorizontal();
            string[] tabs = { "Bones", "Port bones", "Scan / Compare", "Info" };
            for (int i = 0; i < tabs.Length; i++)
            {
                bool active = (int)_tab == i;
                if (GUILayout.Toggle(active, tabs[i], GUI.skin.button, GUILayout.ExpandWidth(true)) && !active)
                    _tab = (Tab)i;
            }
            GUILayout.EndHorizontal();

            if (_file == null)
            {
                GuiUtil.Space(10);
                GUILayout.Label("No file loaded. Use [Open (.skl or other)] to load a skeleton or scan another file for bone data.", GuiUtil.Note);
                GUILayout.EndArea();
                DrawPopups();
                return;
            }

            switch (_tab)
            {
                case Tab.Bones: DrawBonesTab(); break;
                case Tab.Port: DrawPortTab(); break;
                case Tab.Scan: DrawScanTab(); break;
                case Tab.Info: DrawInfoTab(); break;
            }

            GUILayout.EndArea();
            DrawPopups();
        }

        void DrawPopups()
        {
            Rect screen = _ctx.ScreenRect.width > 0 ? _ctx.ScreenRect : new Rect(0, 0, Screen.width, Screen.height);
            _namePicker.OnGUI(screen);
            _parentPicker.OnGUI(screen);
            _mirrorPicker.OnGUI(screen);
            _scanPicker.OnGUI(screen);
        }

        void DrawToolbar()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Open (.skl or other)", GuiUtil.W(140))) BrowseOpen();
            if (GUILayout.Button("Reload", GuiUtil.W(70))) Reload();
            GUI.enabled = _file != null && _dirty;
            if (GUILayout.Button("Save", GuiUtil.W(70))) Save();
            GUI.enabled = true;

            _path = GUILayout.TextField(_path ?? "", GuiUtil.MinW(200));
            if (GUILayout.Button("Load path", GuiUtil.W(80))) LoadPath();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (_file == null)
            {
                GUILayout.Label("No file loaded.", GuiUtil.Note);
            }
            else
            {
                var b = new StringBuilder();
                b.Append(Path.GetFileName(_file.SourcePath ?? "(unsaved)"));
                if (_dirty) b.Append(" *");
                b.Append("   |   ").Append(string.IsNullOrEmpty(_file.MagicText) ? "unknown magic" : _file.MagicText);
                b.Append("   |   ").Append(_file.Layout != null ? _file.Layout.Label : "unknown layout");
                b.Append("   |   ").Append(_file.Bones.Count).Append(" bones");
                b.Append("   |   ").Append(_file.OriginalSize).Append(" bytes");
                GUILayout.Label(b.ToString());

                if (_file.LeftoverBytes != 0)
                {
                    GUI.color = new Color(1f, 0.8f, 0.3f);
                    GUILayout.Label("  warning: " + _file.LeftoverBytes + " unparsed trailing bytes", GuiUtil.W(280));
                    GUI.color = Color.white;
                }
            }
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_statusText))
            {
                GUILayout.Label(_statusText, _statusError ? GuiUtil.StatusError : GuiUtil.StatusOk);
            }
            GuiUtil.Space(2);
        }

        void SetStatus(string msg, bool error)
        {
            _statusText = msg;
            _statusError = error;
            if (_ctx != null) _ctx.SetStatus(msg, error);
        }

        // ------------------------------------------------------------- file I/O
        void BrowseOpen()
        {
            if (!NativeFileDialog.IsSupported)
            {
                SetStatus("Native file dialog is only available in Windows builds. Type a path and press [Load path].", true);
                return;
            }
            _ctx.RunDeferred(() =>
            {
                string p = NativeFileDialog.OpenFile("Open Telltale skeleton",
                    NativeFileDialog.SklFilter, CurrentDirectory());
                if (!string.IsNullOrEmpty(p)) LoadFile(p);
            });
        }

        string CurrentDirectory()
        {
            try
            {
                if (!string.IsNullOrEmpty(_path) && File.Exists(_path)) return Path.GetDirectoryName(_path);
            }
            catch { }
            return null;
        }

        void LoadPath()
        {
            string p = (_path ?? "").Trim().Trim('"');
            if (p.Length == 0) { SetStatus("Enter a path first.", true); return; }
            _ctx.RunDeferred(() => LoadFile(p));
        }

        void LoadFile(string path)
        {
            LoadFile(path, false);
        }

        bool LoadFile(string path, bool keepView)
        {
            try
            {
                SklFile f = SklFile.Load(path, _ctx.BoneNames);
                int keepSel = _selected;
                _file = f;
                _path = path;
                _dirty = false;
                _appliedFilter = null;
                if (keepView && keepSel >= 0 && keepSel < f.Bones.Count)
                {
                    _selected = keepSel;
                    _scrollToRow = keepSel;
                    GuiUtil.ResetBuffers();
                    RebuildDepth();
                    if (_donor != null) RefreshMissing();
                    return true;
                }
                _selected = f.Bones.Count > 0 ? 0 : -1;
                _filter = "";
                _treeScroll = Vector2.zero;
                _inspectorScroll = Vector2.zero;
                _donor = null;
                _donorPath = "";
                _missing.Clear();
                _missingNames.Clear();
                _missingChecked.Clear();
                _portLog = "";
                _scanResult = "";
                _scanSourceName = "";
                RebuildDepth();
                GuiUtil.ResetBuffers();
                SetStatus("Loaded " + Path.GetFileName(path) + " - " + f.Bones.Count + " bones, layout " + f.Layout.Label, false);
                return true;
            }
            catch (SklParseException ex)
            {
                SetStatus("Could not parse " + Path.GetFileName(path) + ": " + ex.Message, true);
                return false;
            }
            catch (Exception ex)
            {
                SetStatus("Could not open file: " + ex.Message, true);
                return false;
            }
        }

        void Reload()
        {
            if (_file == null || string.IsNullOrEmpty(_file.SourcePath))
            {
                SetStatus("Nothing to reload.", true);
                return;
            }
            if (_dirty)
            {
                SetStatus("Unsaved changes - save first or reopen the file.", true);
                return;
            }
            LoadFile(_file.SourcePath);
        }

        bool Save()
        {
            if (_file == null) return true;
            if (string.IsNullOrEmpty(_file.SourcePath))
            {
                SetStatus("This file has no path; use Open to load it from disk.", true);
                return false;
            }
            try
            {
                string path = _file.SourcePath;
                _file.Save(path);
                _dirty = false;
                // Reload so the UI reflects exactly what is on disk (Save rebuilds the bone list).
                int bones = _file.Bones.Count;
                if (!LoadFile(path, true)) return false;
                SetStatus("Saved " + Path.GetFileName(path) + " (" + bones + " bones).", false);
                return true;
            }
            catch (Exception ex)
            {
                SetStatus("Save failed: " + ex.Message, true);
                return false;
            }
        }

        // ------------------------------------------------------------ hierarchy
        void RebuildDepth()
        {
            int n = _file == null ? 0 : _file.Bones.Count;
            _depth = new int[n];
            for (int i = 0; i < n; i++) _depth[i] = 0;
            // Parents must precede children in a valid skeleton; guard the depth anyway.
            for (int i = 0; i < n; i++)
            {
                int p = _file.Bones[i].ParentIndex;
                if (p >= 0 && p < i) _depth[i] = _depth[p] + 1;
            }
        }

        void Refilter()
        {
            if (_appliedFilter == _filter) return;
            _appliedFilter = _filter;
            _visible.Clear();
            string q = (_filter ?? "").Trim();
            var bones = _file.Bones;
            if (q.Length == 0)
            {
                for (int i = 0; i < bones.Count; i++) _visible.Add(i);
            }
            else
            {
                for (int i = 0; i < bones.Count; i++)
                {
                    string name = _file.BoneName(i, _ctx.BoneNames);
                    if (name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) _visible.Add(i);
                    else if (i.ToString() == q) _visible.Add(i);
                }
            }
            _treeScroll = Vector2.zero;
        }

        void DrawBonesTab()
        {
            GUILayout.BeginHorizontal();

            // ---- left: hierarchy
            // _area is in real pixels, so this width is given unscaled (raw) on purpose.
            GUILayout.BeginVertical(GUILayout.Width(Mathf.Clamp(_area.width * 0.30f, 200f, 420f)));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Bones", GuiUtil.SubHeader);
            GUILayout.FlexibleSpace();
            GUILayout.Label(_file.Bones.Count + " total", GuiUtil.Note, GuiUtil.W(70));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Filter", GuiUtil.W(38));
            _filter = GUILayout.TextField(_filter);
            if (GUILayout.Button("x", GuiUtil.W(22))) { _filter = ""; _appliedFilter = null; }
            GUILayout.EndHorizontal();

            Refilter();

            float listH = Mathf.Max(120f, _area.height - GuiUtil.S(150f));
            if (_scrollToRow >= 0)
            {
                int pos = _visible.IndexOf(_scrollToRow);
                if (pos >= 0) GuiUtil.ScrollTo(ref _treeScroll, pos, GuiUtil.RowHeight, listH);
                _scrollToRow = -1;
            }

            _treeScroll = GuiUtil.VirtualList(_treeScroll, listH, _visible.Count, GuiUtil.RowHeight, (row, r) =>
            {
                int idx = _visible[row];
                string name = _file.BoneName(idx, _ctx.BoneNames);
                string label = new string(' ', _depth[idx] * 3) + idx + "  " + name;
                if (GUI.Button(r, label, idx == _selected ? GuiUtil.SelectedRow : GuiUtil.Row))
                    SelectBone(idx);
            });

            GUILayout.EndVertical();

            // ---- right: inspector
            GUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandWidth(true));
            if (_selected < 0 || _selected >= _file.Bones.Count)
            {
                GUILayout.Label("Select a bone to edit it.", GuiUtil.Note);
            }
            else
            {
                _inspectorScroll = GUILayout.BeginScrollView(_inspectorScroll, false, true,
                    GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
                DrawInspector(_file.Bones[_selected]);
                GUILayout.EndScrollView();
            }
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
        }

        void SelectBone(int idx)
        {
            if (_selected == idx) return;
            _selected = idx;
            _inspectorScroll = Vector2.zero;
            GuiUtil.ResetBuffers();
        }

        // ------------------------------------------------------------- inspector
        void DrawInspector(SklBone bone)
        {
            SklFile f = _file;
            HashDatabase db = _ctx.BoneNames;

            GUILayout.BeginHorizontal();
            GUILayout.Label("Bone " + bone.Index, GuiUtil.Header);
            GUILayout.Label("   " + f.BoneName(bone.Index, db), GuiUtil.Header);
            GUILayout.EndHorizontal();

            // ---- identity
            GuiUtil.Space(4);
            GUILayout.Label("Identity", GuiUtil.SubHeader);
            GUILayout.BeginVertical(GUI.skin.box);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", GuiUtil.W(120));
            GUILayout.Label(f.IsTextLayout ? (bone.TextName ?? "") : db.NameOrHex(bone.Hash));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Change name...", GuiUtil.W(110)))
                OpenNamePicker(bone);
            GUILayout.EndHorizontal();
            if (!f.IsTextLayout)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Hash", GuiUtil.W(120));
                GUILayout.Label(HashDatabase.Hex(bone.Hash) + "   ("
                    + (db.Contains(bone.Hash) ? "in name list" : "not in name list") + ")", GuiUtil.Note);
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Parent", GuiUtil.W(120));
            string parentText = bone.ParentIndex < 0
                ? "(none - root bone)"
                : bone.ParentIndex + "  " + f.BoneName(bone.ParentIndex, db);
            GUILayout.Label(parentText);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Change parent...", GuiUtil.W(110)))
                OpenParentPicker(bone);
            GUILayout.EndHorizontal();

            if (bone.HasMirror)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Mirror bone", GuiUtil.W(120));
                string mirrorText = bone.MirrorIndex < 0
                    ? "(none)"
                    : bone.MirrorIndex + "  " + f.BoneName(bone.MirrorIndex, db);
                GUILayout.Label(mirrorText);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Change mirror...", GuiUtil.W(110)))
                    OpenMirrorPicker(bone);
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Record size", GuiUtil.W(120));
            GUILayout.Label(bone.RawLength + " bytes", GuiUtil.Note);
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();

            // ---- transform
            GuiUtil.Space(4);
            GUILayout.Label("Transform", GuiUtil.SubHeader);
            GUILayout.BeginVertical(GUI.skin.box);

            Vec3 pos = bone.LocalPosition;
            if (GuiUtil.Vec3Field("Local position", "t.pos", ref pos))
            {
                bone.LocalPosition = pos;
                MarkDirty();
            }

            Quat rot = bone.LocalRotation;
            if (GuiUtil.RotationField("Local rotation", "t.rot", ref rot))
            {
                bone.LocalRotation = rot;
                MarkDirty();
            }

            GUILayout.EndVertical();

            // ---- rest transform / scale
            // The reference importer (TelltaleGames_D3DMesh.ms) reads these bytes but discards
            // their values, so what they mean is inferred, not documented. Labelled experimental.
            GuiUtil.Space(4);
            ExperimentalHeader("Rest transform and scales",
                "Not used by the reference importer - these values are inferred from the byte layout, "
                + "so treat whatever you do here as experimental.");
            GUILayout.BeginVertical(GUI.skin.box);

            Quat restRot = bone.RestRotation;
            if (GuiUtil.RotationField("Rest rotation", "t.restrot", ref restRot))
            {
                bone.RestRotation = restRot;
                MarkDirty();
            }
            Vec3 restPos = bone.RestPosition;
            if (GuiUtil.Vec3Field("Rest position", "t.restpos", ref restPos))
            {
                bone.RestPosition = restPos;
                MarkDirty();
            }

            GuiUtil.Space(2);
            Vec3 gs = bone.GlobalTranslationScale;
            if (GuiUtil.Vec3Field("Global trans. scale", "t.gs", ref gs))
            {
                bone.GlobalTranslationScale = gs;
                MarkDirty();
            }
            Vec3 ls = bone.LocalTranslationScale;
            if (GuiUtil.Vec3Field("Local trans. scale", "t.ls", ref ls))
            {
                bone.LocalTranslationScale = ls;
                MarkDirty();
            }
            Vec3 an = bone.AnimTranslationScale;
            if (GuiUtil.Vec3Field("Anim trans. scale", "t.an", ref an))
            {
                bone.AnimTranslationScale = an;
                MarkDirty();
            }
            GUILayout.EndVertical();

            // ---- resource groups
            GuiUtil.Space(4);
            ExperimentalHeader("Resource groups (" + bone.ResourceGroups.Count + ")",
                "Bitfield groups the reference importer skips. Only the weight values are understood.");
            GUILayout.BeginVertical(GUI.skin.box);
            if (bone.ResourceGroups.Count == 0)
                GUILayout.Label("None.", GuiUtil.Note);
            for (int i = 0; i < bone.ResourceGroups.Count; i++)
            {
                SklResourceGroup g = bone.ResourceGroups[i];
                GUILayout.BeginHorizontal();
                string gname;
                if (!g.IsHashed) gname = g.Name ?? "(no name)";
                else gname = db.TryGetName(g.Hash, out gname) ? gname : HashDatabase.Hex(g.Hash);
                GUILayout.Label(i + "  " + gname, GuiUtil.MinW(200));
                GUILayout.Label("weight", GuiUtil.Note, GuiUtil.W(44));
                float w = g.Weight;
                if (GuiUtil.FloatField("rg.w" + i, ref w, GuiUtil.W(70)))
                {
                    g.Weight = w;
                    MarkDirty();
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();

            // ---- constraints
            if (bone.HasConstraints)
            {
                GuiUtil.Space(4);
                GUILayout.Label("Constraints", GuiUtil.SubHeader);
                GUILayout.BeginVertical(GUI.skin.box);
                for (int i = 0; i < bone.ConstraintAxes.Count; i++)
                {
                    Vec3 ax = bone.ConstraintAxes[i];
                    if (GuiUtil.Vec3Field("Axis " + i, "c.ax" + i, ref ax))
                    {
                        bone.ConstraintAxes[i] = ax;
                        MarkDirty();
                    }
                }
                if (bone.ConstraintRanges != null)
                {
                    GUILayout.Label("Angle ranges (stored in radians, edited in degrees)", GuiUtil.Note);
                    string[] axisNames = { "X", "Y", "Z" };
                    for (int a = 0; a < 3 && a * 2 + 1 < bone.ConstraintRanges.Length; a++)
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(axisNames[a] + " min/max", GuiUtil.W(150));
                        float min = bone.ConstraintRanges[a * 2] * Mathf.Rad2Deg;
                        float max = bone.ConstraintRanges[a * 2 + 1] * Mathf.Rad2Deg;
                        bool ch = GuiUtil.FloatField("c.min" + a, ref min, GuiUtil.MinW(60));
                        ch |= GuiUtil.FloatField("c.max" + a, ref max, GuiUtil.MinW(60));
                        if (ch)
                        {
                            bone.ConstraintRanges[a * 2] = min * Mathf.Deg2Rad;
                            bone.ConstraintRanges[a * 2 + 1] = max * Mathf.Deg2Rad;
                            MarkDirty();
                        }
                        GUILayout.EndHorizontal();
                    }
                }
                float extra = bone.ConstraintExtra;
                if (GuiUtil.LabeledFloat("Extra", "c.extra", ref extra))
                {
                    bone.ConstraintExtra = extra;
                    MarkDirty();
                }
                GUILayout.EndVertical();
            }

            GuiUtil.Space(6);
            GUILayout.Label("Field names are reverse-engineered from the 3DS Max import script; "
                          + "unlabelled values are not exposed here.", GuiUtil.Note);
            GuiUtil.Space(6);
        }

        void MarkDirty()
        {
            if (!_dirty)
            {
                _dirty = true;
                SetStatus("Modified - press Save to write the changes back to the file.", false);
            }
        }

        /// <summary>Section header that marks the group below it as reverse-engineered.</summary>
        static void ExperimentalHeader(string title, string explanation)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(title, GuiUtil.SubHeader);
            GUILayout.Label("experimental", GuiUtil.ExperimentalTag, GuiUtil.W(90));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(explanation)) GUILayout.Label(explanation, GuiUtil.Note);
        }

        // ----------------------------------------------------------------- pickers
        void OpenNamePicker(SklBone bone)
        {
            int target = bone.Index;
            var boneSet = new HashSet<ulong>();
            foreach (SklBone b in _file.Bones) if (b.Index != target) boneSet.Add(b.Hash);

            _namePicker.Open("Choose bone name", _ctx.BoneNames.SortedNames,
                FindNameIndex(_file.BoneName(target, _ctx.BoneNames)),
                i => boneSet.Contains(HashDatabase.Crc64(_ctx.BoneNames.SortedNames[i]))
                        ? "already used by another bone" : null,
                i => ApplyName(target, _ctx.BoneNames.SortedNames[i]));

            _namePicker.EnableCustomEntry(
                "Advanced: custom name or raw hash",
                "Only use this when the name you need is missing from the list. A custom text name is hashed "
                + "with CRC64, which matches the hash list's algorithm, but game code will only understand "
                + "names it knows. A raw 0x hash is written as-is.",
                text =>
                {
                    ulong h;
                    if (HashDatabase.TryParseHex(text, out h)) return "Will write raw hash " + HashDatabase.Hex(h);
                    return "Will write " + HashDatabase.Hex(HashDatabase.Crc64(text)) + " (CRC64 of \"" + text + "\")";
                },
                text =>
                {
                    ulong h;
                    if (HashDatabase.TryParseHex(text, out h)) ApplyRawHash(target, h);
                    else ApplyName(target, text);
                });
        }

        int FindNameIndex(string name)
        {
            var names = _ctx.BoneNames.SortedNames;
            for (int i = 0; i < names.Count; i++)
                if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        void ApplyName(int boneIndex, string name)
        {
            try
            {
                _file.RenameBone(boneIndex, name, _ctx.BoneNames);
                MarkDirty();
                SetStatus("Renamed bone " + boneIndex + " to " + name + ".", false);
            }
            catch (Exception ex)
            {
                SetStatus("Rename failed: " + ex.Message, true);
            }
        }

        void ApplyRawHash(int boneIndex, ulong hash)
        {
            try
            {
                _file.RenameBoneByHash(boneIndex, hash);
                MarkDirty();
                SetStatus("Bone " + boneIndex + " set to hash " + HashDatabase.Hex(hash) + ".", false);
            }
            catch (Exception ex)
            {
                SetStatus("Rename failed: " + ex.Message, true);
            }
        }

        void OpenParentPicker(SklBone bone)
        {
            int target = bone.Index;
            // Only bones earlier in the list can be a parent (children always follow parents).
            var candidates = new List<string>();
            var indices = new List<int>();
            candidates.Add("(none - make root bone)");
            indices.Add(-1);
            for (int i = 0; i < target; i++)
            {
                candidates.Add(i + "  " + _file.BoneName(i, _ctx.BoneNames));
                indices.Add(i);
            }
            int preselect = 0;
            if (bone.ParentIndex >= 0)
            {
                int pos = indices.IndexOf(bone.ParentIndex);
                preselect = pos >= 0 ? pos : 0;
            }

            _parentPicker.Open("Choose parent bone", candidates, preselect, null,
                i => ApplyParent(target, indices[i]));
        }

        void ApplyParent(int boneIndex, int parentIndex)
        {
            try
            {
                _file.SetParent(boneIndex, parentIndex);
                RebuildDepth();
                MarkDirty();
                SetStatus("Bone " + boneIndex + " parent set to "
                          + (parentIndex < 0 ? "none (root)" : parentIndex + " " + _file.BoneName(parentIndex, _ctx.BoneNames)) + ".", false);
            }
            catch (Exception ex)
            {
                SetStatus("Could not change parent: " + ex.Message, true);
            }
        }

        void OpenMirrorPicker(SklBone bone)
        {
            int target = bone.Index;
            var candidates = new List<string>();
            var indices = new List<int>();
            candidates.Add("(none)");
            indices.Add(-1);
            for (int i = 0; i < _file.Bones.Count; i++)
            {
                if (i == target) continue;
                candidates.Add(i + "  " + _file.BoneName(i, _ctx.BoneNames));
                indices.Add(i);
            }
            int preselect = 0;
            if (bone.MirrorIndex >= 0)
            {
                int pos = indices.IndexOf(bone.MirrorIndex);
                preselect = pos >= 0 ? pos : 0;
            }

            _mirrorPicker.Open("Choose mirror bone", candidates, preselect, null,
                i => ApplyMirror(target, indices[i]));
        }

        void ApplyMirror(int boneIndex, int mirrorIndex)
        {
            try
            {
                _file.SetMirror(boneIndex, mirrorIndex);
                MarkDirty();
                SetStatus("Bone " + boneIndex + " mirror set to " + (mirrorIndex < 0 ? "none" : mirrorIndex.ToString()) + ".", false);
            }
            catch (Exception ex)
            {
                SetStatus("Could not change mirror: " + ex.Message, true);
            }
        }

        // -------------------------------------------------------------- port tab
        void DrawPortTab()
        {
            GUILayout.Label("Copy bones from another skeleton into the open one.", GuiUtil.SubHeader);
            GUILayout.Label("Useful when a modded skeleton references bones that the target file does not have. "
                          + "Bones are appended at the end and parent indices are remapped; mirror indices are remapped too.",
                          GuiUtil.Note);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Choose donor .skl...", GuiUtil.W(160))) BrowseDonor();
            _donorPath = GUILayout.TextField(_donorPath ?? "", GuiUtil.MinW(200));
            if (GUILayout.Button("Load donor", GuiUtil.W(90))) LoadDonor(_donorPath);
            GUILayout.EndHorizontal();

            if (_donor == null)
            {
                GuiUtil.Space(6);
                GUILayout.Label("No donor loaded.", GuiUtil.Note);
                return;
            }

            GUILayout.Label("Donor: " + Path.GetFileName(_donor.SourcePath) + " - " + _donor.Bones.Count
                          + " bones, layout " + _donor.Layout.Label, GuiUtil.Note);

            GuiUtil.Space(4);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Bones missing from the open file: " + _missing.Count, GuiUtil.SubHeader);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("All", GuiUtil.W(50))) SetAllMissing(true);
            if (GUILayout.Button("None", GuiUtil.W(50))) SetAllMissing(false);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Filter", GuiUtil.W(38));
            _missingFilter = GUILayout.TextField(_missingFilter);
            if (GUILayout.Button("x", GuiUtil.W(22))) { _missingFilter = ""; _appliedMissingFilter = null; }
            _remapMirror = GUILayout.Toggle(_remapMirror, "Remap mirror index", GuiUtil.W(160));
            GUILayout.EndHorizontal();

            RefilterMissing();

            float listH = Mathf.Max(100f, _area.height - GuiUtil.S(string.IsNullOrEmpty(_portLog) ? 260f : 410f));
            _missingListScroll = GUILayout.BeginScrollView(_missingListScroll, false, true,
                GUILayout.Height(listH), GUILayout.ExpandWidth(true));
            if (_missingVisible.Count == 0)
                GUILayout.Label(_missing.Count == 0 ? "Nothing to port - the open file already has every donor bone."
                                                    : "No bones match the filter.", GuiUtil.Note);
            for (int row = 0; row < _missingVisible.Count; row++)
            {
                int i = _missingVisible[row];
                GUILayout.BeginHorizontal();
                bool chk = GUILayout.Toggle(_missingChecked[i], _missingNames[i]);
                if (chk != _missingChecked[i]) _missingChecked[i] = chk;
                GUILayout.Label("  donor index " + _missing[i], GuiUtil.Note, GuiUtil.W(130));
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            int checkedCount = 0;
            for (int i = 0; i < _missingChecked.Count; i++) if (_missingChecked[i]) checkedCount++;
            GUILayout.Label(checkedCount + " selected", GuiUtil.W(120));
            GUI.enabled = checkedCount > 0;
            if (GUILayout.Button("Preview", GuiUtil.W(100))) DoPort(true);
            if (GUILayout.Button("Port into open file", GuiUtil.W(160))) DoPort(false);
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_portLog))
            {
                _logScroll = GUILayout.BeginScrollView(_logScroll, false, true, GuiUtil.H(140), GUILayout.ExpandWidth(true));
                GuiUtil.ReadOnlyText(_portLog);
                GUILayout.EndScrollView();
            }
        }

        void BrowseDonor()
        {
            if (!NativeFileDialog.IsSupported)
            {
                SetStatus("Native file dialog is only available in Windows builds. Type a path instead.", true);
                return;
            }
            _ctx.RunDeferred(() =>
            {
                string p = NativeFileDialog.OpenFile("Choose donor skeleton", NativeFileDialog.SklFilter, CurrentDirectory());
                if (!string.IsNullOrEmpty(p))
                {
                    _donorPath = p;
                    LoadDonor(p);
                }
            });
        }

        void LoadDonor(string path)
        {
            try
            {
                path = (path ?? "").Trim().Trim('"');
                if (path.Length == 0) { SetStatus("Enter a donor path first.", true); return; }
                _donor = SklFile.Load(path, _ctx.BoneNames);
                _donorPath = path;
                RefreshMissing();
                _portLog = "";
                SetStatus("Loaded donor " + Path.GetFileName(path) + " - " + _donor.Bones.Count + " bones.", false);
            }
            catch (Exception ex)
            {
                _donor = null;
                SetStatus("Could not load donor: " + ex.Message, true);
            }
        }

        void RefreshMissing()
        {
            _missing.Clear();
            _missingNames.Clear();
            _missingChecked.Clear();
            _appliedMissingFilter = null;
            if (_donor == null || _file == null) return;
            try
            {
                List<int> idx = SklAnalysis.MissingBones(_donor, _file);
                foreach (int i in idx)
                {
                    _missing.Add(i);
                    _missingNames.Add(_donor.BoneName(i, _ctx.BoneNames));
                    _missingChecked.Add(true);
                }
            }
            catch (Exception ex)
            {
                SetStatus("Could not compare skeletons: " + ex.Message, true);
            }
        }

        void SetAllMissing(bool value)
        {
            for (int i = 0; i < _missingChecked.Count; i++) _missingChecked[i] = value;
        }

        void RefilterMissing()
        {
            if (_appliedMissingFilter == _missingFilter) return;
            _appliedMissingFilter = _missingFilter;
            _missingVisible.Clear();
            string q = (_missingFilter ?? "").Trim();
            for (int i = 0; i < _missingNames.Count; i++)
                if (q.Length == 0 || _missingNames[i].IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    _missingVisible.Add(i);
        }

        void DoPort(bool dryRun)
        {
            if (_file == null || _donor == null) return;
            try
            {
                var picked = new List<int>();
                for (int i = 0; i < _missingChecked.Count; i++) if (_missingChecked[i]) picked.Add(_missing[i]);

                PortResult result = _file.PortBones(_donor, picked, _remapMirror, _ctx.BoneNames, dryRun);
                _portLog = result.Log ?? "";
                if (!result.Success)
                {
                    SetStatus("Port failed - see the log below.", true);
                    return;
                }

                if (dryRun)
                {
                    SetStatus("Preview: " + result.AddedCount + " bone(s) would be added. Nothing was written.", false);
                }
                else
                {
                    MarkDirty();
                    RebuildDepth();
                    RefreshMissing();
                    SetStatus("Ported " + result.AddedCount + " bone(s). Press Save to write the file.", false);
                }
            }
            catch (Exception ex)
            {
                _portLog = ex.ToString();
                SetStatus("Port failed: " + ex.Message, true);
            }
        }

        // -------------------------------------------------------------- scan tab
        void DrawScanTab()
        {
            HashDatabase db = _ctx.BoneNames;

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Scan any file for known bone hashes...", GuiUtil.W(280))) BrowseScan();
            if (GUILayout.Button("Compare with another .skl...", GuiUtil.W(200))) BrowseCompare();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Verify hash algorithm", GuiUtil.W(170))) VerifyCrc();
            if (GUILayout.Button("Export bone dump to .txt...", GuiUtil.W(190))) ExportDump();
            if (GUILayout.Button("Recompute bone list", GuiUtil.W(150))) RebuildDepth();
            GUILayout.EndHorizontal();

            GuiUtil.Space(4);

            // A scanned non-.skl file can have the references it contains rewritten in place.
            if (_scanBytes != null && _scanHits.Count > 0)
            {
                DrawScanHitsEditor();
                return;
            }

            if (!string.IsNullOrEmpty(_scanSourceName))
                GUILayout.Label("Result for: " + _scanSourceName, GuiUtil.SubHeader);

            float h = Mathf.Max(120f, _area.height - GuiUtil.S(200f));
            _scanScroll = GUILayout.BeginScrollView(_scanScroll, false, true, GuiUtil.H(h), GUILayout.ExpandWidth(true));
            GuiUtil.ReadOnlyText(string.IsNullOrEmpty(_scanResult) ? "No output yet." : _scanResult);
            GUILayout.EndScrollView();
        }

        /// <summary>Hit list for a scanned file, with per-hit renaming and a write-back.</summary>
        void DrawScanHitsEditor()
        {
            HashDatabase db = _ctx.BoneNames;

            GUILayout.Label("Bone name references in " + Path.GetFileName(_scanPath) + "  (" + _scanHits.Count
                            + " names, " + _scanBytes.Length + " bytes)", GuiUtil.SubHeader);
            GUILayout.Label("Rename a reference to replace every occurrence of that hash in this file. "
                            + "This is a raw byte substitution - it does not know which field it is rewriting.", GuiUtil.Note);

            int replaced = 0;
            foreach (var kv in _scanRenames)
                foreach (HashHit hh in _scanHits)
                    if (hh.Hash == kv.Key) { replaced += hh.Offsets.Count; break; }

            GUILayout.BeginHorizontal();
            GUI.enabled = _scanRenames.Count > 0;
            if (GUILayout.Button("Apply " + _scanRenames.Count + " rename(s) and save", GuiUtil.W(240))) ApplyScanRenames();
            if (GUILayout.Button("Discard renames", GuiUtil.W(140))) { _scanRenames.Clear(); SetStatus("Queued renames discarded.", false); }
            GUI.enabled = true;
            GUILayout.Label(_scanRenames.Count == 0
                ? "No renames queued."
                : _scanRenames.Count + " queued, " + replaced + " byte reference(s) would change.", GuiUtil.Note);
            GUILayout.EndHorizontal();

            GuiUtil.Space(4);

            float listH = Mathf.Max(120f, _area.height - GuiUtil.S(200f));
            _scanHitsScroll = GuiUtil.VirtualList(_scanHitsScroll, listH, _scanHits.Count, GuiUtil.RowHeight, (row, r) =>
            {
                HashHit hit = _scanHits[row];
                ulong pending;
                bool queued = _scanRenames.TryGetValue(hit.Hash, out pending);

                GUI.Label(new Rect(r.x, r.y, r.width - GuiUtil.S(360f), r.height),
                    HashDatabase.Hex(hit.Hash) + "   " + (hit.Name ?? "") + "   x" + hit.Offsets.Count, GuiUtil.Row);

                var btn = new Rect(r.x + r.width - GuiUtil.S(340f), r.y, GuiUtil.S(150f), r.height - 1f);
                if (GUI.Button(btn, queued ? "Change again..." : "Rename..."))
                {
                    _scanRenameOld = hit.Hash;
                    OpenScanNamePicker(hit);
                }

                string target = queued ? db.NameOrHex(pending) : "(unchanged)";
                GUI.color = queued ? new Color(1f, 0.8f, 0.3f) : Color.white;
                GUI.Label(new Rect(r.x + r.width - GuiUtil.S(180f), r.y, GuiUtil.S(180f), r.height), target, GuiUtil.Row);
                GUI.color = Color.white;
            }, true);
        }

        void OpenScanNamePicker(HashHit hit)
        {
            HashDatabase db = _ctx.BoneNames;
            _scanPicker.Open("Rename reference: " + (hit.Name ?? HashDatabase.Hex(hit.Hash)), db.SortedNames, -1, null, index =>
            {
                if (index < 0 || index >= db.SortedNames.Count) return;
                string name = db.SortedNames[index];
                ulong newHash;
                if (!db.TryGetHash(name, out newHash)) newHash = HashDatabase.Crc64(name);
                if (newHash == _scanRenameOld)
                {
                    _scanRenames.Remove(_scanRenameOld);
                    SetStatus("'" + name + "' is the same name - nothing queued.", false);
                    return;
                }
                _scanRenames[_scanRenameOld] = newHash;
                SetStatus("Queued: " + db.NameOrHex(_scanRenameOld) + " -> " + name + ". Press [Apply] to write the file.", false);
            });
            _scanPicker.EnableCustomEntry("Custom name or raw 0x hash",
                "A name that is not in the list hashes to a value no game file will recognise.",
                CustomHashPreview, text =>
                {
                    ulong h;
                    if (!HashDatabase.TryParseHex(text.Trim(), out h))
                    {
                        string s = text.Trim();
                        if (s.Length == 0) return;
                        h = HashDatabase.Crc64(s);
                    }
                    if (h == _scanRenameOld) return;
                    _scanRenames[_scanRenameOld] = h;
                    SetStatus("Queued raw hash " + HashDatabase.Hex(h) + ". Press [Apply] to write the file.", false);
                });
        }

        static string CustomHashPreview(string text)
        {
            string s = (text ?? "").Trim();
            if (s.Length == 0) return "";
            ulong h;
            if (!HashDatabase.TryParseHex(s, out h)) h = HashDatabase.Crc64(s);
            return "Hash: " + HashDatabase.Hex(h);
        }

        void ApplyScanRenames()
        {
            if (_scanBytes == null || _scanRenames.Count == 0) return;
            try
            {
                byte[] data = (byte[])_scanBytes.Clone();
                int changed = 0;
                var replacedPerRename = new StringBuilder();
                foreach (var kv in _scanRenames)
                {
                    byte[] oldBytes = BitConverter.GetBytes(kv.Key);
                    byte[] newBytes = BitConverter.GetBytes(kv.Value);
                    int n = 0;
                    for (int i = 0; i + 8 <= data.Length; i++)
                    {
                        if (BitConverter.ToUInt64(data, i) != kv.Key) continue;
                        Buffer.BlockCopy(newBytes, 0, data, i, 8);
                        n++;
                        i += 7;
                    }
                    changed += n;
                    replacedPerRename.AppendLine("  " + _ctx.BoneNames.NameOrHex(kv.Key) + " -> "
                        + _ctx.BoneNames.NameOrHex(kv.Value) + ": " + n + " occurrence(s)");
                }

                if (changed == 0)
                {
                    SetStatus("Nothing to write - no occurrences found.", true);
                    _scanRenames.Clear();
                    return;
                }

                File.WriteAllBytes(_scanPath, data);
                _scanRenames.Clear();
                DoScan(_scanPath);
                SetStatus("Wrote " + changed + " replacement(s) to " + Path.GetFileName(_scanPath) + ".", false);
                _scanResult = "Renames applied:\n" + replacedPerRename + "\n" + _scanResult;
            }
            catch (Exception ex)
            {
                SetStatus("Could not write the file: " + ex.Message, true);
            }
        }

        void BrowseScan()
        {
            if (!NativeFileDialog.IsSupported)
            {
                SetStatus("Native file dialog is only available in Windows builds.", true);
                return;
            }
            _ctx.RunDeferred(() =>
            {
                string p = NativeFileDialog.OpenFile("Choose a file to scan", NativeFileDialog.AnyFilter, CurrentDirectory());
                if (!string.IsNullOrEmpty(p)) DoScan(p);
            });
        }

        void DoScan(string path)
        {
            try
            {
                byte[] data = File.ReadAllBytes(path);
                List<HashHit> hits = SklAnalysis.ScanForKnownHashes(data, _ctx.BoneNames);
                var sb = new StringBuilder();
                sb.AppendLine("Known bone hashes found in " + Path.GetFileName(path) + ": " + hits.Count + " distinct names");
                sb.AppendLine();
                sb.AppendLine("Hash                Name                          Count  Offsets");
                foreach (HashHit hit in hits)
                {
                    var offs = new StringBuilder();
                    for (int i = 0; i < hit.Offsets.Count && i < 8; i++)
                    {
                        if (i > 0) offs.Append(", ");
                        offs.Append("0x").Append(hit.Offsets[i].ToString("X"));
                    }
                    if (hit.Offsets.Count > 8) offs.Append(", ...");
                    sb.AppendLine(HashDatabase.Hex(hit.Hash).PadRight(20) + " "
                                  + (hit.Name ?? "").PadRight(30) + hit.Offsets.Count.ToString().PadRight(7) + offs);
                }

                // Keep the bytes and hits so the references can be rewritten in place.
                _scanBytes = data;
                _scanPath = path;
                _scanHits = hits;
                _scanRenames.Clear();
                _scanHitsScroll = Vector2.zero;

                _scanResult = sb.ToString();
                _scanSourceName = path;
                _scanScroll = Vector2.zero;
                SetStatus("Scan finished: " + hits.Count + " known names found.", false);
            }
            catch (Exception ex)
            {
                SetStatus("Scan failed: " + ex.Message, true);
            }
        }

        void BrowseCompare()
        {
            if (_file == null) return;
            if (!NativeFileDialog.IsSupported)
            {
                SetStatus("Native file dialog is only available in Windows builds.", true);
                return;
            }
            _ctx.RunDeferred(() =>
            {
                string p = NativeFileDialog.OpenFile("Compare with skeleton", NativeFileDialog.SklFilter, CurrentDirectory());
                if (!string.IsNullOrEmpty(p)) DoCompare(p);
            });
        }

        void DoCompare(string path)
        {
            try
            {
                SklFile other = SklFile.Load(path, _ctx.BoneNames);
                CompareResult cmp = SklAnalysis.Compare(_file, other);
                var sb = new StringBuilder();
                sb.AppendLine("A (open): " + Path.GetFileName(_file.SourcePath) + " - " + _file.Bones.Count + " bones, " + _file.Layout.Label);
                sb.AppendLine("B (other): " + Path.GetFileName(path) + " - " + other.Bones.Count + " bones, " + other.Layout.Label);
                sb.AppendLine();
                sb.AppendLine("In both: " + cmp.InBoth.Count);
                sb.AppendLine();
                sb.AppendLine("=== Bones in the open file but NOT in the other file: " + cmp.OnlyInA.Count + " ===");
                foreach (ulong h in cmp.OnlyInA) sb.AppendLine("  " + HashDatabase.Hex(h) + "  " + _ctx.BoneNames.NameOrHex(h));
                sb.AppendLine();
                sb.AppendLine("=== Bones in the other file but NOT in the open file: " + cmp.OnlyInB.Count + " ===");
                foreach (ulong h in cmp.OnlyInB) sb.AppendLine("  " + HashDatabase.Hex(h) + "  " + _ctx.BoneNames.NameOrHex(h));
                _scanResult = sb.ToString();
                _scanSourceName = path;
                _scanScroll = Vector2.zero;
                SetStatus("Compared with " + Path.GetFileName(path) + ".", false);
            }
            catch (Exception ex)
            {
                SetStatus("Compare failed: " + ex.Message, true);
            }
        }

        void VerifyCrc()
        {
            try
            {
                _scanResult = SklAnalysis.VerifyCrc(_ctx.BoneNames);
                _scanSourceName = "hash algorithm check";
                _scanScroll = Vector2.zero;
            }
            catch (Exception ex)
            {
                SetStatus("Verify failed: " + ex.Message, true);
            }
        }

        void ExportDump()
        {
            if (_file == null) return;
            if (!NativeFileDialog.IsSupported)
            {
                SetStatus("Native file dialog is only available in Windows builds.", true);
                return;
            }
            _ctx.RunDeferred(() =>
            {
                string name = Path.GetFileNameWithoutExtension(_file.SourcePath ?? "skl") + "_dump.txt";
                string p = NativeFileDialog.SaveFile("Export bone dump", NativeFileDialog.TextFilter,
                    CurrentDirectory(), name, "txt");
                if (string.IsNullOrEmpty(p)) return;
                try
                {
                    File.WriteAllText(p, SklAnalysis.Describe(_file, _ctx.BoneNames));
                    SetStatus("Dump written to " + p, false);
                }
                catch (Exception ex)
                {
                    SetStatus("Could not write dump: " + ex.Message, true);
                }
            });
        }

        // -------------------------------------------------------------- info tab
        void DrawInfoTab()
        {
            float h = Mathf.Max(120f, _area.height - GuiUtil.S(120f));
            _infoScroll = GUILayout.BeginScrollView(_infoScroll, false, true, GUILayout.Height(h), GUILayout.ExpandWidth(true));
            string text;
            try { text = SklAnalysis.Describe(_file, _ctx.BoneNames); }
            catch (Exception ex) { text = "Could not describe file: " + ex.Message; }
            GuiUtil.ReadOnlyText(text);
            GUILayout.EndScrollView();
        }
    }
}
