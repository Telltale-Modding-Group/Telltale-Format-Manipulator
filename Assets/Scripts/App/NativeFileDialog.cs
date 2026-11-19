using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

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
        /// Picks a folder with the normal Explorer-style dialog (IFileOpenDialog in folder mode,
        /// Vista and later, so fine on Windows 7): address bar, favourites, typing a path.
        /// Falls back to the old SHBrowseForFolder tree if the modern dialog can't be created.
        /// Returns null if the user cancels.
        /// </summary>
        public static string OpenFolder(string title, string initialDir)
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (!string.IsNullOrEmpty(initialDir) && !Directory.Exists(initialDir)) initialDir = null;
            bool cancelled;
            string modern = OpenFolderModern(title, initialDir, out cancelled);
            if (modern != null || cancelled) return modern;
            return OpenFolderLegacy(title, initialDir);
#else
            return null;
#endif
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        // ---- modern folder picker (IFileOpenDialog via raw vtable calls) ----
        // Calling through the vtable avoids relying on Mono's [ComImport] interop support.

        static readonly Guid CLSID_FileOpenDialog = new Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");
        static readonly Guid IID_IFileOpenDialog = new Guid("d57c7288-d4ad-4768-be02-9d969532d960");
        static readonly Guid IID_IShellItem = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");

        const uint FOS_NOCHANGEDIR = 0x00000008;
        const uint FOS_PICKFOLDERS = 0x00000020;
        const uint FOS_FORCEFILESYSTEM = 0x00000040;
        const uint FOS_PATHMUSTEXIST = 0x00000800;
        const uint SIGDN_FILESYSPATH = 0x80058000;
        const int HRESULT_CANCELLED = unchecked((int)0x800704C7);
        const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
        const int CLSCTX_INPROC_SERVER = 1;
        const int COINIT_APARTMENTTHREADED = 2;

        // IFileDialog vtable slots (IUnknown 0-2, IModalWindow 3, IFileDialog 4+).
        const int Slot_Release = 2;
        const int Slot_Show = 3;
        const int Slot_SetOptions = 9;
        const int Slot_GetOptions = 10;
        const int Slot_SetFolder = 12;
        const int Slot_SetTitle = 17;
        const int Slot_GetResult = 20;
        // IShellItem slot.
        const int Slot_GetDisplayName = 5;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate uint ReleaseFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int ShowFn(IntPtr self, IntPtr hwnd);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int SetOptionsFn(IntPtr self, uint fos);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int GetOptionsFn(IntPtr self, out uint fos);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int SetFolderFn(IntPtr self, IntPtr shellItem);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        delegate int SetTitleFn(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string title);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int GetResultFn(IntPtr self, out IntPtr shellItem);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int GetDisplayNameFn(IntPtr self, uint sigdn, out IntPtr name);

        [DllImport("ole32.dll")]
        static extern int CoInitializeEx(IntPtr reserved, int coInit);
        [DllImport("ole32.dll")]
        static extern void CoUninitialize();
        [DllImport("ole32.dll")]
        static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int clsContext, ref Guid iid, out IntPtr ppv);
        [DllImport("ole32.dll")]
        static extern void CoTaskMemFree(IntPtr ptr);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr bindCtx, ref Guid iid, out IntPtr item);

        static T VtableFn<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr vtable = Marshal.ReadIntPtr(obj);
            IntPtr fn = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer(fn, typeof(T)) as T;
        }

        static void ComRelease(IntPtr obj)
        {
            if (obj != IntPtr.Zero) VtableFn<ReleaseFn>(obj, Slot_Release)(obj);
        }

        /// <summary>Returns the chosen path, or null. cancelled=true means the user closed the dialog
        /// (don't fall back); cancelled=false with null means the modern dialog itself failed.</summary>
        static string OpenFolderModern(string title, string initialDir, out bool cancelled)
        {
            cancelled = false;
            IntPtr dialog = IntPtr.Zero, folderItem = IntPtr.Zero, resultItem = IntPtr.Zero, namePtr = IntPtr.Zero;
            bool uninit = false;
            try
            {
                int hr = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
                if (hr >= 0) uninit = true;              // S_OK / S_FALSE: balance with CoUninitialize
                else if (hr != RPC_E_CHANGED_MODE) return null;

                Guid clsid = CLSID_FileOpenDialog, iid = IID_IFileOpenDialog;
                hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out dialog);
                if (hr < 0 || dialog == IntPtr.Zero) return null;

                uint opts;
                if (VtableFn<GetOptionsFn>(dialog, Slot_GetOptions)(dialog, out opts) < 0) opts = 0;
                opts |= FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR;
                if (VtableFn<SetOptionsFn>(dialog, Slot_SetOptions)(dialog, opts) < 0) return null;

                if (!string.IsNullOrEmpty(title))
                    VtableFn<SetTitleFn>(dialog, Slot_SetTitle)(dialog, title);

                if (!string.IsNullOrEmpty(initialDir))
                {
                    Guid itemIid = IID_IShellItem;
                    if (SHCreateItemFromParsingName(Path.GetFullPath(initialDir), IntPtr.Zero, ref itemIid, out folderItem) >= 0
                        && folderItem != IntPtr.Zero)
                        VtableFn<SetFolderFn>(dialog, Slot_SetFolder)(dialog, folderItem);
                }

                hr = VtableFn<ShowFn>(dialog, Slot_Show)(dialog, GetActiveWindow());
                if (hr == HRESULT_CANCELLED) { cancelled = true; return null; }
                if (hr < 0) return null;

                if (VtableFn<GetResultFn>(dialog, Slot_GetResult)(dialog, out resultItem) < 0 || resultItem == IntPtr.Zero)
                    return null;
                if (VtableFn<GetDisplayNameFn>(resultItem, Slot_GetDisplayName)(resultItem, SIGDN_FILESYSPATH, out namePtr) < 0
                    || namePtr == IntPtr.Zero)
                    return null;

                string path = Marshal.PtrToStringUni(namePtr);
                if (string.IsNullOrEmpty(path)) return null;
                return path;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (namePtr != IntPtr.Zero) CoTaskMemFree(namePtr);
                try { ComRelease(resultItem); } catch { }
                try { ComRelease(folderItem); } catch { }
                try { ComRelease(dialog); } catch { }
                if (uninit) CoUninitialize();
            }
        }

        /// <summary>Old shell32 SHBrowseForFolder tree, kept only as a fallback.</summary>
        static string OpenFolderLegacy(string title, string initialDir)
        {
            IntPtr pidl = IntPtr.Zero;
            try
            {
                BROWSEINFOW bi = new BROWSEINFOW();
                bi.hwndOwner = GetActiveWindow();
                bi.lpszTitle = title ?? "Choose a folder";
                // BIF_RETURNONLYFSDIRS (0x1): only real folders, so the result always has a path.
                // BIF_EDITBOX (0x10): lets the path be typed. Neither needs OLE to be initialised,
                // unlike BIF_NEWDIALOGSTYLE, which can fail inside Unity.
                bi.ulFlags = 0x0001 | 0x0010;

                pidl = SHBrowseForFolderW(ref bi);
                if (pidl == IntPtr.Zero) return null; // cancelled

                StringBuilder path = new StringBuilder(MaxPath);
                if (!SHGetPathFromIDListW(pidl, path)) return null;
                string result = path.ToString();
                return string.IsNullOrEmpty(result) ? null : result;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
            }
        }

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

        // ---- folder browser (shell32) ----

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct BROWSEINFOW
        {
            public IntPtr hwndOwner;
            public IntPtr pidlRoot;
            public string pszDisplayName;
            public string lpszTitle;
            public int ulFlags;
            public IntPtr lpfn;
            public IntPtr lParam;
            public int iImage;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHBrowseForFolderW(ref BROWSEINFOW browse);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder path);

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
