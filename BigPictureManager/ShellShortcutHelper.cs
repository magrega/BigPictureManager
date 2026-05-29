using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BigPictureManager
{
    /// <summary>
    /// Creates and reads shell shortcuts with an explicit AppUserModelID (required for Win10/11 toasts).
    /// </summary>
    internal static class ShellShortcutHelper
    {
        private static readonly PropertyKey AppUserModelIdPropertyKey = new PropertyKey
        {
            fmtid = new Guid(0x9f4c2855, 0x9f79, 0x4b39, 0xa8, 0xd0, 0xe1, 0xd4, 0x2d, 0xe1, 0xd5, 0xf3),
            pid = 5,
        };

        internal static bool TryReadAppUserModelId(string shortcutPath, out string appUserModelId)
        {
            appUserModelId = null;
            if (string.IsNullOrWhiteSpace(shortcutPath) || !File.Exists(shortcutPath))
            {
                return false;
            }

            IShellLinkW link = null;
            try
            {
                link = (IShellLinkW)new CShellLink();
                var persistFile = (IPersistFile)link;
                persistFile.Load(shortcutPath, 0);

                var propertyStore = (IPropertyStore)link;
                var propertyKey = AppUserModelIdPropertyKey;
                var hr = propertyStore.GetValue(ref propertyKey, out var value);
                if (hr != 0 || value.vt != 31 || value.pszVal == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    appUserModelId = Marshal.PtrToStringUni(value.pszVal);
                    return !string.IsNullOrEmpty(appUserModelId);
                }
                finally
                {
                    ClearPropVariant(ref value);
                }
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Toast] Failed to read shortcut AppUserModelID: " + ex.Message);
                return false;
            }
            finally
            {
                if (link != null)
                {
                    Marshal.ReleaseComObject(link);
                }
            }
        }

        internal static bool TryReadTargetPath(string shortcutPath, out string targetPath)
        {
            targetPath = null;
            if (string.IsNullOrWhiteSpace(shortcutPath) || !File.Exists(shortcutPath))
            {
                return false;
            }

            IShellLinkW link = null;
            try
            {
                link = (IShellLinkW)new CShellLink();
                var persistFile = (IPersistFile)link;
                persistFile.Load(shortcutPath, 0);

                var buffer = new StringBuilder(260);
                if (link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0) != 0)
                {
                    return false;
                }

                targetPath = buffer.ToString();
                return !string.IsNullOrEmpty(targetPath);
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Toast] Failed to read shortcut target path: " + ex.Message);
                return false;
            }
            finally
            {
                if (link != null)
                {
                    Marshal.ReleaseComObject(link);
                }
            }
        }

        internal static bool TryCreateOrUpdate(
            string shortcutPath,
            string targetPath,
            string workingDirectory,
            string description,
            string iconLocation,
            string appUserModelId
        )
        {
            if (string.IsNullOrWhiteSpace(shortcutPath)
                || string.IsNullOrWhiteSpace(targetPath)
                || string.IsNullOrWhiteSpace(appUserModelId))
            {
                return false;
            }

            try
            {
                var directory = Path.GetDirectoryName(shortcutPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                CreateBaseShortcut(shortcutPath, targetPath, workingDirectory, description, iconLocation);
                return TryApplyAppUserModelId(shortcutPath, appUserModelId);
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Toast] Failed to create Start Menu shortcut: " + ex.Message);
                return false;
            }
        }

        private static void CreateBaseShortcut(
            string shortcutPath,
            string targetPath,
            string workingDirectory,
            string description,
            string iconLocation
        )
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                throw new InvalidOperationException("WScript.Shell is not available.");
            }

            dynamic shell = Activator.CreateInstance(shellType);
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = workingDirectory;
            shortcut.Description = description;
            if (!string.IsNullOrWhiteSpace(iconLocation))
            {
                shortcut.IconLocation = iconLocation;
            }

            shortcut.Save();
            Marshal.ReleaseComObject(shell);
        }

        private static bool TryApplyAppUserModelId(string shortcutPath, string appUserModelId)
        {
            IShellLinkW link = null;
            try
            {
                link = (IShellLinkW)new CShellLink();
                var persistFile = (IPersistFile)link;
                persistFile.Load(shortcutPath, 2);

                var propertyStore = (IPropertyStore)link;
                var propertyKey = AppUserModelIdPropertyKey;
                var value = PropVariant.FromString(appUserModelId);
                try
                {
                    var hr = propertyStore.SetValue(ref propertyKey, ref value);
                    if (hr != 0)
                    {
                        BpmLog.WriteLine("[Error] [Toast] SetValue(AppUserModelID) failed with HRESULT 0x" + hr.ToString("X8"));
                        return false;
                    }

                    hr = propertyStore.Commit();
                    if (hr != 0)
                    {
                        BpmLog.WriteLine("[Error] [Toast] IPropertyStore.Commit failed with HRESULT 0x" + hr.ToString("X8"));
                        return false;
                    }
                }
                finally
                {
                    value.Dispose();
                }

                persistFile.Save(shortcutPath, true);
                return true;
            }
            finally
            {
                if (link != null)
                {
                    Marshal.ReleaseComObject(link);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PropertyKey
        {
            public Guid fmtid;
            public int pid;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PropVariant : IDisposable
        {
            [FieldOffset(0)]
            public ushort vt;

            [FieldOffset(8)]
            public IntPtr pszVal;

            public static PropVariant FromString(string value)
            {
                if (value == null)
                {
                    throw new ArgumentNullException(nameof(value));
                }

                return new PropVariant
                {
                    vt = 31,
                    pszVal = Marshal.StringToCoTaskMemUni(value),
                };
            }

            public void Dispose()
            {
                if (pszVal != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pszVal);
                    pszVal = IntPtr.Zero;
                }
            }
        }

        private static void ClearPropVariant(ref PropVariant value)
        {
            if (value.vt == 0)
            {
                return;
            }

            PropVariantClear(ref value);
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant pvar);

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class CShellLink { }

        [ComImport]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            [PreserveSig]
            int GetPath(
                [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
                int cch,
                IntPtr pfd,
                int fFlags
            );

            [PreserveSig]
            int GetIDList(out IntPtr ppidl);

            [PreserveSig]
            int SetIDList(IntPtr pidl);

            [PreserveSig]
            int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);

            [PreserveSig]
            int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

            [PreserveSig]
            int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);

            [PreserveSig]
            int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

            [PreserveSig]
            int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);

            [PreserveSig]
            int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

            [PreserveSig]
            int GetHotkey(out short pwHotkey);

            [PreserveSig]
            int SetHotkey(short wHotkey);

            [PreserveSig]
            int GetShowCmd(out int piShowCmd);

            [PreserveSig]
            int SetShowCmd(int iShowCmd);

            [PreserveSig]
            int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath);

            [PreserveSig]
            int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

            [PreserveSig]
            int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);

            [PreserveSig]
            int Resolve(IntPtr hwnd, int fFlags);

            [PreserveSig]
            int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [Guid("0000010c-0000-0000-c000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);

            [PreserveSig]
            int IsDirty();

            [PreserveSig]
            int Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);

            [PreserveSig]
            int Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);

            [PreserveSig]
            int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);

            [PreserveSig]
            int GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

        [ComImport]
        [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig]
            int GetCount(out uint cProps);

            [PreserveSig]
            int GetAt(uint iProp, out PropertyKey pkey);

            [PreserveSig]
            int GetValue(ref PropertyKey key, out PropVariant pv);

            [PreserveSig]
            int SetValue(ref PropertyKey key, ref PropVariant pv);

            [PreserveSig]
            int Commit();
        }
    }
}
