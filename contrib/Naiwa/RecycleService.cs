using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace NaiwaDesktop
{
    internal static class RecycleService
    {
        private const uint DriveFixed = 3;
        private const uint RecycleIfPossible = 0x80;
        private const int CopyEngineDontProcessChildren = 0x00270008;
        private static readonly Guid RecycleBinFolder = new Guid("b7534046-3ecb-4c18-be4e-64cd4cb7d6ac");
        private static readonly Guid TransferHandler = new Guid("d5e346a1-f753-4932-b403-4574800e2498");

        internal static int Recycle(string[] paths)
        {
            if (paths == null || paths.Length == 0)
                return 0;

            IShellItem bin = null;
            try
            {
                Guid folderId = RecycleBinFolder;
                Guid itemId = typeof(IShellItem).GUID;
                if (SHGetKnownFolderItem(ref folderId, 0, IntPtr.Zero, ref itemId, out bin) != 0 || bin == null)
                    return 0;

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int recycled = 0;
                foreach (string path in paths)
                {
                    string localPath = GetLocalPath(path);
                    if (localPath != null && seen.Add(localPath) && TryRecycle(localPath, bin))
                        recycled++;
                }
                return recycled;
            }
            finally
            {
                Release(bin);
            }
        }

        private static string GetLocalPath(string path)
        {
            // CF_HDROP paths must identify a local filesystem item on a fixed drive.
            // Filter network drives, device paths, volume roots and alternate streams
            // before passing the path to the Shell's namespace parser.
            if (string.IsNullOrEmpty(path) || path.Length <= 3 || path[1] != ':' ||
                path[2] != '\\' || path.IndexOf(':', 2) >= 0)
                return null;
            try
            {
                string fullPath = Path.GetFullPath(path).TrimEnd('\\');
                string root = Path.GetPathRoot(fullPath);
                if (fullPath.Length <= 3 || GetDriveType(root) != DriveFixed)
                    return null;
                // Exclude paths that only exist in virtual Shell folders, such as ZIP contents.
                File.GetAttributes(fullPath);
                return fullPath;
            }
            catch (Exception error) when (error is ArgumentException || error is IOException ||
                                          error is UnauthorizedAccessException || error is NotSupportedException ||
                                          error is System.Security.SecurityException)
            {
                return null;
            }
        }

        private static bool TryRecycle(string path, IShellItem bin)
        {
            IShellItem source = null;
            IShellItem parent = null;
            ITransferSource transfer = null;
            IShellItem recycled = null;
            try
            {
                Guid itemId = typeof(IShellItem).GUID;
                Guid handlerId = TransferHandler;
                Guid transferId = typeof(ITransferSource).GUID;
                if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref itemId, out source) != 0 || source == null ||
                    source.GetParent(out parent) != 0 || parent == null ||
                    parent.BindToHandler(IntPtr.Zero, ref handlerId, ref transferId, out transfer) != 0 || transfer == null)
                    return false;

                // RecycleItem only requests the move into the supplied Recycle Bin.
                // Never call RemoveItem or retry with a delete operation on failure.
                int result = transfer.RecycleItem(source, bin, RecycleIfPossible, out recycled);
                return (result == 0 || result == CopyEngineDontProcessChildren) && recycled != null;
            }
            catch (COMException)
            {
                return false;
            }
            finally
            {
                Release(recycled);
                Release(transfer);
                Release(parent);
                Release(source);
            }
        }

        private static void Release(object instance)
        {
            if (instance != null)
                Marshal.ReleaseComObject(instance);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetDriveTypeW", ExactSpelling = true)]
        private static extern uint GetDriveType(string rootPath);

        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern int SHGetKnownFolderItem(ref Guid folderId, uint flags, IntPtr token, ref Guid iid,
            out IShellItem item);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid,
            out IShellItem item);

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out ITransferSource transfer);
            [PreserveSig] int GetParent(out IShellItem parent);
        }

        [ComImport, Guid("00adb003-bde9-45c6-8e29-d09f9353e108"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITransferSource
        {
            [PreserveSig] int Advise(IntPtr sink, out uint cookie);
            [PreserveSig] int Unadvise(uint cookie);
            [PreserveSig] int SetProperties(IntPtr propertyChanges);
            [PreserveSig] int OpenItem(IShellItem item, uint flags, ref Guid iid, out IntPtr resources);
            [PreserveSig] int MoveItem(IShellItem source, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name,
                uint flags, out IShellItem newItem);
            [PreserveSig] int RecycleItem(IShellItem source, IShellItem destination, uint flags, out IShellItem recycledItem);
        }
    }
}
