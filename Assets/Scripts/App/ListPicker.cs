using System;
using System.Collections.Generic;
using UnityEngine;

namespace TFM.App
{
    /// <summary>
    /// Modal, searchable, virtualized list picker. Handles tens of thousands of items:
    /// filtering runs only when the query changes and only visible rows are drawn.
    /// Optional "advanced" free-text section for entering a custom value.
    /// </summary>
    public class ListPicker
    {
        const int WindowId = 7101;

        public bool IsOpen { get; private set; }

        string _title;
        IReadOnlyList<string> _items;
        Func<int, string> _unavailableReason; // non-null result = item can't be chosen
        Action<int> _onPick;

        string _query = "";
        string _appliedQuery;
        readonly List<int> _filtered = new List<int>();
        int _selected = -1;
        Vector2 _scroll;
        bool _focusSearch;
        bool _scrollToSelection;
        float _windowHeight = 600f;

        // Advanced custom-entry section (optional)
        string _customLabel, _customWarning;
        Func<string, string> _customPreview;
        Action<string> _onCustom;
        bool _customOpen;
        string _customText = "";

        public void Open(string title, IReadOnlyList<string> items, int preselect, Func<int, string> unavailableReason, Action<int> onPick)
        {
            _title = title;
            _items = items ?? new List<string>();
            _unavailableReason = unavailableReason;
            _onPick = onPick;
            _query = "";
            _appliedQuery = null;
            _selected = preselect;
            _scroll = Vector2.zero;
            _focusSearch = true;
            _scrollToSelection = preselect >= 0;
            _onCustom = null;
            _customPreview = null;
            _customOpen = false;
            _customText = "";
            IsOpen = true;
        }

        /// <summary>Enables the collapsed custom-entry section. Call right after Open.</summary>
        public void EnableCustomEntry(string label, string warning, Func<string, string> preview, Action<string> onCustom)
        {
            _customLabel = label;
            _customWarning = warning;
            _customPreview = preview;
            _onCustom = onCustom;
        }

        public void Close()
        {
            IsOpen = false;
            _onPick = null;
            _onCustom = null;
        }

        void Refilter()
        {
            if (_appliedQuery == _query) return;
            _appliedQuery = _query;
            _filtered.Clear();
            string q = _query.Trim();
            for (int i = 0; i < _items.Count; i++)
            {
                if (q.Length == 0 || _items[i].IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    _filtered.Add(i);
            }
            if (q.Length > 0) _scroll = Vector2.zero;
        }

        public void OnGUI(Rect screen)
        {
            if (!IsOpen) return;
            float w = Mathf.Min(GuiUtil.S(620f), screen.width - 40f);
            float h = Mathf.Min(GuiUtil.S(640f), screen.height - 40f);
            var rect = new Rect(screen.x + (screen.width - w) * 0.5f, screen.y + (screen.height - h) * 0.5f, w, h);
            _windowHeight = h;
            GuiUtil.DrawModalBackdrop(screen);
            GUI.ModalWindow(WindowId, rect, DrawWindow, _title, GuiUtil.OpaqueWindow);
        }

        void DrawWindow(int id)
        {
            Event e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { Close(); e.Use(); return; }

            Refilter();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Search", GuiUtil.W(50));
            GUI.SetNextControlName("picker.search");
            _query = GUILayout.TextField(_query);
            if (GUILayout.Button("Clear", GuiUtil.W(50))) _query = "";
            GUILayout.EndHorizontal();
            if (_focusSearch && e.type == EventType.Repaint)
            {
                GUI.FocusControl("picker.search");
                _focusSearch = false;
            }

            GUILayout.Label(_filtered.Count + " of " + _items.Count + " shown", GuiUtil.Note);

            float reserved = _onCustom != null ? (_customOpen ? 190f : 34f) : 0f;
            float listH = Mathf.Max(100f, _windowHeight - 140f - reserved);
            if (_scrollToSelection && _selected >= 0)
            {
                int pos = _filtered.IndexOf(_selected);
                if (pos >= 0) GuiUtil.ScrollTo(ref _scroll, pos, GuiUtil.RowHeight, listH);
                _scrollToSelection = false;
            }

            int clickedTwice = -1;
            _scroll = GuiUtil.VirtualList(_scroll, listH, _filtered.Count, GuiUtil.RowHeight, (row, r) =>
            {
                int item = _filtered[row];
                string reason = _unavailableReason != null ? _unavailableReason(item) : null;
                string label = reason == null ? _items[item] : _items[item] + "   (" + reason + ")";
                bool sel = item == _selected;
                bool prevEnabled = GUI.enabled;
                if (reason != null) GUI.enabled = false;
                if (GUI.Button(r, label, sel ? GuiUtil.SelectedRow : GuiUtil.Row))
                {
                    if (Event.current.clickCount >= 2 || sel) clickedTwice = item;
                    _selected = item;
                }
                GUI.enabled = prevEnabled;
            });

            if (clickedTwice >= 0) { Pick(clickedTwice); return; }

            if (_onCustom != null) DrawCustomSection();

            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            string selText = _selected >= 0 && _selected < _items.Count ? _items[_selected] : "(nothing selected)";
            GUILayout.Label("Selected: " + selText);
            GUILayout.FlexibleSpace();
            bool canApply = _selected >= 0 && _selected < _items.Count
                            && (_unavailableReason == null || _unavailableReason(_selected) == null);
            GUI.enabled = canApply;
            bool apply = GUILayout.Button("Apply", GuiUtil.W(90));
            GUI.enabled = true;
            bool cancel = GUILayout.Button("Cancel", GuiUtil.W(90));
            GUILayout.EndHorizontal();
            if (apply && canApply) { Pick(_selected); return; }
            if (cancel) { Close(); return; }

            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && canApply
                && GUI.GetNameOfFocusedControl() != "picker.custom")
            {
                Pick(_selected);
                e.Use();
            }
        }

        void DrawCustomSection()
        {
            GuiUtil.Space(4);
            _customOpen = GUILayout.Toggle(_customOpen, (_customOpen ? "[-] " : "[+] ") + _customLabel, GUI.skin.button);
            if (!_customOpen) return;
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label(_customWarning, GuiUtil.Warning);
            GUI.SetNextControlName("picker.custom");
            _customText = GUILayout.TextField(_customText);
            string preview = _customPreview != null ? _customPreview(_customText) : null;
            if (!string.IsNullOrEmpty(preview)) GUILayout.Label(preview, GuiUtil.Note);
            GUI.enabled = _customText.Trim().Length > 0;
            if (GUILayout.Button("Apply custom value"))
            {
                Action<string> cb = _onCustom;
                string text = _customText.Trim();
                Close();
                if (cb != null) cb(text);
            }
            GUI.enabled = true;
            GUILayout.EndVertical();
        }

        void Pick(int item)
        {
            Action<int> cb = _onPick;
            Close();
            if (cb != null) cb(item);
        }
    }
}
