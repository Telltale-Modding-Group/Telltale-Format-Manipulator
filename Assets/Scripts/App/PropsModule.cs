using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TFM.Core;
using UnityEngine;

namespace TFM.App
{
    /// <summary>
    /// .prop editor: open a property file, inspect and edit individual values, delete whole
    /// properties, and port properties to and from a donor file. Payloads that could not be
    /// decoded are preserved verbatim instead of being rewritten.
    /// </summary>
    public class PropsModule : IFormatModule
    {
        public string Title { get { return "Props"; } }

        enum Tab { Fields, Port, Info }

        AppContext _ctx;
        Rect _area;
        PropFile _file;
        bool _dirty;
        string _path = "";
        string _statusText = "";
        bool _statusError;

        Tab _tab = Tab.Fields;
        int _selected = -1;
        Vector2 _listScroll, _inspectorScroll, _infoScroll, _logScroll;

        // Donor / port state
        string _donorPath = "";
        PropFile _donor;
        PropCompareResult _compare;
        string _portLog = "";
        Vector2 _compareScroll;
        int _compareSelected = -1;
        bool _replaceExisting = true;
        bool _removeFromDonor;

        void SetStatus(string msg, bool error)
        {
            _statusText = msg;
            _statusError = error;
            if (_ctx != null) _ctx.SetStatus(msg, error);
        }

        // ================================================================ module
        public void Initialize(AppContext ctx)
        {
            _ctx = ctx;
        }

        public void Update() { }

        public bool HasUnsavedChanges { get { return _dirty; } }

        public bool SaveAll()
        {
            if (!_dirty) return true;
            return Save();
        }

        public void OnGUI(Rect area)
        {
            _area = area;
            GUILayout.BeginArea(area);

            DrawToolbar();
            DrawTabs();

            if (_file == null)
            {
                GuiUtil.Space(10);
                GUILayout.Label("No file loaded. Use [Open .prop] to load a property file.", GuiUtil.Note);
                GUILayout.EndArea();
                return;
            }

            switch (_tab)
            {
                case Tab.Fields: DrawFieldsTab(); break;
                case Tab.Port: DrawPortTab(); break;
                case Tab.Info: DrawInfoTab(); break;
            }

            GUILayout.EndArea();
        }

        // ================================================================ chrome
        void DrawTabs()
        {
            GUILayout.BeginHorizontal();
            string[] tabs = { "Fields", "Port fields", "Info" };
            for (int i = 0; i < tabs.Length; i++)
            {
                bool active = (int)_tab == i;
                if (GUILayout.Toggle(active, tabs[i], GUI.skin.button, GUILayout.ExpandWidth(true)) && !active)
                    _tab = (Tab)i;
            }
            GUILayout.EndHorizontal();
        }

        void DrawToolbar()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Open .prop...", GuiUtil.W(110))) BrowseOpen();
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
                b.Append("   |   ").Append(_file.MagicText);
                b.Append("   |   ").Append(_file.Fields.Count).Append(" fields");
                if (_file.IsPartial)
                    b.Append("   |   ").Append(_file.OpaqueBlocks.Count).Append(" undecoded");
                GUILayout.Label(b.ToString(), GuiUtil.Note);
            }
            GUILayout.EndHorizontal();
        }

        // ================================================================ file I/O
        void BrowseOpen()
        {
            if (!NativeFileDialog.IsSupported)
            {
                SetStatus("Native file dialog is only available in Windows builds. Type a path and press [Load path].", true);
                return;
            }
            _ctx.RunDeferred(() =>
            {
                string p = NativeFileDialog.OpenFile("Open Telltale property file",
                    NativeFileDialog.PropFilter, CurrentDirectory());
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
            try
            {
                PropFile f = PropFile.Load(path);
                _file = f;
                _path = path;
                _dirty = false;
                _selected = f.Fields.Count > 0 ? 0 : -1;
                _listScroll = Vector2.zero;
                _inspectorScroll = Vector2.zero;
                _donor = null;
                _donorPath = "";
                _compare = null;
                _portLog = "";
                GuiUtil.ResetBuffers();

                string msg = "Loaded " + Path.GetFileName(path) + " - " + f.Fields.Count + " fields";
                if (f.IsPartial) msg += ", partially (" + f.PartialReason + ")";
                SetStatus(msg, false);
            }
            catch (PropParseException ex)
            {
                SetStatus("Could not parse " + Path.GetFileName(path) + ": " + ex.Message, true);
            }
            catch (Exception ex)
            {
                SetStatus("Could not open file: " + ex.Message, true);
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
                byte[] before = _file.Serialize();
                _file.Save(path);
                _dirty = false;
                SetStatus("Saved " + Path.GetFileName(path) + " (" + before.Length + " bytes).", false);
                return true;
            }
            catch (Exception ex)
            {
                SetStatus("Save failed: " + ex.Message, true);
                return false;
            }
        }

        // ================================================================ fields tab
        void DrawFieldsTab()
        {
            GUILayout.BeginHorizontal();

            GUILayout.BeginVertical(GUILayout.Width(Mathf.Clamp(_area.width * 0.32f, 220f, 460f)));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Fields", GuiUtil.SubHeader);
            GUILayout.FlexibleSpace();
            GUILayout.Label(_file.Fields.Count + " total", GuiUtil.Note, GuiUtil.W(70));
            GUILayout.EndHorizontal();

            float listH = Mathf.Max(120f, _area.height - GuiUtil.S(150f));
            if (_selected >= _file.Fields.Count) _selected = _file.Fields.Count - 1;

            _listScroll = GuiUtil.VirtualList(_listScroll, listH, _file.Fields.Count, GuiUtil.RowHeight, (row, r) =>
            {
                PropField f = _file.Fields[row];
                string label = row + "  " + f.TypeName + "  (" + f.Elements.Count + ")";
                if (GUI.Button(r, label, row == _selected ? GuiUtil.SelectedRow : GuiUtil.Row))
                {
                    _selected = row;
                    _inspectorScroll = Vector2.zero;
                    GuiUtil.ResetBuffers();
                }
            });
            GUILayout.EndVertical();

            GUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandWidth(true));
            if (_selected < 0 || _selected >= _file.Fields.Count)
            {
                GUILayout.Label("Select a field to edit it.", GuiUtil.Note);
            }
            else
            {
                _inspectorScroll = GUILayout.BeginScrollView(_inspectorScroll, false, true,
                    GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
                DrawFieldInspector(_file.Fields[_selected]);
                GUILayout.EndScrollView();
            }
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
        }

        void DrawFieldInspector(PropField field)
        {
            GUILayout.Label("Property", GuiUtil.SubHeader);
            GUILayout.Label("Type: " + field.TypeName, GuiUtil.Note);
            GUILayout.Label("Type hash: 0x" + field.TypeHash.ToString("X16"), GuiUtil.Note);
            GUILayout.Label("Values: " + field.Elements.Count + "   |   " + field.ByteLength + " bytes", GuiUtil.Note);
            GuiUtil.Space(6);

            for (int i = 0; i < field.Elements.Count; i++)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label("value " + i + "   key 0x" + field.Elements[i].KeyHash.ToString("X16"), GuiUtil.Note);
                bool changed = DrawElementEditor(field, field.Elements[i], i);
                if (changed) _dirty = true;
                GUILayout.EndVertical();
            }

            if (field.Kind == PropKind.Unknown)
            {
                GuiUtil.Space(4);
                GUILayout.Label("This type layout is not known, so its values are left as raw bytes.", GuiUtil.Warning);
            }

            GuiUtil.Space(10);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Delete this field", GuiUtil.W(150)))
            {
                if (_file.DeleteField(field))
                {
                    _dirty = true;
                    _selected = Mathf.Clamp(_selected, 0, Mathf.Max(0, _file.Fields.Count - 1));
                    GuiUtil.ResetBuffers();
                    SetStatus("Deleted field, " + field.TypeName + ". Save to write the file.", false);
                }
            }
            if (GUILayout.Button("Duplicate", GuiUtil.W(90)))
            {
                _file.InsertField(field.Clone(), _file.IndexOf(field) + 1);
                _dirty = true;
                SetStatus("Duplicated field " + field.TypeName + ".", false);
            }
            GUILayout.EndHorizontal();

            if (_file.IsPartial)
            {
                GuiUtil.Space(6);
                GUILayout.Label("This file has " + _file.OpaqueBlocks.Count
                    + " undecoded region(s), " + _file.RawTailLength
                    + " bytes in total. They are kept verbatim when saving; decoded fields around them stay editable."
                    + " See the Info tab for details.", GuiUtil.Warning);
            }
        }

        /// <summary>Draws an editor for one value. Returns true when the bytes changed.</summary>
        bool DrawElementEditor(PropField field, PropElement el, int index)
        {
            switch (field.Kind)
            {
                case PropKind.Bool:
                    {
                        bool v = el.Value.Length > 0 && el.Value[0] != 0;
                        bool nv = GUILayout.Toggle(v, v ? " true" : " false");
                        if (nv != v)
                        {
                            el.Value[0] = nv ? (byte)1 : (byte)0;
                            return true;
                        }
                        return false;
                    }

                case PropKind.Float:
                    {
                        float v = PropFile.ReadFloat(el.Value, 0);
                        string id = "propf" + index;
                        if (GuiUtil.FloatField(id, ref v))
                        {
                            PropFile.WriteFloat(el.Value, 0, v);
                            return true;
                        }
                        return false;
                    }

                case PropKind.Color:
                    {
                        bool changed = false;
                        GUILayout.BeginHorizontal();
                        for (int c = 0; c < 4; c++)
                        {
                            float v = PropFile.ReadFloat(el.Value, c * 4);
                            if (GuiUtil.FloatField("propc" + index + "_" + c, ref v))
                            {
                                PropFile.WriteFloat(el.Value, c * 4, v);
                                changed = true;
                            }
                        }
                        GUILayout.EndHorizontal();

                        // Show the colour, clamped so it renders sanely.
                        float r = Mathf.Clamp01(PropFile.ReadFloat(el.Value, 0));
                        float g = Mathf.Clamp01(PropFile.ReadFloat(el.Value, 4));
                        float b = Mathf.Clamp01(PropFile.ReadFloat(el.Value, 8));
                        Rect swatch = GUILayoutUtility.GetRect(GuiUtil.S(60f), GuiUtil.S(18f));
                        GUI.color = new Color(r, g, b, 1f);
                        GUI.DrawTexture(swatch, Texture2D.whiteTexture);
                        GUI.color = Color.white;
                        return changed;
                    }

                case PropKind.Vec3:
                    {
                        bool changed = false;
                        GUILayout.BeginHorizontal();
                        for (int c = 0; c < 3; c++)
                        {
                            float v = PropFile.ReadFloat(el.Value, c * 4);
                            if (GuiUtil.FloatField("propv" + index + "_" + c, ref v))
                            {
                                PropFile.WriteFloat(el.Value, c * 4, v);
                                changed = true;
                            }
                        }
                        GUILayout.EndHorizontal();
                        return changed;
                    }

                case PropKind.Text:
                    {
                        string s = Encoding.ASCII.GetString(el.Value);
                        GUILayout.Label("length " + el.Value.Length, GuiUtil.Note);
                        GUILayout.Label(s, GuiUtil.Wrap);
                        string edit = GUILayout.TextField(s);
                        if (edit != s)
                        {
                            el.Value = Encoding.ASCII.GetBytes(edit);
                            return true;
                        }
                        return false;
                    }

                default:
                    {
                        GUILayout.Label(HexDump(el.Value), GuiUtil.Mono);
                        return false;
                    }
            }
        }

        static string HexDump(byte[] b)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < b.Length; i++)
            {
                if (i > 0 && i % 16 == 0) sb.Append('\n');
                sb.Append(b[i].ToString("X2")).Append(' ');
            }
            return sb.ToString();
        }

        // ================================================================ port tab
        void DrawPortTab()
        {
            GUILayout.BeginHorizontal();
            _donorPath = GUILayout.TextField(_donorPath ?? "", GuiUtil.MinW(180));
            if (GUILayout.Button("Choose donor .prop...", GuiUtil.W(160))) BrowseDonor();
            if (GUILayout.Button("Load donor", GuiUtil.W(90))) LoadDonor(_donorPath);
            if (GUILayout.Button("Compare", GuiUtil.W(80))) Compare();
            GUILayout.EndHorizontal();

            if (_donor == null)
            {
                GuiUtil.Space(8);
                GUILayout.Label("Load a donor .prop to add, replace or move fields into the open file.", GuiUtil.Note);
                return;
            }

            GUILayout.Label("Donor: " + Path.GetFileName(_donor.SourcePath) + " - " + _donor.Fields.Count + " fields",
                GuiUtil.SubHeader);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Add all missing fields", GuiUtil.W(180))) AddMissing();
            if (GUILayout.Button("Replace matching fields", GuiUtil.W(180))) ReplaceMatching();
            _replaceExisting = GUILayout.Toggle(_replaceExisting, " Replace existing");
            _removeFromDonor = GUILayout.Toggle(_removeFromDonor, " Remove from donor");
            GUILayout.EndHorizontal();

            GuiUtil.Space(4);

            if (_compare != null)
            {
                GUILayout.Label("identical " + _compare.IdenticalCount
                    + "   |   differs " + _compare.DifferentCount
                    + "   |   only here " + _compare.OnlyInTargetCount
                    + "   |   only in donor " + _compare.OnlyInDonorCount, GuiUtil.Note);
            }

            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(Mathf.Clamp(_area.width * 0.55f, 300f, 700f)));
            float listH = Mathf.Max(110f, _area.height - GuiUtil.S(230f));
            List<PropMatch> rows = _compare != null ? _compare.Matches : new List<PropMatch>();
            _compareScroll = GuiUtil.VirtualList(_compareScroll, listH, rows.Count, GuiUtil.RowHeight, (row, r) =>
            {
                PropMatch m = rows[row];
                string tag = m.OnlyInDonor ? "+ donor" : m.OnlyInTarget ? "- here" : m.Identical ? "= same" : "~ diff";
                string name = m.Target != null ? m.Target.TypeName : m.Donor.TypeName;
                string label = tag.PadRight(7) + name;
                if (GUI.Button(r, label, row == _compareSelected ? GuiUtil.SelectedRow : GuiUtil.Row))
                    _compareSelected = row;
            });
            GUILayout.EndVertical();

            GUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandWidth(true));
            GUILayout.Label("Move one field", GuiUtil.SubHeader);
            PropMatch sel = (_compare != null && _compareSelected >= 0 && _compareSelected < _compare.Matches.Count)
                ? _compare.Matches[_compareSelected] : null;
            if (sel == null)
            {
                GUILayout.Label("Select a row to move that field into the open file.", GuiUtil.Note);
            }
            else if (sel.Donor == null)
            {
                GUILayout.Label("This field is not in the donor.", GuiUtil.Note);
            }
            else
            {
                GUILayout.Label(sel.Donor.TypeName + " - " + sel.Donor.Elements.Count + " values", GuiUtil.Note);
                if (GUILayout.Button("Move into the open file", GuiUtil.W(190))) MoveOne(sel.Donor);
            }
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_portLog))
            {
                _logScroll = GUILayout.BeginScrollView(_logScroll, GuiUtil.H(Mathf.Max(80f, GuiUtil.S(120f))),
                    GUILayout.ExpandWidth(true));
                GuiUtil.ReadOnlyText(_portLog);
                GUILayout.EndScrollView();
            }
        }

        void BrowseDonor()
        {
            if (!NativeFileDialog.IsSupported)
            {
                SetStatus("Native file dialog is only available in Windows builds.", true);
                return;
            }
            _ctx.RunDeferred(() =>
            {
                string p = NativeFileDialog.OpenFile("Choose donor property file",
                    NativeFileDialog.PropFilter, CurrentDirectory());
                if (!string.IsNullOrEmpty(p)) LoadDonor(p);
            });
        }

        void LoadDonor(string path)
        {
            path = (path ?? "").Trim().Trim('"');
            if (path.Length == 0) { SetStatus("Enter a donor path first.", true); return; }
            try
            {
                _donor = PropFile.Load(path);
                _donorPath = path;
                _compare = null;
                _compareSelected = -1;
                Compare();
                SetStatus("Loaded donor " + Path.GetFileName(path) + " - " + _donor.Fields.Count + " fields.", false);
            }
            catch (Exception ex)
            {
                SetStatus("Could not load donor: " + ex.Message, true);
            }
        }

        void Compare()
        {
            if (_file == null || _donor == null) return;
            try
            {
                _compare = PropPort.Compare(_file, _donor);
                _compareSelected = -1;
                _compareScroll = Vector2.zero;
            }
            catch (Exception ex)
            {
                SetStatus("Compare failed: " + ex.Message, true);
            }
        }

        void AddMissing()
        {
            try
            {
                PropPortResult r = PropPort.AddMissing(_file, _donor);
                _portLog = r.Log;
                if (r.Added > 0) { _dirty = true; Compare(); }
                SetStatus("Added " + r.Added + " field(s).", false);
            }
            catch (Exception ex)
            {
                SetStatus("Add failed: " + ex.Message, true);
            }
        }

        void ReplaceMatching()
        {
            try
            {
                PropPortResult r = PropPort.ReplaceMatching(_file, _donor);
                _portLog = r.Log;
                if (r.Replaced > 0) { _dirty = true; Compare(); }
                SetStatus("Replaced " + r.Replaced + " field(s).", false);
            }
            catch (Exception ex)
            {
                SetStatus("Replace failed: " + ex.Message, true);
            }
        }

        void MoveOne(PropField donorField)
        {
            try
            {
                PropPortResult r = PropPort.MoveOne(_file, donorField, _replaceExisting, _removeFromDonor, _donor);
                _portLog = r.Log;
                if (r.Success && (r.Added > 0 || r.Replaced > 0)) { _dirty = true; Compare(); }
                SetStatus(r.Success ? "Moved " + donorField.TypeName + "." : r.Log, !r.Success);
            }
            catch (Exception ex)
            {
                SetStatus("Move failed: " + ex.Message, true);
            }
        }

        // ================================================================ info tab
        void DrawInfoTab()
        {
            _infoScroll = GUILayout.BeginScrollView(_infoScroll, false, true,
                GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));

            var sb = new StringBuilder();
            sb.AppendLine(Path.GetFileName(_file.SourcePath ?? "(unsaved)"));
            sb.AppendLine("Magic:            " + _file.MagicText);
            sb.AppendLine("File size:        " + _file.OriginalSize + " bytes");
            sb.AppendLine("Header size:      " + _file.HeaderSize + " bytes");
            sb.AppendLine("Fields decoded:   " + _file.Fields.Count);
            sb.AppendLine("Stored count:     " + _file.StoredPropCount + " properties");
            sb.AppendLine("Undecoded bytes:  " + _file.RawTailLength + " (" + _file.OpaqueBlocks.Count + " region(s))");
            if (!string.IsNullOrEmpty(_file.PartialReason))
                sb.AppendLine("Why:              " + _file.PartialReason);
            sb.AppendLine();
            if (_file.OpaqueBlocks.Count > 0)
            {
                sb.AppendLine("Undecoded regions (kept verbatim, rewritten in place):");
                for (int i = 0; i < _file.OpaqueBlocks.Count; i++)
                {
                    PropOpaque ob = _file.OpaqueBlocks[i];
                    sb.AppendLine("  " + i + "  type 0x" + ob.TypeHash.ToString("X16")
                        + "  properties " + ob.StoredCount
                        + "  " + ob.ByteLength + " bytes   " + ob.Reason);
                }
                sb.AppendLine();
            }
            sb.AppendLine("Header type table (" + _file.HeaderTable.Count + " entries):");
            for (int i = 0; i < _file.HeaderTable.Count; i++)
            {
                ulong h = _file.HeaderTable[i];
                string nm;
                switch (h)
                {
                    case PropTypeNames.TypeBool: nm = "bool"; break;
                    case PropTypeNames.TypeFloat: nm = "float"; break;
                    case PropTypeNames.TypeString: nm = "string"; break;
                    case PropTypeNames.TypeColor: nm = "color"; break;
                    case PropTypeNames.TypeBounds: nm = "vec3"; break;
                    case PropTypeNames.TypeSymbol: nm = "symbol"; break;
                    default: nm = "(unresolved)"; break;
                }
                sb.AppendLine("  " + i + "  0x" + h.ToString("X16") + "  " + nm);
            }

            GuiUtil.ReadOnlyText(sb.ToString());
            GUILayout.EndScrollView();
        }
    }
}
