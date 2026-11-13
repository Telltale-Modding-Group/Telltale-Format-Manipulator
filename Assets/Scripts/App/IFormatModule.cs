using System;
using System.Collections.Generic;
using TFM.Core;
using UnityEngine;

namespace TFM.App
{
    /// <summary>
    /// A file-type editor shown in the left sidebar. Add a new implementation and register it
    /// in AppMain.RegisterModules to support another Telltale format.
    /// </summary>
    public interface IFormatModule
    {
        string Title { get; }
        void Initialize(AppContext ctx);
        void OnGUI(Rect area);
        void Update();
        bool HasUnsavedChanges { get; }
        bool SaveAll();
    }

    /// <summary>Shared services handed to every module.</summary>
    public class AppContext
    {
        readonly Queue<Action> _deferred = new Queue<Action>();
        readonly Action<string, bool> _status;

        public AppContext(HashDatabase boneNames, Action<string, bool> status)
        {
            BoneNames = boneNames ?? HashDatabase.Empty;
            _status = status;
        }

        public HashDatabase BoneNames { get; private set; }

        /// <summary>Full window rect in GUI coordinates (after UI scaling). Use it to place popups.</summary>
        public Rect ScreenRect { get; set; }

        public void SetStatus(string message, bool error = false)
        {
            if (_status != null) _status(message, error);
        }

        /// <summary>
        /// Queues work to run on the next Update, i.e. outside OnGUI. Native dialogs and file
        /// loads must go through here: they block, and doing that inside OnGUI breaks the UI.
        /// </summary>
        public void RunDeferred(Action action)
        {
            if (action != null) _deferred.Enqueue(action);
        }

        public void PumpDeferred()
        {
            while (_deferred.Count > 0)
            {
                Action a = _deferred.Dequeue();
                try { a(); }
                catch (Exception e) { SetStatus("Error: " + e.Message, true); }
            }
        }
    }
}
