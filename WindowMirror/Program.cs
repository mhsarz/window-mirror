using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

static class Native
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public MOUSEINPUT mi; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint n, INPUT[] inputs, int size);

    // SetForegroundWindow doesnt usually work for background processes...
    // attach to the current foreground thread's input queue instead
    public static void FocusWindow(IntPtr h)
    {
        IntPtr fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        SetForegroundWindow(h);
        if (attached) AttachThreadInput(me, fgThread, false);
    }
   

    public static bool IsForegroundOurs()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        return pid == (uint)Environment.ProcessId;
    }
    public const uint PW_RENDERFULLCONTENT = 2;
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public const uint SWP_NOACTIVATE = 0x0010, SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002;
}

class MirrorForm : Form
{
    const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, 
        WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const int WM_HOTKEY = 0x0312, HOTKEY_ID = 1;

    readonly IntPtr _src;
    readonly bool _clickThrough;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    Bitmap _frame;
    Native.RECT _lastRect;

    public MirrorForm(IntPtr src, bool clickThrough)
    {
        _src = src;
        _clickThrough = clickThrough;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Black;
        DoubleBuffered = true;

        _timer.Tick += (_, _) => Tick();
        Load += (_, _) => _timer.Start();
        Shown += (_, _) => Native.FocusWindow(_src);
    }
    Native.LowLevelMouseProc _hookProc;   // keep a reference so the GC doesn't collect the delegate
    IntPtr _hook;
    int _remappedButtons;                 // bit 1=left, 2=right, 4=middle: downs we remapped, so the matching up is remapped too

    void InstallMouseHook()
    {
        _hookProc = MouseHook;
        _hook = Native.SetWindowsHookEx(14 /*WH_MOUSE_LL*/, _hookProc, Native.GetModuleHandle(null), 0);
    }

    void RemoveMouseHook()
    {
        if (_hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }

    IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var d = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);

            if ((d.flags & 3) == 0)   // ignore events we injected ourselves
            {
                uint flag = 0; int bit = 0; bool down = false, wheel = false;
                switch ((int)wParam)
                {
                    case 0x201: flag = 0x0002; bit = 1; down = true; break;  // LBUTTONDOWN
                    case 0x202: flag = 0x0004; bit = 1; break;               // LBUTTONUP
                    case 0x204: flag = 0x0008; bit = 2; down = true; break;  // RBUTTONDOWN
                    case 0x205: flag = 0x0010; bit = 2; break;               // RBUTTONUP
                    case 0x207: flag = 0x0020; bit = 4; down = true; break;  // MBUTTONDOWN
                    case 0x208: flag = 0x0040; bit = 4; break;               // MBUTTONUP
                    case 0x20A: flag = 0x0800; wheel = true; break;          // MOUSEWHEEL
                }

                if (flag != 0)
                {
                    bool inside = Visible && !Native.IsIconic(_src) &&
                        d.pt.X >= _lastRect.Left && d.pt.X < _lastRect.Right &&
                        d.pt.Y >= _lastRect.Top && d.pt.Y < _lastRect.Bottom;

                    bool handle = (down || wheel) ? inside : (_remappedButtons & bit) != 0;

                    if (handle)
                    {
                        if (!wheel) { if (down) _remappedButtons |= bit; else _remappedButtons &= ~bit; }

                        int mx = _lastRect.Left + _lastRect.Right - 1 - d.pt.X;   // mirror across the center
                        var orig = d.pt;
                        uint data = wheel ? unchecked((uint)(int)(short)(d.mouseData >> 16)) : 0;

                        BeginInvoke(new Action(() => Inject(mx, orig, flag, data)));
                        return (IntPtr)1;   // swallow the original event
                    }
                }
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    void Inject(int mirroredX, Native.POINT orig, uint flag, uint data)
    {
        Native.SetCursorPos(mirroredX, orig.Y);   // the event is stamped with the cursor position at send time
        var input = new Native.INPUT { type = 0, mi = new Native.MOUSEINPUT { mouseData = data, dwFlags = flag } };
        Native.SendInput(1, new[] { input }, Marshal.SizeOf<Native.INPUT>());
        Native.SetCursorPos(orig.X, orig.Y);      // put the cursor back where the user sees it
    }
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            if (_clickThrough) cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_clickThrough) Native.SetLayeredWindowAttributes(Handle, 0, 255, 2 /*LWA_ALPHA*/);
        // Ctrl+Alt+Q quits
        Native.RegisterHotKey(Handle, HOTKEY_ID, 0x0002 | 0x0001, (uint)Keys.Q);
        if (_clickThrough) InstallMouseHook();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Native.UnregisterHotKey(Handle, HOTKEY_ID);
        RemoveMouseHook();
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID) Close();
        base.WndProc(ref m);
    }

    void Tick()
    {
        if (!Native.IsWindow(_src)) { Close(); return; }

        if (Native.IsIconic(_src))          // can't capture minimized windows
        {
            if (Visible) Hide();
            return;
        }

        if (Native.IsForegroundOurs())
            Native.FocusWindow(_src);

        Native.GetWindowRect(_src, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return;

        // copy the source bounds
        if (r.Left != _lastRect.Left || r.Top != _lastRect.Top ||
            r.Right != _lastRect.Right || r.Bottom != _lastRect.Bottom)
        {
            Bounds = new Rectangle(r.Left, r.Top, w, h);
            _lastRect = r;
        }

        if (!Visible)
        {
            Show();
        }
        // keep on top of everything including the source without stealing focus
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOACTIVATE | Native.SWP_NOSIZE | Native.SWP_NOMOVE);

        // reallocate frame buffer if size changed
        if (_frame == null || _frame.Width != w || _frame.Height != h)
        {
            _frame?.Dispose();
            _frame = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        }

        // capture the source even if its covered by other windows
        using (var g = Graphics.FromImage(_frame))
        {
            g.Clear(Color.Black);
            IntPtr hdc = g.GetHdc();
            try { Native.PrintWindow(_src, hdc, Native.PW_RENDERFULLCONTENT); }
            finally { g.ReleaseHdc(hdc); }
        }

        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e) { /* avoid flicker */ }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_frame == null) return;
        var g = e.Graphics;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        // horizontal flip about the window vertical centerline
        g.TranslateTransform(ClientSize.Width, 0);
        g.ScaleTransform(-1, 1);
        g.DrawImage(_frame, 0, 0, ClientSize.Width, ClientSize.Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _frame?.Dispose(); }
        base.Dispose(disposing);
    }
}

class PickerForm : Form
{
    public IntPtr Selected;

    // supports auto pick
    public PickerForm(string autoSelectTitle = null)
    {
        Text = "Pick a window to mirror";
        Width = 600; Height = 450;
        StartPosition = FormStartPosition.CenterScreen;

        var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        var items = new List<(IntPtr h, string t)>();

        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            Native.DwmGetWindowAttribute(h, 14 /*DWMWA_CLOAKED*/, out int cloaked, 4);
            if (cloaked != 0) return true;
            int len = Native.GetWindowTextLength(h);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            Native.GetWindowText(h, sb, sb.Capacity);
            items.Add((h, sb.ToString()));
            return true;
        }, IntPtr.Zero);

        foreach (var it in items) list.Items.Add(it.t);

        // if a title was supplied, automatically select the matching window
        if (!string.IsNullOrWhiteSpace(autoSelectTitle))
        {
            var match = items.FindIndex(it =>
                it.t.IndexOf(
                    autoSelectTitle,
                    StringComparison.OrdinalIgnoreCase) >= 0);

            if (match >= 0)
            {
                Selected = items[match].h;

                Shown += (_, _) =>
                {
                    DialogResult = DialogResult.OK;
                };
            }
        }


        void Choose()
        {
            if (list.SelectedIndex < 0) return;
            Selected = items[list.SelectedIndex].h;
            DialogResult = DialogResult.OK;
        }
        list.DoubleClick += (_, _) => Choose();
        var ok = new Button { Text = "Mirror selected window", Dock = DockStyle.Bottom, Height = 36 };
        ok.Click += (_, _) => Choose();

        Controls.Add(list);
        Controls.Add(ok);
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        bool clickThrough = !Array.Exists(args, a => a == "--absorb");
        string autoSelectTitle = null;

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--title")
            {
                autoSelectTitle = args[i + 1];
                break;
            }
        }

        IntPtr target;
        using (var picker = new PickerForm(autoSelectTitle))
        {
            if (picker.ShowDialog() != DialogResult.OK)
                return;
            target = picker.Selected;
        }
        Application.Run(new MirrorForm(target, clickThrough));
    }
}

