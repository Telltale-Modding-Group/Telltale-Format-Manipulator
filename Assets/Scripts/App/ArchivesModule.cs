using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TFM.Core;
using UnityEngine;

namespace TFM.App
{
    /// <summary>
    /// Archive name extractor: open a .ttarch or .ttarch2, read the list of file names it
    /// contains, and dump that list to disk. Nothing is extracted; the archive is never
    /// modified.
    /// </summary>
    /// <remarks>
    /// An archive carries no game identifier, so the title is worked out from the Blowfish key
    /// that successfully decrypts its entry table. Automatic mode tries every known key and
    /// reports which one validated; manual mode checks only the key the user picks or enters.
    /// </remarks>
    public class ArchivesModule : IFormatModule
    {
        public string Title { get { return "Archives"; } }

        AppContext _ctx;
        Rect _area;

        string _path = "";
        TtArchiveResult _result;
        string _statusText = "";
        bool _statusError;

        // Key handling
        bool _auto = true;
        TtArchiveKey _manualKey;
        bool _customKey;
        string _customKeyText = "";
        byte[] _customKeyBytes;

        Vector2 _listScroll, _infoScroll, _logScroll;
        int _selected = -1;
        int _uniqueNames;   // cached; counting it every frame would build a dictionary per repaint

        readonly ListPicker _keyPicker = new ListPicker();
        readonly List<string> _keyLabels = new List<string>();
        readonly List<int> _keyIndex = new List<int>();

        // ---- folder batch ----
        // A folder run walks every archive in a folder one at a time, dumping each. It runs on
        // the main thread across frames rather than inside one Deferred call so the UI keeps
        // drawing and the stop button stays clickable while it works.
        sealed class BatchItem
        {
            public string Path;
            public bool Ok;
            public int Named;
            public string Detail;
        }

        readonly List<string> _batchQueue = new List<string>();
        readonly List<BatchItem> _batchDone = new List<BatchItem>();
        string _batchFolder = "";
        string _batchExt = "txt";
        bool _batchRunning;
        volatile bool _batchStop;
        string _batchCurrent = "";
        Vector2 _batchScroll;
        int _batchIndex;
        int _batchTotal;
        int _batchFailed;

        // ================================================================ module
        public void Initialize(AppContext ctx)
        {
            _ctx = ctx;
            BuildKeyList();
        }

        public void Update()
        {
            StepBatch();
        }

        // The window is read-only, so there is never anything to save.
        public bool HasUnsavedChanges { get { return false; } }
        public bool SaveAll() { return true; }

        void BuildKeyList()
        {
            _keyLabels.Clear();
            _keyIndex.Clear();
            IList<TtArchiveKey> all = TtArchiveKeys.All;
            for (int i = 0; i < all.Count; i++)
            {
                _keyLabels.Add(all[i].Label);
                _keyIndex.Add(i);
            }
        }

        void SetStatus(string msg, bool error)
        {
            _statusText = msg;
            _statusError = error;
            if (_ctx != null) _ctx.SetStatus(msg, error);
        }

        public void OnGUI(Rect area)
        {
            Event e = Event.current;
            if (e.type == EventType.Layout || e.type == EventType.Repaint) GuiUtil.BeginFrame();

            _area = area;
            GUILayout.BeginArea(area);

            DrawToolbar();
            GuiUtil.Space(4);
            DrawKeySection();
            GuiUtil.Space(4);
            DrawDumpBar();
            GuiUtil.Space(4);
            DrawFolderRow();
            GuiUtil.Space(4);

            GUILayout.BeginHorizontal();
            DrawResults();
            GUILayout.EndHorizontal();

            GUILayout.EndArea();
            DrawPopups();
        }

        void DrawPopups()
        {
            Rect screen = _ctx.ScreenRect.width > 0 ? _ctx.ScreenRect : new Rect(0, 0, Screen.width, Screen.height);
            _keyPicker.OnGUI(screen);
        }

        // ================================================================ toolbar
        void DrawToolbar()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Open archive...", GuiUtil.W(120))) BrowseOpen();
            GUI.enabled = _result != null || (_path ?? "").Trim().Length > 0;
            if (GUILayout.Button("Reload", GuiUtil.W(75))) Reload();
            GUI.enabled = true;

            _path = GUILayout.TextField(_path ?? "", GuiUtil.MinW(200));
            if (GUILayout.Button("Load path", GuiUtil.W(80))) LoadPath();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (_result == null)
            {
                GUILayout.Label("No archive loaded. Open a .ttarch or .ttarch2 to list the file names inside it.",
                    GuiUtil.Note);
            }
            else if (!string.IsNullOrEmpty(_statusText))
            {
                GUILayout.Label(_statusText, _statusError ? GuiUtil.Error : GuiUtil.Note);
            }
            else
            {
                GUILayout.Label(_result.FileName, GuiUtil.Note);
            }
            GUILayout.EndHorizontal();
        }

        // ============================================================ key section
        void DrawKeySection()
        {
            GUILayout.BeginVertical(GUI.skin.box);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Key", GuiUtil.SubHeader, GuiUtil.W(34));
            int mode = _auto ? 0 : 1;
            int newMode = GUILayout.Toolbar(mode, new[] { "Automatic", "Manual" }, GuiUtil.W(190));
            if (newMode != mode)
            {
                _auto = newMode == 0;
                ApplyKeyChange();
            }
            GUILayout.FlexibleSpace();
            GUILayout.Label(_auto ? "default" : "only the chosen key is tried", GuiUtil.Note, GuiUtil.W(240));
            GUILayout.EndHorizontal();

            if (_auto)
            {
                GUILayout.Label("Tries the archive with no key first, then with each of the "
                                + TtArchiveKeys.Count + " known game keys. Whichever key opens the entry"
                                + " table is the game the archive came from; that is how the title is identified.",
                    GuiUtil.Note);
            }
            else
            {
                GUILayout.Label("Only the key below is checked. If it is wrong the archive will not open,"
                                + " even if another key would have worked.", GuiUtil.Warning);

                GUILayout.BeginHorizontal();
                GUILayout.Label("Key", GuiUtil.W(34));
                string label = _customKey
                    ? (_customKeyBytes != null
                        ? "Custom key (" + _customKeyBytes.Length + " bytes)"
                        : "Custom key (not entered yet)")
                    : (_manualKey != null ? _manualKey.Label : "(none selected)");
                if (GUILayout.Button(label, GUI.skin.button, GuiUtil.MinW(300))) OpenKeyPicker();
                if (GUILayout.Button("Clear", GuiUtil.W(60)))
                {
                    _manualKey = null;
                    _customKey = false;
                    _customKeyBytes = null;
                    ApplyKeyChange();
                }
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("Own key", GuiUtil.W(60));
                _customKeyText = GUILayout.TextField(_customKeyText ?? "", GuiUtil.MinW(300));
                if (GUILayout.Button("Use hex", GuiUtil.W(80))) UseCustomKey();
                if (GUILayout.Button("Clear", GuiUtil.W(60))) _customKeyText = "";
                GUILayout.EndHorizontal();

                GUILayout.Label("Paste the 55 key bytes as hex, e.g. 96 CA 99 9F ... Spaces, dashes and"
                                + " a leading 0x are ignored.", GuiUtil.Note);
            }

            GUILayout.EndVertical();
        }

        void OpenKeyPicker()
        {
            int preselect = -1;
            if (_manualKey != null)
            {
                IList<TtArchiveKey> all = TtArchiveKeys.All;
                for (int i = 0; i < all.Count; i++) if (all[i] == _manualKey) { preselect = i; break; }
            }
            _keyPicker.Open("Choose the game key", _keyLabels, preselect, null, index =>
            {
                if (index < 0 || index >= _keyIndex.Count) return;
                _manualKey = TtArchiveKeys.All[_keyIndex[index]];
                _customKey = false;
                _customKeyBytes = null;
                ApplyKeyChange();
            });
        }

        void UseCustomKey()
        {
            byte[] bytes = ParseHex(_customKeyText);
            if (bytes == null || bytes.Length == 0)
            {
                SetStatus("That is not valid hex. Enter the key's bytes as hex, for example 96 CA 99 9F ...", true);
                return;
            }
            if (bytes.Length % 8 != 0)
            {
                SetStatus("A Blowfish key must be a whole number of 8-byte blocks; " + bytes.Length
                          + " bytes is not.", true);
                return;
            }

            _customKeyBytes = bytes;
            _customKey = true;
            ApplyKeyChange();
        }

        static byte[] ParseHex(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            StringBuilder digits = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (isHex) { digits.Append(c); continue; }
                if (c == ' ' || c == '\t' || c == '-' || c == ',' || c == ':') continue;
                if (c == 'x' || c == 'X') continue; // from a leading 0x
                return null;
            }

            if (digits.Length == 0 || (digits.Length % 2) != 0) return null;

            byte[] result = new byte[digits.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                int high = HexDigit(digits[i * 2]);
                int low = HexDigit(digits[i * 2 + 1]);
                if (high < 0 || low < 0) return null;
                result[i] = (byte)((high << 4) | low);
            }
            return result;
        }

        static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        void ApplyKeyChange()
        {
            if (_result == null && (_path ?? "").Trim().Length == 0) return;
            Reload();
        }

        // ============================================================== dump bar
        void DrawDumpBar()
        {
            bool named = _result != null && _result.Ok && _uniqueNames > 0;

            GUILayout.BeginHorizontal();
            GUI.enabled = named;
            if (GUILayout.Button("Dump Txt", GuiUtil.W(100))) Dump("txt");
            if (GUILayout.Button("Dump Binary", GuiUtil.W(110))) Dump("bin");
            GUI.enabled = true;

            if (_result == null || !_result.Ok)
            {
                GUILayout.Label("Load an archive to enable the dumps.", GuiUtil.Note);
            }
            else if (named)
            {
                GUILayout.Label("Writes <archive>_names.txt or <archive>_names.bin next to the archive: "
                                + _uniqueNames + " unique names. Both hold the same names; the binary"
                                + " file is a hash-and-name table meant to be read back by this tool.",
                    GuiUtil.Note);
            }
            else
            {
                GUILayout.Label("There are no names to dump.", GuiUtil.Note);
            }
            GUILayout.EndHorizontal();
        }

        // ============================================================= folder row
        /// <summary>
        /// Batch mode: dump every archive in a folder. The quiet button starts the run with
        /// the format shown in the toolbar toggle, so batch and single dumps agree.
        /// </summary>
        void DrawFolderRow()
        {
            GUILayout.BeginVertical(GUI.skin.box);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Folder", GuiUtil.SubHeader, GuiUtil.W(48));

            GUI.enabled = !_batchRunning;
            if (GUILayout.Button("Dump whole folder...", GuiUtil.MinW(150))) BrowseBatchFolder();

            GUILayout.Label("as", GuiUtil.W(16));
            int format = _batchExt == "bin" ? 1 : 0;
            int newFormat = GUILayout.Toolbar(format, new[] { "Txt", "Binary" }, GuiUtil.W(130));
            if (newFormat != format) _batchExt = newFormat == 0 ? "txt" : "bin";
            GUI.enabled = true;

            if (_batchRunning)
            {
                if (GUILayout.Button("STOP", GuiUtil.W(90))) StopBatch();
                GUILayout.Label(_batchIndex + " / " + _batchTotal
                                + (_batchCurrent.Length > 0 ? "   " + _batchCurrent : ""), GuiUtil.Note);
            }
            else
            {
                GUILayout.Label("Every .ttarch and .ttarch2 in the folder is read with the key mode above,"
                                + " and a name file is written next to each archive.", GuiUtil.Note);
            }
            GUILayout.EndHorizontal();

            if (_batchDone.Count > 0)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(_batchDone.Count + " processed, " + _batchFailed + " failed", GuiUtil.Note,
                    GuiUtil.W(180));
                if (!_batchRunning && GUILayout.Button("Clear report", GuiUtil.W(90)))
                {
                    _batchDone.Clear();
                    _batchFailed = 0;
                    _batchIndex = 0;
                    _batchTotal = 0;
                }
                GUILayout.EndHorizontal();

                float height = Mathf.Min(GuiUtil.S(96f), _batchDone.Count * GuiUtil.RowHeight + GuiUtil.S(4f));
                _batchScroll = GuiUtil.VirtualList(_batchScroll, height, _batchDone.Count, GuiUtil.RowHeight,
                    (row, rect) =>
                    {
                        BatchItem item = _batchDone[row];
                        string label = Path.GetFileName(item.Path) + "   " + item.Detail;
                        bool prev = GUI.enabled;
                        GUI.enabled = !_batchRunning;
                        if (GUI.Button(rect, label, item.Ok ? GuiUtil.Row : GuiUtil.SelectedRow))
                            SelectBatchItem(item);
                        GUI.enabled = prev;
                    });
            }

            GUILayout.EndVertical();
        }

        void SelectBatchItem(BatchItem item)
        {
            _path = item.Path;
            _ctx.RunDeferred(() => LoadFile(item.Path));
        }

        // =============================================================== results
        void DrawResults()
        {
            float leftW = Mathf.Max(GuiUtil.S(220f), _area.width * 0.62f);
            GUILayout.BeginVertical(GUILayout.Width(leftW));
            if (_result == null)
            {
                GUILayout.Label("Nothing loaded.", GuiUtil.Note);
                GUILayout.EndVertical();
                return;
            }

            if (!_result.Ok)
            {
                GUILayout.Label(_result.Error, GuiUtil.Error);
                if (_auto)
                {
                    GuiUtil.Space(4);
                    GUILayout.Label("If you know which game this archive belongs to, switch the key mode"
                                    + " above to Manual and pick that game, or paste its key.", GuiUtil.Note);
                }
                GuiUtil.Space(6);
                DrawLog();
                GUILayout.EndVertical();
                return;
            }

            GUILayout.Label("Files", GuiUtil.SubHeader);
            int shown = _result.Entries.Count;
            _listScroll = GuiUtil.VirtualList(_listScroll, Mathf.Max(GuiUtil.S(120f), _area.height - GuiUtil.S(120f)),
                shown, GuiUtil.RowHeight, (row, rect) =>
                {
                    TtArchiveEntry entry = _result.Entries[row];
                    string label = entry.Name.Length > 0
                        ? entry.Name + "   " + entry.Size + " bytes"
                        : "(name not recovered)   0x" + entry.Hash.ToString("X16") + "   " + entry.Size + " bytes";
                    bool sel = row == _selected;
                    if (GUI.Button(rect, label, sel ? GuiUtil.SelectedRow : GuiUtil.Row))
                        _selected = row;
                });

            GUILayout.EndVertical();

            GUILayout.Space(GuiUtil.S(6f));
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            DrawInfo();
            GUILayout.EndVertical();
        }

        void DrawInfo()
        {
            _infoScroll = GUILayout.BeginScrollView(_infoScroll, false, true,
                GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("File:        " + _result.FileName);
            sb.AppendLine("Size:        " + _result.FileLength + " bytes");
            sb.AppendLine("Format:      " + _result.VersionText);
            sb.AppendLine("Container:   " + _result.ContainerText);
            sb.AppendLine("Compression: " + _result.CompressionText);
            sb.AppendLine("Files:       " + _result.DeclaredCount + " listed, " + _result.NamedCount + " named");
            sb.AppendLine("Unique:      " + _uniqueNames + " distinct names");
            sb.AppendLine();

            if (_result.Key != null)
            {
                sb.AppendLine("Opened with key " + _result.Key.Id);
                sb.AppendLine("  game: " + TtArchiveKeys.NamesSharing(_result.Key));
            }
            else if (_result.Encrypted)
            {
                sb.AppendLine("Encrypted, opened with the selected key");
            }
            else
            {
                sb.AppendLine("Not encrypted, no key needed");
            }

            if (!_result.NamesComplete)
            {
                sb.AppendLine();
                sb.AppendLine("Incomplete: " + _result.Note);
            }

            if (_selected >= 0 && _selected < _result.Entries.Count)
            {
                TtArchiveEntry entry = _result.Entries[_selected];
                sb.AppendLine();
                sb.AppendLine("Selected entry:");
                sb.AppendLine("  name:   " + (entry.Name.Length > 0 ? entry.Name : "(not recovered)"));
                sb.AppendLine("  hash:   0x" + entry.Hash.ToString("X16"));
                sb.AppendLine("  offset: " + entry.Offset);
                sb.AppendLine("  size:   " + entry.Size);
            }

            sb.AppendLine();
            sb.AppendLine("Attempt log:");
            for (int i = 0; i < _result.Log.Count; i++) sb.AppendLine("  " + _result.Log[i]);

            GuiUtil.ReadOnlyText(sb.ToString());
            GUILayout.EndScrollView();
        }

        void DrawLog()
        {
            _logScroll = GUILayout.BeginScrollView(_logScroll, false, true,
                GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < _result.Log.Count; i++) sb.AppendLine(_result.Log[i]);
            GuiUtil.ReadOnlyText(sb.ToString());
            GUILayout.EndScrollView();
        }

        // =============================================================== file I/O
        void BrowseOpen()
        {
            if (!NativeFileDialog.IsSupported)
            {
                SetStatus("The native file dialog is only available in Windows builds. Type a path above and press [Load path].", true);
                return;
            }
            _ctx.RunDeferred(() =>
            {
                string p = NativeFileDialog.OpenFile("Open Telltale archive",
                    NativeFileDialog.ArchiveFilter, CurrentDirectory());
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

        void Reload()
        {
            string p = (_path ?? "").Trim().Trim('"');
            if (p.Length == 0) return;
            _ctx.RunDeferred(() => LoadFile(p));
        }

        void LoadFile(string path)
        {
            if (!File.Exists(path))
            {
                SetStatus("No such file: " + path, true);
                return;
            }

            TtArchiveKey key = null;
            if (!_auto)
            {
                key = _customKey
                    ? (_customKeyBytes != null ? new TtArchiveKey("custom", "custom key", _customKeyBytes) : null)
                    : _manualKey;
                if (key == null)
                {
                    SetStatus("Manual key mode is on but no key is selected. Pick one, or switch to Automatic.", true);
                    return;
                }
            }

            TtArchiveResult result = TtArchiveReader.Load(path, key, _auto);
            _path = path;
            _result = result;
            _uniqueNames = result.Ok ? result.UniqueCount : 0;
            _selected = -1;
            _listScroll = Vector2.zero;

            if (!result.Ok)
            {
                SetStatus("Could not read the archive: " + result.Error, true);
                return;
            }

            string msg = result.DeclaredCount + " files, " + _uniqueNames + " unique names";
            if (result.Key != null) msg += ", opened with " + TtArchiveKeys.NamesSharing(result.Key);
            else if (!result.Encrypted) msg += ", not encrypted";
            SetStatus(msg, false);
        }

        void Dump(string ext)
        {
            if (_result == null || !_result.Ok || _uniqueNames == 0)
            {
                SetStatus("There is nothing to dump.", true);
                return;
            }

            string source = _path;
            _ctx.RunDeferred(() =>
            {
                string suggested = Path.GetFileName(source) + "_names." + ext;
                string dir = null;
                try { dir = Path.GetDirectoryName(source); } catch { }

                string target;
                if (NativeFileDialog.IsSupported)
                {
                    target = NativeFileDialog.SaveFile("Save the name list", FilterFor(ext), dir, suggested, ext);
                    if (string.IsNullOrEmpty(target)) return; // cancelled
                }
                else
                {
                    target = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, suggested);
                }

                try
                {
                    List<NamePair> names = CollectUnique();
                    if (ext == "bin") WriteBinary(target, names);
                    else WriteText(target, names);
                    SetStatus("Wrote " + names.Count + " names to " + target, false);
                }
                catch (Exception e)
                {
                    SetStatus("Could not write " + target + ": " + e.Message, true);
                }
            });
        }

        static string FilterFor(string ext)
        {
            return ext == "bin"
                ? "Name list, binary (*.bin)\0*.bin\0All files (*.*)\0*.*\0"
                : "Name list, text (*.txt)\0*.txt\0All files (*.*)\0*.*\0";
        }

        // ============================================================ folder batch

        /// <summary>
        /// Finds the archives in a folder and queues them. Nothing is read yet: the queue is
        /// drained one archive per frame by <see cref="StepBatch"/>.
        /// </summary>
        void StartBatch(string folder, string ext)
        {
            if (!Directory.Exists(folder))
            {
                SetStatus("No such folder: " + folder, true);
                return;
            }

            TtArchiveKey key = null;
            if (!_auto)
            {
                key = CurrentManualKey();
                if (key == null)
                {
                    SetStatus("Manual key mode is on but no key is selected. Pick one, or switch to Automatic.", true);
                    return;
                }
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(folder);
            }
            catch (Exception e)
            {
                SetStatus("Could not list the folder: " + e.Message, true);
                return;
            }

            _batchQueue.Clear();
            for (int i = 0; i < files.Length; i++)
            {
                if (IsArchiveName(files[i])) _batchQueue.Add(files[i]);
            }
            _batchQueue.Sort(StringComparer.OrdinalIgnoreCase);

            if (_batchQueue.Count == 0)
            {
                SetStatus("No .ttarch or .ttarch2 files in " + folder + ".", true);
                return;
            }

            _batchFolder = folder;
            _batchExt = ext;
            _batchDone.Clear();
            _batchIndex = 0;
            _batchTotal = _batchQueue.Count;
            _batchFailed = 0;
            _batchCurrent = "";
            _batchScroll = Vector2.zero;
            _batchStop = false;
            _batchRunning = true;
            _result = null;
            _uniqueNames = 0;
            _selected = -1;

            SetStatus("Dumping " + _batchTotal + " archives to ." + ext + " ...", false);
        }

        static bool IsArchiveName(string path)
        {
            string ext = Path.GetExtension(path);
            return string.Equals(ext, ".ttarch", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".ttarch2", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Processes one archive per call so the window stays responsive.</summary>
        void StepBatch()
        {
            if (!_batchRunning) return;

            if (_batchStop)
            {
                EndBatch("Stopped");
                return;
            }

            if (_batchIndex >= _batchQueue.Count)
            {
                EndBatch(null);
                return;
            }

            string path = _batchQueue[_batchIndex];
            _batchCurrent = Path.GetFileName(path);

            BatchItem item = new BatchItem();
            item.Path = path;
            try
            {
                TtArchiveKey key = _auto ? null : CurrentManualKey();
                TtArchiveResult result = TtArchiveReader.Load(path, key, _auto);
                item.Ok = result.Ok && result.UniqueCount > 0;

                if (!result.Ok)
                {
                    item.Detail = result.Error;
                }
                else if (result.UniqueCount == 0)
                {
                    item.Detail = "no names recovered";
                }
                else
                {
                    List<NamePair> names = CollectUnique(result);
                    string target = Path.Combine(_batchFolder,
                        Path.GetFileName(path) + "_names." + _batchExt);
                    if (_batchExt == "bin") WriteBinary(target, names);
                    else WriteText(target, names);

                    item.Named = names.Count;
                    item.Detail = names.Count + " names";
                }
            }
            catch (Exception e)
            {
                item.Ok = false;
                item.Detail = e.Message;
            }

            if (!item.Ok) _batchFailed++;
            _batchDone.Add(item);

            _batchIndex++;
            _batchCurrent = "";
            // Update the counter every frame; the progress line reads this.
            _ctx.SetStatus("Dumping " + _batchIndex + " / " + _batchTotal + " archives...", false);
        }

        void EndBatch(string stopped)
        {
            _batchRunning = false;
            _batchCurrent = "";

            int written = _batchDone.Count - _batchFailed;
            string msg = stopped == null ? "Finished: " : stopped + ": ";
            msg += written + " of " + _batchDone.Count + " archives dumped";
            if (_batchFailed > 0) msg += ", " + _batchFailed + " failed";
            if (stopped == null && _batchQueue.Count - _batchDone.Count > 0)
                msg += " (" + (_batchQueue.Count - _batchDone.Count) + " not processed)";
            msg += ".";
            SetStatus(msg, _batchFailed > 0);
        }
        void StopBatch()
        {
            if (!_batchRunning) return;
            _batchStop = true;
        }

        void BrowseBatchFolder()
        {
            if (!NativeFileDialog.IsSupported)
            {
                SetStatus("The native folder dialog is only available in Windows builds.", true);
                return;
            }
            _ctx.RunDeferred(() =>
            {
                string folder = NativeFileDialog.OpenFolder("Choose a folder of archives", _batchFolder);
                if (!string.IsNullOrEmpty(folder)) StartBatch(folder, _batchExt);
            });
        }

        TtArchiveKey CurrentManualKey()
        {
            if (_customKey)
                return _customKeyBytes != null
                    ? new TtArchiveKey("custom", "custom key", _customKeyBytes)
                    : null;
            return _manualKey;
        }

        struct NamePair
        {
            public ulong Hash;
            public string Name;
        }

        /// <summary>Distinct names by hash, sorted by name so the output diffs cleanly.</summary>
        List<NamePair> CollectUnique() { return CollectUnique(_result); }

        static List<NamePair> CollectUnique(TtArchiveResult result)
        {
            Dictionary<ulong, string> byHash = new Dictionary<ulong, string>();
            for (int i = 0; i < result.Entries.Count; i++)
            {
                TtArchiveEntry entry = result.Entries[i];
                if (entry.Name.Length == 0) continue;
                if (!byHash.ContainsKey(entry.Hash)) byHash[entry.Hash] = entry.Name;
            }

            List<NamePair> list = new List<NamePair>(byHash.Count);
            foreach (KeyValuePair<ulong, string> kv in byHash)
            {
                NamePair pair;
                pair.Hash = kv.Key;
                pair.Name = kv.Value;
                list.Add(pair);
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        void WriteText(string path, List<NamePair> names)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < names.Count; i++)
            {
                sb.Append(names[i].Name);
                sb.Append("\r\n");
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        void WriteBinary(string path, List<NamePair> names)
        {
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                // Header: magic, format version, entry count. Little-endian, like every other
                // field this tool reads. Each entry is the name's hash plus its bytes.
                byte[] magic = Encoding.ASCII.GetBytes("TFMNAMES");
                w.Write(magic);
                w.Write((uint)1);
                w.Write((uint)names.Count);

                for (int i = 0; i < names.Count; i++)
                {
                    byte[] text = Encoding.ASCII.GetBytes(names[i].Name);
                    w.Write(names[i].Hash);
                    w.Write((ushort)text.Length);
                    w.Write(text);
                }
            }
        }
    }
}
