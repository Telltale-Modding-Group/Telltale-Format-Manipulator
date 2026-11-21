using System;
using System.Collections.Generic;
using System.Globalization;
using TFM.Core;
using TFM.Core.Meta;
using UnityEngine;

namespace TFM.App
{
    /// <summary>
    /// IMGUI editor for any decoded MetaStream value: foldouts for classes, lists and maps, and
    /// editable leaves (bools, numbers, strings, hashes). Used by the chore and dialogue editors
    /// for the "all fields" views, so every value in a file can be read and changed even where no
    /// dedicated editor exists.
    /// </summary>
    public sealed class MetaTreeView
    {
        /// <summary>Name for a symbol or file hash, or null.</summary>
        public Func<ulong, string> SymbolName;

        /// <summary>Called when the user asks to pick a name for a hash value (file reference or symbol).</summary>
        public Action<MetaValue> PickHash;

        public bool ReadOnly;

        readonly HashSet<MetaValue> _open = new HashSet<MetaValue>();
        readonly Dictionary<MetaValue, int> _shown = new Dictionary<MetaValue, int>();
        const int Page = 100;

        public void Expand(MetaValue v) { if (v != null) _open.Add(v); }

        public void Clear()
        {
            _open.Clear();
            _shown.Clear();
        }

        public static string Id(MetaValue v) { return "mv" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(v).ToString(CultureInfo.InvariantCulture); }

        static string Label(string field)
        {
            if (field == null) return "";
            if (field.StartsWith("Baseclass_", StringComparison.Ordinal)) return "(base) " + field.Substring(10);
            if (field.StartsWith("#", StringComparison.Ordinal)) return field.Substring(1);
            return field;
        }

        /// <summary>Draws one value. Returns true when something was edited.</summary>
        public bool Draw(string label, MetaValue v, int depth)
        {
            if (v == null) return false;
            bool composite = v.Kind == MetaKind.Struct || v.Kind == MetaKind.Array || v.Kind == MetaKind.Map;
            if (!composite) return DrawLeafRow(label, v, depth);

            GUILayout.BeginHorizontal();
            GUILayout.Space(GuiUtil.S(12f * depth));
            bool open = _open.Contains(v);
            string head = (open ? "[-] " : "[+] ") + label + "   " + Short(v);
            if (GUILayout.Button(head, GuiUtil.Row, GUILayout.ExpandWidth(true)))
            {
                if (open) _open.Remove(v); else _open.Add(v);
                open = !open;
            }
            GUILayout.EndHorizontal();
            if (!open) return false;

            bool changed = false;
            if (v.Kind == MetaKind.Struct)
            {
                if (v.Type == "PropertySet") return DrawPropertySet(v, depth + 1);
                for (int i = 0; i < v.Fields.Count; i++) changed |= Draw(Label(v.Fields[i].Name), v.Fields[i].Value, depth + 1);
                if (v.Fields.Count == 0) Note("(no fields)", depth + 1);
            }
            else
            {
                int shown;
                if (!_shown.TryGetValue(v, out shown)) shown = Page;
                int n = Math.Min(shown, v.Items.Count);
                for (int i = 0; i < n; i++)
                {
                    string l = v.Kind == MetaKind.Map ? "[" + KeyText(v.Keys[i]) + "]" : "[" + i + "]";
                    if (v.Polymorphic) l += " " + v.Items[i].Type;
                    changed |= Draw(l, v.Items[i], depth + 1);
                }
                if (v.Items.Count > n)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(GuiUtil.S(12f * (depth + 1)));
                    if (GUILayout.Button("Show " + Math.Min(Page, v.Items.Count - n) + " more of " + (v.Items.Count - n) + " remaining", GuiUtil.W(260)))
                        _shown[v] = shown + Page;
                    GUILayout.EndHorizontal();
                }
                if (v.Items.Count == 0) Note("(empty)", depth + 1);
            }
            return changed;
        }

        bool DrawPropertySet(MetaValue ps, int depth)
        {
            if (MetaProps.IsRaw(ps))
            {
                Note("This property set could not be decoded and is kept as raw bytes.", depth);
                return false;
            }
            bool changed = false;
            MetaValue parents = ps["#parents"];
            if (parents != null && parents.Items.Count > 0)
                for (int i = 0; i < parents.Items.Count; i++) changed |= DrawLeafRow("parent " + i, parents.Items[i], depth);
            List<MetaValue> groups = MetaProps.Groups(ps);
            int count = 0;
            for (int g = 0; g < groups.Count; g++)
            {
                MetaValue grp = groups[g];
                for (int i = 0; i < grp.Items.Count; i++)
                {
                    count++;
                    changed |= Draw(KeyText(grp.Keys[i]) + "  (" + grp.Type + ")", grp.Items[i], depth);
                }
            }
            if (count == 0) Note("(no properties)", depth);
            if (ps["#embedded"] != null) changed |= Draw("embedded parent", ps["#embedded"], depth);
            return changed;
        }

        string KeyText(MetaValue key)
        {
            if (key.Kind == MetaKind.Hash)
            {
                string n = SymbolName != null ? SymbolName(key.Bits) : null;
                return n ?? "0x" + key.Bits.ToString("X16");
            }
            return key.Summary();
        }

        string Short(MetaValue v)
        {
            switch (v.Kind)
            {
                case MetaKind.Array: return v.Type + "  [" + v.Items.Count + "]";
                case MetaKind.Map: return v.Type + "  {" + v.Items.Count + "}";
                default: return v.Type;
            }
        }

        void Note(string text, int depth)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(GuiUtil.S(12f * depth));
            GUILayout.Label(text, GuiUtil.Note);
            GUILayout.EndHorizontal();
        }

        bool DrawLeafRow(string label, MetaValue v, int depth)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(GuiUtil.S(12f * depth));
            GUILayout.Label(label, GuiUtil.W(200));
            bool changed = DrawLeaf(v);
            GUILayout.EndHorizontal();
            return changed;
        }

        /// <summary>Draws an editor for a leaf value inline (inside a horizontal group). Returns true when edited.</summary>
        public bool DrawLeaf(MetaValue v)
        {
            bool was = GUI.enabled;
            if (ReadOnly) GUI.enabled = false;
            bool changed = false;
            try
            {
                switch (v.Kind)
                {
                    case MetaKind.Bool:
                        {
                            bool b = GUILayout.Toggle(v.AsBool, v.AsBool ? " true" : " false", GuiUtil.W(80));
                            if (b != v.AsBool) { v.AsBool = b; changed = true; }
                            GUILayout.FlexibleSpace();
                            break;
                        }
                    case MetaKind.Int:
                        changed = IntField(v);
                        GUILayout.Label(v.Type, GuiUtil.Note, GuiUtil.W(90));
                        GUILayout.FlexibleSpace();
                        break;
                    case MetaKind.Float:
                    case MetaKind.Double:
                        {
                            float f = v.AsFloat;
                            if (GuiUtil.FloatField(Id(v), ref f, GuiUtil.W(120))) { v.AsFloat = f; changed = true; }
                            GUILayout.FlexibleSpace();
                            break;
                        }
                    case MetaKind.String:
                        {
                            string s = v.Text;
                            string e = s.Length > 70 || s.IndexOf('\n') >= 0
                                ? GUILayout.TextArea(s, GUILayout.ExpandWidth(true))
                                : GUILayout.TextField(s, GUILayout.ExpandWidth(true));
                            if (e != s) { v.Text = e; changed = true; }
                            break;
                        }
                    case MetaKind.Hash:
                        changed = HashField(v);
                        break;
                    case MetaKind.HandleBase:
                        {
                            string n = v.Bits == 0 ? "(empty)" : (SymbolName != null ? SymbolName(v.Bits) : null) ?? "0x" + v.Bits.ToString("X16");
                            GUILayout.Label(n, GuiUtil.Wrap, GUILayout.ExpandWidth(true));
                            break;
                        }
                    case MetaKind.Raw:
                        GUILayout.Label((v.Bytes != null ? v.Bytes.Length : 0) + " bytes kept as-is (not decoded" + (string.IsNullOrEmpty(v.Note) ? "" : ": " + v.Note) + ")",
                            GuiUtil.Warning, GUILayout.ExpandWidth(true));
                        break;
                }
            }
            finally
            {
                GUI.enabled = was;
            }
            return changed;
        }

        readonly Dictionary<string, string> _buf = new Dictionary<string, string>();

        bool IntField(MetaValue v)
        {
            string id = Id(v);
            string shown = v.Summary();
            string buf;
            bool focused = GUI.GetNameOfFocusedControl() == id;
            if (!focused || !_buf.TryGetValue(id, out buf)) buf = shown;
            GUI.SetNextControlName(id);
            string e = GUILayout.TextField(buf, GuiUtil.W(120));
            if (e == buf) return false;
            _buf[id] = e;
            long parsed;
            if (!long.TryParse(e.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                ulong up;
                if (!ulong.TryParse(e.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out up)) return false;
                parsed = (long)up;
            }
            int w = MetaTypes.IntWidth(v.Type);
            ulong bits = w == 8 ? (ulong)parsed : (ulong)parsed & ((1UL << (w * 8)) - 1);
            if (bits == v.Bits) return false;
            v.Bits = bits;
            return true;
        }

        bool HashField(MetaValue v)
        {
            string n = v.Bits == 0 ? "(empty)" : SymbolName != null ? SymbolName(v.Bits) : null;
            GUILayout.Label(n ?? "(unresolved)", n == null ? GuiUtil.Note : GuiUtil.Wrap, GUILayout.ExpandWidth(true));
            string id = Id(v);
            string shown = v.Bits.ToString("X16");
            string buf;
            bool focused = GUI.GetNameOfFocusedControl() == id;
            if (!focused || !_buf.TryGetValue(id, out buf)) buf = shown;
            GUI.SetNextControlName(id);
            string e = GUILayout.TextField(buf, GuiUtil.Mono, GuiUtil.W(150));
            bool changed = false;
            if (e != buf)
            {
                _buf[id] = e;
                ulong h;
                if (HashDatabase.TryParseHex(e, out h) && h != v.Bits) { v.Bits = h; changed = true; }
            }
            if (GUILayout.Button("Copy", GuiUtil.W(48))) GUIUtility.systemCopyBuffer = "0x" + v.Bits.ToString("X16");
            if (!ReadOnly && GUILayout.Button("Paste", GuiUtil.W(50)))
            {
                ulong h;
                if (HashDatabase.TryParseHex((GUIUtility.systemCopyBuffer ?? "").Trim(), out h) && h != v.Bits) { v.Bits = h; changed = true; }
            }
            if (PickHash != null && !ReadOnly && GUILayout.Button("Name...", GuiUtil.W(64))) PickHash(v);
            return changed;
        }
    }
}

namespace TFM.App
{
    /// <summary>Single-line list row styles: long names are clipped instead of wrapping onto a hidden second line.</summary>
    public static class ClipRows
    {
        static GUIStyle _row, _sel, _note;
        static float _scale = -1f;

        static void Init()
        {
            if (_row != null && _scale == GuiUtil.Scale) return;
            _scale = GuiUtil.Scale;
            _row = new GUIStyle(GuiUtil.Row) { wordWrap = false, clipping = TextClipping.Clip };
            _sel = new GUIStyle(GuiUtil.SelectedRow) { wordWrap = false, clipping = TextClipping.Clip };
            _note = new GUIStyle(GuiUtil.Note) { wordWrap = false, clipping = TextClipping.Clip };
        }

        public static GUIStyle Row { get { Init(); return _row; } }
        public static GUIStyle Selected { get { Init(); return _sel; } }
        public static GUIStyle Note { get { Init(); return _note; } }
    }
}
