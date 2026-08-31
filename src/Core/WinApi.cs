using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;

namespace TxwExtract.Core;

public sealed record WinInfo(IntPtr Hwnd, string Title, int Pid, string ProcessName);

/// <summary>
/// 原生 Windows 互操作：窗口枚举、后台截图（PrintWindow）、窗口激活、鼠标点击。
/// 全部为直接 P/Invoke —— 不经任何外部进程/脚本，是本版性能优势的来源。
/// </summary>
public static class WinApi
{
    public const uint PW_RENDERFULLCONTENT = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hDC, uint nFlags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();

    // ---- PostMessage（窗口相对坐标消息，窗口被遮挡时仍生效）----
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP   = 0x0202;
    public const uint WM_MOUSEWHEEL  = 0x020A;
    public const uint MK_LBUTTON     = 0x0001;
    public const int  WHEEL_DELTA    = 120;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    /// <summary>
    /// 向目标窗口投递鼠标点击消息（WM_LBUTTONDOWN/UP）—— 后台点击：窗口被完全遮挡也能命中。
    /// 传入坐标以窗口外框左上角为原点（与 CaptureWindow 截图同源，直接给 OCR 相对坐标即可），
    /// 内部换算为客户区坐标（WM_LBUTTONDOWN 的 lParam 语义是客户区坐标）。
    /// 返回值仅表示消息投递成功；若目标使用自绘控件（DirectUI/游戏引擎）不处理该消息，
    /// 点击会静默失效 —— 可退化为"临时置顶窗口 → SendInput → 还原"。
    /// </summary>
    public static bool ClickWindow(IntPtr hwnd, int relX, int relY)
    {
        if (hwnd == IntPtr.Zero) return false;
        // 前台模式（天学网客户端为 Chromium 自绘窗口，PostMessage 对其不可靠）：
        // 窗口外框原点相对坐标 → 屏幕坐标 → SendInput 真实点击（Chromium/Electron 必响应）。
        // 自动回答启动时已 Activate 目标窗口，用户做题时客户端本就在前台，因此前台点击成立。
        if (GetWindowRect(hwnd, out var wr))
            return ClickAt(wr.Left + relX, wr.Top + relY);

        // 兜底：PostMessage（窗口相对坐标；被遮挡时仍可投递，但对自绘窗口可能无效）
        var p = new POINT { X = 0, Y = 0 };
        if (ClientToScreen(hwnd, ref p) && GetWindowRect(hwnd, out var wr2))
        {
            relX -= p.X - wr2.Left;
            relY -= p.Y - wr2.Top;
        }
        // lParam：低 16 位 = x，高 16 位 = y（客户区坐标）
        IntPtr lp = (IntPtr)((relY << 16) | (relX & 0xFFFF));
        bool down = PostMessage(hwnd, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lp);
        bool up   = PostMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, lp);
        return down && up;
    }

    /// <summary>
    /// 向窗口投递滚轮消息。deltaLines 负 = 向下滚动。
    /// v2.1.20：天学网客户端为 Chromium 自绘窗口，不响应 PostMessage(WM_MOUSEWHEEL)（与点击同理，
    /// 记忆实测"PostMessage 对 Chromium 不可靠"），原实现导致"下滚页面操作不执行"。
    /// 改为前台 SendInput 滚轮：先把光标移到窗口中心（滚轮消息投递到光标下窗口），
    /// 再发送 MOUSEEVENTF_WHEEL —— 与 ClickAt 前台点击同机制，Chromium 必响应。
    /// 仅当前台滚轮失败（窗口不在前台等）时兜底 PostMessage。
    /// </summary>
    public static bool ScrollWindow(IntPtr hwnd, int deltaLines)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowRect(hwnd, out var r);
        int mx = (r.Left + r.Right) / 2, my = (r.Top + r.Bottom) / 2;
        int wheel = deltaLines * WHEEL_DELTA;

        // 方案一：前台 SendInput 滚轮（光标移入窗口后再滚，Chromium 必响应）
        if (MoveTo(mx, my))
        {
            var inputs = new INPUT[1];
            inputs[0].type = INPUT_MOUSE;
            inputs[0].u.mi.dx = 0; inputs[0].u.mi.dy = 0;
            inputs[0].u.mi.mouseData = (uint)wheel;
            inputs[0].u.mi.dwFlags = MOUSEEVENTF_WHEEL;
            if (SendInput(1, inputs, Marshal.SizeOf<INPUT>()) == 1) return true;
        }

        // 方案二（兜底）：PostMessage 窗口相对坐标滚轮（被遮挡时仍可投递，但对自绘窗口可能无效）
        uint wp = ((uint)wheel) << 16;
        IntPtr lp = (IntPtr)((my << 16) | (mx & 0xFFFF));
        return PostMessage(hwnd, WM_MOUSEWHEEL, (IntPtr)(int)wp, lp);
    }

    public const int SW_RESTORE = 9;
    public const int SW_SHOW = 5;

    // ---- SendInput（比 mouse_event 更现代可靠）----
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public HARDWAREINPUT hi; }
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public INPUTUNION u; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    public static bool MoveTo(int x, int y)
    {
        // 绝对坐标：需要把像素坐标映射到 0..65535
        int sw = System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Width;
        int sh = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height;
        var inputs = new INPUT[1];
        inputs[0].type = INPUT_MOUSE;
        inputs[0].u.mi.dx = (int)Math.Round(x * 65535.0 / sw);
        inputs[0].u.mi.dy = (int)Math.Round(y * 65535.0 / sh);
        inputs[0].u.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE;
        return SendInput(1, inputs, Marshal.SizeOf<INPUT>()) == 1;
    }

    /// <summary>在屏幕坐标 (x,y) 处点击一次（绝对坐标，支持高分屏）。</summary>
    public static bool ClickAt(int x, int y)
    {
        int sw = System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Width;
        int sh = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height;
        int ax = (int)Math.Round(x * 65535.0 / sw);
        int ay = (int)Math.Round(y * 65535.0 / sh);

        var inputs = new INPUT[3];
        inputs[0].type = INPUT_MOUSE;
        inputs[0].u.mi.dx = ax; inputs[0].u.mi.dy = ay;
        inputs[0].u.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE;

        inputs[1].type = INPUT_MOUSE;
        inputs[1].u.mi.dx = ax; inputs[1].u.mi.dy = ay;
        inputs[1].u.mi.dwFlags = MOUSEEVENTF_LEFTDOWN;

        inputs[2].type = INPUT_MOUSE;
        inputs[2].u.mi.dx = ax; inputs[2].u.mi.dy = ay;
        inputs[2].u.mi.dwFlags = MOUSEEVENTF_LEFTUP;

        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == (uint)inputs.Length;
    }

    /// <summary>枚举可见顶层窗口（含进程名，便于按"天学网客户端"等进程名精确定位）。</summary>
    public static List<WinInfo> EnumTopLevelWindows()
    {
        var list = new List<WinInfo>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;
            GetWindowThreadProcessId(hWnd, out uint pid);
            string pname = "";
            try { pname = Process.GetProcessById((int)pid).ProcessName; } catch { }
            list.Add(new WinInfo(hWnd, title, (int)pid, pname));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>在候选窗口列表中取屏幕面积最大者（同名多窗口时选主窗口——客户端常有弹窗/悬浮窗与主窗口同标题）。</summary>
    public static WinInfo? LargestWindow(IEnumerable<WinInfo> list)
    {
        WinInfo? best = null; long max = 0;
        foreach (var w in list)
        {
            if (GetWindowRect(w.Hwnd, out var r))
            {
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > max) { max = area; best = w; }
            }
        }
        return best;
    }

    /// <summary>查找窗口：优先按进程名，其次按标题关键字。</summary>
    public static WinInfo? FindWindow(string? processName, string? title)
    {
        var all = EnumTopLevelWindows();
        if (!string.IsNullOrWhiteSpace(processName))
        {
            var hit = all.FirstOrDefault(w => w.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        if (!string.IsNullOrWhiteSpace(title))
            return all.FirstOrDefault(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    /// <summary>把窗口激活到前台（先还原最小化）。</summary>
    public static bool Activate(IntPtr hwnd)
    {
        try
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            else ShowWindow(hwnd, SW_SHOW);
            return SetForegroundWindow(hwnd);
        }
        catch { return false; }
    }

    /// <summary>
    /// 后台窗口截图（PW_RENDERFULLCONTENT）：即使窗口被遮挡也能截到完整内容。
    /// 返回位图的原点 = 窗口外框左上角，因此屏幕坐标 = rect.Left/Top + OCR 相对坐标。
    /// </summary>
    public static Bitmap? CaptureWindow(IntPtr hwnd, out RECT rect)
    {
        rect = default;
        if (hwnd == IntPtr.Zero) return null;
        GetWindowRect(hwnd, out rect);
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) return null;

        // 方案一：后台 PrintWindow（普通 Win32 窗口有效，被遮挡也能截）
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            bool ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
            g.ReleaseHdc(hdc);
            if (ok) return bmp;
            bmp.Dispose();
        }

        // 方案二：PrintWindow 失败（Chromium/GPU 自绘窗口，如天学网客户端——实测三种 flags 均黑屏）
        // → 激活窗口到前台 + CopyFromScreen 屏幕截图。返回位图保持"窗口外框原点相对"坐标语义
        //   （屏幕外部分留黑边，与 PrintWindow 路径一致），AutoEngine 的 rel 坐标与点击换算零改动。
        try
        {
            Activate(hwnd);
            System.Threading.Thread.Sleep(150);   // 等激活/渲染完成
            GetWindowRect(hwnd, out rect);        // 激活后 rect 可能变化，重取
            w = rect.Right - rect.Left; h = rect.Bottom - rect.Top;
            if (w <= 0 || h <= 0) return null;

            int offX = Math.Max(0, -rect.Left), offY = Math.Max(0, -rect.Top);   // 黑边偏移（最大化窗口 -7 边框）
            int srcW = Math.Min(w - offX, Screen.PrimaryScreen!.Bounds.Width - Math.Max(0, rect.Left));
            int srcH = Math.Min(h - offY, Screen.PrimaryScreen.Bounds.Height - Math.Max(0, rect.Top));
            srcW = Math.Max(1, srcW); srcH = Math.Max(1, srcH);

            var fg = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(fg))
                g.CopyFromScreen(Math.Max(0, rect.Left), Math.Max(0, rect.Top),
                                 offX, offY, new Size(srcW, srcH), CopyPixelOperation.SourceCopy);
            return fg;
        }
        catch { return null; }
    }
}
