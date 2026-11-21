using System;
using System.Collections.Generic;
using TFM.Core;
using UnityEngine;

namespace TFM.App
{
    /// <summary>
    /// Picks a name for a hash: a searchable list of known file names (optionally one extension),
    /// plus a free-text entry that hashes what is typed and records it in the CustomNames folder.
    /// </summary>
    public sealed class HashPicker
    {
        readonly ListPicker _picker = new ListPicker();
        readonly AppContext _ctx;
        static readonly Dictionary<string, List<string>> ByExt = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        static int _listCount = -1;

        public HashPicker(AppContext ctx) { _ctx = ctx; }

        public bool IsOpen { get { return _picker.IsOpen; } }

        /// <summary>Known file names with this extension (".anm"); all names for null.</summary>
        public static IReadOnlyList<string> Names(AppContext ctx, string ext)
        {
            IReadOnlyList<string> all = ctx.PropNames.FileNameList;
            if (string.IsNullOrEmpty(ext)) return all;
            if (all.Count != _listCount)
            {
                ByExt.Clear();
                _listCount = all.Count;
            }
            List<string> list;
            if (ByExt.TryGetValue(ext, out list)) return list;
            list = new List<string>();
            for (int i = 0; i < all.Count; i++) if (all[i].EndsWith(ext, StringComparison.OrdinalIgnoreCase)) list.Add(all[i]);
            ByExt[ext] = list;
            return list;
        }

        /// <summary>
        /// Opens the picker. <paramref name="onSet"/> receives the chosen hash and name.
        /// </summary>
        public void Open(string title, string ext, ulong current, Action<ulong, string> onSet)
        {
            IReadOnlyList<string> names = Names(_ctx, ext);
            string cur = _ctx.PropNames.FileRef(current);
            int pre = -1;
            if (cur != null)
                for (int i = 0; i < names.Count; i++)
                    if (string.Equals(names[i], cur, StringComparison.OrdinalIgnoreCase)) { pre = i; break; }

            _picker.Open(title, names, pre, null, i => onSet(HashDatabase.Crc64(names[i]), names[i]));
            _picker.EnableCustomEntry("Type a name",
                "Only the hash of the name is stored in the file. The name itself is saved in "
                + CustomNameStore.FolderName + "/" + CustomNameStore.DefaultFileName + " so it can be shown again.",
                text =>
                {
                    string clean, why;
                    if (!CustomNameStore.Validate(text, out clean, out why)) return why;
                    ulong h = HashDatabase.Crc64(clean);
                    string known = _ctx.PropNames.FileRef(h);
                    return "hash 0x" + h.ToString("X16") + (known != null ? "  (known: " + known + ")" : "");
                },
                text =>
                {
                    string clean, why;
                    if (!CustomNameStore.Validate(text, out clean, out why)) { _ctx.SetStatus(why, true); return; }
                    ulong h = HashDatabase.Crc64(clean);
                    if (_ctx.PropNames.FileRef(h) == null && _ctx.CustomNames != null)
                    {
                        try
                        {
                            _ctx.CustomNames.Append(clean);
                            _ctx.PropNames.AddCustomFileName(h, clean);
                        }
                        catch (Exception e)
                        {
                            _ctx.SetStatus("Could not save the custom name: " + e.Message, true);
                        }
                    }
                    onSet(h, clean);
                });
        }

        public void OnGUI(Rect screen) { _picker.OnGUI(screen); }
    }

    /// <summary>Implemented by modules that can open a file handed over from another module.</summary>
    public interface IFileOpener
    {
        bool OpenFile(string path);
    }
}
