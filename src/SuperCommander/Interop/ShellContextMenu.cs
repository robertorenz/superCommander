using System.Runtime.InteropServices;
using System.Windows.Interop;
using static SuperCommander.Interop.NativeMethods;

namespace SuperCommander.Interop;

/// <summary>
/// Shows the genuine Explorer shell context menu for a set of files that share
/// a parent folder. Handles IContextMenu2/3 owner-draw callbacks by hooking the
/// host window while the popup is tracking.
/// </summary>
internal static class ShellContextMenu
{
    private const uint CmdFirst = 1;
    private const uint CmdLast = 0x7FFF;

    private static IContextMenu2? _cm2;
    private static IContextMenu3? _cm3;

    /// <summary>
    /// Shows the shell menu at the given <b>physical screen pixel</b> position.
    /// Returns false when the menu could not be constructed (caller should fall
    /// back to the built-in menu).
    /// </summary>
    internal static bool Show(IntPtr hwnd, IReadOnlyList<string> paths, int screenX, int screenY,
        bool extendedVerbs = false)
    {
        if (paths.Count == 0 || hwnd == IntPtr.Zero) return false;

        var absolutePidls = new List<IntPtr>(paths.Count);
        var childPidls = new List<IntPtr>(paths.Count);
        IntPtr folderPtr = IntPtr.Zero;
        IntPtr cmPtr = IntPtr.Zero;
        IntPtr hMenu = IntPtr.Zero;
        object? folderObj = null;
        IContextMenu? cm = null;
        HwndSource? source = null;
        HwndSourceHook? hook = null;

        try
        {
            // Resolve every path to an absolute PIDL first.
            foreach (var p in paths)
            {
                if (SHParseDisplayName(p, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
                    continue;
                absolutePidls.Add(pidl);
            }
            if (absolutePidls.Count == 0) return false;

            // Bind to the shared parent folder using the first item.
            var iidFolder = IID_IShellFolder;
            if (SHBindToParent(absolutePidls[0], ref iidFolder, out folderPtr, out var firstChild) != 0
                || folderPtr == IntPtr.Zero)
                return false;

            folderObj = Marshal.GetObjectForIUnknown(folderPtr);
            if (folderObj is not IShellFolder folder) return false;

            childPidls.Add(firstChild);
            for (int i = 1; i < absolutePidls.Count; i++)
            {
                var iid = IID_IShellFolder;
                if (SHBindToParent(absolutePidls[i], ref iid, out var otherFolder, out var child) == 0)
                {
                    if (otherFolder != IntPtr.Zero) Marshal.Release(otherFolder);
                    if (child != IntPtr.Zero) childPidls.Add(child);
                }
            }

            var iidCm = IID_IContextMenu;
            if (folder.GetUIObjectOf(hwnd, (uint)childPidls.Count, childPidls.ToArray(),
                    ref iidCm, IntPtr.Zero, out cmPtr) != 0 || cmPtr == IntPtr.Zero)
                return false;

            cm = (IContextMenu)Marshal.GetObjectForIUnknown(cmPtr);
            _cm2 = cm as IContextMenu2;
            _cm3 = cm as IContextMenu3;

            hMenu = CreatePopupMenu();
            if (hMenu == IntPtr.Zero) return false;

            uint flags = CMF_NORMAL | CMF_EXPLORE | CMF_CANRENAME;
            if (extendedVerbs) flags |= CMF_EXTENDEDVERBS;
            if (cm.QueryContextMenu(hMenu, 0, CmdFirst, CmdLast, flags) < 0)
                return false;

            // Owner-draw / dynamic submenu callbacks arrive on the host window
            // while TrackPopupMenuEx runs its own modal loop.
            source = HwndSource.FromHwnd(hwnd);
            if (source is not null)
            {
                hook = MenuHook;
                source.AddHook(hook);
            }

            uint cmd = TrackPopupMenuEx(hMenu,
                TPM_RETURNCMD | TPM_LEFTALIGN | TPM_RIGHTBUTTON,
                screenX, screenY, hwnd, IntPtr.Zero);

            if (source is not null && hook is not null) { source.RemoveHook(hook); hook = null; }

            if (cmd == 0) return true; // dismissed - still counts as handled

            var invoke = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE,
                hwnd = hwnd,
                lpVerb = (IntPtr)(cmd - CmdFirst),
                lpVerbW = (IntPtr)(cmd - CmdFirst),
                nShow = SW_SHOWNORMAL,
                ptInvoke = new POINT { X = screenX, Y = screenY }
            };
            cm.InvokeCommand(ref invoke);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
        finally
        {
            if (source is not null && hook is not null) source.RemoveHook(hook);
            _cm2 = null;
            _cm3 = null;

            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            if (cm is not null) Marshal.FinalReleaseComObject(cm);
            if (cmPtr != IntPtr.Zero) Marshal.Release(cmPtr);
            if (folderObj is not null) Marshal.FinalReleaseComObject(folderObj);
            if (folderPtr != IntPtr.Zero) Marshal.Release(folderPtr);

            // Child PIDLs point into the absolute ones - never freed separately.
            foreach (var pidl in absolutePidls) ILFree(pidl);
        }
    }

    private static IntPtr MenuHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_INITMENUPOPUP:
            case WM_DRAWITEM:
            case WM_MEASUREITEM:
            case WM_MENUCHAR:
                if (_cm3 is not null)
                {
                    if (_cm3.HandleMenuMsg2((uint)msg, wParam, lParam, out var result) == 0)
                    {
                        handled = true;
                        return result;
                    }
                }
                else if (_cm2 is not null)
                {
                    if (_cm2.HandleMenuMsg((uint)msg, wParam, lParam) == 0)
                    {
                        handled = true;
                        return IntPtr.Zero;
                    }
                }
                break;
        }
        return IntPtr.Zero;
    }
}
