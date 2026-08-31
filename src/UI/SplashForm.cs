using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace TxwExtract.UI;

/// <summary>
/// 开屏动画（单层自绘表面，独立 STA 线程运行，见 Program.Main）：
/// 纯白起幕 → 商标回弹跳出并居中（白对勾渐进描画 + 落定涟漪）→ 呼吸停留（三点律动）
/// → 沿贝塞尔弧线飞向左上角（webui header 中 logo 的落点，含残影与轻微倾斜）→ 落定涟漪、背景渐变为品牌底色，
/// 待页面首帧确认后整体淡出——落点与 webui logo 完全重合，淡出即"无缝交接"。
/// 设计要点（踩过的坑）：
/// 1. 尺寸与主窗体一致并四周各多留 4px，完整覆盖主窗体（含边框/阴影边缘）；
/// 2. 必须 TopMost —— 主窗体（含 WebView2 Chromium 子窗口）创建后会成为活动窗口；
/// 3. 必须运行在独立 STA 线程，WebView2 初始化阻塞主 UI 线程时动画也不冻结；
/// 4. 商标为纯图形（渐变圆角方块 + 白对勾，与 app.ico / webui header 一致），全程不出现文字；
/// 5. 飞行落点由 MainForm.ClientScreenRect（客户区屏幕矩形）换算，自动适配 DPI 与窗口实际位置；
/// 6. 撤下时机：MainForm 用 CapturePreview 轮询确认页面已真实绘制后，由 Program 调 FadeOut 淡出。
/// </summary>
public sealed class SplashForm : Form
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 8 };
    private readonly DateTime _t0 = DateTime.Now;
    private bool _fadingOut;
    private bool _flyInit;
    private PointF _flyStart, _flyCtrl, _flyEnd;

    // ---- 编排时间轴（ms）----
    private const int TBounce0 = 160;   // 纯白静默结束，商标开始跳出
    private const int TBounce1 = 840;   // 跳出完成（回弹 + 对勾描画 + 涟漪）
    private const int TFly0 = 1480;     // 居中停留结束，起飞
    private const int TFly1 = 2160;     // 抵达左上角
    private const int TLand0 = 2160;    // 落定：涟漪 + 背景白→品牌底色

    // 品牌色（与前端 CSS 变量一致；Cbg 对应 --bg，落定背景须与 webui 同色才能无缝淡出）
    private static readonly Color Cbg = Color.FromArgb(0xf0, 0xf3, 0xf9);
    private static readonly Color Cblue = Color.FromArgb(0x3a, 0x63, 0xf3);
    private static readonly Color Cblue2 = Color.FromArgb(0x6b, 0x8b, 0xff);

    public SplashForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 840);
        Size = new Size(Math.Min(1288, wa.Width), Math.Min(848, wa.Height));
        Text = "天学网答案提取 - 正在启动";
        BackColor = Color.White;
        ShowInTaskbar = false;
        TopMost = true;
        Opacity = 0;                    // 入场淡入从 0 开始（背景纯白，视觉无感）
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = new Font("Microsoft YaHei UI", 9f);

        Paint += OnPaintSplash;
        _timer.Tick += OnTick;
        _timer.Start();

        State("SplashForm ctor size=" + Size.Width + "x" + Size.Height);
        Load += (_, _) => State("SplashForm Load");
        timeBeginPeriod(1);   // 提升系统定时器精度到 1ms：否则 WM_TIMER 粒度 ~15.6ms，8ms 间隔实际只有 ~64fps，快速移动会拖影
    }

    [DllImport("winmm.dll")] private static extern int timeBeginPeriod(int ms);
    [DllImport("winmm.dll")] private static extern int timeEndPeriod(int ms);

    private void OnTick(object? sender, EventArgs e)
    {
        if (_fadingOut)
        {
            // 交叉淡出：线性降到 0 后关闭
            Opacity -= 0.07;
            if (Opacity <= 0) { try { _timer.Stop(); Close(); } catch { } return; }
        }
        else if (Opacity < 1)
        {
            Opacity = Math.Min(1, Opacity + 0.09);
        }
        Invalidate();   // 全程整面重绘（双缓冲，30ms 一次开销可控）
    }

    /// <summary>交叉淡出并关闭（由 Program 在页面真实绘制完成后调用）。</summary>
    public void FadeOut()
    {
        if (_fadingOut) return;
        _fadingOut = true;
    }

    private void OnPaintSplash(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        double ms = (DateTime.Now - _t0).TotalMilliseconds;
        float k = DeviceDpi / 96f;      // DPI 缩放系数（本窗体与主窗体同进程同屏，比例一致）

        // ---- 背景：纯白 →（落定时 400ms 内）→ 品牌底色，淡出时与 webui 同底色无缝衔接 ----
        g.Clear(LerpColor(Color.White, Cbg, EaseInOutCubic(Clamp01((ms - TLand0) / 400.0))));

        int cx = ClientSize.Width / 2, cy = ClientSize.Height / 2;
        float sMax = 84f * k;           // 居中阶段商标边长（≈ webui 的 2.6 倍，视觉主角）
        float sEnd = 32f * k;           // webui header 中 logo 的 CSS 尺寸 32px

        // 起飞前一刻计算飞行轨迹（落点需主窗体已就位）
        if (ms >= TFly0 && !_flyInit) InitFly(new PointF(cx, cy));

        // ---- 商标状态机：跳出 → 停留 → 飞行 → 落定 ----
        PointF logoC = default;
        float logoS = 0, rot = 0;
        int alpha = 0;
        double checkP = 1;              // 对勾描画进度

        if (ms < TBounce1)
        {
            // 跳出：easeOutBack 回弹缩放 + 对勾渐进描画
            double p = Clamp01((ms - TBounce0) / (TBounce1 - TBounce0));
            logoS = sMax * (float)Math.Max(0, EaseOutBack(p));
            logoC = new PointF(cx, cy);
            alpha = Clamp255((ms - TBounce0) / 200.0);
            checkP = EaseOutCubic(Clamp01((ms - TBounce0 - 240) / 420.0));
        }
        else if (ms < TFly0)
        {
            // 居中停留：轻微呼吸
            double breathe = 1 + 0.018 * Math.Sin((ms - TBounce1) * 2 * Math.PI / 1400);
            logoS = sMax * (float)breathe;
            logoC = new PointF(cx, cy);
            alpha = 255;
        }
        else if (ms < TFly1)
        {
            // 飞行：贝塞尔弧线 + 缩小 + 轻微倾斜 + 彗星尾
            double p = Clamp01((ms - TFly0) / (TFly1 - TFly0));
            double eFly = EaseInOutCubic(p);
            logoC = Bezier(_flyStart, _flyCtrl, _flyEnd, eFly);
            logoS = Lerp(sMax, sEnd, (float)eFly);
            alpha = 255;
            rot = (float)(-6 * Math.Sin(Math.PI * p));   // 途中轻倾，落定回正

            // 彗星尾拖影：沿轨迹密集取样历史位置，尺寸与主商标一致、透明度指数衰减。
            // 密集重叠 → 连续的运动模糊光尾（而非多个图标的离散重影）；
            // 起步时 pi<=0 的样本自然跳过（尾巴从无到有），落定时样本收敛于终点、尾巴自然收拢消失。
            const int n = 12;
            const double dp = 0.016;          // 每级回溯的轨迹参数（飞行 680ms ≈ 每级 11ms）
            for (int i = n; i >= 1; i--)      // 越旧越先画，压在主商标下面
            {
                double pi = p - dp * i;
                if (pi <= 0) continue;
                var gc = Bezier(_flyStart, _flyCtrl, _flyEnd, EaseInOutCubic(pi));
                int ga = (int)(120 * Math.Exp(-i * 0.28));
                float gr = (float)(-6 * Math.Sin(Math.PI * pi));
                DrawLogo(g, gc, logoS, ga, gr, 1, sEnd);
            }
        }
        else
        {
            logoC = _flyEnd;
            logoS = sEnd;
            alpha = 255;
        }

        DrawLogo(g, logoC, logoS, alpha, rot, checkP, sEnd);

        // ---- 落定涟漪（居中跳出结束时一次、抵达左上角时一次）----
        Ripple(g, new PointF(cx, cy), TBounce1, ms, sMax / 2);
        if (_flyInit) Ripple(g, _flyEnd, TLand0, ms, sEnd / 2);

        // ---- 停留期：商标下方三点律动（起飞时快速淡出）----
        if (ms >= TBounce1 && ms < TFly1)
        {
            int dA = ms < TFly0 ? 255 : Math.Max(0, (int)(255 * (1 - (ms - TFly0) / 160.0)));
            if (dA > 0)
            {
                float dy = cy + sMax / 2 + 34 * k;
                for (int i = 0; i < 3; i++)
                {
                    double u = (ms / 520.0 + i * 0.16) % 1.0;
                    float lift = (float)Math.Abs(Math.Sin(u * Math.PI)) * 8 * k;
                    int a2 = Math.Min(255, (int)(dA * (0.45 + 0.55 * Math.Abs(Math.Sin(u * Math.PI)))));
                    using var b = new SolidBrush(Color.FromArgb(a2, Cblue));
                    g.FillEllipse(b, cx - 24 * k + i * 18 * k - 3.5f * k, dy - lift - 3.5f * k, 7 * k, 7 * k);
                }
            }
        }

        // ---- 落定后：商标右侧三点脉冲（加载中暗示，淡出交接前保持）----
        if (_flyInit && ms >= TLand0)
        {
            int pA = Math.Min(255, (int)(255 * (ms - TLand0) / 300.0));
            float dx0 = _flyEnd.X + sEnd / 2 + 16 * k;
            for (int i = 0; i < 3; i++)
            {
                double u = (ms / 520.0 + i * 0.16) % 1.0;
                int a2 = Math.Min(255, (int)(pA * (0.35 + 0.65 * Math.Abs(Math.Sin(u * Math.PI)))));
                using var b = new SolidBrush(Color.FromArgb(a2, Cblue));
                g.FillEllipse(b, dx0 + i * 10 * k - 3 * k, _flyEnd.Y - 3 * k, 6 * k, 6 * k);
            }
        }
    }

    /// <summary>绘制商标：渐变圆角方块 + 白对勾（可部分描画），居中阶段带柔和投影。纯图形，无文字。</summary>
    private void DrawLogo(Graphics g, PointF c, float size, int alpha, float rotDeg, double checkP, float endSize)
    {
        if (alpha <= 0 || size <= 1) return;
        var state = g.Save();
        g.TranslateTransform(c.X, c.Y);
        if (Math.Abs(rotDeg) > 0.01f) g.RotateTransform(rotDeg);

        var r = new RectangleF(-size / 2, -size / 2, size, size);
        float rad = size * 9f / 32f;    // webui：32px 圆角 9px，等比放大

        // 居中阶段的柔和投影
        if (size > endSize * 1.15f)
        {
            var rs = new RectangleF(r.Left, r.Top + size * 0.05f, size, size);
            using (var path = RoundedRectF(rs, rad))
            using (var b = new SolidBrush(Color.FromArgb(alpha * 26 / 255, 15, 23, 42)))
                g.FillPath(b, path);
        }

        using (var path = RoundedRectF(r, rad))
        // GDI 角度顺时针从 +x 起：45° 指向右下 = CSS linear-gradient(135deg)，保证与 webui 商标渐变方向一致
        using (var b = new LinearGradientBrush(r, Color.FromArgb(alpha, Cblue), Color.FromArgb(alpha, Cblue2), 45f))
            g.FillPath(b, path);

        // 对勾：三点折线按进度描画（webui SVG path 等比：M27 53 L44 70 L76 33，比例 0.135）
        var A = P(r, 0.27f, 0.53f);
        var B = P(r, 0.44f, 0.70f);
        var C = P(r, 0.76f, 0.33f);
        double lab = Dist(A, B), lbc = Dist(B, C);
        double want = Clamp01(checkP) * (lab + lbc);
        using (var pen = new Pen(Color.FromArgb(alpha, System.Drawing.Color.White), size * 0.135f)
                   { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            if (want > 0) g.DrawLine(pen, A, want >= lab ? B : LerpP(A, B, want / lab));
            if (want > lab) g.DrawLine(pen, B, LerpP(B, C, (want - lab) / lbc));
        }
        g.Restore(state);
    }

    // ================= 飞行轨迹与落点 =================

    /// <summary>起飞时刻计算：起点=当前屏幕中心，终点=主窗体客户区内 webui logo 中心，控制点在上方形成弧线。</summary>
    private void InitFly(PointF start)
    {
        _flyInit = true;
        _flyStart = start;
        _flyEnd = FlyTarget();
        float dx = _flyEnd.X - _flyStart.X, dy = _flyEnd.Y - _flyStart.Y;
        _flyCtrl = new PointF(_flyStart.X + dx * 0.55f,
                              Math.Min(_flyStart.Y, _flyEnd.Y) - Math.Abs(dy) * 0.18f - 40);
    }

    /// <summary>落点：主窗体客户区左上角 + webui header 内 logo 中心偏移（CSS：padding 22 + 16，header 高 58 之半）。</summary>
    private PointF FlyTarget()
    {
        float k = DeviceDpi / 96f;
        int ox = (int)Math.Round(38 * k), oy = (int)Math.Round(29 * k);
        var cr = MainForm.ClientScreenRect;             // 主窗体客户区的屏幕矩形（跨线程只读缓存）
        if (cr.Width > 0)
        {
            var p = PointToClient(new Point(cr.Left + ox, cr.Top + oy));
            return new PointF(p.X, p.Y);
        }
        // 兜底估算：主窗体居中 1280×840 + 标准边框/标题栏偏移
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 840);
        int mw = Math.Min(1280, wa.Width), mh = Math.Min(840, wa.Height);
        int mx = wa.Left + (wa.Width - mw) / 2, my = wa.Top + (wa.Height - mh) / 2;
        return new PointF(mx + 9 * k + ox, my + 31 * k + oy);
    }

    private static PointF Bezier(PointF s, PointF c, PointF e, double t)
    {
        double u = 1 - t;
        return new PointF(
            (float)(u * u * s.X + 2 * u * t * c.X + t * t * e.X),
            (float)(u * u * s.Y + 2 * u * t * c.Y + t * t * e.Y));
    }

    private static void Ripple(Graphics g, PointF c, double start, double now, float r0)
    {
        double u = (now - start) / 480.0;
        if (u < 0 || u > 1) return;
        float r = r0 * (float)(1 + 0.9 * EaseOutCubic(u));
        using var pen = new Pen(Color.FromArgb((int)(130 * (1 - u)), Cblue), 2f);
        g.DrawEllipse(pen, c.X - r, c.Y - r, r * 2, r * 2);
    }

    // ================= 基础工具 =================

    private static PointF P(RectangleF r, float ux, float uy) => new(r.Left + ux * r.Width, r.Top + uy * r.Height);
    private static double Dist(PointF a, PointF b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static PointF LerpP(PointF a, PointF b, double t) => new((float)(a.X + (b.X - a.X) * t), (float)(a.Y + (b.Y - a.Y) * t));
    private static float Lerp(float a, float b, double t) => (float)(a + (b - a) * t);
    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    private static int Clamp255(double v) => v < 0 ? 0 : v > 1 ? 255 : (int)(255 * v);
    private static Color LerpColor(Color a, Color b, double t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    private static double EaseOutCubic(double p) => 1 - Math.Pow(1 - Clamp01(p), 3);
    private static double EaseInOutCubic(double p)
    {
        p = Clamp01(p);
        return p < 0.5 ? 4 * p * p * p : 1 - Math.Pow(-2 * p + 2, 3) / 2;
    }
    private static double EaseOutBack(double p)
    {
        p = Clamp01(p);
        const double c1 = 1.70158, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(p - 1, 3) + c1 * Math.Pow(p - 1, 2);
    }

    private static GraphicsPath RoundedRectF(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        if (radius <= 0) { p.AddRectangle(r); return p; }
        float d = radius * 2;
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static void State(string msg)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TxwExtract");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "startup_state.txt"), $"[{DateTime.Now:HH:mm:ss}] {msg}\r\n");
        }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { _timer.Stop(); _timer.Dispose(); } catch { } try { timeEndPeriod(1); } catch { } }
        base.Dispose(disposing);
    }
}
