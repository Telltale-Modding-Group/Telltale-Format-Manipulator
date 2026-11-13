using System;
using UnityEngine;

namespace TFM.App
{
    /// <summary>Small modal message box with up to three buttons.</summary>
    public class ConfirmDialog
    {
        const int WindowId = 7102;

        public bool IsOpen { get; private set; }

        string _title, _message;
        string[] _buttons;
        Action<int> _onChoice;

        /// <summary>onChoice receives the index of the pressed button (Escape = last button).</summary>
        public void Open(string title, string message, string[] buttons, Action<int> onChoice)
        {
            _title = title;
            _message = message;
            _buttons = buttons != null && buttons.Length > 0 ? buttons : new[] { "OK" };
            _onChoice = onChoice;
            IsOpen = true;
        }

        public void OnGUI(Rect screen)
        {
            if (!IsOpen) return;
            float w = Mathf.Min(GuiUtil.S(460f), screen.width - 40f);
            float h = Mathf.Min(GuiUtil.S(170f), screen.height - 40f);
            var rect = new Rect(screen.x + (screen.width - w) * 0.5f, screen.y + (screen.height - h) * 0.5f, w, h);
            GuiUtil.DrawModalBackdrop(screen);
            GUI.ModalWindow(WindowId, rect, Draw, _title, GuiUtil.OpaqueWindow);
        }

        void Draw(int id)
        {
            Event e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                e.Use();
                Choose(_buttons.Length - 1);
                return;
            }
            GuiUtil.Space(6);
            GUILayout.Label(_message, GuiUtil.Wrap);
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            int chosen = -1;
            for (int i = 0; i < _buttons.Length; i++)
                if (GUILayout.Button(_buttons[i], GuiUtil.MinW(90))) chosen = i;
            GUILayout.EndHorizontal();
            if (chosen >= 0) Choose(chosen);
        }

        void Choose(int index)
        {
            IsOpen = false;
            Action<int> cb = _onChoice;
            _onChoice = null;
            if (cb != null) cb(index);
        }
    }
}
