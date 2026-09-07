using System.Runtime.InteropServices;
using System.Text;

namespace SuperCommander.Interop;

/// <summary>
/// Raw P/Invoke surface. Nothing in here should be called directly from view
/// models - wrap it in <see cref="ShellServices"/> or <see cref="ShellContextMenu"/>.
/// </summary>
internal static class NativeMethods
{
    // ---------------------------------------------------------------- shell32

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [Flags]
    internal enum SHGFI : uint
    {
        Icon = 0x000000100,
        DisplayName = 0x000000200,
        TypeName = 0x000000400,
        Attributes = 0x000000800,
        IconLocation = 0x000001000,
        ExeType = 0x000002000,
        SysIconIndex = 0x000004000,
        LinkOverlay = 0x000008000,
        Selected = 0x000010000,
        LargeIcon = 0x000000000,
        SmallIcon = 0x000000001,
        OpenIcon = 0x000000002,
        ShellIconSize = 0x000000004,
        Pidl = 0x000000008,
        UseFileAttributes = 0x000000010,
        AddOverlays = 0x000000020,
        OverlayIndex = 0x000000040
    }

    internal const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    internal const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    // ------------------------------------------------------- file operations

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 8)]
    internal struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    internal const uint FO_MOVE = 0x0001;
    internal const uint FO_COPY = 0x0002;
    internal const uint FO_DELETE = 0x0003;
    internal const uint FO_RENAME = 0x0004;

    internal const ushort FOF_MULTIDESTFILES = 0x0001;
    internal const ushort FOF_SILENT = 0x0004;
    internal const ushort FOF_RENAMEONCOLLISION = 0x0008;
    internal const ushort FOF_NOCONFIRMATION = 0x0010;
    internal const ushort FOF_ALLOWUNDO = 0x0040;
    internal const ushort FOF_FILESONLY = 0x0080;
    internal const ushort FOF_SIMPLEPROGRESS = 0x0100;
    internal const ushort FOF_NOCONFIRMMKDIR = 0x0200;
    internal const ushort FOF_NOERRORUI = 0x0400;
    internal const ushort FOF_WANTNUKEWARNING = 0x4000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    // ---------------------------------------------------------- ShellExecute

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    internal const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    internal const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;
    internal const uint SEE_MASK_FLAG_NO_UI = 0x00000400;
    internal const uint SEE_MASK_NOASYNC = 0x00000100;
    internal const int SW_SHOWNORMAL = 1;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    // ----------------------------------------------------------------- PIDLs

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHParseDisplayName(string pszName, IntPtr pbc,
        out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    internal static extern int SHGetDesktopFolder(out IntPtr ppshf);

    [DllImport("shell32.dll")]
    internal static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr ILCombine(IntPtr pidl1, IntPtr pidl2);

    [DllImport("shell32.dll")]
    internal static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll", EntryPoint = "#155")]
    internal static extern IntPtr ILClone(IntPtr pidl);

    // ------------------------------------------------------------ menu (user32)

    [DllImport("user32.dll")]
    internal static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyMenu(IntPtr hMenu);

    [StructLayout(LayoutKind.Sequential)]
    internal struct TPMPARAMS
    {
        public int cbSize;
        public RECT rcExclude;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X, Y;
    }

    internal const uint TPM_LEFTALIGN = 0x0000;
    internal const uint TPM_RETURNCMD = 0x0100;
    internal const uint TPM_RIGHTBUTTON = 0x0002;
    internal const uint TPM_LEFTBUTTON = 0x0000;

    [DllImport("user32.dll")]
    internal static extern uint TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y,
        IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    internal const int WM_INITMENUPOPUP = 0x0117;
    internal const int WM_DRAWITEM = 0x002B;
    internal const int WM_MEASUREITEM = 0x002C;
    internal const int WM_MENUCHAR = 0x0120;
    internal const int WM_MENUSELECT = 0x011F;

    // ---------------------------------------------------------------- DWM

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;

    // --------------------------------------------------------------- volumes

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetDiskFreeSpaceEx(string lpDirectoryName,
        out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVolumeInformation(string rootPathName,
        StringBuilder volumeNameBuffer, int volumeNameSize, out uint volumeSerialNumber,
        out uint maximumComponentLength, out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer, int fileSystemNameSize);

    // ---------------------------------------------------------- COM interfaces

    internal static Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    internal static Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");
    internal static Guid IID_IContextMenu2 = new("000214F4-0000-0000-C000-000000000046");
    internal static Guid IID_IContextMenu3 = new("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719");

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    internal interface IShellFolder
    {
        [PreserveSig]
        int ParseDisplayName(IntPtr hwnd, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            ref uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);

        [PreserveSig] int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);

        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);

        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);

        [PreserveSig] int GetAttributesOf(uint cidl,
            [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);

        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl,
            [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid,
            IntPtr rgfReserved, out IntPtr ppv);

        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);

        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl,
            [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpTitle;
        public IntPtr lpVerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParametersW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitleW;
        public POINT ptInvoke;
    }

    internal const uint CMIC_MASK_UNICODE = 0x00004000;
    internal const uint CMIC_MASK_PTINVOKE = 0x20000000;

    internal const uint CMF_NORMAL = 0x00000000;
    internal const uint CMF_DEFAULTONLY = 0x00000001;
    internal const uint CMF_VERBSONLY = 0x00000002;
    internal const uint CMF_EXPLORE = 0x00000004;
    internal const uint CMF_NOVERBS = 0x00000008;
    internal const uint CMF_CANRENAME = 0x00000010;
    internal const uint CMF_NODEFAULT = 0x00000020;
    internal const uint CMF_ITEMMENU = 0x00000080;
    internal const uint CMF_EXTENDEDVERBS = 0x00000100;

    internal const uint GCS_VERBW = 0x00000004;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    internal interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        [PreserveSig]
        int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved,
            [Out, MarshalAs(UnmanagedType.LPArray)] byte[] pszName, uint cchMax);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F4-0000-0000-C000-000000000046")]
    internal interface IContextMenu2
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        [PreserveSig]
        int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved,
            [Out, MarshalAs(UnmanagedType.LPArray)] byte[] pszName, uint cchMax);

        [PreserveSig]
        int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
    internal interface IContextMenu3
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        [PreserveSig]
        int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved,
            [Out, MarshalAs(UnmanagedType.LPArray)] byte[] pszName, uint cchMax);

        [PreserveSig]
        int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);

        [PreserveSig]
        int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }
}
