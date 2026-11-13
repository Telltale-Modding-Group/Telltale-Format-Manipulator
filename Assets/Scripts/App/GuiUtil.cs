using System.Collections.Generic;
using System.Globalization;
using TFM.Core;
using UnityEngine;

namespace TFM.App
{
    /// <summary>
    /// Runtime IMGUI helpers. Runtime GUI has no FloatField, so FloatField keeps a per-control
    /// text buffer: partial input ("-", "1.", "") stays visible while typing, the value is
    /// committed whenever the text parses, and invalid text reverts once focus leaves.
    /// </summary>
    public static class GuiUtil
    {
        static readonly Dictionary<string, string> Buffers = new Dictionary<string, string>();
        static string _focusedLastFrame;

        static GUIStyle _wrap, _header, _subHeader, _error, _warning, _selectedRow, _row, _mono, _note, _statusOk, _statusErr, _tag;

        public static GUIStyle Wrap { get { Init(); return _wrap; } }
        public static GUIStyle Header { get { Init(); return _header; } }
        public static GUIStyle SubHeader { get { Init(); return _subHeader; } }
        public static GUIStyle Error { get { Init(); return _error; } }
        public static GUIStyle Warning { get { Init(); return _warning; } }
        public static GUIStyle SelectedRow { get { Init(); return _selectedRow; } }
        public static GUIStyle Row { get { Init(); return _row; } }
        public static GUIStyle Mono { get { Init(); return _mono; } }
        public static GUIStyle Note { get { Init(); return _note; } }
        public static GUIStyle StatusOk { get { Init(); return _statusOk; } }
        public static GUIStyle StatusError { get { Init(); return _statusErr; } }
        public static GUIStyle ExperimentalTag { get { Init(); return _tag; } }

        /// <summary>Row height for virtualized lists, in screen pixels.</summary>
        public static float RowHeight { get { return S(20f); } }

        // ---- UI scaling -------------------------------------------------------
        // Scaling is applied through GUIStyle.fontSize and layout dimensions, never through
        // GUI.matrix: a matrix scale resamples the already-rasterised glyph atlas afterwards,
        // which is what makes scaled IMGUI text look blurry. Sizes here are in unscaled units
        // and multiplied by Scale at the point of use.
        static float _scale = 1f;
        static float _baseFontSize = -1f;

        /// <summary>Current UI scale factor (1 = 100%).</summary>
        public static float Scale { get { return _scale; } }

        /// <summary>Scales an unscaled dimension to screen pixels.</summary>
        public static float S(float value) { return value * _scale; }

        /// <summary>Shorthand for a scaled width option.</summary>
        public static GUILayoutOption W(float value) { return GUILayout.Width(S(value)); }

        /// <summary>Shorthand for a scaled height option.</summary>
        public static GUILayoutOption H(float value) { return GUILayout.Height(S(value)); }

        /// <summary>Shorthand for a scaled minimum width option.</summary>
        public static GUILayoutOption MinW(float value) { return GUILayout.MinWidth(S(value)); }

        /// <summary>Shorthand for a scaled maximum width option.</summary>
        public static GUILayoutOption MaxW(float value) { return GUILayout.MaxWidth(S(value)); }

        /// <summary>Shorthand for a scaled space.</summary>
        public static void Space(float value) { GUILayout.Space(S(value)); }

        /// <summary>
        /// Sets the scale factor and re-derives every cached style's font size. Called from
        /// OnGUI (never from Awake) because GUIStyle construction needs the GUI skin.
        /// </summary>
        public static void SetScale(float scale)
        {
            Init();
            if (Mathf.Approximately(scale, _scale) && _stylesScaled) return;
            _scale = scale;
            ApplyFontSize(GUI.skin.label, StyleScaleCap.Label);
            ApplyStyle();
        }

        static bool _stylesScaled;

        // Per-style cap on how much the font may grow, so headers do not dwarf body text at 175%.
        static class StyleScaleCap
        {
            public const float Label = 1f;
            public const float Header = 0.75f;
        }

        static int ScaledFont(float capFactor)
        {
            if (_baseFontSize <= 0f) _baseFontSize = 13f;
            int size = Mathf.RoundToInt(_baseFontSize * (1f + (_scale - 1f) * capFactor));
            return Mathf.Max(9, size);
        }

        static void ApplyFontSize(GUIStyle style, float capFactor)
        {
            if (style == null) return;
            int size = ScaledFont(capFactor);
            style.fontSize = size;
        }

        static void ApplyStyle()
        {
            if (_header == null) return;
            _stylesScaled = true;
            ApplyFontSize(_wrap, StyleScaleCap.Label);
            ApplyFontSize(_header, StyleScaleCap.Header);
            ApplyFontSize(_subHeader, StyleScaleCap.Label);
            ApplyFontSize(_error, StyleScaleCap.Label);
            ApplyFontSize(_warning, StyleScaleCap.Label);
            ApplyFontSize(_note, StyleScaleCap.Header * 0.9f);
            ApplyFontSize(_row, StyleScaleCap.Label);
            ApplyFontSize(_selectedRow, StyleScaleCap.Label);
            ApplyFontSize(_mono, StyleScaleCap.Label);
            ApplyFontSize(_statusOk, StyleScaleCap.Label);
            ApplyFontSize(_statusErr, StyleScaleCap.Label);
        }

        static Texture2D _selTex, _hoverTex;

        static Texture2D SolidTex(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        static void Init()
        {
            if (_header != null) return;
            _wrap = new GUIStyle(GUI.skin.label) { wordWrap = true };
            _header = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            _subHeader = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            _error = new GUIStyle(GUI.skin.label) { wordWrap = true };
            _error.normal.textColor = new Color(1f, 0.45f, 0.4f);
            _warning = new GUIStyle(GUI.skin.label) { wordWrap = true };
            _warning.normal.textColor = new Color(1f, 0.8f, 0.3f);
            _note = new GUIStyle(GUI.skin.label) { wordWrap = true, fontStyle = FontStyle.Italic };
            _note.normal.textColor = new Color(0.7f, 0.7f, 0.7f);

            _selTex = SolidTex(new Color(0.24f, 0.42f, 0.7f, 1f));
            _hoverTex = SolidTex(new Color(1f, 1f, 1f, 0.08f));

            _row = new GUIStyle(GUI.skin.label) { padding = new RectOffset(4, 4, 2, 2), margin = new RectOffset(0, 0, 0, 0), clipping = TextClipping.Clip };
            _row.hover.background = _hoverTex;
            _row.hover.textColor = Color.white;
            _selectedRow = new GUIStyle(_row);
            _selectedRow.normal.background = _selTex;
            _selectedRow.hover.background = _selTex;
            _selectedRow.normal.textColor = Color.white;

            _mono = new GUIStyle(GUI.skin.textArea) { wordWrap = false };
            Font f = TryMonoFont();
            if (f != null) _mono.font = f;

            _statusOk = new GUIStyle(GUI.skin.label) { clipping = TextClipping.Clip };
            _statusOk.normal.textColor = new Color(0.75f, 0.9f, 0.75f);
            _statusErr = new GUIStyle(_statusOk);
            _statusErr.normal.textColor = new Color(1f, 0.45f, 0.4f);

            _tag = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Italic, alignment = TextAnchor.MiddleLeft };
            _tag.normal.textColor = new Color(1f, 0.7f, 0.35f);

            // The default skin's window texture is semi-transparent; popups use a solid one.
            _windowTex = SolidTex(new Color(0.19f, 0.19f, 0.21f, 1f));
            _backdropTex = SolidTex(new Color(0f, 0f, 0f, 0.55f));
            _window = new GUIStyle(GUI.skin.window);
            _window.normal.background = _windowTex;
            _window.onNormal.background = _windowTex;
            _window.focused.background = _windowTex;
            _window.onFocused.background = _windowTex;
            _window.normal.textColor = Color.white;
            _window.onNormal.textColor = Color.white;
            _window.fontStyle = FontStyle.Bold;
        }

        static Texture2D _windowTex, _backdropTex;
        static GUIStyle _window;

        /// <summary>Fully opaque window style for modal popups.</summary>
        public static GUIStyle OpaqueWindow { get { Init(); return _window; } }

        /// <summary>Dims everything behind a modal popup. Call right before GUI.ModalWindow.</summary>
        public static void DrawModalBackdrop(Rect screen)
        {
            Init();
            if (Event.current.type == EventType.Repaint) GUI.DrawTexture(screen, _backdropTex);
        }

        static Font TryMonoFont()
        {
            try { return Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Courier New", "Lucida Console" }, 13); }
            catch { return null; }
        }

        /// <summary>Call once per frame at the start of OnGUI (Repaint/Layout) to drop stale buffers.</summary>
        public static void BeginFrame()
        {
            if (Event.current.type != EventType.Repaint) return;
            string focused = GUI.GetNameOfFocusedControl();
            if (_focusedLastFrame != null && _focusedLastFrame != focused) Buffers.Remove(_focusedLastFrame);
            _focusedLastFrame = string.IsNullOrEmpty(focused) ? null : focused;
        }

        /// <summary>Forget all text buffers, e.g. after a reload or selection change.</summary>
        public static void ResetBuffers()
        {
            Buffers.Clear();
            _focusedLastFrame = null;
        }

        public static string Fmt(float v)
        {
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }

        public static bool TryParse(string s, out float v)
        {
            return float.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                   && !float.IsNaN(v) && !float.IsInfinity(v);
        }

        /// <summary>Editable float. Returns true when the value changed. id must be unique per control.</summary>
        public static bool FloatField(string id, ref float value, params GUILayoutOption[] options)
        {
            string buf;
            bool focused = GUI.GetNameOfFocusedControl() == id;
            if (!focused || !Buffers.TryGetValue(id, out buf)) buf = Fmt(value);

            GUI.SetNextControlName(id);
            string edited = GUILayout.TextField(buf, options);
            if (edited == buf) return false;

            Buffers[id] = edited;
            float parsed;
            if (TryParse(edited, out parsed) && parsed != value)
            {
                value = parsed;
                return true;
            }
            return false;
        }

        public static bool LabeledFloat(string label, string id, ref float value, float labelWidth = 120f)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(S(labelWidth)));
            bool changed = FloatField(id, ref value, MinW(60));
            GUILayout.EndHorizontal();
            return changed;
        }

        public static bool Vec3Field(string label, string id, ref Vec3 v, float labelWidth = 150f)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(S(labelWidth)));
            bool c = false;
            GUILayout.Label("X", W(12)); c |= FloatField(id + ".x", ref v.X, MinW(50));
            GUILayout.Label("Y", W(12)); c |= FloatField(id + ".y", ref v.Y, MinW(50));
            GUILayout.Label("Z", W(12)); c |= FloatField(id + ".z", ref v.Z, MinW(50));
            GUILayout.EndHorizontal();
            return c;
        }

        public static bool QuatField(string label, string id, ref Quat q, float labelWidth = 150f)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(S(labelWidth)));
            bool c = false;
            GUILayout.Label("X", W(12)); c |= FloatField(id + ".x", ref q.X, MinW(45));
            GUILayout.Label("Y", W(12)); c |= FloatField(id + ".y", ref q.Y, MinW(45));
            GUILayout.Label("Z", W(12)); c |= FloatField(id + ".z", ref q.Z, MinW(45));
            GUILayout.Label("W", W(14)); c |= FloatField(id + ".w", ref q.W, MinW(45));
            GUILayout.EndHorizontal();
            return c;
        }

        /// <summary>Quaternion editor plus an Euler-degrees helper row and a Normalize button.</summary>
        public static bool RotationField(string label, string id, ref Quat q, float labelWidth = 150f)
        {
            bool changed = QuatField(label, id, ref q, labelWidth);

            Vec3 e = q.ToEulerDegrees();
            Vec3 before = e;
            GUILayout.BeginHorizontal();
            GUILayout.Label("   Euler (degrees)", GUILayout.Width(S(labelWidth)));
            bool ec = false;
            GUILayout.Label("X", W(12)); ec |= FloatField(id + ".ex", ref e.X, MinW(45));
            GUILayout.Label("Y", W(12)); ec |= FloatField(id + ".ey", ref e.Y, MinW(45));
            GUILayout.Label("Z", W(12)); ec |= FloatField(id + ".ez", ref e.Z, MinW(45));
            float len = Mathf.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
            if (GUILayout.Button("Normalize", W(80)) && len > 1e-8f && Mathf.Abs(len - 1f) > 1e-7f)
            {
                q = q.Normalized();
                changed = true;
            }
            GUILayout.EndHorizontal();

            if (ec && (e.X != before.X || e.Y != before.Y || e.Z != before.Z))
            {
                q = Quat.FromEulerDegrees(e);
                changed = true;
            }
            if (Mathf.Abs(len - 1f) > 0.001f)
                GUILayout.Label("   Not a unit quaternion (length " + Fmt(len) + ").", Warning);
            return changed;
        }

        /// <summary>
        /// Read-only, selectable/copyable text. Edits made by the user are discarded each frame.
        /// </summary>
        public static void ReadOnlyText(string text, params GUILayoutOption[] options)
        {
            GUILayout.TextArea(text ?? "", Mono, options);
        }

        /// <summary>
        /// Virtualized list: scroll view of `count` fixed-height rows inside a box `height` pixels
        /// tall, calling drawRow(index, rect) only for rows inside the visible window.
        /// </summary>
        public static Vector2 VirtualList(Vector2 scroll, float height, int count, float rowHeight,
                                          System.Action<int, Rect> drawRow, bool reserveScrollbar = true)
        {
            scroll = GUILayout.BeginScrollView(scroll, false, reserveScrollbar,
                GUILayout.Height(height), GUILayout.ExpandWidth(true));

            float contentH = count * rowHeight;
            Rect content = GUILayoutUtility.GetRect(0f, contentH, GUILayout.ExpandWidth(true));

            int first = Mathf.Max(0, Mathf.FloorToInt(scroll.y / rowHeight) - 1);
            int last = Mathf.Min(count - 1, Mathf.CeilToInt((scroll.y + height) / rowHeight) + 1);
            for (int i = first; i <= last; i++)
                drawRow(i, new Rect(content.x, content.y + i * rowHeight, content.width, rowHeight));

            GUILayout.EndScrollView();
            return scroll;
        }

        /// <summary>Scrolls a VirtualList so that row `index` is visible.</summary>
        public static void ScrollTo(ref Vector2 scroll, int index, float rowHeight, float viewHeight)
        {
            float top = index * rowHeight;
            if (top < scroll.y) scroll.y = top;
            else if (top + rowHeight > scroll.y + viewHeight) scroll.y = Mathf.Max(0f, top + rowHeight - viewHeight);
        }
    }
}
