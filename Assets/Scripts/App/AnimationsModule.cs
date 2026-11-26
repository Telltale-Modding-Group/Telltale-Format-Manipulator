using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TFM.Core;
using TFM.Core.Lipsync;
using TFM.Core.Meta;
using UnityEngine;

namespace TFM.App
{
    /// <summary>
    /// Animation (.anm) editor: the header, one row per animated value on a timeline, and a keyframe
    /// editor for the selected key. Uncompressed animations are fully editable (times, transforms,
    /// phonemes, added/removed tracks). Files whose pose is stored compressed are shown read-only
    /// with a warning until the quantized payload is decoded.
    /// </summary>
    public class AnimationsModule : IFormatModule, IFileOpener
    {
        public const string ModuleTitle = "Animations";
        public string Title { get { return ModuleTitle; } }

        const string AnmFilter = "Telltale animation (*.anm)\0*.anm\0All files (*.*)\0*.*\0";

        enum Tab { Timeline, Tracks, Lipsync, Info }

        AppContext _ctx;
        Rect _area;
        Tab _tab = Tab.Timeline;

        AnmDocument _doc;
        bool _dirty;
        string _path = "";

        AnmTrack _selTrack;
        AnmSample _selSample;
        Vector2 _tlScroll, _inspScroll, _infoScroll, _trackScroll;
        float _pxPerSec = 120f;
        bool _snap = true;

        readonly MetaTreeView _tree = new MetaTreeView();
        readonly ConfirmDialog _confirm = new ConfirmDialog();
        readonly ListPicker _bonePicker = new ListPicker();
        readonly ListPicker _trackPicker = new ListPicker();

        // Keyframe drag state (timeline).
        AnmTrack _dragTrack;
        AnmSample _dragSample;
        float _dragMouseX0, _dragTime0;

        // Phoneme name text buffer, reset when the selection changes.
        AnmSample _phonFor;
        string _phonBuf = "";

        float _newKeyTime = 0.5f;

        // ------------------------------------------------ lipsync generator
        string _lsAudio = "", _lsTemplate = "", _lsOut = "";
        float _lsSensitivity = 0.5f;
        float _lsMinPause = 0.12f;
        float _lsMinKeyMs = 100f;      // the shipped lines change viseme about every 100 ms
        int _lsSmooth = 1;
        float _lsOpenScale = 1f;       // multiplies how far the mouth opens

        // 0 = built-in rules, 1 = shipped pre-calibration, 2 = calibration trained here
        int _lsModelMode;
        VisemeModel _preModel;
        const string ModelModePref = "tfm.lipsync.modelMode";
        bool _lsUseOpenAsTemplate = true;
        bool _lsOpenResult = true;
        string _lsInFolder = "", _lsOutFolder = "";
        bool _lsSameFolder = true;
        bool _lsOverwrite;
        BatchJob _job;
        string _jobSummary = "";

        VisemeModel _model;
        string _modelPath = "";
        string _lsAnmFolder = "";
        CalibJob _calib;
        string _calibSummary = "";

        sealed class CalibJob
        {
            public string[] Files;
            public string AudioFolder;
            public VisemeModel Model;
            public int Index, FilesUsed, Keys;
            public bool Waiting;
            public string LastError;
        }

        sealed class BatchJob
        {
            public string[] Files;
            public string OutFolder;
            public string Template;
            public bool Overwrite;
            public float Sensitivity, MinPause, MinKeyMs, OpenScale;
            public int Smoothing;
            public VisemeModel Model;
            public int Index, Ok, Failed, Skipped;
            public bool Waiting;
            public string LastError;
            public int Total { get { return Files != null ? Files.Length : 0; } }
        }

        /// <summary>The viseme symbols the shipped lipsync files use; also offered as quick buttons.</summary>
        static readonly string[] PhonemeNames = {
            "aA", "eE", "i", "o", "u", "ah", "ss", "sH", "fV", "tH", "mM", "nN", "lL", "default"
        };
        static readonly string[] TangentNames = { "Unknown", "Stepped", "Knot", "Smooth", "Flat" };

        static Dictionary<ulong, string> _phonemeMap;

        // ================================================================ module
        public void Initialize(AppContext ctx)
        {
            _ctx = ctx;
            _tree.SymbolName = Lookup;
            LoadModel();
        }

        void LoadModel()
        {
            try
            {
                _modelPath = Path.Combine(AppMain.CustomNamesFolder(), "lipsync-model.txt");
                if (File.Exists(_modelPath)) _model = VisemeModel.Parse(File.ReadAllText(_modelPath));
            }
            catch { _model = null; }

            try
            {
                if (_ctx != null && !string.IsNullOrEmpty(_ctx.LipsyncModelText))
                    _preModel = VisemeModel.Parse(_ctx.LipsyncModelText);
            }
            catch { _preModel = null; }

            _lsModelMode = PlayerPrefs.GetInt(ModelModePref, _model != null ? 2 : _preModel != null ? 1 : 0);
            if (_lsModelMode == 1 && _preModel == null) _lsModelMode = _model != null ? 2 : 0;
            if (_lsModelMode == 2 && _model == null) _lsModelMode = _preModel != null ? 1 : 0;
        }

        /// <summary>The model the chosen mode uses, or null for the built-in rules.</summary>
        VisemeModel ActiveModel()
        {
            if (_lsModelMode == 1) return _preModel;
            if (_lsModelMode == 2) return _model;
            return null;
        }

        void SetModelMode(int mode)
        {
            _lsModelMode = mode;
            PlayerPrefs.SetInt(ModelModePref, mode);
            PlayerPrefs.Save();
        }

        void SaveModel()
        {
            try
            {
                if (_model != null && _model.IsTrained) File.WriteAllText(_modelPath, _model.Serialize());
            }
            catch (Exception e) { SetStatus("Could not save the calibration: " + e.Message, true); }
        }

        public void Update()
        {
            if (_job != null && !_job.Waiting) StepBatch();
            if (_calib != null && !_calib.Waiting) StepCalibration();
        }

        public bool HasUnsavedChanges { get { return _dirty; } }

        public bool SaveAll() { return !_dirty || Save(); }

        void SetStatus(string msg, bool error) { if (_ctx != null) _ctx.SetStatus(msg, error); }

        void MarkDirty(string msg)
        {
            _dirty = true;
            if (msg != null) SetStatus(msg + " Save to write the file.", false);
        }

        // ================================================================ names
        static Dictionary<ulong, string> PhonemeMap()
        {
            if (_phonemeMap == null)
            {
                _phonemeMap = new Dictionary<ulong, string>();
                for (int i = 0; i < PhonemeNames.Length; i++)
                {
                    ulong h = HashDatabase.Crc64(PhonemeNames[i]);
                    if (!_phonemeMap.ContainsKey(h)) _phonemeMap[h] = PhonemeNames[i];
                }
            }
            return _phonemeMap;
        }

        string PhonemeName(ulong h)
        {
            string n;
            if (h == 0) return "(none)";
            n = SpeechAnalyzer.VisemeName(h);
            if (n != null) return n;
            if (PhonemeMap().TryGetValue(h, out n)) return n;
            if (_ctx != null && _ctx.BoneNames != null && _ctx.BoneNames.TryGetName(h, out n)) return n;
            return "0x" + h.ToString("X16");
        }

        string Lookup(ulong h)
        {
            if (h == 0) return null;
            string n;
            if (_ctx.BoneNames != null && _ctx.BoneNames.TryGetName(h, out n)) return n;
            if (PhonemeMap().TryGetValue(h, out n)) return n;
            if (h == HashDatabase.Crc64("phoneme")) return "phoneme";
            if (h == HashDatabase.Crc64("skeletonPose")) return "skeletonPose";
            n = _ctx.PropNames != null ? _ctx.PropNames.FileRef(h) : null;
            if (n != null) return n;
            n = MetaLayouts.Current.TypeName(h);
            return n;
        }

        string TrackLabel(AnmTrack t)
        {
            string n = Lookup(t.Name);
            if (n == null) n = "0x" + t.Name.ToString("X16");
            return n;
        }

        static string KindTag(AnmTrack t)
        {
            switch (t.Kind)
            {
                case AnmTrackKind.Phoneme: return "phoneme";
                case AnmTrackKind.Transform: return "transform";
                case AnmTrackKind.LocationInfo: return "location";
                case AnmTrackKind.Scalar: return "float";
                case AnmTrackKind.Toggle: return "bool";
                case AnmTrackKind.Vector: return "vector";
                case AnmTrackKind.Pose: return "COMPRESSED POSE";
                default: return t.Element ?? t.ShortType;
            }
        }

        bool IsCompressed
        {
            get
            {
                if (_doc == null) return false;
                for (int i = 0; i < _doc.Tracks.Count; i++) if (_doc.Tracks[i].IsPose) return true;
                return false;
            }
        }

        // ================================================================ GUI
        public void OnGUI(Rect area)
        {
            _area = area;
            if (Event.current.type == EventType.Layout || Event.current.type == EventType.Repaint) GuiUtil.BeginFrame();
            GUILayout.BeginArea(area);
            DrawToolbar();
            if (_doc == null)
            {
                GuiUtil.Space(10);
                GUILayout.Label("No animation loaded. Use [Open .anm...] or type a path and press [Load path].", GuiUtil.Note);
                GUILayout.Label("Animations hold keyframed values: bone transforms, object locations, floats, toggles"
                    + " and lipsync phonemes. Each row on the timeline is one animated bone or value.", GuiUtil.Wrap);
                GUILayout.EndArea();
                DrawPopups();
                return;
            }

            DrawTabs();
            switch (_tab)
            {
                case Tab.Timeline: DrawTimelineTab(); break;
                case Tab.Tracks: DrawTracksTab(); break;
                case Tab.Lipsync: DrawLipsyncTab(); break;
                case Tab.Info: DrawInfoTab(); break;
            }
            GUILayout.EndArea();
            DrawPopups();
        }

        void DrawPopups()
        {
            Rect screen = _ctx.ScreenRect.width > 0 ? _ctx.ScreenRect : new Rect(0, 0, Screen.width, Screen.height);
            if (_confirm.IsOpen) _confirm.OnGUI(screen);
            if (_bonePicker.IsOpen) _bonePicker.OnGUI(screen);
            if (_trackPicker.IsOpen) _trackPicker.OnGUI(screen);
        }

        void DrawToolbar()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Open .anm...", GuiUtil.W(110))) BrowseOpen();
            if (GUILayout.Button("Reload", GuiUtil.W(70))) Reload();
            GUI.enabled = _doc != null && _dirty;
            if (GUILayout.Button("Save", GuiUtil.W(70))) Save();
            GUI.enabled = _doc != null;
            if (GUILayout.Button("Save as...", GuiUtil.W(80))) SaveAs();
            GUI.enabled = true;
            _path = GUILayout.TextField(_path ?? "", GuiUtil.MinW(180));
            if (GUILayout.Button("Load path", GuiUtil.W(80)))
            {
                string p = (_path ?? "").Trim().Trim('"');
                if (p.Length > 0) _ctx.RunDeferred(() => LoadFile(p));
            }
            GUILayout.EndHorizontal();

            if (_doc == null) return;

            StringBuilder b = new StringBuilder();
            b.Append(Path.GetFileName(_doc.SourcePath ?? "(unsaved)"));
            if (_dirty) b.Append(" *");
            b.Append("   |   ").Append(_doc.Tracks.Count).Append(" value(s)");
            b.Append("   |   length ").Append(GuiUtil.Fmt(_doc.Length)).Append(" s");
            b.Append("   |   name ").Append(Lookup(_doc.RootName) ?? "0x" + _doc.RootName.ToString("X16"));
            if (!_doc.File.FullyDecoded) b.Append("   |   ").Append(_doc.File.RawRegions).Append(" undecoded region(s)");
            GUILayout.Label(b.ToString(), GuiUtil.Note);

            if (IsCompressed)
            {
                GUI.color = new Color(1f, 0.55f, 0.55f);
                GUILayout.Label("THIS FILE IS COMPRESSED AND IS NOT SUPPORTED YET", GuiUtil.Header);
                GUI.color = Color.white;
                GUILayout.Label("Its pose is stored as quantized skeleton data that has not been decoded. Everything can be"
                    + " inspected and the file saved unchanged, but the pose keyframes cannot be edited.", GuiUtil.Note);
            }
        }

        void DrawTabs()
        {
            GUILayout.BeginHorizontal();
            string[] tabs = { "Timeline", "Tracks", "Lipsync from audio", "Info / all fields" };
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
                string p = NativeFileDialog.OpenFile("Open Telltale animation", AnmFilter, CurrentDirectory());
                if (!string.IsNullOrEmpty(p)) LoadFile(p);
            });
        }

        string CurrentDirectory()
        {
            try { if (!string.IsNullOrEmpty(_path) && File.Exists(_path)) return Path.GetDirectoryName(_path); } catch { }
            return null;
        }

        public bool OpenFile(string path)
        {
            if (_dirty)
            {
                _confirm.Open("Unsaved changes", "The open animation has unsaved changes. Open " + Path.GetFileName(path) + " anyway?",
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
                AnmDocument doc = AnmDocument.Load(path);
                _doc = doc;
                _path = path;
                _dirty = false;
                _selTrack = doc.Tracks.Count > 0 ? doc.Tracks[0] : null;
                _selSample = _selTrack != null && _selTrack.Samples.Count > 0 ? _selTrack.Samples[0] : null;
                _tlScroll = _inspScroll = _infoScroll = _trackScroll = Vector2.zero;
                _newKeyTime = _selTrack != null && _selTrack.Samples.Count > 0 ? _selTrack.Samples[0].Time : 0f;
                _tree.Clear();
                GuiUtil.ResetBuffers();
                _phonFor = null;

                string msg = "Loaded " + Path.GetFileName(path) + " - " + doc.Tracks.Count + " value(s)";
                if (IsCompressed) msg += ". THIS FILE IS COMPRESSED AND IS NOT SUPPORTED YET";
                SetStatus(msg + ".", IsCompressed || !doc.File.FullyDecoded);
            }
            catch (MetaParseException e) { SetStatus("Could not read " + Path.GetFileName(path) + ": " + e.Message, true); }
            catch (Exception e) { SetStatus("Could not open file: " + e.Message, true); }
        }

        void Reload()
        {
            if (_doc == null || string.IsNullOrEmpty(_doc.SourcePath)) { SetStatus("Nothing to reload.", true); return; }
            if (_dirty)
            {
                string p = _doc.SourcePath;
                _confirm.Open("Reload", "Discard unsaved changes and reload " + Path.GetFileName(p) + "?", new[] { "Reload", "Cancel" },
                    c => { if (c == 0) _ctx.RunDeferred(() => LoadFile(p)); });
                return;
            }
            LoadFile(_doc.SourcePath);
        }

        bool Save()
        {
            if (_doc == null) return true;
            if (string.IsNullOrEmpty(_doc.SourcePath)) { SetStatus("This animation has no path; use Save as.", true); return false; }
            return SaveTo(_doc.SourcePath);
        }

        void SaveAs()
        {
            if (!NativeFileDialog.IsSupported) { SetStatus("The file dialog is only available in Windows builds.", true); return; }
            _ctx.RunDeferred(() =>
            {
                string p = NativeFileDialog.SaveFile("Save animation as", AnmFilter, CurrentDirectory(),
                    Path.GetFileName(_doc.SourcePath ?? "animation.anm"), "anm");
                if (!string.IsNullOrEmpty(p)) SaveTo(p);
            });
        }

        bool SaveTo(string path)
        {
            try
            {
                _doc.Save(path);
                _path = path;
                _dirty = false;
                SetStatus("Saved " + Path.GetFileName(path) + ".", false);
                return true;
            }
            catch (Exception e)
            {
                SetStatus("Save failed: " + e.Message, true);
                return false;
            }
        }

        // ================================================================ timeline
        float TimelineEnd()
        {
            float end = _doc.Length;
            for (int i = 0; i < _doc.Tracks.Count; i++)
            {
                float e = _doc.Tracks[i].EndTime;
                if (e > end) end = e;
            }
            return end <= 0.001f ? 1f : end;
        }

        static void Fill(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        static Color TrackColor(AnmTrackKind k)
        {
            switch (k)
            {
                case AnmTrackKind.Phoneme: return new Color(0.35f, 0.65f, 0.95f);
                case AnmTrackKind.Transform: return new Color(0.55f, 0.8f, 0.45f);
                case AnmTrackKind.LocationInfo: return new Color(0.85f, 0.75f, 0.4f);
                case AnmTrackKind.Scalar: return new Color(0.8f, 0.6f, 0.9f);
                case AnmTrackKind.Toggle: return new Color(0.6f, 0.6f, 0.6f);
                case AnmTrackKind.Vector: return new Color(0.45f, 0.75f, 0.75f);
                case AnmTrackKind.Pose: return new Color(0.9f, 0.4f, 0.35f);
                default: return new Color(0.7f, 0.7f, 0.7f);
            }
        }

        void DrawTimelineTab()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Zoom", GuiUtil.W(40));
            _pxPerSec = GUILayout.HorizontalSlider(_pxPerSec, 20f, 800f, GuiUtil.W(200));
            GUILayout.Label(Mathf.RoundToInt(_pxPerSec) + " px/s", GuiUtil.W(70));
            _snap = GUILayout.Toggle(_snap, " Snap to 1/30 s", GuiUtil.W(130));
            GUILayout.FlexibleSpace();
            GUILayout.Label("Click a keyframe to edit it, drag it sideways to retime it.", GuiUtil.Note);
            GUILayout.EndHorizontal();

            float len = TimelineEnd();
            float labelW = GuiUtil.S(260f);
            float rowH = GuiUtil.S(20f);
            float rulerH = GuiUtil.S(20f);
            float scale = _pxPerSec * GuiUtil.Scale;
            float trackW = Mathf.Max(300f, len * scale + GuiUtil.S(60f));
            float viewH = Mathf.Max(150f, (_area.height - GuiUtil.S(120f)) * 0.58f);
            Rect view = GUILayoutUtility.GetRect(10f, viewH, GUILayout.ExpandWidth(true));
            Rect content = new Rect(0, 0, labelW + trackW, rulerH + _doc.Tracks.Count * rowH + 4f);
            _tlScroll = GUI.BeginScrollView(view, _tlScroll, content);

            Func<float, float> X = t => labelW + t * scale;

            Fill(new Rect(labelW, 0, trackW, rulerH), new Color(0.12f, 0.12f, 0.14f));
            float step = NiceStep(60f / scale);
            for (float t = 0f; t <= len + 0.0001f; t += step)
            {
                float x = X(t);
                Fill(new Rect(x, rulerH - 6f, 1f, 6f), Color.gray);
                GUI.Label(new Rect(x + 2f, 0, 80f, rulerH), GuiUtil.Fmt((float)Math.Round(t, 3)), GuiUtil.Note);
            }

            Event e = Event.current;
            for (int i = 0; i < _doc.Tracks.Count; i++)
            {
                AnmTrack t = _doc.Tracks[i];
                float y = rulerH + i * rowH;
                bool sel = t == _selTrack;
                if (sel) Fill(new Rect(0, y, labelW + trackW, rowH), new Color(0.22f, 0.26f, 0.32f));

                Rect lab = new Rect(_tlScroll.x, y, labelW - 4f, rowH);
                GUI.Label(lab, "[" + KindTag(t) + "] " + TrackLabel(t) + "  (" + t.Samples.Count + ")", ClipRows.Row);
                if (e.type == EventType.MouseDown && e.button == 0 && lab.Contains(e.mousePosition))
                {
                    _selTrack = t; _selSample = null; GuiUtil.ResetBuffers(); _phonFor = null; e.Use();
                }

                Color col = TrackColor(t.Kind);
                for (int k = 0; k < t.Samples.Count; k++)
                {
                    AnmSample s = t.Samples[k];
                    float x = X(s.Time);
                    bool ssel = s == _selSample;
                    Rect hit = new Rect(x - GuiUtil.S(4f), y, GuiUtil.S(8f), rowH);
                    if (ssel) Fill(hit, new Color(1f, 1f, 1f, 0.18f));
                    Rect tick = new Rect(x - GuiUtil.S(1f), y + GuiUtil.S(2f), GuiUtil.S(2f), rowH - GuiUtil.S(4f));
                    Fill(tick, ssel ? Color.white : col);
                    if (e.type == EventType.MouseDown && e.button == 0 && hit.Contains(e.mousePosition))
                    {
                        _selTrack = t;
                        _selSample = s;
                        _dragTrack = t;
                        _dragSample = s;
                        _dragMouseX0 = e.mousePosition.x;
                        _dragTime0 = s.Time;
                        GuiUtil.ResetBuffers();
                        _phonFor = null;
                        e.Use();
                    }
                }
            }

            float lx = X(_doc.Length);
            Fill(new Rect(lx, 0, 2f, content.height), new Color(1f, 0.3f, 0.3f, 0.8f));

            if (e.type == EventType.MouseDrag && _dragSample != null)
            {
                float dt = (e.mousePosition.x - _dragMouseX0) / scale;
                float nt = _dragTime0 + dt;
                if (_snap) nt = Mathf.Round(nt * 30f) / 30f;
                _dragSample.Time = nt;
                _dirty = true;
                e.Use();
            }
            if (e.rawType == EventType.MouseUp && _dragSample != null)
            {
                if (_dragTrack != null) _doc.SortSamples(_dragTrack);
                _dragSample = null;
                _dragTrack = null;
                MarkDirty("Keyframe retimed.");
            }
            GUI.EndScrollView();

            GuiUtil.Space(4);
            _inspScroll = GUILayout.BeginScrollView(_inspScroll, GUI.skin.box, GUILayout.ExpandHeight(true));
            if (_selTrack == null) GUILayout.Label("Select a track row or a keyframe.", GuiUtil.Note);
            else if (_selSample != null) DrawSampleEditor(_selTrack, _selSample);
            else DrawTrackSummary(_selTrack);
            GUILayout.EndScrollView();
        }

        static float NiceStep(float minStep)
        {
            float[] steps = { 0.01f, 0.02f, 0.05f, 0.1f, 0.2f, 0.25f, 0.5f, 1f, 2f, 5f, 10f, 15f, 30f, 60f };
            for (int i = 0; i < steps.Length; i++) if (steps[i] >= minStep) return steps[i];
            return 120f;
        }

        // ================================================================ inspector
        void DrawTrackSummary(AnmTrack t)
        {
            GUILayout.Label(TrackLabel(t) + "   (" + t.Type + ")", GuiUtil.SubHeader);
            GUILayout.Label("Keyframes: " + t.Samples.Count + "   |   range " + GuiUtil.Fmt(t.StartTime) + " .. "
                + GuiUtil.Fmt(t.EndTime) + " s", GuiUtil.Note);
            if (t.Samples.Count > 0)
                GUILayout.Label("Click a keyframe dot on the timeline to edit it.", GuiUtil.Note);
        }

        static string Id(object o, string suffix)
        {
            return "anm." + suffix + "." + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o).ToString();
        }

        void DrawSampleEditor(AnmTrack t, AnmSample s)
        {
            GUILayout.Label("Keyframe in " + TrackLabel(t), GuiUtil.SubHeader);

            float time = s.Time;
            GUILayout.BeginHorizontal();
            if (GuiUtil.LabeledFloat("Time (s)", Id(s, "time"), ref time, 90))
            {
                s.Time = time;
                MarkDirty("Keyframe moved.");
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Delete keyframe", GuiUtil.W(120)))
            {
                _doc.RemoveSample(t, s);
                _selSample = t.Samples.Count > 0 ? t.Samples[0] : null;
                MarkDirty("Keyframe deleted.");
                GuiUtil.ResetBuffers();
                return;
            }
            GUILayout.EndHorizontal();

            bool interp = s.InterpolateToNext;
            bool now = GUILayout.Toggle(interp, " Interpolate to next key");
            if (now != interp) { s.InterpolateToNext = now; MarkDirty("Interpolation changed."); }

            int tm = s.TangentMode;
            int ntm = tm >= 0 && tm < TangentNames.Length ? tm : 0;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Tangent", GuiUtil.W(90));
            int picked = GUILayout.Toolbar(Mathf.Clamp(ntm, 0, TangentNames.Length - 1), TangentNames, GuiUtil.H(20));
            GUILayout.EndHorizontal();
            if (picked != ntm && picked != tm) { s.TangentMode = picked; MarkDirty("Tangent mode changed."); }

            GuiUtil.Space(4);
            MetaValue v = s.Value;
            if (v == null) return;

            if (t.Kind == AnmTrackKind.Phoneme) DrawPhonemeEditor(s, v);
            else if (t.Kind == AnmTrackKind.Transform) DrawTransformEditor(s, v);
            else if (t.Kind == AnmTrackKind.Vector)
            {
                Vec3 vec;
                if (AnmDocument.TryGetVector(v, out vec) && GuiUtil.Vec3Field("Value", Id(s, "vec"), ref vec))
                {
                    AnmDocument.SetVector(v, vec);
                    MarkDirty("Value changed.");
                }
            }
            else if (t.Kind == AnmTrackKind.Scalar)
            {
                float f = v.AsFloat;
                if (GuiUtil.LabeledFloat("Value", Id(s, "f"), ref f, 90)) { v.AsFloat = f; MarkDirty("Value changed."); }
            }
            else if (t.Kind == AnmTrackKind.Toggle)
            {
                bool b = v.AsBool;
                bool nb = GUILayout.Toggle(b, " Value");
                if (nb != b) { v.AsBool = nb; MarkDirty("Value changed."); }
            }
            else
            {
                MetaValue copy = v;
                if (_tree.Draw("Value (" + v.Type + ")", copy, 0)) MarkDirty("Value changed.");
            }
        }

        void DrawPhonemeEditor(AnmSample s, MetaValue v)
        {
            ulong ph; float fi, ho, fo, co;
            if (!AnmDocument.TryGetPhoneme(v, out ph, out fi, out ho, out fo, out co)) return;

            if (_phonFor != s) { _phonFor = s; _phonBuf = PhonemeName(ph); }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Phoneme", GuiUtil.W(90));
            _phonBuf = GUILayout.TextField(_phonBuf ?? "", GuiUtil.MinW(100));
            if (GUILayout.Button("Set", GuiUtil.W(50)))
            {
                string name = (_phonBuf ?? "").Trim();
                if (name.Length > 0)
                {
                    ulong h = HashDatabase.Crc64(name);
                    AnmDocument.SetPhoneme(v, h, fi, ho, fo, co);
                    _phonBuf = PhonemeName(h);
                    MarkDirty("Phoneme set to " + name + ".");
                }
            }
            GUILayout.Label("hash " + ph.ToString("X16"), GuiUtil.Note);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Space(GuiUtil.S(90f));
            string quick = null;
            int shown = 0;
            for (int i = 0; i < PhonemeNames.Length && shown < 8; i++)
            {
                if (GUILayout.Button(PhonemeNames[i], GuiUtil.W(42))) quick = PhonemeNames[i];
                shown++;
            }
            GUILayout.EndHorizontal();
            if (quick != null)
            {
                ulong h = HashDatabase.Crc64(quick);
                AnmDocument.SetPhoneme(v, h, fi, ho, fo, co);
                _phonBuf = quick;
                MarkDirty("Phoneme set to " + quick + ".");
            }

            bool changed = false;
            float nfi = fi, nho = ho, nfo = fo, nco = co;
            GUILayout.BeginHorizontal();
            changed |= GuiUtil.LabeledFloat("Fade in", Id(s, "fi"), ref nfi, 90);
            changed |= GuiUtil.LabeledFloat("Hold", Id(s, "ho"), ref nho, 90);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            changed |= GuiUtil.LabeledFloat("Fade out", Id(s, "fo"), ref nfo, 90);
            changed |= GuiUtil.LabeledFloat("Contribution", Id(s, "co"), ref nco, 90);
            GUILayout.EndHorizontal();
            if (changed)
            {
                AnmDocument.SetPhoneme(v, ph, nfi, nho, nfo, nco);
                MarkDirty("Phoneme timing changed.");
            }
        }

        void DrawTransformEditor(AnmSample s, MetaValue v)
        {
            Quat rot; Vec3 pos;
            if (!AnmDocument.TryGetTransform(v, out rot, out pos))
            {
                if (_tree.Draw("Value (" + v.Type + ")", v, 0)) MarkDirty("Value changed.");
                return;
            }
            bool changed = false;
            GUILayout.Label("Rotation", GuiUtil.Note);
            changed |= GuiUtil.RotationField("Quaternion", Id(s, "rot"), ref rot, 120);
            GUILayout.Label("Translation", GuiUtil.Note);
            changed |= GuiUtil.Vec3Field("Position", Id(s, "pos"), ref pos, 120);
            if (changed)
            {
                AnmDocument.SetTransform(v, rot, pos);
                MarkDirty("Transform changed.");
            }
        }

        // ================================================================ tracks tab
        void DrawTracksTab()
        {
            GUILayout.Label("Values (" + _doc.Tracks.Count + ")", GuiUtil.SubHeader);
            _trackScroll = GUILayout.BeginScrollView(_trackScroll, GUI.skin.box, GUILayout.Height(Mathf.Max(120f, _area.height * 0.45f)));
            for (int i = 0; i < _doc.Tracks.Count; i++)
            {
                AnmTrack t = _doc.Tracks[i];
                bool sel = t == _selTrack;
                string row = i + ".  [" + KindTag(t) + "]  " + TrackLabel(t) + "   -   " + t.Samples.Count + " key(s)";
                if (GUILayout.Toggle(sel, row, sel ? ClipRows.Selected : ClipRows.Row))
                {
                    if (!sel) { _selTrack = t; _selSample = null; GuiUtil.ResetBuffers(); _phonFor = null; }
                }
            }
            GUILayout.EndScrollView();

            if (_selTrack == null)
            {
                GuiUtil.Space(6);
                GUILayout.Label("Select a value above. Adding a copy of a Transform value is how a bone is added to an animation.", GuiUtil.Note);
                return;
            }

            GuiUtil.Space(6);
            GUILayout.Label("Selected: " + TrackLabel(_selTrack) + "   (" + _selTrack.Type + ")", GuiUtil.Wrap);

            GUILayout.BeginHorizontal();
            GUI.enabled = !IsCompressed;
            if (GUILayout.Button("Duplicate as bone...", GuiUtil.W(150))) PickBone(_selTrack);
            if (GUILayout.Button("Rename...", GuiUtil.W(90))) RenameTrack(_selTrack);
            if (GUILayout.Button("Delete value", GuiUtil.W(100)))
            {
                AnmTrack t = _selTrack;
                _confirm.Open("Delete value", "Remove \"" + TrackLabel(t) + "\" and its " + t.Samples.Count + " keyframe(s) from this animation?",
                    new[] { "Delete", "Cancel" }, c =>
                    {
                        if (c != 0) return;
                        _doc.RemoveTrack(t);
                        _selTrack = _doc.Tracks.Count > 0 ? _doc.Tracks[0] : null;
                        _selSample = null;
                        MarkDirty("Value removed.");
                    });
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (_selTrack.IsPose)
            {
                GuiUtil.Space(6);
                GUI.color = new Color(1f, 0.55f, 0.55f);
                GUILayout.Label("THIS FILE IS COMPRESSED AND IS NOT SUPPORTED YET", GuiUtil.Header);
                GUI.color = Color.white;
                MetaValue data = _selTrack.Node["#data"];
                MetaValue size = _selTrack.Node["mDataSize"];
                GUILayout.Label("Pose data: " + (data != null && data.Bytes != null ? data.Bytes.Length : 0) + " bytes"
                    + (size != null ? " (declared " + size.AsInt + ")" : ""), GuiUtil.Note);
                return;
            }

            GUI.enabled = !IsCompressed && _selTrack.Samples.Count > 0;
            GUILayout.BeginHorizontal();
            _newKeyTime = _newKeyTime < 0f ? 0f : _newKeyTime;
            if (GuiUtil.LabeledFloat("New keyframe at (s)", "anm.newkey", ref _newKeyTime, 140))
            {
            }
            if (GUILayout.Button("Add", GuiUtil.W(60)))
            {
                AnmSample s = _doc.AddSample(_selTrack, _newKeyTime);
                if (s == null) SetStatus("No keyframe template of this type exists in the file, so a key cannot be added.", true);
                else { _selSample = s; MarkDirty("Keyframe added."); }
            }
            GUILayout.EndHorizontal();
            GUI.enabled = true;

            _trackScroll = GUILayout.BeginScrollView(_trackScroll, GUI.skin.box);
            if (_selTrack.Samples.Count == 0) GUILayout.Label("No keyframes.", GuiUtil.Note);
            for (int i = 0; i < _selTrack.Samples.Count; i++)
            {
                AnmSample s = _selTrack.Samples[i];
                bool sel = s == _selSample;
                string row = "#" + i + "   t=" + GuiUtil.Fmt(s.Time) + "   " + SampleSummary(s);
                if (GUILayout.Toggle(sel, row, sel ? ClipRows.Selected : ClipRows.Row))
                {
                    if (!sel) { _selSample = s; GuiUtil.ResetBuffers(); _phonFor = null; }
                }
            }
            GUILayout.EndScrollView();

            if (_selSample != null && _selTrack.Samples.Contains(_selSample))
            {
                GuiUtil.Space(6);
                DrawSampleEditor(_selTrack, _selSample);
            }
        }

        string SampleSummary(AnmSample s)
        {
            MetaValue v = s.Value;
            if (v == null) return "";
            switch (_selTrack != null ? _selTrack.Kind : AnmTrackKind.Other)
            {
                case AnmTrackKind.Phoneme:
                    {
                        ulong ph; float fi, ho, fo, co;
                        if (AnmDocument.TryGetPhoneme(v, out ph, out fi, out ho, out fo, out co))
                            return PhonemeName(ph) + "  fade " + GuiUtil.Fmt(fi) + "/" + GuiUtil.Fmt(ho) + "/" + GuiUtil.Fmt(fo) + "  w=" + GuiUtil.Fmt(co);
                        break;
                    }
                case AnmTrackKind.Transform:
                    {
                        Quat r; Vec3 p;
                        if (AnmDocument.TryGetTransform(v, out r, out p)) return "pos " + p.ToString();
                        break;
                    }
                case AnmTrackKind.Vector:
                    {
                        Vec3 p;
                        if (AnmDocument.TryGetVector(v, out p)) return p.ToString();
                        break;
                    }
                case AnmTrackKind.Scalar: return GuiUtil.Fmt(v.AsFloat);
                case AnmTrackKind.Toggle: return v.AsBool ? "true" : "false";
            }
            return v.Type;
        }

        void RenameTrack(AnmTrack t)
        {
            List<string> names = BoneList();
            int pre = -1;
            string cur = Lookup(t.Name);
            for (int i = 0; i < names.Count; i++) if (string.Equals(names[i], cur, StringComparison.OrdinalIgnoreCase)) { pre = i; break; }
            _trackPicker.Open("Set the name of this value", names, pre, null, i =>
            {
                ulong h = HashDatabase.Crc64(names[i]);
                t.Name = h;
                MarkDirty("Value renamed to " + names[i] + ".");
            });
        }

        void PickBone(AnmTrack t)
        {
            if (t.Kind != AnmTrackKind.Transform && t.Kind != AnmTrackKind.Other)
            {
                SetStatus("Only a Transform value can be duplicated as a bone.", true);
                return;
            }
            List<string> names = BoneList();
            _bonePicker.Open("Choose a bone for the new value", names, -1, null, i =>
            {
                ulong h = HashDatabase.Crc64(names[i]);
                AnmTrack copy = _doc.DuplicateTrack(t, h);
                if (copy == null) { SetStatus("Could not duplicate the value.", true); return; }
                _selTrack = copy;
                _selSample = copy.Samples.Count > 0 ? copy.Samples[0] : null;
                MarkDirty("Added a " + names[i] + " value.");
            });
        }

        List<string> BoneList()
        {
            List<string> list = new List<string>();
            if (_ctx.BoneNames != null)
            {
                IReadOnlyList<string> src = _ctx.BoneNames.SortedNames;
                for (int i = 0; i < src.Count; i++) list.Add(src[i]);
            }
            return list;
        }

        // ================================================================ lipsync
        string TemplatePath()
        {
            if (_lsUseOpenAsTemplate && _doc != null && !IsCompressed && _doc.FirstPhonemeTrack() != null) return _doc.SourcePath;
            return string.IsNullOrEmpty(_lsTemplate) ? null : _lsTemplate;
        }

        void PickFile(string title, string filter, Action<string> set)
        {
            if (!NativeFileDialog.IsSupported) { SetStatus("The file dialog is only available in Windows builds.", true); return; }
            _ctx.RunDeferred(() =>
            {
                string p = NativeFileDialog.OpenFile(title, filter, CurrentDirectory());
                if (!string.IsNullOrEmpty(p)) set(p);
            });
        }

        void PickFolder(string title, Action<string> set)
        {
            if (!NativeFileDialog.IsSupported) { SetStatus("The folder dialog is only available in Windows builds.", true); return; }
            _ctx.RunDeferred(() =>
            {
                string p = NativeFileDialog.OpenFolder(title, CurrentDirectory());
                if (!string.IsNullOrEmpty(p)) set(p);
            });
        }

        void DrawLipsyncTab()
        {
            const string audioFilter = "Audio (*.wav;*.mp3;*.ogg)\0*.wav;*.mp3;*.ogg\0All files (*.*)\0*.*\0";
            _inspScroll = GUILayout.BeginScrollView(_inspScroll);
            // Bound the page width: expanded text fields otherwise widen the scroll content and
            // produce a horizontal scrollbar that also hides the buttons on the right.
            float contentW = Mathf.Max(360f, _area.width - GuiUtil.S(96f));
            GUILayout.BeginVertical(GUILayout.Width(contentW));

            GUILayout.Label("Generate lipsync from audio", GuiUtil.SubHeader);
            GUILayout.Label("The template supplies the file layout and rig; the audio supplies the timing. The result is a"
                + " normal uncompressed phoneme animation, editable on the Timeline tab afterwards.", GuiUtil.Wrap);

            GuiUtil.Space(6);
            GUILayout.Label("Viseme classifier", GuiUtil.SubHeader);
            GUILayout.BeginHorizontal();
            string[] modes = { "Basic rules", "Pre-calibrated data", "Custom calibration" };
            float[] widths = { 120f, 170f, 160f };
            for (int i = 0; i < modes.Length; i++)
            {
                bool active = _lsModelMode == i;
                if (GUILayout.Toggle(active, modes[i], GUI.skin.button, GuiUtil.W(widths[i])) && !active) SetModelMode(i);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (_lsModelMode == 0)
                GUILayout.Label("Acoustic rules written by hand. Nothing to set up; the safest default when the audio does not match the game.", GuiUtil.Note);
            else if (_lsModelMode == 1)
                GUILayout.Label(_preModel != null
                    ? "Calibrated on " + _preModel.KeysUsed + " keys from the shipped lipsync lines (Assets/Data/LipsyncModel.bytes)."
                    : "The pre-calibrated data is missing (Assets/Data/LipsyncModel.bytes); using the built-in rules.", GuiUtil.Note);
            else
                GUILayout.Label(_model != null
                    ? "Your own calibration: " + _model.KeysUsed + " keys, saved to " + _modelPath + "."
                    : "No custom calibration yet - run Calibrate below. Until then the built-in rules are used.", GuiUtil.Note);

            GuiUtil.Space(6);
            GUILayout.Label("Template", GuiUtil.SubHeader);
            _lsUseOpenAsTemplate = GUILayout.Toggle(_lsUseOpenAsTemplate, " Use the animation open in this tab as the template");
            bool hasOpenPhoneme = _doc != null && _doc.FirstPhonemeTrack() != null && !IsCompressed;
            if (_lsUseOpenAsTemplate && !hasOpenPhoneme)
                GUILayout.Label("(The open file has no phoneme track - pick a template below.)", GuiUtil.Warning);
            GUI.enabled = !_lsUseOpenAsTemplate;
            GUILayout.BeginHorizontal();
            _lsTemplate = GUILayout.TextField(_lsTemplate ?? "", GuiUtil.W(Mathf.Max(160f, contentW - GuiUtil.S(200f))));
            if (GUILayout.Button("Browse...", GuiUtil.W(80))) PickFile("Choose a lipsync template", AnmFilter, t => _lsTemplate = t);
            GUILayout.EndHorizontal();
            GUI.enabled = true;

            GuiUtil.Space(8);
            GUILayout.Label("One file", GuiUtil.SubHeader);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Audio", GuiUtil.W(70));
            _lsAudio = GUILayout.TextField(_lsAudio ?? "", GuiUtil.W(Mathf.Max(160f, contentW - GuiUtil.S(200f))));
            if (GUILayout.Button("Browse...", GuiUtil.W(80)))
                PickFile("Choose audio", audioFilter, t =>
                {
                    _lsAudio = t;
                    _lsOut = Path.Combine(Path.GetDirectoryName(t) ?? "", Path.GetFileNameWithoutExtension(t) + ".anm");
                });
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Output .anm", GuiUtil.W(70));
            _lsOut = GUILayout.TextField(_lsOut ?? "", GuiUtil.W(Mathf.Max(160f, contentW - GuiUtil.S(200f))));
            if (GUILayout.Button("Browse...", GuiUtil.W(80)))
            {
                if (!NativeFileDialog.IsSupported) { SetStatus("The file dialog is only available in Windows builds.", true); }
                else _ctx.RunDeferred(() =>
                {
                    string t = NativeFileDialog.SaveFile("Save generated lipsync as", AnmFilter, CurrentDirectory(),
                        Path.GetFileNameWithoutExtension(_lsAudio ?? "lipsync") + ".anm", "anm");
                    if (!string.IsNullOrEmpty(t)) _lsOut = t;
                });
            }
            GUILayout.EndHorizontal();
            DrawLipsyncSettings();

            GUI.enabled = _job == null;
            if (GUILayout.Button("Generate", GuiUtil.W(140))) GenerateSingle();
            GUI.enabled = true;

            GuiUtil.Space(10);
            GUILayout.Label("Custom calibration", GuiUtil.SubHeader);
            GUILayout.Label("Trains a model on the game's own lines: point at a folder of lipsync .anm files and the folder"
                + " holding their audio. The result is saved and selected as \"Custom calibration\".", GuiUtil.Note);
            if (_model != null && _model.IsTrained)
                GUILayout.Label("Model: " + _model.KeysUsed + " keys, " + _model.Visemes.Count + " visemes.  " + _modelPath, GuiUtil.Note);
            else
                GUILayout.Label("Model: not calibrated - using built-in rules.", GuiUtil.Note);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Lipsync .anm folder", GuiUtil.W(120));
            _lsAnmFolder = GUILayout.TextField(_lsAnmFolder ?? "", GuiUtil.W(Mathf.Max(160f, contentW - GuiUtil.S(200f))));
            if (GUILayout.Button("Browse...", GuiUtil.W(80))) PickFolder("Choose the folder of shipped lipsync files", t => _lsAnmFolder = t);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUI.enabled = _calib == null && _job == null && !string.IsNullOrEmpty(_lsAnmFolder) && Directory.Exists(_lsAnmFolder ?? "");
            if (GUILayout.Button("Calibrate from folders", GuiUtil.W(180))) StartCalibration();
            GUI.enabled = true;
            if (_calib != null && GUILayout.Button("Cancel", GuiUtil.W(80)))
            {
                _calibSummary = "Calibration cancelled after " + _calib.Index + " file(s).";
                _calib = null;
            }
            if (_model != null && GUILayout.Button("Forget calibration", GuiUtil.W(140)))
            {
                _model = null;
                try { if (File.Exists(_modelPath)) File.Delete(_modelPath); } catch { }
                if (_lsModelMode == 2) SetModelMode(_preModel != null ? 1 : 0);
                _calibSummary = "Custom calibration cleared.";
            }
            GUILayout.EndHorizontal();

            if (_calib != null)
            {
                GUILayout.Label("Calibrating: " + Mathf.Min(_calib.Index, _calib.Files.Length) + " / " + _calib.Files.Length
                    + " files, " + _calib.Keys + " keys", GuiUtil.Note);
                if (_calib.LastError != null) GUILayout.Label(_calib.LastError, GuiUtil.Warning);
            }
            else if (!string.IsNullOrEmpty(_calibSummary))
            {
                GUILayout.Label(_calibSummary, GuiUtil.Note);
            }

            GuiUtil.Space(10);
            GUILayout.Label("Whole folder", GuiUtil.SubHeader);
            GUILayout.Label("Every WAV / MP3 / OGG in the input folder becomes <same name>.anm. Existing files are skipped"
                + " unless overwriting is allowed.", GuiUtil.Note);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Input folder", GuiUtil.W(90));
            _lsInFolder = GUILayout.TextField(_lsInFolder ?? "", GuiUtil.W(Mathf.Max(160f, contentW - GuiUtil.S(200f))));
            if (GUILayout.Button("Browse...", GuiUtil.W(80))) PickFolder("Choose the folder of voice files", t => _lsInFolder = t);
            GUILayout.EndHorizontal();

            _lsSameFolder = GUILayout.Toggle(_lsSameFolder, " Write the .anm files next to the audio (same folder as input)");

            GUILayout.BeginHorizontal();
            GUILayout.Label("Output folder", GuiUtil.W(90));
            GUI.enabled = !_lsSameFolder;
            _lsOutFolder = GUILayout.TextField(_lsOutFolder ?? "", GuiUtil.W(Mathf.Max(160f, contentW - GuiUtil.S(200f))));
            if (GUILayout.Button("Browse...", GuiUtil.W(80))) PickFolder("Choose the output folder", t => _lsOutFolder = t);
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            _lsOverwrite = GUILayout.Toggle(_lsOverwrite, " Overwrite existing .anm files");
            _lsOpenResult = GUILayout.Toggle(_lsOpenResult, " Open the result of a single generation");

            int pending = 0;
            string inputFolder = ResolveInputFolder();
            if (!string.IsNullOrEmpty(inputFolder) && Directory.Exists(inputFolder)) pending = CountAudio(inputFolder);

            GUILayout.BeginHorizontal();
            GUI.enabled = _job == null && pending > 0 && TemplatePath() != null;
            if (GUILayout.Button("Generate " + pending + " file(s)", GuiUtil.W(180))) StartBatch();
            GUI.enabled = true;
            if (_job != null && GUILayout.Button("Cancel", GuiUtil.W(80)))
            {
                _jobSummary = "Cancelled after " + _job.Index + " file(s).";
                _job = null;
            }
            GUILayout.EndHorizontal();

            if (_job != null)
            {
                GuiUtil.Space(4);
                GUILayout.Label("Generating: " + Mathf.Min(_job.Index, _job.Total) + " / " + _job.Total
                    + "   ok " + _job.Ok + ", failed " + _job.Failed + ", skipped " + _job.Skipped, GuiUtil.Note);
                if (_job.LastError != null) GUILayout.Label(_job.LastError, GuiUtil.Warning);
            }
            else if (!string.IsNullOrEmpty(_jobSummary))
            {
                GUILayout.Label(_jobSummary, GuiUtil.Note);
            }

            GUILayout.EndVertical();
            GUILayout.EndScrollView();
        }

        void DrawLipsyncSettings()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Sensitivity", GuiUtil.W(70));
            _lsSensitivity = GUILayout.HorizontalSlider(_lsSensitivity, 0f, 1f, GuiUtil.W(150));
            GUILayout.Label(GuiUtil.Fmt(_lsSensitivity), GuiUtil.W(36));
            GUILayout.Space(GuiUtil.S(10f));
            GuiUtil.LabeledFloat("Ignore pauses shorter than (s)", "anm.ls.pause", ref _lsMinPause, 180);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Shortest viseme", GuiUtil.W(100));
            _lsMinKeyMs = GUILayout.HorizontalSlider(_lsMinKeyMs, 60f, 300f, GuiUtil.W(150));
            GUILayout.Label(Mathf.RoundToInt(_lsMinKeyMs) + " ms", GuiUtil.W(52));
            GUILayout.Space(GuiUtil.S(10f));
            GUILayout.Label("Smoothing", GuiUtil.W(72));
            _lsSmooth = Mathf.RoundToInt(GUILayout.HorizontalSlider(_lsSmooth, 0, 3, GuiUtil.W(90)));
            GUILayout.Label(_lsSmooth.ToString(), GuiUtil.W(20));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Mouth openness", GuiUtil.W(100));
            _lsOpenScale = GUILayout.HorizontalSlider(_lsOpenScale, 0f, 2f, GuiUtil.W(150));
            GUILayout.Label("x" + GuiUtil.Fmt(_lsOpenScale), GuiUtil.W(52));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Label("Shortest viseme and smoothing calm the mouth; openness scales how wide it opens. The shipped lines"
                + " change viseme about every 100 ms, hold ~30 ms, fade out over ~130 ms and overlap the next key.", GuiUtil.Note);
        }

        string ResolveInputFolder()
        {
            if (!string.IsNullOrEmpty(_lsInFolder) && Directory.Exists(_lsInFolder)) return _lsInFolder;
            if (!string.IsNullOrEmpty(_lsInFolder)) return null;
            return CurrentDirectory();
        }

        static int CountAudio(string folder)
        {
            int n = 0;
            foreach (string f in Directory.GetFiles(folder)) if (AudioLoader.IsSupported(f)) n++;
            return n;
        }

        void GenerateSingle()
        {
            string template = TemplatePath();
            if (template == null) { SetStatus("Choose a template animation with a phoneme track first.", true); return; }
            if (string.IsNullOrEmpty(_lsAudio) || !File.Exists(_lsAudio)) { SetStatus("Choose an audio file first.", true); return; }
            string outPath = string.IsNullOrEmpty(_lsOut)
                ? Path.Combine(Path.GetDirectoryName(_lsAudio) ?? "", Path.GetFileNameWithoutExtension(_lsAudio) + ".anm")
                : _lsOut;

            AudioLoader.Load(_lsAudio, (audio, err) =>
            {
                if (audio == null) { SetStatus("Could not read audio: " + err, true); return; }
                string genErr;
                if (!GenerateLipsync(ActiveModel(), template, audio, outPath, _lsSensitivity, _lsMinPause, _lsMinKeyMs, _lsSmooth, _lsOpenScale, out genErr))
                {
                    SetStatus("Generation failed: " + genErr, true);
                    return;
                }
                SetStatus("Wrote " + Path.GetFileName(outPath) + " (" + GuiUtil.Fmt(audio.Duration) + " s).", false);
                if (_lsOpenResult) LoadFile(outPath);
            });
        }

        void StartBatch()
        {
            string template = TemplatePath();
            if (template == null) { SetStatus("Choose a template animation with a phoneme track first.", true); return; }
            string inputFolder = ResolveInputFolder();
            if (string.IsNullOrEmpty(inputFolder) || !Directory.Exists(inputFolder)) { SetStatus("Choose an input folder.", true); return; }
            string outFolder = _lsSameFolder ? inputFolder : _lsOutFolder;
            if (string.IsNullOrEmpty(outFolder)) { SetStatus("Choose an output folder, or tick same folder as input.", true); return; }

            List<string> files = new List<string>();
            string[] all = Directory.GetFiles(inputFolder);
            Array.Sort(all, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < all.Length; i++) if (AudioLoader.IsSupported(all[i])) files.Add(all[i]);
            if (files.Count == 0) { SetStatus("No WAV, MP3 or OGG files in " + inputFolder + ".", true); return; }

            BatchJob j = new BatchJob();
            j.Files = files.ToArray();
            j.OutFolder = outFolder;
            j.Template = template;
            j.Overwrite = _lsOverwrite;
            j.Sensitivity = _lsSensitivity;
            j.MinPause = _lsMinPause;
            j.MinKeyMs = _lsMinKeyMs;
            j.Smoothing = _lsSmooth;
            j.OpenScale = _lsOpenScale;
            j.Model = ActiveModel();
            _job = j;
            _jobSummary = "";
            SetStatus("Generating " + files.Count + " lipsync file(s) into " + outFolder + ".", false);
        }

        void StepBatch()
        {
            BatchJob j = _job;
            if (j == null) return;
            if (j.Index >= j.Files.Length)
            {
                _jobSummary = "Done: " + j.Ok + " generated, " + j.Failed + " failed, " + j.Skipped + " skipped.";
                SetStatus(_jobSummary, j.Failed > 0);
                _job = null;
                return;
            }

            string input = j.Files[j.Index];
            j.Index++;
            string outPath = Path.Combine(j.OutFolder, Path.GetFileNameWithoutExtension(input) + ".anm");
            try { Directory.CreateDirectory(j.OutFolder); }
            catch (Exception e) { j.Failed++; j.LastError = "Cannot create " + j.OutFolder + ": " + e.Message; return; }

            if (!j.Overwrite && File.Exists(outPath)) { j.Skipped++; return; }

            j.Waiting = true;
            AudioLoader.Load(input, (audio, err) =>
            {
                j.Waiting = false;
                if (audio == null) { j.Failed++; j.LastError = Path.GetFileName(input) + ": " + err; return; }
                string genErr;
                if (!GenerateLipsync(j.Model, j.Template, audio, outPath, j.Sensitivity, j.MinPause, j.MinKeyMs, j.Smoothing, j.OpenScale, out genErr))
                {
                    j.Failed++;
                    j.LastError = Path.GetFileName(input) + ": " + genErr;
                    return;
                }
                j.Ok++;
                j.LastError = null;
            });
        }

        /// <summary>Builds one lipsync file from audio, using the template's file layout.</summary>
        static bool GenerateLipsync(VisemeModel model, string templatePath, AudioData audio, string outPath, float sensitivity, float minPause, float minKeyMs, int smoothing, float openScale, out string error)
        {
            error = null;
            try
            {
                AnmDocument tmpl = AnmDocument.Load(templatePath);
                AnmTrack track = tmpl.FirstPhonemeTrack();
                if (track == null) { error = "the template has no phoneme track"; return false; }
                List<LipKey> keys = SpeechAnalyzer.Analyze(audio, sensitivity, minPause, model, minKeyMs, smoothing, openScale);
                if (keys.Count == 0) { error = "no speech found in the audio"; return false; }
                if (!tmpl.ReplacePhonemeKeys(track, keys, audio.Duration)) { error = "could not write the keyframes"; return false; }
                string dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                tmpl.Save(outPath);
                return true;
            }
            catch (Exception e) { error = e.Message; return false; }
        }

        void StartCalibration()
        {
            if (string.IsNullOrEmpty(_lsAnmFolder) || !Directory.Exists(_lsAnmFolder))
            {
                SetStatus("Choose the folder holding the shipped lipsync .anm files.", true);
                return;
            }
            string audioFolder = ResolveInputFolder();
            if (string.IsNullOrEmpty(audioFolder) || !Directory.Exists(audioFolder))
            {
                SetStatus("Choose the audio folder (the one holding the matching WAV/OGG/MP3 files).", true);
                return;
            }
            List<string> files = new List<string>();
            string[] all = Directory.GetFiles(_lsAnmFolder, "*.anm");
            Array.Sort(all, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < all.Length; i++) files.Add(all[i]);
            if (files.Count == 0) { SetStatus("No .anm files in " + _lsAnmFolder + ".", true); return; }

            CalibJob c = new CalibJob();
            c.Files = files.ToArray();
            c.AudioFolder = audioFolder;
            c.Model = new VisemeModel();
            _calib = c;
            _calibSummary = "";
            SetStatus("Calibrating from " + files.Count + " lipsync file(s).", false);
        }

        void StepCalibration()
        {
            CalibJob c = _calib;
            if (c == null) return;
            if (c.Index >= c.Files.Length)
            {
                if (c.Model.Finish())
                {
                    _model = c.Model;
                    SaveModel();
                    SetModelMode(2);
                    _calibSummary = "Calibrated on " + c.Keys + " keys from " + c.FilesUsed + " file(s); saved to " + _modelPath + ".";
                    SetStatus(_calibSummary, false);
                }
                else
                {
                    _calibSummary = "Calibration found no usable keys.";
                    SetStatus(_calibSummary, true);
                }
                _calib = null;
                return;
            }

            string anmPath = c.Files[c.Index++];
            string stem = Path.GetFileNameWithoutExtension(anmPath);
            string audioPath = null;
            for (int i = 0; i < AudioLoader.Extensions.Length && audioPath == null; i++)
            {
                string candidate = Path.Combine(c.AudioFolder, stem + AudioLoader.Extensions[i]);
                if (File.Exists(candidate)) audioPath = candidate;
            }
            if (audioPath == null) return;

            c.Waiting = true;
            AudioLoader.Load(audioPath, (audio, err) =>
            {
                c.Waiting = false;
                if (audio == null) { c.LastError = err; return; }
                int keys = AddCalibrationKey(c.Model, anmPath, audio);
                if (keys > 0) { c.FilesUsed++; c.Keys += keys; }
            });
        }

        /// <summary>Adds the game's own (features, viseme) pairs from one lipsync file.</summary>
        static int AddCalibrationKey(VisemeModel model, string anmPath, AudioData audio)
        {
            try
            {
                AnmDocument d = AnmDocument.Load(anmPath);
                AnmTrack t = d.FirstPhonemeTrack();
                if (t == null || t.Samples.Count == 0) return 0;
                SpeechAnalyzer.Frame[] fr = SpeechAnalyzer.Frames(audio);
                if (fr.Length == 0) return 0;

                int hop = Math.Max(1, audio.SampleRate * 10 / 1000);
                int used = 0;
                for (int i = 0; i < t.Samples.Count; i++)
                {
                    AnmSample s = t.Samples[i];
                    ulong ph; float fi, ho, fo, co;
                    if (!AnmDocument.TryGetPhoneme(s.Value, out ph, out fi, out ho, out fo, out co)) continue;
                    string viseme = SpeechAnalyzer.VisemeName(ph);
                    if (viseme == null) continue;
                    int a = (int)((s.Time + fi) * 1000f / 10f);
                    int b = (int)((s.Time + fi + Math.Max(0.01f, ho)) * 1000f / 10f);
                    float[] vec = SpeechAnalyzer.SlotFeatures(fr, a, b);
                    if (vec == null) continue;
                    model.Add(viseme, vec);
                    used++;
                }
                return used;
            }
            catch { return 0; }
        }

        // ================================================================ info tab
        void DrawInfoTab()
        {
            _infoScroll = GUILayout.BeginScrollView(_infoScroll);
            GUILayout.Label("Animation", GuiUtil.SubHeader);
            GUILayout.Label("Name hash: " + Lookup(_doc.RootName) + "   (0x" + _doc.RootName.ToString("X16") + ")", GuiUtil.Note);
            GUILayout.Label("Version: " + _doc.Version + "   |   " + _doc.File.Types.Count + " class type(s) in the header", GuiUtil.Note);

            float len = _doc.Length;
            GUILayout.BeginHorizontal();
            if (GuiUtil.LabeledFloat("Length (s)", "anm.len", ref len, 90)) { _doc.SetLength(len); MarkDirty("Length changed."); }
            if (GUILayout.Button("Use last keyframe", GuiUtil.W(130)))
            {
                float e = 0f;
                for (int i = 0; i < _doc.Tracks.Count; i++) if (_doc.Tracks[i].EndTime > e) e = _doc.Tracks[i].EndTime;
                _doc.SetLength(e);
                MarkDirty("Length set to " + GuiUtil.Fmt(e) + " s.");
                GuiUtil.ResetBuffers();
            }
            GUILayout.EndHorizontal();

            if (IsCompressed)
            {
                GuiUtil.Space(6);
                GUI.color = new Color(1f, 0.55f, 0.55f);
                GUILayout.Label("THIS FILE IS COMPRESSED AND IS NOT SUPPORTED YET", GuiUtil.Header);
                GUI.color = Color.white;
            }

            GuiUtil.Space(8);
            GUILayout.Label("All fields", GuiUtil.SubHeader);
            _tree.ReadOnly = IsCompressed;
            if (_tree.Draw("Animation", _doc.Root, 0)) MarkDirty("Field changed.");
            GUILayout.EndScrollView();
        }
    }
}
