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

        enum Tab { Fields, Material, Port, Info }

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

        // Picker for changing a file reference (mesh, texture, chore...) to another name.
        readonly ListPicker _refPicker = new ListPicker();
        readonly ConfirmDialog _confirm = new ConfirmDialog();

        // Inline "Add Custom Name" editor: which reference it is open for, and the typed text.
        PropElement _customFor;
        int _customOffset;
        string _customInput = "";
        string _customError;

        // Material tab. The decoded block is cached against the value array it came from, so it
        // is decoded again only after the block itself is replaced.
        byte[] _matSource;
        PropMaterial _mat;
        string _matWhy;
        Vector2 _matScroll;
        bool _matShowDetails;
        string _matReport;

        void SetStatus(string msg, bool error)
        {
            _statusText = msg;
            _statusError = error;
            if (_ctx != null) _ctx.SetStatus(msg, error);
        }

        // ---------------------------------------------------------------- name lookup

        /// <summary>Engine type name when the type database knows the hash, else the hex hash.</summary>
        string TypeLabel(ulong hash)
        {
            string n = _ctx != null ? _ctx.PropNames.Type(hash) : null;
            return n ?? "0x" + hash.ToString("X16");
        }

        /// <summary>Property key name when the key database knows the hash, else the hex hash.</summary>
        string KeyLabel(ulong hash)
        {
            string n = _ctx != null ? _ctx.PropNames.Key(hash) : null;
            return n != null ? n + "  (0x" + hash.ToString("X16") + ")" : "0x" + hash.ToString("X16");
        }

        /// <summary>The file an 8-byte reference points at, if the archive name dumps contain it.</summary>
        string FileLabel(ulong hash)
        {
            return _ctx != null ? _ctx.PropNames.FileRef(hash) : null;
        }

        /// <summary>Key name or null, for code that needs to test names.</summary>
        string KeyName(ulong hash)
        {
            return _ctx != null ? _ctx.PropNames.Key(hash) : null;
        }

        /// <summary>Key name when known, else the hex hash; no hash suffix.</summary>
        string KeyShort(ulong hash)
        {
            return KeyName(hash) ?? "0x" + hash.ToString("X16");
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
                case Tab.Material: DrawMaterialTab(); break;
                case Tab.Port: DrawPortTab(); break;
                case Tab.Info: DrawInfoTab(); break;
            }

            GUILayout.EndArea();

            // The picker is a modal window, so it must draw outside the module's area.
            if (_refPicker.IsOpen)
            {
                Rect screen = _ctx.ScreenRect.width > 0 ? _ctx.ScreenRect : new Rect(0, 0, Screen.width, Screen.height);
                _refPicker.OnGUI(screen);
            }
            if (_confirm.IsOpen)
            {
                Rect screen = _ctx.ScreenRect.width > 0 ? _ctx.ScreenRect : new Rect(0, 0, Screen.width, Screen.height);
                _confirm.OnGUI(screen);
            }
        }

        // ================================================================ chrome
        void DrawTabs()
        {
            GUILayout.BeginHorizontal();
            string[] tabs = { "Fields", "Material", "Port fields", "Info" };
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
                _customFor = null;
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
                string label = row + "  " + TypeLabel(f.TypeHash) + "  (" + f.Elements.Count + ")";
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
            string typeName = TypeLabel(field.TypeHash);
            GUILayout.Label("Type: " + typeName, GuiUtil.Note);
            GUILayout.Label("Type hash: 0x" + field.TypeHash.ToString("X16"), GuiUtil.Note);
            GUILayout.Label("Values: " + field.Elements.Count + "   |   " + field.ByteLength + " bytes", GuiUtil.Note);
            GuiUtil.Space(6);

            for (int i = 0; i < field.Elements.Count; i++)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                string keyName = KeyLabel(field.Elements[i].KeyHash);
                GUILayout.Label("value " + i + "   key " + keyName, GuiUtil.Note);
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
                        // The game stores bools as the characters '0' and '1'; a raw 0/1 byte is
                        // accepted when reading, and the character form is always written.
                        bool v = BoolValue(el.Value);
                        bool nv = GUILayout.Toggle(v, v ? " true" : " false");
                        if (nv != v)
                        {
                            el.Value[0] = nv ? (byte)'1' : (byte)'0';
                            return true;
                        }
                        return false;
                    }

                case PropKind.Vec2:
                    {
                        bool changed = false;
                        GUILayout.BeginHorizontal();
                        for (int c = 0; c < 2; c++)
                        {
                            float v = PropFile.ReadFloat(el.Value, c * 4);
                            if (GuiUtil.FloatField("prop2" + index + "_" + c, ref v))
                            {
                                PropFile.WriteFloat(el.Value, c * 4, v);
                                changed = true;
                            }
                        }
                        GUILayout.EndHorizontal();
                        return changed;
                    }

                case PropKind.Material:
                    {
                        GUILayout.Label("Compiled material data, " + el.Value.Length + " bytes. It is shown, and its"
                            + " textures and values are edited, in the Material tab.", GuiUtil.Note);
                        if (GUILayout.Button("Open the Material tab", GuiUtil.W(170))) _tab = Tab.Material;
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

                case PropKind.Symbol:
                    return DrawReferenceEditor(el, index, 0, "this reference");

                default:
                    {
                        DrawReferenceSlots(el, index);
                        GUILayout.Label(HexDump(el.Value), GuiUtil.Mono);
                        return false;
                    }
            }
        }

        /// <summary>
        /// Editor for one 8-byte file reference at <paramref name="offset"/> inside an element's
        /// value. Returns true when the bytes changed: the new hash is written in place, so the
        /// value stays the same size and everything after it keeps its layout.
        /// </summary>
        bool DrawReferenceEditor(PropElement el, int elementIndex, int offset, string caption)
        {
            if (offset + 8 > el.Value.Length)
            {
                GUILayout.Label("The value is too short to hold a reference.", GuiUtil.Note);
                return false;
            }

            ulong h = PropFile.ReadU64(el.Value, offset);
            string file = FileLabel(h);
            GUILayout.Label(caption + ": " + (file ?? "(no name for this hash)"), GuiUtil.Note);
            GUILayout.Label("hash 0x" + h.ToString("X16"), GuiUtil.Note);

            GUILayout.BeginHorizontal();
            bool pick = GUILayout.Button("Replace reference...", GuiUtil.W(170));
            bool add = GUILayout.Button("Add Custom Name...", GuiUtil.W(150));
            bool copy = GUILayout.Button("Copy hash", GuiUtil.W(90));
            GUILayout.EndHorizontal();
            GUILayout.Label("Pick a name from the archive dumps and the CustomNames folder, or type your own with"
                            + " [Add Custom Name...].", GuiUtil.Note);

            if (copy)
            {
                GUIUtility.systemCopyBuffer = "0x" + h.ToString("X16");
                SetStatus("Copied 0x" + h.ToString("X16") + ".", false);
            }
            if (pick) OpenReferencePicker(el, elementIndex, offset);
            if (add) OpenCustomName(el, offset);
            DrawCustomNameEditor(el, offset);
            return false;
        }

        GUIStyle _bigWarning;
        int _bigWarningSize;

        /// <summary>Large bold warning text; rebuilt when the UI scale changes.</summary>
        GUIStyle BigWarning
        {
            get
            {
                int size = Mathf.RoundToInt(GuiUtil.S(15f));
                if (_bigWarning == null || _bigWarningSize != size)
                {
                    _bigWarning = new GUIStyle(GuiUtil.Warning);
                    _bigWarning.fontStyle = FontStyle.Bold;
                    _bigWarning.fontSize = size;
                    _bigWarning.wordWrap = true;
                    _bigWarning.normal.textColor = new Color(1f, 0.72f, 0.2f);
                    _bigWarningSize = size;
                }
                return _bigWarning;
            }
        }

        void OpenCustomName(PropElement el, int offset)
        {
            bool same = ReferenceEquals(_customFor, el) && _customOffset == offset;
            _customFor = same ? null : el; // the button toggles the editor
            _customOffset = offset;
            _customInput = "";
            _customError = null;
        }

        /// <summary>
        /// Inline editor under a reference: the typed name is saved to custom_names.txt and the
        /// reference is pointed at it. Draws nothing unless it was opened for this reference.
        /// </summary>
        void DrawCustomNameEditor(PropElement el, int offset)
        {
            if (!ReferenceEquals(_customFor, el) || _customOffset != offset) return;

            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("WARNING: the prop file stores only a hash of this name, not the name itself. The name is"
                + " recorded in " + CustomNameStore.FolderName + "/" + CustomNameStore.DefaultFileName
                + " and nowhere else. If that file is deleted or edited, or the prop is opened by a copy of the tool"
                + " that does not have this name in its CustomNames folder, the reference shows as an unreadable hash"
                + " and the name cannot be recovered from the prop.", BigWarning);
            GuiUtil.Space(4);
            GUILayout.Label("New name, with its extension (for example my_texture.d3dtx):", GuiUtil.Wrap);

            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("customNameField");
            _customInput = GUILayout.TextField(_customInput ?? "", 260, GuiUtil.MinW(240));
            bool enter = Event.current.type == EventType.KeyDown
                         && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                         && GUI.GetNameOfFocusedControl() == "customNameField";
            bool ok = GUILayout.Button("Add and apply", GuiUtil.W(120));
            bool cancel = GUILayout.Button("Cancel", GuiUtil.W(70));
            GUILayout.EndHorizontal();

            string clean, why;
            if (CustomNameStore.Validate(_customInput, out clean, out why))
            {
                ulong h = HashDatabase.Crc64(clean);
                string known = FileLabel(h);
                GUILayout.Label("hash 0x" + h.ToString("X16") + (known != null ? "   (already known as " + known + ")" : ""),
                    GuiUtil.Note);
            }
            if (!string.IsNullOrEmpty(_customError)) GUILayout.Label(_customError, GuiUtil.Error);
            GUILayout.EndVertical();

            if (enter) Event.current.Use();
            if (cancel) { _customFor = null; _customError = null; return; }
            if (ok || enter) ApplyCustomName(el, offset);
        }

        void ApplyCustomName(PropElement el, int offset)
        {
            string clean, why;
            if (!CustomNameStore.Validate(_customInput, out clean, out why)) { _customError = why; return; }
            if (_ctx.CustomNames == null) { _customError = "The CustomNames folder is not available."; return; }

            ulong hash = HashDatabase.Crc64(clean);
            bool alreadyKnown = FileLabel(hash) != null;
            try
            {
                // A name the tool can already resolve is not written twice; the reference is still set.
                if (!alreadyKnown)
                {
                    _ctx.CustomNames.Append(clean);
                    _ctx.PropNames.AddCustomFileName(hash, clean);
                }
            }
            catch (Exception ex)
            {
                _customError = "Could not save to " + _ctx.CustomNames.DefaultFile + ": " + ex.Message;
                return;
            }

            PropFile.WriteU64(el.Value, offset, hash);
            _dirty = true;
            _customFor = null;
            _customError = null;
            GuiUtil.ResetBuffers();
            SetStatus((alreadyKnown ? "The name was already known; reference set to " : "Added custom name, reference set to ")
                + clean + " (0x" + hash.ToString("X16") + "). Save to write the file.", false);
        }

        /// <summary>
        /// Lists every 8-byte slot in an undecoded value that resolves to a known file name, and
        /// makes each one editable. Layout is unknown, so slots are recognised by their content.
        /// </summary>
        void DrawReferenceSlots(PropElement el, int elementIndex)
        {
            byte[] value = el.Value;
            int shown = 0;
            for (int o = 0; o + 8 <= value.Length && shown < 32; o++)
            {
                ulong h = PropFile.ReadU64(value, o);
                if (h == 0 || FileLabel(h) == null) continue;
                if (shown == 0) GUILayout.Label("File references inside this value:", GuiUtil.SubHeader);
                DrawReferenceEditor(el, elementIndex, o, "+" + o);
                shown++;
                o += 7;
            }
        }

        void OpenReferencePicker(PropElement el, int elementIndex, int offset)
        {
            IReadOnlyList<string> names = _ctx.PropNames.FileNameList;
            if (names.Count == 0)
            {
                SetStatus("No names are loaded to pick from. Use [Add Custom Name...] to type one.", true);
                return;
            }

            ulong current = PropFile.ReadU64(el.Value, offset);
            string currentName = FileLabel(current);
            int preselect = -1;
            if (currentName != null)
            {
                // The list is sorted case-insensitively; scan only until the first match.
                for (int i = 0; i < names.Count; i++)
                {
                    if (string.Equals(names[i], currentName, StringComparison.OrdinalIgnoreCase)) { preselect = i; break; }
                }
            }

            // Names are ASCII; the target name is shown with its hash before applying.
            Action<int> onPick = chosen =>
            {
                ulong hash = HashDatabase.Crc64(names[chosen]);
                PropFile.WriteU64(el.Value, offset, hash);
                _dirty = true;
                GuiUtil.ResetBuffers();
                SetStatus("Reference set to " + names[chosen] + " (0x" + hash.ToString("X16") + "). Save to write the file.", false);
            };

            _refPicker.Open("Choose the file this reference should point at", names, preselect, null, onPick);
        }

        static bool BoolValue(byte[] v)
        {
            return v.Length > 0 && v[0] != 0 && v[0] != (byte)'0';
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

        // ================================================================ material tab
        /// <summary>The decoded material of the open file, re-decoded only when the block changes.</summary>
        PropMaterial CurrentMaterial(out PropField field)
        {
            field = MaterialPort.FindMaterialField(_file);
            if (field == null)
            {
                _matSource = null; _mat = null; _matWhy = null; _matReport = null;
                return null;
            }
            byte[] src = field.Elements[0].Value;
            if (!ReferenceEquals(src, _matSource))
            {
                _matSource = src;
                _mat = PropMaterial.TryParse(src, out _matWhy);
                _matReport = null;
            }
            return _mat;
        }

        void DrawMaterialTab()
        {
            PropField mf;
            PropMaterial m = CurrentMaterial(out mf);

            _matScroll = GUILayout.BeginScrollView(_matScroll, false, true,
                GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));

            if (mf == null)
            {
                GUILayout.Label("This file has no material (no T3MaterialData property). A donor's material can be"
                    + " transplanted into it from the Port fields tab.", GuiUtil.Note);
                GUILayout.EndScrollView();
                return;
            }
            if (m == null)
            {
                GUILayout.Label("The material block could not be decoded (" + _matWhy + "). It is kept unchanged when saving.",
                    GuiUtil.Warning);
                GUILayout.EndScrollView();
                return;
            }

            GUILayout.Label("Material", GuiUtil.SubHeader);
            var head = new StringBuilder();
            head.Append("Domain ").Append(MaterialEnums.DomainName(m.Domain));
            head.Append("   |   version ").Append(m.Version);
            head.Append("   |   ").Append(m.RuntimeProperties.Count).Append(" runtime properties");
            for (int i = 0; i < m.Compiled.Count; i++)
                head.Append("   |   ").Append(MaterialEnums.QualityName(m.Compiled[i].Quality))
                    .Append(" quality: ").Append(m.Compiled[i].Passes.Count).Append(" passes");
            GUILayout.Label(head.ToString(), GuiUtil.Note);
            GUILayout.Label("The material block is compiled shader data and is shown read-only. Its textures and values are"
                + " ordinary properties of this file, and the ones it already has can be changed here. Adding or removing"
                + " textures or values can change which compiled shader the material needs; to replace a material"
                + " safely, transplant a whole one from another file (Port fields tab).", GuiUtil.Wrap);
            GuiUtil.Space(8);

            HashSet<ulong> owned = MaterialPort.OwnedKeys(_file, m, KeyName);
            HashSet<ulong> statics = m.StaticKeys();
            HashSet<ulong> slotKeys = new HashSet<ulong>();
            int ui = 0; // keeps editor ids unique within this tab

            // ---- textures
            List<MatTexture> slots = m.TextureSlots();
            GUILayout.Label("Textures (" + slots.Count + " slots)", GuiUtil.SubHeader);
            GUILayout.Label("Pick a texture from the archive dumps and the CustomNames folder, or type your own with"
                + " [Add Custom Name...].", GuiUtil.Note);
            for (int i = 0; i < slots.Count; i++)
            {
                MatTexture t = slots[i];
                slotKeys.Add(t.Name);
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label(KeyShort(t.Name) + "    " + MaterialEnums.LayoutName(t.Layout) + ", "
                    + MaterialEnums.TextureTypeNames(t.TextureTypes), GuiUtil.Note);
                PropField owner;
                PropElement el = MaterialPort.FindValue(_file, t.Name, out owner);
                if (el == null)
                    GUILayout.Label("Not set in this file; the engine default (or a parent prop) applies.", GuiUtil.Note);
                else if (el.Value.Length >= 8)
                {
                    ulong h = PropFile.ReadU64(el.Value, 0);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(FileLabel(h) ?? "0x" + h.ToString("X16") + " (no name for this hash)", GuiUtil.Wrap);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Replace texture...", GuiUtil.W(140))) OpenReferencePicker(el, 30000 + ui, 0);
                    if (GUILayout.Button("Add Custom Name...", GuiUtil.W(150))) OpenCustomName(el, 0);
                    if (GUILayout.Button("Copy hash", GuiUtil.W(90)))
                    {
                        GUIUtility.systemCopyBuffer = "0x" + h.ToString("X16");
                        SetStatus("Copied 0x" + h.ToString("X16") + ".", false);
                    }
                    GUILayout.EndHorizontal();
                    DrawCustomNameEditor(el, 0);
                }
                else
                    GUILayout.Label(HexDump(el.Value), GuiUtil.Mono);
                ui++;
                GUILayout.EndVertical();
            }
            GuiUtil.Space(8);

            // ---- other values the material owns
            GUILayout.Label("Values set in this file", GuiUtil.SubHeader);
            int shown = 0;
            for (int f = 0; f < _file.Fields.Count; f++)
            {
                PropField field = _file.Fields[f];
                if (field.Kind == PropKind.Material) continue;
                for (int j = 0; j < field.Elements.Count; j++)
                {
                    PropElement el = field.Elements[j];
                    if (!owned.Contains(el.KeyHash) || slotKeys.Contains(el.KeyHash)) continue;
                    shown++;
                    GUILayout.BeginVertical(GUI.skin.box);
                    GUILayout.Label(KeyShort(el.KeyHash) + "    (" + TypeLabel(field.TypeHash) + ")", GuiUtil.Note);
                    if (statics.Contains(el.KeyHash))
                    {
                        GUILayout.Label(ValueText(field, el), GuiUtil.Mono);
                        GUILayout.Label("Static switch: it was fixed when the material was compiled, so changing it may not"
                            + " match the compiled shaders. Shown read-only.", GuiUtil.Note);
                    }
                    else if (DrawElementEditor(field, el, 40000 + ui))
                    {
                        _dirty = true;
                    }
                    ui++;
                    GUILayout.EndVertical();
                }
            }
            if (shown == 0) GUILayout.Label("None besides the textures.", GuiUtil.Note);

            int unset = 0;
            foreach (ulong k in m.ReferencedKeys())
            {
                PropField o;
                if (MaterialPort.FindValue(_file, k, out o) == null) unset++;
            }
            GUILayout.Label(unset + " other input(s) the material reads are not set in this file and use their defaults.",
                GuiUtil.Note);
            GuiUtil.Space(8);

            // ---- full listing
            _matShowDetails = GUILayout.Toggle(_matShowDetails, " Show the full compiled data");
            if (_matShowDetails)
            {
                if (_matReport == null) _matReport = MaterialReport(m);
                GuiUtil.ReadOnlyText(_matReport);
            }

            GUILayout.EndScrollView();
        }

        /// <summary>One-line, read-only rendering of a value.</summary>
        string ValueText(PropField field, PropElement el)
        {
            byte[] v = el.Value;
            switch (field.Kind)
            {
                case PropKind.Bool: return BoolValue(v) ? "true" : "false";
                case PropKind.Float: return GuiUtil.Fmt(PropFile.ReadFloat(v, 0));
                case PropKind.Vec2:
                case PropKind.Vec3:
                case PropKind.Color:
                    {
                        var sb = new StringBuilder();
                        for (int o = 0; o + 4 <= v.Length; o += 4)
                            sb.Append(o == 0 ? "" : ", ").Append(GuiUtil.Fmt(PropFile.ReadFloat(v, o)));
                        return sb.ToString();
                    }
                case PropKind.Text: return Encoding.ASCII.GetString(v);
                default:
                    if (v.Length == 8)
                    {
                        ulong h = PropFile.ReadU64(v, 0);
                        return FileLabel(h) ?? KeyName(h) ?? "0x" + h.ToString("X16");
                    }
                    return HexDump(v);
            }
        }

        /// <summary>Text listing of everything in the compiled block.</summary>
        string MaterialReport(PropMaterial m)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Material name      0x" + m.MaterialName.ToString("X16"));
            sb.AppendLine("Runtime set name   0x" + m.RuntimePropertiesName.ToString("X16"));
            sb.AppendLine("Legacy properties  " + KeyShort(m.LegacyRenderTextureProperty) + ", " + KeyShort(m.LegacyBlendModeRuntimeProperty));
            sb.AppendLine("Domain             " + MaterialEnums.DomainName(m.Domain));
            sb.AppendLine("Flags              0x" + m.Flags.ToString("X"));
            sb.AppendLine("Version            " + m.Version);
            sb.AppendLine();
            sb.AppendLine("Runtime properties (" + m.RuntimeProperties.Count + "): property -> runtime name");
            for (int i = 0; i < m.RuntimeProperties.Count; i++)
                sb.AppendLine("  " + KeyShort(m.RuntimeProperties[i].Name) + "  ->  0x" + m.RuntimeProperties[i].RuntimeName.ToString("X16"));

            for (int v = 0; v < m.Compiled.Count; v++)
            {
                MatCompiled c = m.Compiled[v];
                sb.AppendLine();
                sb.AppendLine("==== Variant " + v + ": " + MaterialEnums.QualityName(c.Quality) + " quality");
                sb.AppendLine("Passes (" + c.Passes.Count + "):");
                for (int i = 0; i < c.Passes.Count; i++)
                    sb.AppendLine("  " + MaterialEnums.PassName(c.Passes[i].PassType).PadRight(26)
                        + MaterialEnums.BlendName(c.Passes[i].BlendMode).PadRight(16)
                        + "crc 0x" + c.Passes[i].MaterialCrc.ToString("X16"));
                sb.AppendLine("Parameters (" + c.Parameters.Count + "): name, value type, property, flags, scalar offset, pre-shader offset");
                for (int i = 0; i < c.Parameters.Count; i++)
                {
                    MatParameter p = c.Parameters[i];
                    sb.AppendLine("  " + KeyShort(p.Name).PadRight(44) + MaterialEnums.ValueTypeName(p.ValueType).PadRight(10)
                        + MaterialEnums.PropertyTypeName(p.PropertyType).PadRight(20) + ("0x" + p.Flags.ToString("X")).PadRight(8)
                        + p.ScalarOffset.ToString().PadRight(7) + p.PreShaderScalarOffset
                        + (p.NestedMaterialIndex >= 0 ? "  nested " + p.NestedMaterialIndex : ""));
                }
                sb.AppendLine("Textures (" + c.Textures.Count + "): slot, layout, types, texture index, first param, param count");
                for (int i = 0; i < c.Textures.Count; i++)
                {
                    MatTexture t = c.Textures[i];
                    sb.AppendLine("  " + KeyShort(t.Name).PadRight(44) + MaterialEnums.LayoutName(t.Layout).PadRight(10)
                        + MaterialEnums.TextureTypeNames(t.TextureTypes).PadRight(24) + t.TextureIndex.ToString().PadRight(5)
                        + t.FirstParamIndex.ToString().PadRight(5) + t.ParamCount);
                }
                sb.AppendLine("Transforms (" + c.Transforms.Count + "):");
                for (int i = 0; i < c.Transforms.Count; i++)
                    sb.AppendLine("  " + KeyShort(c.Transforms[i].ParameterPrefix) + "  offsets " + c.Transforms[i].ScalarOffset0
                        + ", " + c.Transforms[i].ScalarOffset1);
                sb.AppendLine("Static parameters (" + c.StaticParameters.Count + "):");
                for (int i = 0; i < c.StaticParameters.Count; i++)
                    sb.AppendLine("  " + KeyShort(c.StaticParameters[i].Name));
                sb.AppendLine("Pre-shaders " + c.PreShaders.Count + ", pre-shader code " + c.PreShaderBuffer.Length + " bytes"
                    + ", nested materials " + c.NestedMaterials.Count + ", texture parameters " + c.TextureParams.Count);
                sb.AppendLine("Parameter buffer scalars " + c.ParameterBufferScalarSize0 + " / " + c.ParameterBufferScalarSize1
                    + ", pre-shader scalars " + c.PreShaderParameterBufferScalarSize + ", flags 0x" + c.Flags.ToString("X"));
                sb.AppendLine("Bitsets: blend modes 0x" + c.BlendModes[0].ToString("X") + ", passes 0x" + c.PassMask[0].ToString("X")
                    + ", channels 0x" + c.Channels[1].ToString("X8") + c.Channels[0].ToString("X8")
                    + ", scene textures 0x" + c.SceneTextures[0].ToString("X") + ", optional properties 0x" + c.OptionalPropertyTypes[0].ToString("X"));
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

            GUILayout.BeginHorizontal();
            bool donorHasMaterial = MaterialPort.FindMaterialField(_donor) != null;
            GUI.enabled = donorHasMaterial;
            if (GUILayout.Button("Transplant material from donor...", GuiUtil.W(240))) ConfirmTransplant();
            GUI.enabled = true;
            GUILayout.Label(donorHasMaterial
                ? "Replaces this file's material block and all of its textures and values with the donor's."
                : "The donor has no material to transplant.", GuiUtil.Note);
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

        void ConfirmTransplant()
        {
            if (_file == null || _donor == null) return;
            string msg = "Replace the material of " + Path.GetFileName(_file.SourcePath) + " with the one from "
                + Path.GetFileName(_donor.SourcePath) + "? Its material block, textures and material values are all"
                + " replaced; other properties are kept. Nothing is written until you save.";
            _confirm.Open("Transplant material", msg, new[] { "Transplant", "Cancel" }, choice =>
            {
                if (choice == 0) DoTransplant();
            });
        }

        void DoTransplant()
        {
            try
            {
                MaterialTransplantResult r = MaterialPort.Transplant(_file, _donor, KeyName);
                _portLog = r.Log;
                if (!r.Success)
                {
                    SetStatus(r.Log.Trim(), true);
                    return;
                }
                _dirty = true;
                _selected = Mathf.Clamp(_selected, 0, Mathf.Max(0, _file.Fields.Count - 1));
                GuiUtil.ResetBuffers();
                Compare();
                SetStatus("Transplanted the material from " + Path.GetFileName(_donor.SourcePath) + ". Save to write the file.", false);
            }
            catch (Exception ex)
            {
                SetStatus("Transplant failed: " + ex.Message, true);
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
                string nm = TypeLabel(h);
                switch (h)
                {
                    case PropTypeNames.TypeBool: nm += "  (struct: 1 byte)"; break;
                    case PropTypeNames.TypeFloat: nm += "  (struct: 4 bytes)"; break;
                    case PropTypeNames.TypeString: nm += "  (length-prefixed)"; break;
                    case PropTypeNames.TypeColor: nm += "  (struct: 16 bytes)"; break;
                    case PropTypeNames.TypeBounds: nm += "  (struct: 12 bytes)"; break;
                    case PropTypeNames.TypeSymbol: nm += "  (struct: 8 bytes)"; break;
                }
                sb.AppendLine("  " + i + "  0x" + h.ToString("X16") + "  " + nm);
            }

            GuiUtil.ReadOnlyText(sb.ToString());
            GUILayout.EndScrollView();
        }
    }
}
