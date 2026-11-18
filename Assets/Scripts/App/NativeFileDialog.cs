using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TFM.App
{
    /// <summary>
    /// Classic Win32 open/save dialogs (comdlg32). Available on every Windows version since XP,
    /// including Windows 7. Blocks the calling thread, so only call it via AppContext.RunDeferred.
    /// Filter format: "Description\0*.ext\0Other\0*.*\0".
    /// </summary>
    public static class NativeFileDialog
    {
        public const string SklFilter = "Telltale skeleton (*.skl)\0*.skl\0All files (*.*)\0*.*\0";
        public const string PropFilter = "Telltale property (*.prop)\0*.prop\0All files (*.*)\0*.*\0";
        public const string ArchiveFilter = "Telltale archive (*.ttarch;*.ttarch2)\0*.ttarch;*.ttarch2\0All files (*.*)\0*.*\0";
        public const string AnyFilter = "All files (*.*)\0*.*\0";
        public const string TextFilter = "Text file (*.txt)\0*.txt\0All files (*.*)\0*.*\0";

        public static bool IsSupported
        {
            get
            {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                return true;
#else
                return false;
#endif
            }
        }

        public static string OpenFile(string title, string filter, string initialDir)
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            return Show(false, title, filter, initialDir, null, null);
#else
            return null;
#endif
        }

        public static string SaveFile(string title, string filter, string initialDir, string defaultName, string defaultExt)
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            return Show(true, title, filter, initialDir, defaultName, defaultExt);
#else
            return null;
#endif
        }

        /// <summary>
        /// Picks a folder. Uses the shell's own folder browser, which unlike the comdlg32
        /// dialogs above has no equivalent on very old Windows, so it is guarded and may
        /// return null on those systems.
        /// </summary>
        public static string OpenFolder(string title, string initialDir)
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            try
            {
                if (!string.IsNullOrEmpty(initialDir) && !Directory.Exists(initialDir)) initialDir = null;
                // Modern shell folder picker: an ordinary COM object, no Vista-only APIs.
                Type dialogType = Type.GetTypeFromProgID("Shell.Application");
                if (dialogType == null) return null;
                object shell = Activator.CreateInstance(dialogType);
                try
                {
                    // Flags 0x1 = only return file-system folders, so the user cannot pick
                    // a virtual shell folder that has no path.
                    const int ReturnOnlyFileSystem = 0x1;
                    object folder = dialogType.InvokeMember("BrowseForFolder",
                        System.Reflection.BindingFlags.InvokeMethod, null, shell,
                        new object[] { 0, title ?? "Choose a folder", ReturnOnlyFileSystem, initialDir });
                    if (folder == null) return null; // cancelled

                    object self = folder.GetType().InvokeMember("Self",
                        System.Reflection.BindingFlags.GetProperty, null, folder, null);
                    string path = (string)self.GetType().InvokeMember("Path",
                        System.Reflection.BindingFlags.GetProperty, null, self, null);
                    return string.IsNullOrEmpty(path) ? null : path;
                }
                finally
                {
                    Marshal.ReleaseComObject(shell);
                }
            }
            catch
            {
                return null;
            }
#else
            return null;
#endif
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        const int OFN_OVERWRITEPROMPT = 0x00000002;
        const int OFN_HIDEREADONLY = 0x00000004;
        const int OFN_NOCHANGEDIR = 0x00000008;
        const int OFN_PATHMUSTEXIST = 0x00000800;
        const int OFN_FILEMUSTEXIST = 0x00001000;
        const int OFN_EXPLORER = 0x00080000;
        const int MaxPath = 4096;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct OpenFileName
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public IntPtr lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile;
            public int nMaxFile;
            public IntPtr lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public IntPtr lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetOpenFileNameW(ref OpenFileName ofn);

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetSaveFileNameW(ref OpenFileName ofn);

        [DllImport("user32.dll")]
        static extern IntPtr GetActiveWindow();

        static string Show(bool save, string title, string filter, string initialDir, string defaultName, string defaultExt)
        {
            if (!string.IsNullOrEmpty(initialDir) && !Directory.Exists(initialDir)) initialDir = null;

            // Buffer receives the selected path; pre-filled with the default file name.
            IntPtr buffer = Marshal.AllocHGlobal(MaxPath * 2);
            try
            {
                char[] init = new char[MaxPath];
                if (!string.IsNullOrEmpty(defaultName))
                    defaultName.CopyTo(0, init, 0, Math.Min(defaultName.Length, MaxPath - 1));
                Marshal.Copy(init, 0, buffer, MaxPath);

                var ofn = new OpenFileName();
                ofn.lStructSize = Marshal.SizeOf(typeof(OpenFileName));
                ofn.hwndOwner = GetActiveWindow();
                ofn.lpstrFilter = string.IsNullOrEmpty(filter) ? AnyFilter : filter;
                ofn.nFilterIndex = 1;
                ofn.lpstrFile = buffer;
                ofn.nMaxFile = MaxPath;
                ofn.lpstrInitialDir = initialDir;
                ofn.lpstrTitle = title;
                ofn.lpstrDefExt = defaultExt;
                ofn.Flags = OFN_EXPLORER | OFN_NOCHANGEDIR | OFN_HIDEREADONLY | OFN_PATHMUSTEXIST
                            | (save ? OFN_OVERWRITEPROMPT : OFN_FILEMUSTEXIST);

                bool ok = save ? GetSaveFileNameW(ref ofn) : GetOpenFileNameW(ref ofn);
                if (!ok) return null;
                string path = Marshal.PtrToStringUni(buffer);
                return string.IsNullOrEmpty(path) ? null : path;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
#endif
    }
}
