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
            PropNames = new PropNameLookup();
            _status = status;
        }

        public HashDatabase BoneNames { get; private set; }

        /// <summary>Type, property-key and file-reference names shown by the props editor.</summary>
        public PropNameLookup PropNames { get; set; }

        /// <summary>The CustomNames folder: user-added file names, merged into PropNames.</summary>
        public CustomNameStore CustomNames { get; set; }

        /// <summary>
        /// Text of the shipped viseme calibration (Assets/Data/LipsyncModel.bytes), learned from the
        /// game's own lipsync lines. Empty when it is missing, in which case the built-in rules run.
        /// </summary>
        public string LipsyncModelText { get; set; }

        /// <summary>Re-reads the CustomNames folder into PropNames. Returns how many names it holds.</summary>
        public int ReloadCustomNames()
        {
            if (CustomNames == null) return 0;
            System.Collections.Generic.Dictionary<ulong, string> names = CustomNames.Load();
            PropNames.SetCustomFileNames(names);
            return names.Count;
        }

        /// <summary>Full window rect in GUI coordinates (after UI scaling). Use it to place popups.</summary>
        public Rect ScreenRect { get; set; }

        /// <summary>
        /// Switches to the module with this title and opens a file in it (the module must implement
        /// IFileOpener). Set by AppMain. Returns false when no such module exists or the open failed.
        /// </summary>
        public Func<string, string, bool> OpenInModule { get; set; }

        /// <summary>
        /// Text of a dialogue line by id from the language file the Dialogues module has loaded, or
        /// null. Lets the chore editor show what a LanguageRes resource says.
        /// </summary>
        public Func<uint, string> LineText { get; set; }

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
