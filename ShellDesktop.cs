using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace BlackHole
{
    [StructLayout(LayoutKind.Sequential)]
    public struct PT { public int X, Y; }

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    interface IShellWindows
    {
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW([In, MarshalAs(UnmanagedType.Struct)] ref object pvarLoc,
                            [In, MarshalAs(UnmanagedType.Struct)] ref object pvarLocRoot,
                            [In] int swClass, [Out] out int pHWND, [In] int swfwOptions);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProviderCom
    {
        [PreserveSig] int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppv);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out IntPtr h);
        [PreserveSig] int ContextSensitiveHelp(bool b);
        [PreserveSig] int InsertMenusSB(IntPtr a, IntPtr b);
        [PreserveSig] int SetMenuSB(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int RemoveMenusSB(IntPtr a);
        [PreserveSig] int SetStatusTextSB(IntPtr a);
        [PreserveSig] int EnableModelessSB(bool b);
        [PreserveSig] int TranslateAcceleratorSB(IntPtr a, ushort b);
        [PreserveSig] int BrowseObject(IntPtr pidl, uint flags);
        [PreserveSig] int GetViewStateStream(uint mode, out IntPtr s);
        [PreserveSig] int GetControlWindow(uint id, out IntPtr h);
        [PreserveSig] int SendControlMsg(uint id, uint msg, IntPtr w, IntPtr l, out IntPtr ret);
        [PreserveSig] int QueryActiveShellView(out IntPtr ppshv);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellView
    {
        [PreserveSig] int GetWindow(out IntPtr h);
        [PreserveSig] int ContextSensitiveHelp(bool b);
        [PreserveSig] int TranslateAccelerator(IntPtr msg);
        [PreserveSig] int EnableModeless(bool b);
        [PreserveSig] int UIActivate(uint state);
        [PreserveSig] int Refresh();
    }

    [ComImport, Guid("cde725b0-ccc9-4519-917e-325d72fab4ce"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFolderView
    {
        [PreserveSig] int GetCurrentViewMode(out uint mode);
        [PreserveSig] int SetCurrentViewMode(uint mode);
        [PreserveSig] int GetFolder(ref Guid riid, out IntPtr ppv);
        [PreserveSig] int Item(int index, out IntPtr ppidl);
        [PreserveSig] int ItemCount(uint flags, out int count);
        [PreserveSig] int Items(uint flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetSelectionMarkedItem(out int item);
        [PreserveSig] int GetFocusedItem(out int item);
        [PreserveSig] int GetItemPosition(IntPtr pidl, out PT pt);
        [PreserveSig] int GetSpacing(out PT pt);
        [PreserveSig] int GetDefaultSpacing(out PT pt);
        [PreserveSig] int GetAutoArrange();
        [PreserveSig] int SelectItem(int index, uint flags);
        [PreserveSig] int SelectAndPositionItems(uint cidl, IntPtr apidl, IntPtr apt, uint flags);
    }

    public class DesktopItem
    {
        public string Path;
        public IntPtr Pidl;
        public int X, Y;
    }

    public class DesktopView
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdn, out IntPtr name);

        IFolderView fv;
        IShellView sv;
        public PT Spacing;

        public static DesktopView Open()
        {
            try
            {
                Type t = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
                IShellWindows sw = (IShellWindows)Activator.CreateInstance(t);
                object loc = 0, root = Type.Missing;
                int hwnd;
                object disp = sw.FindWindowSW(ref loc, ref root, 8, out hwnd, 1);
                IServiceProviderCom sp = (IServiceProviderCom)disp;
                Guid sidTop = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
                Guid iidSb = new Guid("000214E2-0000-0000-C000-000000000046");
                IntPtr psb;
                if (sp.QueryService(ref sidTop, ref iidSb, out psb) != 0) return null;
                IShellBrowser sb = (IShellBrowser)Marshal.GetObjectForIUnknown(psb);
                IntPtr psv;
                if (sb.QueryActiveShellView(out psv) != 0) return null;
                object svo = Marshal.GetObjectForIUnknown(psv);
                DesktopView dv = new DesktopView();
                dv.sv = (IShellView)svo;
                dv.fv = (IFolderView)svo;
                dv.fv.GetSpacing(out dv.Spacing);
                return dv;
            }
            catch (Exception ex) { Console.WriteLine(ex); return null; }
        }

        public bool AutoArrange { get { return fv.GetAutoArrange() == 0; } }

        public void Refresh() { try { sv.Refresh(); } catch (Exception) { } }

        public List<DesktopItem> Items()
        {
            List<DesktopItem> list = new List<DesktopItem>();
            int n;
            if (fv.ItemCount(2 /*SVGIO_ALLVIEW*/, out n) != 0) return list;
            for (int i = 0; i < n; i++)
            {
                IntPtr pidl;
                if (fv.Item(i, out pidl) != 0) continue;
                PT pt;
                if (fv.GetItemPosition(pidl, out pt) != 0) { Marshal.FreeCoTaskMem(pidl); continue; }
                IntPtr pn;
                string path = "";
                if (SHGetNameFromIDList(pidl, 0x80028000, out pn) == 0 && pn != IntPtr.Zero)
                {
                    path = Marshal.PtrToStringUni(pn);
                    Marshal.FreeCoTaskMem(pn);
                }
                DesktopItem it = new DesktopItem();
                it.Path = path; it.Pidl = pidl; it.X = pt.X; it.Y = pt.Y;
                list.Add(it);
            }
            return list;
        }

        public static void Free(List<DesktopItem> items)
        {
            foreach (DesktopItem it in items) if (it.Pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(it.Pidl);
            items.Clear();
        }

        public int SetPositions(List<IntPtr> pidls, List<PT> pts)
        {
            int n = pidls.Count; if (n == 0) return 0;
            IntPtr pp = Marshal.AllocHGlobal(IntPtr.Size * n);
            IntPtr pt = Marshal.AllocHGlobal(8 * n);
            try
            {
                for (int i = 0; i < n; i++)
                {
                    Marshal.WriteIntPtr(pp, i * IntPtr.Size, pidls[i]);
                    Marshal.WriteInt32(pt, i * 8, pts[i].X);
                    Marshal.WriteInt32(pt, i * 8 + 4, pts[i].Y);
                }
                return fv.SelectAndPositionItems((uint)n, pp, pt, 0x80);
            }
            finally { Marshal.FreeHGlobal(pp); Marshal.FreeHGlobal(pt); }
        }

        public void SetPosition(IntPtr pidl, int x, int y)
        {
            IntPtr pp = Marshal.AllocHGlobal(IntPtr.Size);
            IntPtr pt = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.WriteIntPtr(pp, pidl);
                Marshal.WriteInt32(pt, 0, x);
                Marshal.WriteInt32(pt, 4, y);
                fv.SelectAndPositionItems(1, pp, pt, 0x80 /*SVSI_POSITIONITEM*/);
            }
            finally { Marshal.FreeHGlobal(pp); Marshal.FreeHGlobal(pt); }
        }
    }
}
