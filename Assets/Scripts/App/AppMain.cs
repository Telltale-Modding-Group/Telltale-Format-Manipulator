using System;
using System.Collections.Generic;
using System.IO;
using TFM.Core;
using UnityEngine;

namespace TFM.App
{
    /// <summary>
    /// Entry point. Wire the hash database TextAsset in the scene and add the other format
    /// modules to RegisterModules as they are written.
    /// </summary>
    public class AppMain : MonoBehaviour
    {
        [Tooltip("Assets/Data/BoneNames.HashDB.bytes - the bone name / hash lookup table.")]
        public TextAsset boneHashDatabase;

        [Tooltip("Assets/Data/PropsTypeNames.bytes - engine type names for .prop files.")]
        public TextAsset propTypeNames;

        [Tooltip("Assets/Data/PropsKeyNames.bytes - property key names for .prop files.")]
        public TextAsset propKeyNames;

        [Tooltip("Folder of archive name dumps (Dump Binary output). Read at startup to resolve "
                 + "mesh, texture and chore references in .prop files. Relative to the data folder.")]
        public string nameDumpFolder = "Resources/Names";

        [Tooltip("UI scale as a percentage. The dropdown in the top bar changes this at runtime.")]
        public int uiScalePercent = 100;

        static readonly int[] ScaleSteps = { 100, 125, 150, 175 };
        int _scaleIndex;
        Vector2 _sidebarScroll;

        readonly List<IFormatModule> _modules = new List<IFormatModule>();
        int _activeModule;

        HashDatabase _db = HashDatabase.Empty;
        AppContext _ctx;

        string _status = "";
        bool _statusError;

        bool _quitPending, _allowQuit;
        readonly ConfirmDialog _quitDialog = new ConfirmDialog();

        const string ScalePrefKey = "tfm.uiScale";
        const int WindowId = 7100;

        void Awake()
        {
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 1;

            LoadDatabase();

            _ctx = new AppContext(_db, (msg, err) =>
            {
                _status = msg;
                _statusError = err;
            });

            LoadPropNames();

            RegisterModules();

            int pref = PlayerPrefs.GetInt(ScalePrefKey, uiScalePercent);
            _scaleIndex = 0;
            for (int i = 0; i < ScaleSteps.Length; i++) if (ScaleSteps[i] == pref) _scaleIndex = i;

            Application.wantsToQuit += OnWantsToQuit;
        }

        void OnDestroy()
        {
            Application.wantsToQuit -= OnWantsToQuit;
        }

        void LoadPropNames()
        {
            PropNameLookup names = _ctx.PropNames;

            if (propTypeNames != null)
                names.TypeNames = NameTable.Load(propTypeNames.bytes, "TFMTYPES", "type names");
            if (propKeyNames != null)
                names.KeyNames = NameTable.Load(propKeyNames.bytes, "TFMKEYNM", "key names");

            // Archive name dumps are read straight off disk at startup: there are hundreds of
            // them, and they change whenever more archives are dumped.
            if (!string.IsNullOrEmpty(nameDumpFolder))
            {
                string folder = Path.Combine(Application.dataPath, nameDumpFolder);
                NameTable files = FileNameDumps.LoadFolder(folder);
                names.FileNames = files;
            }
        }

        void LoadDatabase()
        {
            if (boneHashDatabase == null)
            {
                _db = HashDatabase.Empty;
                _status = "No bone name database assigned to the scene (AppMain.boneHashDatabase). Names will show as raw hashes.";
                _statusError = true;
                return;
            }

            try
            {
                _db = HashDatabase.Load(boneHashDatabase.bytes);
                _status = "Loaded " + _db.Count + " bone names from the database.";
                _statusError = false;
            }
            catch (Exception e)
            {
                _db = HashDatabase.Empty;
                _status = "Could not read the bone name database: " + e.Message;
                _statusError = true;
            }
        }

        void RegisterModules()
        {
            // To support another format, write an IFormatModule and add it here.
            _modules.Add(new ArchivesModule());
            _modules.Add(new BonesModule());
            _modules.Add(new PropsModule());

            for (int i = 0; i < _modules.Count; i++) _modules[i].Initialize(_ctx);
        }

        void Update()
        {
            for (int i = 0; i < _modules.Count; i++) _modules[i].Update();
            _ctx.PumpDeferred();
        }

        // ------------------------------------------------------------------ GUI
        void OnGUI()
        {
            float scale = ScaleSteps[_scaleIndex] / 100f;
            GuiUtil.SetScale(scale);

            float w = Screen.width;
            float h = Screen.height;
            float topH = GuiUtil.S(32f);
            float statusH = GuiUtil.S(24f);
            float sideW = GuiUtil.S(190f);

            // Top bar
            GUI.Box(new Rect(0, 0, w, topH), GUIContent.none);
            GUILayout.BeginArea(new Rect(GuiUtil.S(8f), GuiUtil.S(6f), w - GuiUtil.S(16f), topH - GuiUtil.S(8f)));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Telltale Format Manipulator", GuiUtil.Header);
            GUILayout.FlexibleSpace();

            GUILayout.Label("UI scale", GuiUtil.W(56));
            string[] labels = { "100%", "125%", "150%", "175%" };
            int newIndex = GUILayout.Toolbar(_scaleIndex, labels, GuiUtil.W(230));
            if (newIndex != _scaleIndex)
            {
                _scaleIndex = newIndex;
                PlayerPrefs.SetInt(ScalePrefKey, ScaleSteps[_scaleIndex]);
                PlayerPrefs.Save();
            }

            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            // Sidebar
            GUI.Box(new Rect(0, topH, sideW, h - topH - statusH), GUIContent.none);
            GUILayout.BeginArea(new Rect(GuiUtil.S(6f), topH + GuiUtil.S(8f), sideW - GuiUtil.S(12f), h - topH - statusH - GuiUtil.S(16f)));
            _sidebarScroll = GUILayout.BeginScrollView(_sidebarScroll, false, false);
            GUILayout.Label("File type", GuiUtil.SubHeader);
            for (int i = 0; i < _modules.Count; i++)
            {
                bool active = i == _activeModule;
                if (GUILayout.Toggle(active, _modules[i].Title, GUI.skin.button) && !active)
                    _activeModule = i;
            }
            GuiUtil.Space(8);
            GUILayout.Label("More formats can be added here later.", GuiUtil.Note);

            if (_db.RepairedRecords > 0)
            {
                GuiUtil.Space(8);
                GUILayout.Label("Name database: " + _db.Count + " names (" + _db.RepairedRecords
                                + " recovered from a malformed record).", GuiUtil.Note);
            }
            else
            {
                GuiUtil.Space(8);
                GUILayout.Label("Name database: " + _db.Count + " names.", GuiUtil.Note);
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();

            // Main area
            float mainX = sideW, mainY = topH;
            float mainW = w - sideW, mainH = h - topH - statusH;
            _ctx.ScreenRect = new Rect(0, 0, w, h);
            try
            {
                _modules[_activeModule].OnGUI(new Rect(mainX + GuiUtil.S(8f), mainY + GuiUtil.S(6f),
                    Mathf.Max(50f, mainW - GuiUtil.S(16f)), Mathf.Max(50f, mainH - GuiUtil.S(12f))));
            }
            catch (ExitGUIException)
            {
                throw;
            }
            catch (Exception e)
            {
                // A UI exception must never take the whole app down; report it and keep going.
                _status = "UI error: " + e.Message;
                _statusError = true;
                Debug.LogException(e);
            }

            // Status bar
            GUI.Box(new Rect(0, h - statusH, w, statusH), GUIContent.none);
            GUILayout.BeginArea(new Rect(GuiUtil.S(8f), h - statusH + GuiUtil.S(3f), w - GuiUtil.S(16f), statusH - GuiUtil.S(6f)));
            GUILayout.BeginHorizontal();
            GUILayout.Label(string.IsNullOrEmpty(_status) ? "Ready." : _status,
                            _statusError ? GuiUtil.StatusError : GuiUtil.StatusOk);
            GUILayout.FlexibleSpace();
            if (_modules[_activeModule].HasUnsavedChanges)
                GUILayout.Label("unsaved changes", GuiUtil.Warning, GuiUtil.W(140));
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            var screen = new Rect(0, 0, w, h);
            _quitDialog.OnGUI(screen);
        }

        // ----------------------------------------------------------------- quit
        bool OnWantsToQuit()
        {
            if (_allowQuit) return true;
            if (_quitPending) return false;
            bool dirty = false;
            for (int i = 0; i < _modules.Count; i++) dirty |= _modules[i].HasUnsavedChanges;
            if (!dirty) return true;

            _quitPending = true;
            string msg = "There are unsaved changes. Save before closing?";
            _quitDialog.Open("Unsaved changes", msg, new[] { "Save and quit", "Quit without saving", "Cancel" }, choice =>
            {
                if (choice == 0)
                {
                    bool ok = true;
                    for (int i = 0; i < _modules.Count; i++)
                        if (_modules[i].HasUnsavedChanges) ok &= _modules[i].SaveAll();
                    if (!ok)
                    {
                        _quitPending = false;
                        return;
                    }
                    QuitNow();
                }
                else if (choice == 1)
                {
                    QuitNow();
                }
                else
                {
                    _quitPending = false;
                }
            });
            return false;
        }

        void QuitNow()
        {
            _allowQuit = true;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
