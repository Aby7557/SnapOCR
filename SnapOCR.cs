// SnapOCR —— 微信式截图 + 提取文字(OCR) + 翻译
// 零依赖单文件 C# 应用：使用系统自带 .NET Framework 4.8 编译（build.cmd），无需安装任何运行时。
//   截图   : GDI CopyFromScreen（DPI 感知，支持多显示器）
//   OCR    : Windows 10 内置 Windows.Media.Ocr（离线）
//   翻译   : cn.bing.com 网页版流程（免费、无需密钥）
//   热键   : Alt+A 全局热键，托盘常驻

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

[assembly: AssemblyTitle("SnapOCR")]
[assembly: AssemblyDescription("微信式截图 · 标注 · 离线 OCR · 翻译")]
[assembly: AssemblyProduct("SnapOCR")]
[assembly: AssemblyCompany("Ty")]
[assembly: AssemblyCopyright("Copyright (C) 2026 Ty")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

static partial class Program
{
    const string CFG_KEY = @"Software\SnapOCR";

    public static ToolbarStyle LoadToolbarStyle()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(CFG_KEY))
            {
                if (k != null)
                {
                    int v = Convert.ToInt32(k.GetValue("ToolbarStyle", 0), CultureInfo.InvariantCulture);
                    if (v >= 0 && v <= 2) return (ToolbarStyle)v;
                }
            }
        }
        catch { }
        return ToolbarStyle.IconText;
    }

    public static void SaveToolbarStyle(ToolbarStyle s)
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(CFG_KEY))
            {
                k.SetValue("ToolbarStyle", (int)s, RegistryValueKind.DWord);
            }
        }
        catch { }
    }

    public static bool LoadAvoidEnabled()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(CFG_KEY))
            {
                if (k != null)
                {
                    int v = Convert.ToInt32(k.GetValue("AvoidWeChatHotkey", 1), CultureInfo.InvariantCulture);
                    return v != 0;
                }
            }
        }
        catch { }
        return true;
    }

    public static void SaveAvoidEnabled(bool enabled)
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(CFG_KEY))
            {
                k.SetValue("AvoidWeChatHotkey", enabled ? 1 : 0, RegistryValueKind.DWord);
            }
        }
        catch { }
    }

    public static void Log(string msg)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "SnapOCR_app.log"),
                DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + msg + "\r\n");
        }
        catch { }
    }

    [STAThread]
    static void Main(string[] args)
    {
        // TLS 必须最先设置（自检模式也会用到网络）
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
        Native.SetProcessDPIAware();

        if (args != null && args.Length > 0)
        {
            string a0 = args[0].TrimStart('-', '/').ToLowerInvariant();
            if (a0 == "selftest")
            {
                Environment.ExitCode = SelfTest.Run();
                return;
            }
        }

        bool createdNew = false;
        using (Mutex mutex = new Mutex(true, "SnapOCR_SingleInstance_{6F9A2C1D}", out createdNew))
        {
            if (!createdNew)
            {
                MessageBox.Show("SnapOCR 已经在运行（请查看任务栏右下角托盘图标）。\n\n按 Alt+A 即可开始截图。",
                    "SnapOCR", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new HotkeyContext());
        }
    }

    /// <summary>抓取整个虚拟屏幕（物理像素，多显示器）。</summary>
    public static Bitmap CaptureScreen()
    {
        Rectangle vs = SystemInformation.VirtualScreen;
        Bitmap bmp = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(vs.X, vs.Y, 0, 0, new Size(vs.Width, vs.Height), CopyPixelOperation.SourceCopy);
        }
        return bmp;
    }

    /// <summary>不依赖窗口句柄获取 DPI 缩放系数。</summary>
    public static float UiScale()
    {
        using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
        {
            float dpi = g.DpiX;
            if (dpi < 96f) dpi = 96f;
            return dpi / 96f;
        }
    }
}

// ============================== 自检模式 ==============================

static class SelfTest
{
    public static int Run()
    {
        string logPath = Path.Combine(Path.GetTempPath(), "SnapOCR_selftest.log");
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("SnapOCR 自检报告  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        bool[] ok = new bool[4];
        Task.Run(delegate
        {
            // [1] OCR 语言
            try
            {
                List<string> langs = new List<string>();
                foreach (Language l in OcrEngine.AvailableRecognizerLanguages) langs.Add(l.LanguageTag);
                sb.AppendLine("[1] OCR 可用语言: " + (langs.Count > 0 ? string.Join(", ", langs.ToArray()) : "(无)"));
                ok[0] = langs.Count > 0;
            }
            catch (Exception ex) { ok[0] = false; sb.AppendLine("[1] OCR 语言枚举失败: " + ex.Message); }

            // [2] OCR 识别（常规大图）
            try
            {
                using (Bitmap bmp = RenderTestImage())
                {
                    string text = OcrHelper.Recognize(bmp);
                    if (text == null)
                    {
                        ok[1] = false;
                        sb.AppendLine("[2] OCR 大图识别: 失败 - " + OcrHelper.LastError);
                    }
                    else
                    {
                        string flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
                        bool pass = flat.Replace(" ", "").Length >= 8;
                        ok[1] = pass;
                        sb.AppendLine("[2] OCR 大图识别: \"" + flat + "\"  ->  " + (pass ? "PASS" : "结果过弱"));
                    }
                }
            }
            catch (Exception ex) { ok[1] = false; sb.AppendLine("[2] OCR 大图识别异常: " + ex); }

            // [2b] 小字条识别（模拟用户框选一行小字）
            try
            {
                using (Bitmap small = RenderTestImageSmall())
                {
                    string text = OcrHelper.Recognize(small);
                    if (text == null)
                    {
                        ok[3] = false;
                        sb.AppendLine("[3] OCR 小字条(28px): 失败 - " + OcrHelper.LastError);
                    }
                    else
                    {
                        string flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
                        bool pass = flat.Replace(" ", "").Length >= 6;
                        ok[3] = pass;
                        sb.AppendLine("[3] OCR 小字条(28px): \"" + flat + "\"  ->  " + (pass ? "PASS" : "结果过弱"));
                    }
                }
            }
            catch (Exception ex) { ok[3] = false; sb.AppendLine("[3] OCR 小字条异常: " + ex); }

            // [4] 翻译
            try
            {
                TransResult r = TranslateHelper.TranslateAsync("Hello world, this is a screenshot selftest.", "zh-Hans").GetAwaiter().GetResult();
                bool pass = r != null && !string.IsNullOrEmpty(r.Text);
                ok[2] = pass;
                sb.AppendLine("[4] Bing 翻译(en->zh): \"" + (r == null ? "" : r.Text) + "\"  ->  " + (pass ? "PASS" : "FAIL"));
            }
            catch (Exception ex) { ok[2] = false; sb.AppendLine("[4] 翻译失败: " + ex.ToString()); }

            sb.AppendLine(ok[0] && ok[1] && ok[2] && ok[3] ? "结果: 全部通过" : "结果: 存在失败项");
            try { File.WriteAllText(logPath, sb.ToString(), Encoding.UTF8); } catch { }
        }).GetAwaiter().GetResult();

        try { File.WriteAllText(logPath, sb.ToString(), Encoding.UTF8); } catch { }
        return (ok[0] && ok[1] && ok[2] && ok[3]) ? 0 : 2;
    }

    static Bitmap RenderTestImage()
    {
        Bitmap bmp = new Bitmap(780, 190, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (Font f = new Font("Microsoft YaHei UI", 44f, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                g.DrawString("SnapOCR 截图测试 Hello 123", f, Brushes.Black, 14, 50);
            }
        }
        return bmp;
    }

    static Bitmap RenderTestImageSmall()
    {
        Bitmap bmp = new Bitmap(700, 30, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (Font f = new Font("Microsoft YaHei UI", 17f, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                g.DrawString("SnapOCR 小字识别测试 ABC 123", f, Brushes.Black, 8, 5);
            }
        }
        return bmp;
    }
}

// ============================== Win32 ==============================

static class Native
{
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_NOREPEAT = 0x4000;
    public const int WM_HOTKEY = 0x0312;
    public const int HOTKEY_ID = 1;
}

// ============================== 托盘图标 ==============================

static class AppIcon
{
    static Icon _icon;
    public static Icon Get()
    {
        if (_icon == null)
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(0, 122, 204)))
                using (GraphicsPath p = new GraphicsPath())
                {
                    p.AddArc(1, 1, 10, 10, 180, 90);
                    p.AddArc(21, 1, 10, 10, 270, 90);
                    p.AddArc(21, 21, 10, 10, 0, 90);
                    p.AddArc(1, 21, 10, 10, 90, 90);
                    p.CloseFigure();
                    g.FillPath(b, p);
                }
                using (Font f = new Font("Microsoft YaHei UI", 16f, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    g.DrawString("截", f, Brushes.White, 3f, 5f);
                }
            }
            _icon = Icon.FromHandle(bmp.GetHicon());
        }
        return _icon;
    }
}

// ============================== 托盘上下文（主入口） ==============================

class HotkeyContext : ApplicationContext
{
    const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RUN_VALUE = "SnapOCR";

    NotifyIcon _tray;
    HotkeyWindow _win;
    OverlayForm _overlay;
    Icon _icon;
    ToolStripMenuItem _miStIconText;
    ToolStripMenuItem _miStIconOnly;
    ToolStripMenuItem _miStTextOnly;
    System.Windows.Forms.Timer _wxTimer;
    bool _wxActive;          // 微信当前是否在运行
    bool _hotkeyRegistered;  // Alt+A 热键当前是否已注册
    bool _avoidEnabled;      // 微信热键避让开关

    public HotkeyContext()
    {
        _icon = AppIcon.Get();

        _win = new HotkeyWindow(this);
        IntPtr h = _win.Handle; // 强制创建句柄
        bool hotOk = _win.RegisterHotkey();
        _hotkeyRegistered = hotOk;
        Program.Log("startup hotkey=" + hotOk);

        // 微信热键避让：轮询检测微信进程，运行则暂停 Alt+A，退出则恢复（可在托盘菜单关闭）
        _avoidEnabled = Program.LoadAvoidEnabled();
        if (_avoidEnabled)
        {
            _wxActive = WeChatRunning();
            if (_wxActive) DisableHotkey(true);
            StartWxTimer();
        }

        ToolStripMenuItem miCap = new ToolStripMenuItem("立即截图", null, delegate { StartCapture(); });
        miCap.ShortcutKeyDisplayString = "Alt+A";
        miCap.Font = new Font(miCap.Font, FontStyle.Bold);

        ToolStripMenuItem miAuto = new ToolStripMenuItem("开机自动启动", null, OnToggleAutoStart);
        miAuto.CheckOnClick = true;
        miAuto.Checked = AutoStartEnabled();

        ToolStripMenuItem miAvoid = new ToolStripMenuItem("微信运行时自动让出 Alt+A", null, OnToggleAvoid);
        miAvoid.CheckOnClick = true;
        miAvoid.Checked = _avoidEnabled;

        // 工具条显示样式（互斥勾选，持久化到注册表）
        _miStIconText = new ToolStripMenuItem("图标 + 文字", null, delegate { SetToolbarStyle(ToolbarStyle.IconText); });
        _miStIconOnly = new ToolStripMenuItem("仅图标", null, delegate { SetToolbarStyle(ToolbarStyle.IconOnly); });
        _miStTextOnly = new ToolStripMenuItem("仅文字", null, delegate { SetToolbarStyle(ToolbarStyle.TextOnly); });
        RefreshStyleChecks(Program.LoadToolbarStyle());
        ToolStripMenuItem miStyle = new ToolStripMenuItem("工具条显示");
        miStyle.DropDownItems.AddRange(new ToolStripItem[] { _miStIconText, _miStIconOnly, _miStTextOnly });

        ToolStripMenuItem miAbout = new ToolStripMenuItem("使用说明", null, delegate
        {
            using (AboutForm f = new AboutForm())
            {
                f.ShowDialog();
            }
        });

        ToolStripMenuItem miExit = new ToolStripMenuItem("退出", null, delegate { ExitApp(); });

        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[] {
            miCap, new ToolStripSeparator(), miAuto, miAvoid, miStyle, miAbout, new ToolStripSeparator(), miExit
        });

        _tray = new NotifyIcon();
        _tray.Icon = _icon;
        _tray.Text = "SnapOCR - 按 Alt+A 截图";
        _tray.Visible = true;
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += delegate { StartCapture(); };

        if (!hotOk)
        {
            _tray.ShowBalloonTip(4000, "SnapOCR",
                "全局热键 Alt+A 注册失败（可能被其他程序占用），仍可通过托盘菜单或双击图标截图。",
                ToolTipIcon.Warning);
        }
    }

    /// <summary>微信 PC 版是否在运行（新版 Weixin.exe / 旧版 WeChat.exe）。</summary>
    static bool WeChatRunning()
    {
        try
        {
            return Process.GetProcessesByName("Weixin").Length > 0
                || Process.GetProcessesByName("WeChat").Length > 0;
        }
        catch { return false; }
    }

    void StartWxTimer()
    {
        if (_wxTimer == null)
        {
            _wxTimer = new System.Windows.Forms.Timer();
            _wxTimer.Interval = 3000;
            _wxTimer.Tick += delegate { CheckWeChat(); };
        }
        _wxTimer.Start();
    }

    void StopWxTimer()
    {
        if (_wxTimer != null) _wxTimer.Stop();
    }

    void OnToggleAvoid(object sender, EventArgs e)
    {
        ToolStripMenuItem item = (ToolStripMenuItem)sender;
        _avoidEnabled = item.Checked;
        Program.SaveAvoidEnabled(_avoidEnabled);
        Program.Log("avoid=" + _avoidEnabled);
        if (_avoidEnabled)
        {
            StartWxTimer();
            CheckWeChat();   // 立即对齐当前状态
        }
        else
        {
            StopWxTimer();
            if (_wxActive)
            {
                _wxActive = false;
                EnableHotkey(false);   // 尝试恢复；微信若在跑仍会失败并显示"被占用"
            }
        }
        if (_tray != null)
            _tray.ShowBalloonTip(2000, "SnapOCR", _avoidEnabled ? "已开启微信热键避让" : "已关闭微信热键避让", ToolTipIcon.Info);
    }

    void CheckWeChat()
    {
        bool running = WeChatRunning();
        if (running == _wxActive) return;
        _wxActive = running;
        if (running) DisableHotkey(false);
        else EnableHotkey(true);
    }

    void DisableHotkey(bool quiet)
    {
        Program.Log("hotkey disabled (WeChat running)");
        if (_tray != null)
        {
            _tray.Text = "SnapOCR - 微信运行中，热键已暂停（托盘菜单仍可截图）";
            if (!quiet && _hotkeyRegistered)
                _tray.ShowBalloonTip(3000, "SnapOCR", "检测到微信已运行，Alt+A 截图热键已自动让出；需要截图时用托盘菜单或双击图标。", ToolTipIcon.Info);
        }
        if (_hotkeyRegistered)
        {
            try { _win.UnregisterHotkey(); } catch { }
            _hotkeyRegistered = false;
        }
    }

    void EnableHotkey(bool balloon)
    {
        if (_hotkeyRegistered) return;
        try { _hotkeyRegistered = _win.RegisterHotkey(); } catch { _hotkeyRegistered = false; }
        Program.Log("hotkey restored=" + _hotkeyRegistered);
        if (_tray != null)
        {
            _tray.Text = _hotkeyRegistered ? "SnapOCR - 按 Alt+A 截图" : "SnapOCR - Alt+A 被占用";
            if (balloon && _hotkeyRegistered)
                _tray.ShowBalloonTip(3000, "SnapOCR", "微信已退出，Alt+A 截图热键已恢复。", ToolTipIcon.Info);
        }
    }

    public void StartCapture()
    {
        Program.Log("StartCapture enter");
        if (_overlay != null && !_overlay.IsDisposed)
        {
            Program.Log("StartCapture skip: overlay exists");
            return;
        }
        try
        {
            Bitmap shot = Program.CaptureScreen();
            _overlay = new OverlayForm(shot);
            _overlay.Show();
            Program.Log("StartCapture shown");
        }
        catch (Exception ex)
        {
            Program.Log("StartCapture ERROR: " + ex);
            MessageBox.Show("截屏失败：" + ex.Message, "SnapOCR", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void SetToolbarStyle(ToolbarStyle s)
    {
        Program.SaveToolbarStyle(s);
        RefreshStyleChecks(s);
    }

    void RefreshStyleChecks(ToolbarStyle s)
    {
        _miStIconText.Checked = (s == ToolbarStyle.IconText);
        _miStIconOnly.Checked = (s == ToolbarStyle.IconOnly);
        _miStTextOnly.Checked = (s == ToolbarStyle.TextOnly);
    }

    bool AutoStartEnabled()
    {
        using (RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RUN_KEY))
        {
            return k != null && k.GetValue(RUN_VALUE) != null;
        }
    }

    void OnToggleAutoStart(object sender, EventArgs e)
    {
        ToolStripMenuItem item = (ToolStripMenuItem)sender;
        try
        {
            if (item.Checked)
            {
                string target = EnsureStableCopy();
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
                {
                    k.SetValue(RUN_VALUE, "\"" + target + "\"");
                }
            }
            else
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
                {
                    k.DeleteValue(RUN_VALUE, false);
                }
            }
            if (_tray != null)
                _tray.ShowBalloonTip(2000, "SnapOCR", item.Checked ? "已开启开机自动启动" : "已关闭开机自动启动", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            item.Checked = AutoStartEnabled();
            MessageBox.Show("设置开机自启失败：" + ex.Message, "SnapOCR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>把 exe 复制到 %LOCALAPPDATA%\SnapOCR 作为自启路径（避免工作目录变动导致自启失效）。</summary>
    string EnsureStableCopy()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapOCR");
            string target = Path.Combine(dir, "SnapOCR.exe");
            string current = Application.ExecutablePath;
            if (!string.Equals(Path.GetFullPath(current), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(dir);
                File.Copy(current, target, true);
                return target;
            }
        }
        catch { }
        return Application.ExecutablePath;
    }

    void ExitApp()
    {
        if (_wxTimer != null) _wxTimer.Stop();
        _win.UnregisterHotkey();
        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }
}

// ============================== 热键窗口（不可见） ==============================

class HotkeyWindow : Form
{
    HotkeyContext _ctx;

    public HotkeyWindow(HotkeyContext ctx)
    {
        _ctx = ctx;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        Opacity = 0;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-3000, -3000);
        Size = new Size(1, 1);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x80;        // WS_EX_TOOLWINDOW（不出现在 Alt+Tab）
            cp.ExStyle |= 0x08000000;  // WS_EX_NOACTIVATE
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == Native.HOTKEY_ID)
        {
            Program.Log("WM_HOTKEY received");
            _ctx.StartCapture();
            return;
        }
        base.WndProc(ref m);
    }

    public bool RegisterHotkey()
    {
        return Native.RegisterHotKey(this.Handle, Native.HOTKEY_ID, Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)'A');
    }

    public void UnregisterHotkey()
    {
        try { Native.UnregisterHotKey(this.Handle, Native.HOTKEY_ID); } catch { }
    }
}

// ============================== 截图覆盖层（核心交互） ==============================

enum EditMode { None, Pen, Mosaic, Rect }

enum ToolbarStyle { IconText, IconOnly, TextOnly }

delegate void IconDrawer(Graphics g, Rectangle r, Color c);

/// <summary>工具条按钮（自绘）。</summary>
class ToolButton
{
    public string Text;
    public IconDrawer Icon;
    public Action OnClick;
    public EditMode ModeTag;
    public Rectangle Bounds;
    public Color IconColor = Color.WhiteSmoke;
}

/// <summary>一条标注笔迹（画笔 / 马赛克 / 矩形）。</summary>
class Stroke
{
    public bool IsRect;
    public bool IsMosaic;
    public List<Point> Pts;
    public Rectangle Rect;
}

class OverlayForm : Form
{
    Bitmap _shot;
    Bitmap _work;            // 工作图 = 原图 + 标注（裁剪/OCR 都来自它）
    Bitmap _mosaic;          // 像素化图（马赛克笔刷纹理）
    Rectangle _sel;          // 选区（窗体坐标 = 虚拟屏幕坐标）
    bool _dragging;
    Point _start;
    Point _lastMouse;
    Panel _toolbar;
    float _s;
    bool _busy;
    EditMode _mode;
    List<Stroke> _strokes;
    Stroke _cur;
    bool _drawing;
    Rectangle _rectCur;
    ToolbarStyle _tbStyle;
    List<ToolButton> _buttons;
    int _hoverIndex;

    public OverlayForm(Bitmap shot)
    {
        _shot = shot;
        _s = Program.UiScale();
        _work = (Bitmap)shot.Clone();
        _strokes = new List<Stroke>();
        _mode = EditMode.None;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Rectangle vs = SystemInformation.VirtualScreen;
        Bounds = new Rectangle(vs.X, vs.Y, vs.Width, vs.Height);
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        Cursor = Cursors.Default;   // 按用户要求：默认箭头，不用十字
        BackColor = Color.Black;
        AutoScaleMode = AutoScaleMode.None;

        BuildToolbar();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW（不进 Alt+Tab 列表）
            return cp;
        }
    }

    int S(int px) { return (int)Math.Round(px * _s); }

    // ---------- 工具条 ----------

    void BuildToolbar()
    {
        _tbStyle = Program.LoadToolbarStyle();
        _hoverIndex = -1;

        _toolbar = new Panel();
        _toolbar.BackColor = Color.FromArgb(45, 45, 45);
        _toolbar.Visible = false;
        _toolbar.Paint += OnToolbarPaint;
        _toolbar.MouseMove += OnToolbarMouseMove;
        _toolbar.MouseLeave += delegate { _hoverIndex = -1; _toolbar.Invalidate(); };
        _toolbar.MouseDown += OnToolbarMouseDown;

        _buttons = new List<ToolButton>();
        AddButton("OCR", IconDoc, delegate { RunOcr(); }, EditMode.None);
        AddButton("翻译", IconGlobe, delegate { RunTranslate(); }, EditMode.None);
        AddButton("画笔", IconPen, delegate { ToggleMode(EditMode.Pen); }, EditMode.Pen);
        AddButton("马赛克", IconMosaic, delegate { ToggleMode(EditMode.Mosaic); }, EditMode.Mosaic);
        AddButton("方框", IconRect, delegate { ToggleMode(EditMode.Rect); }, EditMode.Rect);
        AddButton("撤销", IconUndo, delegate { Undo(); }, EditMode.None);
        AddButton("复制", IconCopy, delegate { CopySelection(); }, EditMode.None);
        AddButton("保存", IconSave, delegate { SaveSelection(); }, EditMode.None);
        ToolButton bClose = new ToolButton();
        bClose.Text = "";
        bClose.Icon = IconClose;
        bClose.IconColor = Color.FromArgb(232, 64, 64);   // 红色叉
        bClose.OnClick = delegate { this.Close(); };
        _buttons.Add(bClose);

        RecalcToolbar();
        this.Controls.Add(_toolbar);
    }

    void AddButton(string text, IconDrawer icon, Action onClick, EditMode modeTag)
    {
        ToolButton b = new ToolButton();
        b.Text = text;
        b.Icon = icon;
        b.OnClick = onClick;
        b.ModeTag = modeTag;
        _buttons.Add(b);
    }

    Font MakeToolbarFont()
    {
        return new Font("Microsoft YaHei UI", 11f * _s, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    void RecalcToolbar()
    {
        int iconSize = S(18);
        int gap = S(1);
        int pad, h;
        if (_tbStyle == ToolbarStyle.IconOnly) { h = S(34); pad = S(6); }
        else if (_tbStyle == ToolbarStyle.TextOnly) { h = S(26); pad = S(6); }
        else
        {
            // 图标+文字：高度按内容算（图标 + 间隙 + 文字行高 + 上下各 3px），保证内容上下居中
            float maxTextH = 0f;
            using (Graphics mg = _toolbar.CreateGraphics())
            using (Font f = MakeToolbarFont())
            {
                foreach (ToolButton b in _buttons)
                {
                    if (b.Text.Length == 0) continue;
                    SizeF ts = mg.MeasureString(b.Text, f);
                    if (ts.Height > maxTextH) maxTextH = ts.Height;
                }
            }
            h = iconSize + S(1) + (int)Math.Ceiling(maxTextH) + S(6);
            pad = S(8);
        }

        using (Graphics g = _toolbar.CreateGraphics())
        {
            Font font = MakeToolbarFont();
            int x = 1;
            foreach (ToolButton b in _buttons)
            {
                int w;
                if (_tbStyle == ToolbarStyle.IconOnly)
                {
                    w = iconSize + pad * 2;
                }
                else
                {
                    SizeF ts = g.MeasureString(b.Text, font);
                    int tw = (int)Math.Ceiling(ts.Width);
                    if (_tbStyle == ToolbarStyle.TextOnly)
                        w = (b.Text.Length == 0 ? S(30) : tw) + pad * 2;
                    else
                        w = Math.Max(iconSize, tw) + pad * 2;
                }
                b.Bounds = new Rectangle(x, 1, w, h);
                x += w + gap;
            }
            font.Dispose();
            _toolbar.Width = x + 1;
            _toolbar.Height = h + 2;
        }

        using (GraphicsPath p = new GraphicsPath())
        {
            int r = S(7);
            p.AddArc(0, 0, r * 2, r * 2, 180, 90);
            p.AddArc(_toolbar.Width - r * 2 - 1, 0, r * 2, r * 2, 270, 90);
            p.AddArc(_toolbar.Width - r * 2 - 1, _toolbar.Height - r * 2 - 1, r * 2, r * 2, 0, 90);
            p.AddArc(0, _toolbar.Height - r * 2 - 1, r * 2, r * 2, 90, 90);
            p.CloseFigure();
            _toolbar.Region = new Region(p);
        }
    }

    void CycleToolbarStyle()
    {
        RecalcToolbar();
        if (_toolbar.Visible) PositionToolbar();
        _toolbar.Invalidate();
    }

    void ToggleMode(EditMode m)
    {
        _mode = (_mode == m) ? EditMode.None : m;
        _toolbar.Invalidate();
    }

    int HitButton(Point p)
    {
        for (int i = 0; i < _buttons.Count; i++)
        {
            if (_buttons[i].Bounds.Contains(p)) return i;
        }
        return -1;
    }

    void OnToolbarMouseMove(object sender, MouseEventArgs e)
    {
        int idx = HitButton(e.Location);
        if (idx != _hoverIndex)
        {
            _hoverIndex = idx;
            _toolbar.Invalidate();
        }
    }

    void OnToolbarMouseDown(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        int idx = HitButton(e.Location);
        if (idx >= 0 && _buttons[idx].OnClick != null) _buttons[idx].OnClick();
    }

    void OnToolbarPaint(object sender, PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        Font font = MakeToolbarFont();
        int iconSize = S(18);
        for (int i = 0; i < _buttons.Count; i++)
        {
            ToolButton b = _buttons[i];
            bool active = (b.ModeTag != EditMode.None && b.ModeTag == _mode);
            bool hover = (i == _hoverIndex);
            Color back = active ? Color.FromArgb(0, 122, 204)
                       : hover ? Color.FromArgb(80, 80, 80) : Color.FromArgb(45, 45, 45);
            using (GraphicsPath bp = RoundedRectPath(b.Bounds, S(4)))
            using (SolidBrush br = new SolidBrush(back))
            {
                g.FillPath(br, bp);
            }

            if (_tbStyle == ToolbarStyle.TextOnly)
            {
                if (b.Text.Length == 0)
                {
                    // 无文字按钮（样式切换）在仅文字模式下用小图标居中显示
                    Rectangle ir = new Rectangle(b.Bounds.X + (b.Bounds.Width - iconSize) / 2,
                        b.Bounds.Y + (b.Bounds.Height - iconSize) / 2, iconSize, iconSize);
                    b.Icon(g, ir, Color.WhiteSmoke);
                }
                else
                {
                    SizeF ts = g.MeasureString(b.Text, font);
                    g.DrawString(b.Text, font, Brushes.WhiteSmoke,
                        b.Bounds.X + (b.Bounds.Width - ts.Width) / 2f,
                        b.Bounds.Y + (b.Bounds.Height - ts.Height) / 2f);
                }
            }
            else if (_tbStyle == ToolbarStyle.IconOnly)
            {
                Rectangle ir = new Rectangle(b.Bounds.X + (b.Bounds.Width - iconSize) / 2,
                    b.Bounds.Y + (b.Bounds.Height - iconSize) / 2, iconSize, iconSize);
                b.Icon(g, ir, b.IconColor);
            }
            else
            {
                // 图标+文字：整体在按钮内上下居中
                SizeF ts = g.MeasureString(b.Text, font);
                int contentH = iconSize + S(1) + (int)Math.Ceiling(ts.Height);
                int top = b.Bounds.Y + Math.Max(S(1), (b.Bounds.Height - contentH) / 2);
                Rectangle ir = new Rectangle(b.Bounds.X + (b.Bounds.Width - iconSize) / 2, top, iconSize, iconSize);
                b.Icon(g, ir, b.IconColor);
                g.DrawString(b.Text, font, Brushes.WhiteSmoke,
                    b.Bounds.X + (b.Bounds.Width - ts.Width) / 2f,
                    top + iconSize + S(1));
            }
        }
        font.Dispose();
    }

    GraphicsPath RoundedRectPath(Rectangle r, int rad)
    {
        GraphicsPath p = new GraphicsPath();
        p.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
        p.AddArc(r.Right - rad * 2 - 1, r.Y, rad * 2, rad * 2, 270, 90);
        p.AddArc(r.Right - rad * 2 - 1, r.Bottom - rad * 2 - 1, rad * 2, rad * 2, 0, 90);
        p.AddArc(r.X, r.Bottom - rad * 2 - 1, rad * 2, rad * 2, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ---------- 内置图标（16x16 逻辑坐标，白色线条） ----------

    static void IconDoc(Graphics g, Rectangle r, Color c)
    {
        float s = r.Width / 16f;
        using (Pen p = new Pen(c, 1.4f * s))
        {
            g.DrawRectangle(p, r.X + 3f * s, r.Y + 2f * s, 10f * s, 12f * s);
            g.DrawLine(p, r.X + 5f * s, r.Y + 5.5f * s, r.X + 11f * s, r.Y + 5.5f * s);
            g.DrawLine(p, r.X + 5f * s, r.Y + 8.5f * s, r.X + 11f * s, r.Y + 8.5f * s);
            g.DrawLine(p, r.X + 5f * s, r.Y + 11.5f * s, r.X + 9f * s, r.Y + 11.5f * s);
        }
    }

    static void IconGlobe(Graphics g, Rectangle r, Color c)
    {
        float s = r.Width / 16f;
        using (Pen p = new Pen(c, 1.3f * s))
        {
            g.DrawEllipse(p, r.X + 2f * s, r.Y + 2f * s, 12f * s, 12f * s);          // 球体
            g.DrawLine(p, r.X + 2f * s, r.Y + 8f * s, r.X + 14f * s, r.Y + 8f * s);  // 赤道
            g.DrawEllipse(p, r.X + 5.5f * s, r.Y + 2f * s, 5f * s, 12f * s);         // 经线
            g.DrawLine(p, r.X + 3.2f * s, r.Y + 5f * s, r.X + 12.8f * s, r.Y + 5f * s);   // 北纬线
            g.DrawLine(p, r.X + 3.2f * s, r.Y + 11f * s, r.X + 12.8f * s, r.Y + 11f * s); // 南纬线
        }
    }

    static void IconPen(Graphics g, Rectangle r, Color c)
    {
        float s = r.Width / 16f;
        using (Pen p = new Pen(c, 1.6f * s))
        {
            p.StartCap = LineCap.Round;
            p.EndCap = LineCap.Round;
            g.DrawLine(p, r.X + 4.5f * s, r.Y + 11.5f * s, r.X + 12f * s, r.Y + 4f * s);
            g.DrawLine(p, r.X + 11f * s, r.Y + 3f * s, r.X + 13f * s, r.Y + 5f * s);
        }
        using (SolidBrush b = new SolidBrush(c))
        {
            g.FillPolygon(b, new PointF[] {
                new PointF(r.X + 3f * s, r.Y + 13f * s), new PointF(r.X + 6f * s, r.Y + 13f * s), new PointF(r.X + 3f * s, r.Y + 10f * s) });
        }
    }

    static void IconMosaic(Graphics g, Rectangle r, Color c)
    {
        float s = r.Width / 16f;
        using (SolidBrush b = new SolidBrush(c))
        {
            g.FillRectangle(b, r.X + 2f * s, r.Y + 2f * s, 5f * s, 5f * s);
            g.FillRectangle(b, r.X + 9f * s, r.Y + 9f * s, 5f * s, 5f * s);
        }
        using (Pen p = new Pen(c, 1.2f * s))
        {
            g.DrawRectangle(p, r.X + 9f * s, r.Y + 2f * s, 5f * s, 5f * s);
            g.DrawRectangle(p, r.X + 2f * s, r.Y + 9f * s, 5f * s, 5f * s);
        }
    }

    static void IconRect(Graphics g, Rectangle r, Color c)
    {
        float s = r.Width / 16f;
        using (Pen p = new Pen(c, 1.5f * s))
        {
            g.DrawRectangle(p, r.X + 2f * s, r.Y + 4.5f * s, 12f * s, 7f * s);
        }
    }

    static void IconUndo(Graphics g, Rectangle r, Color c)
    {
        float s = r.Width / 16f;
        using (Pen p = new Pen(c, 1.5f * s))
        {
            p.StartCap = LineCap.Round;
            p.EndCap = LineCap.Round;
            p.LineJoin = LineJoin.Round;
            // 顶部水平段
            g.DrawLine(p, r.X + 5f * s, r.Y + 5f * s, r.X + 10f * s, r.Y + 5f * s);
            // 右端向下弯钩（四分之一圆弧 + 竖直尾段）
            g.DrawArc(p, r.X + 6.5f * s, r.Y + 5f * s, 7f * s, 7f * s, 270, 90);
            g.DrawLine(p, r.X + 13.5f * s, r.Y + 8.5f * s, r.X + 13.5f * s, r.Y + 12.5f * s);
        }
        using (SolidBrush b = new SolidBrush(c))
        {
            // 左端向左的箭头
            g.FillPolygon(b, new PointF[] {
                new PointF(r.X + 2.5f * s, r.Y + 5f * s),
                new PointF(r.X + 6.5f * s, r.Y + 2.6f * s),
                new PointF(r.X + 6.5f * s, r.Y + 7.4f * s) });
        }
    }

    static void IconCopy(Graphics g, Rectangle r, Color c)
    {
        float s = r.Width / 16f;
        using (Pen p = new Pen(c, 1.4f * s))
        {
            g.DrawRectangle(p, r.X + 5.5f * s, r.Y + 2f * s, 8f * s, 10f * s);
            g.DrawRectangle(p, r.X + 2.5f * s, r.Y + 4.5f * s, 8f * s, 10f * s);
        }
    }

    static void IconSave(Graphics g, Rectangle r, Color c)
    {
        float s = r.Width / 16f;
        using (Pen p = new Pen(c, 1.4f * s))
        {
            p.StartCap = LineCap.Round;
            p.EndCap = LineCap.Round;
            p.LineJoin = LineJoin.Round;
            // 开口托盘（左壁 → 底 → 右壁）
            g.DrawLines(p, new PointF[] {
                new PointF(r.X + 3f * s, r.Y + 8.5f * s),
                new PointF(r.X + 3f * s, r.Y + 13.5f * s),
                new PointF(r.X + 13f * s, r.Y + 13.5f * s),
                new PointF(r.X + 13f * s, r.Y + 8.5f * s) });
            // 竖直箭头杆
            g.DrawLine(p, r.X + 8f * s, r.Y + 2f * s, r.X + 8f * s, r.Y + 9.5f * s);
        }
        using (SolidBrush b = new SolidBrush(c))
        {
            // 箭头头
            g.FillPolygon(b, new PointF[] {
                new PointF(r.X + 5.2f * s, r.Y + 7f * s),
                new PointF(r.X + 10.8f * s, r.Y + 7f * s),
                new PointF(r.X + 8f * s, r.Y + 10.2f * s) });
        }
    }

    static void IconClose(Graphics g, Rectangle r, Color c)
    {
        float s = r.Width / 16f;
        using (Pen p = new Pen(c, 1.6f * s))
        {
            g.DrawLine(p, r.X + 4f * s, r.Y + 4f * s, r.X + 12f * s, r.Y + 12f * s);
            g.DrawLine(p, r.X + 12f * s, r.Y + 4f * s, r.X + 4f * s, r.Y + 12f * s);
        }
    }

    static void IconStyle(Graphics g, Rectangle r, Color c)
    {
        float s = r.Width / 16f;
        using (SolidBrush b = new SolidBrush(c))
        {
            g.FillRectangle(b, r.X + 2f * s, r.Y + 3f * s, 5f * s, 5f * s);
        }
        using (Pen p = new Pen(c, 1.2f * s))
        {
            g.DrawRectangle(p, r.X + 9f * s, r.Y + 3f * s, 5f * s, 5f * s);
            g.DrawLine(p, r.X + 2f * s, r.Y + 12.5f * s, r.X + 14f * s, r.Y + 12.5f * s);
        }
    }

    void ShowToolbar()
    {
        if (_mosaic == null) _mosaic = Pixelate(_shot, 12);
        PositionToolbar();
        _toolbar.Visible = true;
        _toolbar.BringToFront();
        Program.Log("toolbar at " + _toolbar.Bounds.ToString() + " w=" + _toolbar.Width);
    }

    void PositionToolbar()
    {
        int tw = _toolbar.Width, th = _toolbar.Height;
        int x = _sel.Left + (_sel.Width - tw) / 2;   // 水平居中于选区
        int y = _sel.Bottom + S(8);
        if (y + th > this.ClientSize.Height - 2) y = _sel.Top - th - S(8);
        if (y < 2) y = Math.Max(2, this.ClientSize.Height - th - 2);
        if (x < 2) x = 2;
        if (x + tw > this.ClientSize.Width - 2) x = this.ClientSize.Width - tw - 2;
        _toolbar.Location = new Point(x, y);
    }

    // ---------- 鼠标 / 键盘 ----------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Program.Log("overlay mousedown " + e.Button + " " + e.Location + " mode=" + _mode);
        if (_busy) return;
        if (e.Button == MouseButtons.Left)
        {
            if (_mode != EditMode.None && _sel.Contains(e.Location))
            {
                // 编辑模式：在选区内开始一笔
                if (_mode == EditMode.Rect)
                {
                    _start = e.Location;
                    _rectCur = new Rectangle(e.Location, Size.Empty);
                    _drawing = true;
                }
                else
                {
                    _cur = new Stroke();
                    _cur.IsMosaic = (_mode == EditMode.Mosaic);
                    _cur.Pts = new List<Point>();
                    _cur.Pts.Add(e.Location);
                    _drawing = true;
                }
                return;
            }

            // 重新框选（清空旧标注）
            _dragging = true;
            _toolbar.Visible = false;
            _start = e.Location;
            _lastMouse = e.Location;
            _sel = Rectangle.Empty;
            if (_strokes.Count > 0 || _mode != EditMode.None)
            {
                _strokes.Clear();
                _mode = EditMode.None;
                _toolbar.Invalidate();
                using (Graphics g = Graphics.FromImage(_work)) g.DrawImage(_shot, 0, 0);
            }
            this.Invalidate();
        }
        else if (e.Button == MouseButtons.Right)
        {
            this.Close(); // 右键取消
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _lastMouse = e.Location;
        if (_drawing)
        {
            if (_mode == EditMode.Rect)
            {
                _rectCur = RectFrom(_start, e.Location);
                this.Invalidate();
            }
            else if (_cur != null)
            {
                // 笔迹限制在选区内
                Point pt = e.Location;
                if (pt.X < _sel.Left) pt.X = _sel.Left;
                if (pt.Y < _sel.Top) pt.Y = _sel.Top;
                if (pt.X >= _sel.Right) pt.X = _sel.Right - 1;
                if (pt.Y >= _sel.Bottom) pt.Y = _sel.Bottom - 1;
                _cur.Pts.Add(pt);
                using (Graphics g = Graphics.FromImage(_work)) DrawStroke(g, _cur);
                this.Invalidate(_sel);
            }
            return;
        }
        if (_dragging)
        {
            _sel = RectFrom(_start, e.Location);
            this.Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;

        if (_drawing)
        {
            _drawing = false;
            if (_cur != null)
            {
                if (_cur.Pts.Count == 1)
                {
                    using (Graphics g = Graphics.FromImage(_work)) DrawStroke(g, _cur); // 单点也留痕
                }
                _strokes.Add(_cur);
                _cur = null;
            }
            else if (_rectCur.Width > S(2) && _rectCur.Height > S(2))
            {
                Stroke s = new Stroke();
                s.IsRect = true;
                s.Rect = _rectCur;
                _strokes.Add(s);
                using (Graphics g = Graphics.FromImage(_work)) DrawStroke(g, s);
                _rectCur = Rectangle.Empty;
            }
            else
            {
                _rectCur = Rectangle.Empty;
                RedrawWork(); // 清掉无效预览
            }
            this.Invalidate();
            return;
        }

        if (_dragging)
        {
            _dragging = false;
            if (_sel.Width >= S(8) && _sel.Height >= S(8))
            {
                Program.Log("overlay mouseup sel=" + _sel.ToString() + " -> toolbar");
                ShowToolbar();
            }
            else
            {
                Program.Log("overlay mouseup sel too small: " + _sel.ToString());
                _sel = Rectangle.Empty;
            }
            this.Invalidate();
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (_mode != EditMode.None || _drawing) return; // 编辑模式中不触发
        if (e.Button == MouseButtons.Left && _sel.Width > 0 && !_busy) CopySelection();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) { this.Close(); }
        else if (e.KeyCode == Keys.Enter && _sel.Width > 0 && !_busy) { CopySelection(); }
        else if (e.KeyCode == Keys.Z && e.Control) { Undo(); }
    }

    static Rectangle RectFrom(Point a, Point b)
    {
        int x = Math.Min(a.X, b.X);
        int y = Math.Min(a.Y, b.Y);
        return new Rectangle(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    // ---------- 绘制 ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        if (_shot == null) return;

        g.DrawImage(_shot, 0, 0);

        using (SolidBrush dim = new SolidBrush(Color.FromArgb(100, 0, 0, 0)))
        {
            Rectangle r = _sel;
            if (r.Width < 1 || r.Height < 1)
            {
                g.FillRectangle(dim, this.ClientRectangle); // 未选区时全屏变暗
            }
            else
            {
                g.FillRectangle(dim, 0, 0, Width, r.Top);
                g.FillRectangle(dim, 0, r.Bottom, Width, Height - r.Bottom);
                g.FillRectangle(dim, 0, r.Top, r.Left, r.Height);
                g.FillRectangle(dim, r.Right, r.Top, Width - r.Right, r.Height);
            }
        }

        if (_sel.Width > 0 && _sel.Height > 0)
        {
            // 选区内显示带标注的工作图
            g.SetClip(_sel);
            g.DrawImage(_work, 0, 0);
            if (_drawing && _mode == EditMode.Rect && _rectCur.Width > 0)
            {
                using (Pen rp = new Pen(Color.Red, 3f)) g.DrawRectangle(rp, _rectCur);
            }
            g.ResetClip();

            using (Pen pen = new Pen(Color.FromArgb(235, 60, 60), 2f))
            {
                g.DrawRectangle(pen, _sel.X + 1, _sel.Y + 1, _sel.Width - 2, _sel.Height - 2);
            }
        }

        if (_dragging)
        {
            DrawSizeHint(g);
            DrawMagnifier(g);
        }
    }

    void DrawSizeHint(Graphics g)
    {
        string txt = _sel.Width + " × " + _sel.Height;
        using (Font f = new Font("Microsoft YaHei UI", 12f * _s, GraphicsUnit.Pixel))
        {
            SizeF sz = g.MeasureString(txt, f);
            int w = (int)sz.Width + 12, h = (int)sz.Height + 6;
            int x = _lastMouse.X + 16, y = _lastMouse.Y + 20;
            if (x + w > Width - 4) x = _lastMouse.X - w - 16;
            if (y + h > Height - 4) y = _lastMouse.Y - h - 20;
            if (x < 2) x = 2;
            if (y < 2) y = 2;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(210, 0, 0, 0)))
            {
                g.FillRectangle(b, x, y, w, h);
            }
            g.DrawString(txt, f, Brushes.White, x + 6, y + 3);
        }
    }

    void DrawMagnifier(Graphics g)
    {
        const int src = 11;     // 源区域 11x11 像素
        const int dst = 121;    // 放大后 121x121（11 倍）

        int cx = Clamp(_lastMouse.X, src / 2, Math.Min(_shot.Width, Width) - src / 2 - 1);
        int cy = Clamp(_lastMouse.Y, src / 2, Math.Min(_shot.Height, Height) - src / 2 - 1);

        int x = _lastMouse.X + 22, y = _lastMouse.Y + 26;
        int boxH = dst + S(26);
        if (x + dst > Width - 4) x = _lastMouse.X - dst - 22;
        if (y + boxH > Height - 4) y = _lastMouse.Y - boxH - 26;
        if (x < 2) x = 2;
        if (y < 2) y = 2;

        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        Rectangle srcRect = new Rectangle(cx - src / 2, cy - src / 2, src, src);
        Rectangle dstRect = new Rectangle(x, y, dst, dst);
        g.DrawImage(_shot, dstRect, srcRect, GraphicsUnit.Pixel);
        g.InterpolationMode = InterpolationMode.Default;
        g.PixelOffsetMode = PixelOffsetMode.Default;

        using (Pen border = new Pen(Color.FromArgb(200, 255, 255, 255), 1f))
        {
            g.DrawRectangle(border, x, y, dst - 1, dst - 1);
        }

        // 中心像素颜色
        try
        {
            Color c = _shot.GetPixel(Clamp(_lastMouse.X, 0, _shot.Width - 1), Clamp(_lastMouse.Y, 0, _shot.Height - 1));
            string hex = "#" + c.R.ToString("X2", CultureInfo.InvariantCulture) + c.G.ToString("X2", CultureInfo.InvariantCulture) + c.B.ToString("X2", CultureInfo.InvariantCulture);
            int infoY = y + dst + 4;
            g.FillRectangle(new SolidBrush(c), x, infoY, S(16), S(16));
            using (Pen p = new Pen(Color.FromArgb(200, 255, 255, 255), 1f)) g.DrawRectangle(p, x, infoY, S(16), S(16));
            using (Font f = new Font("Consolas", 11f * _s, GraphicsUnit.Pixel))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(210, 0, 0, 0)))
            {
                g.FillRectangle(b, x + S(20), infoY, S(58), S(16));
                g.DrawString(hex, f, Brushes.White, x + S(22), infoY + 1);
            }
        }
        catch { }
    }

    static int Clamp(int v, int lo, int hi)
    {
        if (hi < lo) hi = lo;
        if (v < lo) return lo;
        if (v > hi) return hi;
        return v;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Program.Log("overlay shown bounds=" + this.Bounds.ToString() + " dpi=" + this.DeviceDpi);
        this.Activate();
        this.Focus();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        if (_shot != null) { _shot.Dispose(); _shot = null; }
        if (_work != null) { _work.Dispose(); _work = null; }
        if (_mosaic != null) { _mosaic.Dispose(); _mosaic = null; }
    }

    // ---------- 标注绘制 ----------

    Pen MakeStrokePen(bool mosaic)
    {
        Pen p;
        if (mosaic && _mosaic != null)
            p = new Pen(new TextureBrush(_mosaic), 20f);
        else
            p = new Pen(Color.Red, 4f);
        p.StartCap = LineCap.Round;
        p.EndCap = LineCap.Round;
        p.LineJoin = LineJoin.Round;
        return p;
    }

    void DrawStroke(Graphics g, Stroke s)
    {
        if (s == null) return;
        if (s.IsRect)
        {
            using (Pen p = new Pen(Color.Red, 3f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                g.DrawRectangle(p, s.Rect);
            }
            return;
        }
        if (s.Pts == null || s.Pts.Count == 0) return;
        using (Pen p = MakeStrokePen(s.IsMosaic))
        {
            if (s.Pts.Count == 1)
            {
                int r2 = (int)p.Width / 2;
                if (s.IsMosaic)
                    g.FillRectangle(p.Brush, s.Pts[0].X - r2, s.Pts[0].Y - r2, p.Width, p.Width);
                else
                    g.FillEllipse(Brushes.Red, s.Pts[0].X - r2, s.Pts[0].Y - r2, p.Width, p.Width);
            }
            else
            {
                g.DrawLines(p, s.Pts.ToArray());
            }
        }
    }

    void RedrawWork()
    {
        using (Graphics g = Graphics.FromImage(_work))
        {
            g.DrawImage(_shot, 0, 0);
            for (int i = 0; i < _strokes.Count; i++) DrawStroke(g, _strokes[i]);
        }
    }

    Bitmap Pixelate(Bitmap src, int block)
    {
        int w = Math.Max(1, src.Width / block);
        int h = Math.Max(1, src.Height / block);
        Bitmap big = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (Bitmap small = new Bitmap(w, h))
        {
            using (Graphics g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, w, h);
            }
            using (Graphics g = Graphics.FromImage(big))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(small, 0, 0, src.Width, src.Height);
            }
        }
        return big;
    }

    void Undo()
    {
        if (_strokes.Count == 0) return;
        _strokes.RemoveAt(_strokes.Count - 1);
        RedrawWork();
        this.Invalidate(_sel);
    }

    // ---------- 裁剪与动作 ----------

    Bitmap Crop()
    {
        Rectangle r = _sel;
        r.Intersect(new Rectangle(0, 0, _work.Width, _work.Height));
        Bitmap b = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.DrawImage(_work, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
        }
        return b;
    }

    /// <summary>调试转储：把 OCR 输入图像与选区写到 %TEMP%\SnapOCR_debug（存在开关文件时启用）。</summary>
    void DumpDebug(Bitmap crop, Rectangle sel, string stage)
    {
        try
        {
            string flag = Path.Combine(Path.GetTempPath(), "SnapOCR_debug_enable");
            if (!File.Exists(flag)) return;
            string dir = Path.Combine(Path.GetTempPath(), "SnapOCR_debug");
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            crop.Save(Path.Combine(dir, stage + "_" + stamp + ".png"), ImageFormat.Png);
            File.WriteAllText(Path.Combine(dir, stage + "_" + stamp + ".txt"),
                "sel=" + sel.ToString() + " mode=" + _mode + " strokes=" + _strokes.Count);
        }
        catch { }
    }

    void CopySelection()
    {
        using (Bitmap crop = Crop())
        {
            Clipboard.SetImage(crop);
        }
        this.Close();
    }

    void SaveSelection()
    {
        using (Bitmap crop = Crop())
        using (SaveFileDialog dlg = new SaveFileDialog())
        {
            dlg.Filter = "PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg|位图 (*.bmp)|*.bmp";
            dlg.FileName = "截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            dlg.DefaultExt = "png";
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                ImageFormat fmt = ImageFormat.Png;
                string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
                if (ext == ".jpg" || ext == ".jpeg") fmt = ImageFormat.Jpeg;
                else if (ext == ".bmp") fmt = ImageFormat.Bmp;
                crop.Save(dlg.FileName, fmt);
                this.Close();
            }
        }
    }

    async void RunOcr()
    {
        if (_busy || _sel.Width < 1) return;
        _busy = true;
        Bitmap crop = Crop();
        Rectangle selSnapshot = _sel;
        DumpDebug(crop, selSnapshot, "ocr");
        this.Close();

        string text = null;
        try
        {
            text = await Task.Run(delegate { return OcrHelper.Recognize(crop); });
        }
        catch (Exception ex)
        {
            Program.Log("RunOcr exception: " + ex);
            MessageBox.Show("OCR 失败：" + ex.Message, "SnapOCR", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { crop.Dispose(); }

        Program.Log("RunOcr result len=" + (text == null ? -1 : text.Length));
        if (text == null)
        {
            MessageBox.Show(OcrHelper.LastError, "SnapOCR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (text.Length == 0)
        {
            Program.ShowOcrResult("", "未识别到文字，请调整选区后重试");
            return;
        }
        try { Clipboard.SetText(text); } catch { }
        Program.ShowOcrResult(text, "已自动复制到剪贴板 ✓");
    }

    async void RunTranslate()
    {
        if (_busy || _sel.Width < 1) return;
        _busy = true;
        Bitmap crop = Crop();
        Rectangle selSnapshot = _sel;
        DumpDebug(crop, selSnapshot, "trans");
        this.Close();

        string text = null;
        try
        {
            text = await Task.Run(delegate { return OcrHelper.Recognize(crop); });
        }
        catch (Exception ex)
        {
            Program.Log("RunTranslate exception: " + ex);
            MessageBox.Show("OCR 失败：" + ex.Message, "SnapOCR", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { crop.Dispose(); }

        Program.Log("RunTranslate ocr len=" + (text == null ? -1 : text.Length));
        if (text == null)
        {
            MessageBox.Show(OcrHelper.LastError, "SnapOCR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (text.Length == 0)
        {
            MessageBox.Show("未识别到文字，无法翻译。请调整选区后重试。", "SnapOCR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string target = LooksChinese(text) ? "en" : "zh-Hans";
        Program.ShowTranslateResult(text, target);
    }

    static bool LooksChinese(string s)
    {
        int cjk = 0, total = 0;
        int n = Math.Min(s.Length, 300);
        for (int i = 0; i < n; i++)
        {
            char c = s[i];
            if (char.IsLetter(c))
            {
                total++;
                if (c >= 0x4E00 && c <= 0x9FFF) cjk++;
            }
        }
        return total > 0 && cjk * 100 / total >= 25;
    }
}

// ============================== 结果窗口（OCR / 翻译） ==============================

class LangItem
{
    public string Name;
    public string Code;
    public LangItem(string name, string code) { Name = name; Code = code; }
    public override string ToString() { return Name; }
}

class ResultForm : Form
{
    bool _translateMode;
    string _source;
    string _target;
    string _initStatus;

    TextBox _text;      // OCR 模式
    TextBox _src, _dst; // 翻译模式
    ComboBox _lang;
    Button _reBtn;
    Label _status;
    bool _ready;

    public ResultForm(bool translateMode, string source, string initialTarget, string initStatus)
    {
        _translateMode = translateMode;
        _source = source;
        _target = initialTarget;
        _initStatus = initStatus;

        float s = Program.UiScale();
        Font uiFont = MakeFont(13f, s);
        this.Font = uiFont;
        this.TopMost = true;
        this.ShowInTaskbar = false;
        this.StartPosition = FormStartPosition.CenterScreen;
        this.Icon = AppIcon.Get();
        this.KeyPreview = true;
        this.MinimumSize = new Size((int)(420 * s), (int)(300 * s));

        if (translateMode)
        {
            this.Text = "翻译 - SnapOCR";
            BuildTranslateUi(s);
        }
        else
        {
            this.Text = "提取文字 - SnapOCR";
            BuildOcrUi(s);
        }

        this.KeyDown += delegate(object snd, KeyEventArgs ev)
        {
            if (ev.KeyCode == Keys.Escape) this.Close();
        };
    }

    static Font MakeFont(float px, float s)
    {
        return new Font("Microsoft YaHei UI", px * s, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    Button MakeButton(string text, float s)
    {
        Button b = new Button();
        b.Text = text;
        b.Font = MakeFont(13f, s);
        b.Size = new Size(S(84, s), S(30, s));
        b.UseVisualStyleBackColor = true;
        return b;
    }

    static int S(int px, float s) { return (int)Math.Round(px * s); }

    // ---- OCR 模式 ----
    void BuildOcrUi(float s)
    {
        this.ClientSize = new Size(S(560, s), S(420, s));

        _status = new Label();
        _status.Location = new Point(S(12, s), S(10, s));
        _status.Size = new Size(S(400, s), S(20, s));
        _status.Font = MakeFont(12.5f, s);
        _status.Text = _initStatus;
        _status.ForeColor = Color.ForestGreen;
        this.Controls.Add(_status);

        _text = new TextBox();
        _text.Multiline = true;
        _text.ScrollBars = ScrollBars.Vertical;
        _text.WordWrap = true;
        _text.Font = MakeFont(14f, s);
        _text.Location = new Point(S(12, s), S(36, s));
        _text.Size = new Size(S(536, s), S(330, s));
        _text.Text = _source;
        this.Controls.Add(_text);

        Button copy = MakeButton("复制", s);
        copy.Location = new Point(this.ClientSize.Width - S(84, s) * 2 - S(20, s), this.ClientSize.Height - S(40, s));
        copy.Click += delegate
        {
            try { Clipboard.SetText(_text.Text); SetStatus("已复制到剪贴板 ✓", Color.ForestGreen); } catch { }
        };
        this.Controls.Add(copy);

        Button close = MakeButton("关闭", s);
        close.Location = new Point(this.ClientSize.Width - S(84, s) - S(12, s), this.ClientSize.Height - S(40, s));
        close.Click += delegate { this.Close(); };
        this.Controls.Add(close);
    }

    // ---- 翻译模式 ----
    void BuildTranslateUi(float s)
    {
        this.ClientSize = new Size(S(560, s), S(540, s));

        Label l1 = new Label();
        l1.Text = "原文（OCR 识别结果，可修改）";
        l1.Location = new Point(S(12, s), S(10, s));
        l1.AutoSize = true;
        l1.Font = MakeFont(12.5f, s);
        this.Controls.Add(l1);

        _src = new TextBox();
        _src.Multiline = true;
        _src.ScrollBars = ScrollBars.Vertical;
        _src.WordWrap = true;
        _src.ReadOnly = true;
        _src.BackColor = Color.White;
        _src.Font = MakeFont(13.5f, s);
        _src.Location = new Point(S(12, s), S(32, s));
        _src.Size = new Size(S(536, s), S(100, s));
        _src.Text = _source;
        this.Controls.Add(_src);

        Label l2 = new Label();
        l2.Text = "目标语言";
        l2.Location = new Point(S(12, s), S(142, s));
        l2.AutoSize = true;
        l2.Font = MakeFont(12.5f, s);
        this.Controls.Add(l2);

        _lang = new ComboBox();
        _lang.DropDownStyle = ComboBoxStyle.DropDownList;
        _lang.Font = MakeFont(12.5f, s);
        _lang.Location = new Point(S(78, s), S(138, s));
        _lang.Size = new Size(S(130, s), S(26, s));
        _lang.Items.Add(new LangItem("简体中文", "zh-Hans"));
        _lang.Items.Add(new LangItem("English", "en"));
        _lang.Items.Add(new LangItem("日本語", "ja"));
        _lang.Items.Add(new LangItem("한국어", "ko"));
        _lang.Items.Add(new LangItem("Русский", "ru"));
        _lang.Items.Add(new LangItem("Français", "fr"));
        _lang.Items.Add(new LangItem("Deutsch", "de"));
        _lang.Items.Add(new LangItem("Español", "es"));
        _lang.SelectedIndexChanged += delegate
        {
            if (!_ready) return;
            _target = ((LangItem)_lang.SelectedItem).Code;
            TranslateNow();
        };
        int idx = 0;
        for (int i = 0; i < _lang.Items.Count; i++)
        {
            if (((LangItem)_lang.Items[i]).Code == _target) { idx = i; break; }
        }
        _lang.SelectedIndex = idx;
        this.Controls.Add(_lang);

        _reBtn = MakeButton("重新翻译", s);
        _reBtn.Size = new Size(S(92, s), S(26, s));
        _reBtn.Location = new Point(S(220, s), S(138, s));
        _reBtn.Click += delegate { TranslateNow(); };
        this.Controls.Add(_reBtn);

        Label l3 = new Label();
        l3.Text = "译文";
        l3.Location = new Point(S(12, s), S(172, s));
        l3.AutoSize = true;
        l3.Font = MakeFont(12.5f, s);
        this.Controls.Add(l3);

        _dst = new TextBox();
        _dst.Multiline = true;
        _dst.ScrollBars = ScrollBars.Vertical;
        _dst.WordWrap = true;
        _dst.ReadOnly = true;
        _dst.BackColor = Color.White;
        _dst.Font = MakeFont(14f, s);
        _dst.Location = new Point(S(12, s), S(194, s));
        _dst.Size = new Size(S(536, s), S(280, s));
        this.Controls.Add(_dst);

        _status = new Label();
        _status.Location = new Point(S(12, s), this.ClientSize.Height - S(38, s));
        _status.Size = new Size(S(240, s), S(20, s));
        _status.Font = MakeFont(12f, s);
        this.Controls.Add(_status);

        Button copySrc = MakeButton("复制原文", s);
        copySrc.Size = new Size(S(92, s), S(30, s));
        copySrc.Location = new Point(this.ClientSize.Width - S(92, s) * 3 - S(32, s), this.ClientSize.Height - S(42, s));
        copySrc.Click += delegate
        {
            try { Clipboard.SetText(_src.Text); SetStatus("原文已复制 ✓", Color.ForestGreen); } catch { }
        };
        this.Controls.Add(copySrc);

        Button copyDst = MakeButton("复制译文", s);
        copyDst.Size = new Size(S(92, s), S(30, s));
        copyDst.Location = new Point(this.ClientSize.Width - S(92, s) * 2 - S(20, s), this.ClientSize.Height - S(42, s));
        copyDst.Click += delegate
        {
            try { Clipboard.SetText(_dst.Text); SetStatus("译文已复制 ✓", Color.ForestGreen); } catch { }
        };
        this.Controls.Add(copyDst);

        Button close = MakeButton("关闭", s);
        close.Location = new Point(this.ClientSize.Width - S(92, s) - S(8, s), this.ClientSize.Height - S(42, s));
        close.Click += delegate { this.Close(); };
        this.Controls.Add(close);
    }

    void SetStatus(string text, Color color)
    {
        if (_status == null) return;
        _status.Text = text;
        _status.ForeColor = color;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ready = true;
        if (_translateMode) TranslateNow();
    }

    async void TranslateNow()
    {
        if (_reBtn == null) return;
        _reBtn.Enabled = false;
        _lang.Enabled = false;
        _dst.Text = "";
        SetStatus("翻译中…", Color.Gray);
        try
        {
            string src = _source.Length > 4000 ? _source.Substring(0, 4000) : _source;
            TransResult r = await TranslateHelper.TranslateAsync(src, _target);
            _dst.Text = r.Text;
            SetStatus("", Color.Gray);
        }
        catch (Exception ex)
        {
            SetStatus("翻译失败：" + ex.Message, Color.Firebrick);
        }
        _reBtn.Enabled = true;
        _lang.Enabled = true;
    }
}

// ============================== 使用说明窗口 ==============================

class AboutForm : Form
{
    RichTextBox _rtb;
    float _s;

    public AboutForm()
    {
        _s = Program.UiScale();
        this.Text = "使用说明 - SnapOCR";
        this.ClientSize = new Size(S(628), S(600));
        this.StartPosition = FormStartPosition.CenterScreen;
        this.Icon = AppIcon.Get();
        this.MaximizeBox = false;
        this.Font = new Font("Microsoft YaHei UI", 12f * _s, FontStyle.Regular, GraphicsUnit.Pixel);
        this.KeyPreview = true;
        this.KeyDown += delegate(object snd, KeyEventArgs ev)
        {
            if (ev.KeyCode == Keys.Escape) this.Close();
        };

        PictureBox pb = new PictureBox();
        pb.Size = new Size(S(40), S(40));
        pb.Location = new Point(S(16), S(14));
        pb.SizeMode = PictureBoxSizeMode.StretchImage;
        pb.Image = AppIcon.Get().ToBitmap();
        this.Controls.Add(pb);

        Label title = new Label();
        title.Text = "SnapOCR  v" + Application.ProductVersion;
        title.Font = new Font("Microsoft YaHei UI", 17f * _s, FontStyle.Bold, GraphicsUnit.Pixel);
        title.Location = new Point(S(68), S(13));
        title.AutoSize = true;
        this.Controls.Add(title);

        Label sub = new Label();
        sub.Text = "微信式截图 · 标注 · OCR 文字提取 · 多语言翻译";
        sub.Font = new Font("Microsoft YaHei UI", 11.5f * _s, FontStyle.Regular, GraphicsUnit.Pixel);
        sub.ForeColor = Color.Gray;
        sub.Location = new Point(S(69), S(38));
        sub.AutoSize = true;
        this.Controls.Add(sub);

        _rtb = new RichTextBox();
        _rtb.ReadOnly = true;
        _rtb.BorderStyle = BorderStyle.None;
        _rtb.BackColor = Color.White;
        _rtb.Font = new Font("Microsoft YaHei UI", 12f * _s, FontStyle.Regular, GraphicsUnit.Pixel);
        _rtb.Location = new Point(S(16), S(64));
        _rtb.Size = new Size(this.ClientSize.Width - S(32), this.ClientSize.Height - S(64) - S(48));
        this.Controls.Add(_rtb);

        BuildContent();

        Button close = new Button();
        close.Text = "关闭";
        close.Font = new Font("Microsoft YaHei UI", 12.5f * _s, FontStyle.Regular, GraphicsUnit.Pixel);
        close.Size = new Size(S(96), S(30));
        close.Location = new Point(this.ClientSize.Width - close.Width - S(16), this.ClientSize.Height - S(42));
        close.Click += delegate { this.Close(); };
        this.Controls.Add(close);
        this.AcceptButton = close;
    }

    int S(int px) { return (int)Math.Round(px * _s); }

    void AppendText(string text, bool bold, Color color, float sizePx)
    {
        _rtb.SelectionStart = _rtb.TextLength;
        _rtb.SelectionLength = 0;
        _rtb.SelectionFont = new Font("Microsoft YaHei UI", sizePx * _s, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        _rtb.SelectionColor = color;
        _rtb.AppendText(text);
    }

    void BuildContent()
    {
        Color head = Color.FromArgb(0, 102, 180);
        Color body = Color.FromArgb(40, 40, 40);
        Color dim = Color.FromArgb(110, 110, 110);

        AppendText("■ 快速上手\n", true, head, 13);
        AppendText("1. 按 Alt+A（或双击托盘图标）唤起截图\n", false, body, 12);
        AppendText("2. 按住左键拖拽框选区域，Esc / 右键 取消\n", false, body, 12);
        AppendText("3. 松开鼠标弹出工具条；Enter 或双击 = 复制图像\n", false, body, 12);
        AppendText("\n", false, body, 4);
        AppendText("■ 工具条\n", true, head, 13);
        AppendText("· OCR —— 离线识别选区文字，结果自动复制（小字自动放大识别）\n", false, body, 12);
        AppendText("· 翻译 —— 识别后翻译，可选中/英/日/韩/俄/法/德/西\n", false, body, 12);
        AppendText("· 画笔 / 马赛克 / 方框 —— 标注工具，再点一次退出编辑，笔迹只在选区内\n", false, body, 12);
        AppendText("· 撤销 —— 撤销最后一笔标注（也可按 Ctrl+Z）\n", false, body, 12);
        AppendText("· 复制 / 保存 —— 图像进剪贴板 / 另存 PNG · JPG · BMP\n", false, body, 12);
        AppendText("· 标注后再 OCR / 翻译 / 复制 / 保存，得到的都是标注后的图；重新框选会清空标注\n", false, dim, 11.5f);
        AppendText("\n", false, body, 4);
        AppendText("■ 快捷键\n", true, head, 13);
        AppendText("· Alt+A —— 全局截图（微信运行时自动让出，微信退出后自动恢复）\n", false, body, 12);
        AppendText("· Enter —— 复制当前选区图像\n", false, body, 12);
        AppendText("· Esc —— 取消截图\n", false, body, 12);
        AppendText("· Ctrl+Z —— 撤销标注\n", false, body, 12);
        AppendText("\n", false, body, 4);
        AppendText("■ 托盘菜单\n", true, head, 13);
        AppendText("· 立即截图 / 开机自动启动 / 微信运行时自动让出 Alt+A\n", false, body, 12);
        AppendText("· 工具条显示：图标+文字 / 仅图标 / 仅文字（自动记住选择）\n", false, body, 12);
        AppendText("\n", false, body, 4);
        AppendText("■ 小提示\n", true, head, 13);
        AppendText("· 框选时右下角放大镜可放大查看像素并取色\n", false, body, 12);
        AppendText("· OCR 与标注离线可用；翻译走 Bing 服务，需要联网\n", false, body, 12);
        AppendText("· 图标+文字 / 仅图标 / 仅文字 三种样式，点托盘菜单即可切换\n", false, dim, 11.5f);
        AppendText("\n", false, body, 4);
        AppendText("■ 关于\n", true, head, 13);
        AppendText("作者：Ty\n", false, body, 12);
        AppendText("项目主页：github.com/Aby7557/SnapOCR\n", false, dim, 11.5f);
        AppendText("问题反馈：github.com/Aby7557/SnapOCR/issues\n", false, dim, 11.5f);
        _rtb.SelectionStart = 0;
        _rtb.SelectionLength = 0;
    }
}

// ============================== WinRT 互操作辅助 ==============================

/// <summary>手动等待 IAsyncOperation 完成（轮询，不依赖 System.Runtime.WindowsRuntime 扩展）。</summary>
static class WinRtOps
{
    public static T Wait<T>(Windows.Foundation.IAsyncOperation<T> op, int timeoutMs)
    {
        int waited = 0;
        while (op.Status == AsyncStatus.Started)
        {
            if (waited >= timeoutMs)
            {
                try { op.Cancel(); } catch { }
                throw new Exception("操作超时（" + timeoutMs + "ms）");
            }
            Thread.Sleep(15);
            waited += 15;
        }
        if (op.Status != AsyncStatus.Completed)
        {
            int hr = 0;
            try { hr = op.ErrorCode.HResult; } catch { }
            throw new Exception("WinRT 操作失败，状态: " + op.Status + "，HRESULT=0x" + hr.ToString("X8", CultureInfo.InvariantCulture));
        }
        return op.GetResults();
    }

    /// <summary>
    /// GDI Bitmap → PNG 字节 → 内存流 → BitmapDecoder → SoftwareBitmap。
    /// 全部使用 winmd 世界自己的类型（不走手写 IBuffer，也无需 LockBits）。
    /// </summary>
    public static SoftwareBitmap SoftwareBitmapFromBitmap(Bitmap bmp)
    {
        byte[] png;
        using (MemoryStream ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            png = ms.ToArray();
        }

        Windows.Storage.Streams.InMemoryRandomAccessStream stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        try
        {
            Windows.Storage.Streams.DataWriter writer = new Windows.Storage.Streams.DataWriter(stream);
            writer.WriteBytes(png);
            Wait(writer.StoreAsync(), 15000);
            writer.DetachStream();
            writer.Dispose();

            BitmapDecoder decoder = Wait(BitmapDecoder.CreateAsync(stream), 15000);
            SoftwareBitmap sw = Wait(decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore), 15000);
            return sw;
        }
        finally
        {
            try { stream.Dispose(); } catch { }
        }
    }
}

// ============================== OCR（Windows 内置离线引擎） ==============================

public static class OcrHelper
{
    public static string LastError = "";

    static Bitmap ResizeBitmap(Bitmap src, double factor)
    {
        int w = Math.Max(1, (int)Math.Round(src.Width * factor));
        int h = Math.Max(1, (int)Math.Round(src.Height * factor));
        Bitmap dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(dst))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(src, new Rectangle(0, 0, w, h), new Rectangle(0, 0, src.Width, src.Height), GraphicsUnit.Pixel);
        }
        return dst;
    }

    /// <summary>识别图像中的文字。返回 null = 失败（见 LastError）；空串 = 未识别到。</summary>
    public static string Recognize(Bitmap bmp)
    {
        LastError = "";
        OcrEngine engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null)
        {
            engine = OcrEngine.TryCreateFromLanguage(new Language("zh-Hans-CN"));
            if (engine == null) engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"));
        }
        if (engine == null)
        {
            LastError = "未找到可用的 OCR 语言包。请到 Windows 设置 → 时间和语言 → 语言 添加中文或英文语言（包含\"文字识别\"可选功能）后重试。";
            return null;
        }

        SoftwareBitmap sw = null;
        try
        {
            // 小图/小字放大后再识别（Windows OCR 对行高 <20px 的文字经常返回空）
            Bitmap input = bmp;
            bool scaled = false;
            if (bmp.Height < 80 || bmp.Width < 200)
            {
                double factor = Math.Max(80.0 / bmp.Height, 200.0 / bmp.Width);
                if (factor > 4) factor = 4;
                if (factor > 1.05)
                {
                    input = ResizeBitmap(bmp, factor);
                    scaled = true;
                    Program.Log("ocr scaled x" + factor.ToString("0.0", CultureInfo.InvariantCulture) +
                        " (" + bmp.Width + "x" + bmp.Height + " -> " + input.Width + "x" + input.Height + ")");
                }
            }

            try
            {
                sw = WinRtOps.SoftwareBitmapFromBitmap(input);
                OcrResult result = WinRtOps.Wait(engine.RecognizeAsync(sw), 30000);
                if (result == null) return "";

                StringBuilder sb = new StringBuilder();
                bool first = true;
                foreach (OcrLine line in result.Lines)
                {
                    if (!first) sb.Append("\r\n");
                    sb.Append(line.Text);
                    first = false;
                }
                return sb.ToString().Trim();
            }
            finally
            {
                if (scaled && input != null) input.Dispose();
            }
        }
        catch (Exception ex)
        {
            LastError = "OCR 调用失败：" + ex.ToString();
            return null;
        }
        finally
        {
            if (sw != null) sw.Dispose();
        }
    }
}

// ============================== 翻译（Bing 网页版流程，无需密钥） ==============================

public class TransResult
{
    public string Text;
    public string Detected;
}

public static class TranslateHelper
{
    const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36 Edg/126.0.0.0";

    static HttpClient _http;
    static string _ig = "";
    static string _iid = "";
    static string _token = "";
    static string _sub = "www.";
    static long _key;
    static long _intervalMs = 3000000;
    static DateTime _fetchedAt = DateTime.MinValue;
    static string _detected = "";

    static HttpClient Http
    {
        get
        {
            if (_http == null)
            {
                HttpClientHandler h = new HttpClientHandler();
                h.AllowAutoRedirect = true;
                h.UseCookies = true;
                h.CookieContainer = new CookieContainer();
                _http = new HttpClient(h);
                _http.Timeout = TimeSpan.FromSeconds(20);
                _http.DefaultRequestHeaders.UserAgent.ParseAdd(UA);
            }
            return _http;
        }
    }

    static bool TokenValid
    {
        get
        {
            return _token.Length > 0 && (DateTime.UtcNow - _fetchedAt).TotalMilliseconds < (_intervalMs - 120000);
        }
    }

    static async Task EnsureConfigAsync(bool force)
    {
        if (!force && TokenValid) return;
        await RefreshConfigAsync();
    }

    static async Task RefreshConfigAsync()
    {
        HttpResponseMessage resp = await Http.GetAsync("https://www.bing.com/translator");
        string page = await resp.Content.ReadAsStringAsync();
        Uri final = resp.RequestMessage.RequestUri;
        string sub = "www.";
        if (final != null && !string.IsNullOrEmpty(final.Host))
        {
            int dot = final.Host.IndexOf('.');
            if (dot > 0) sub = final.Host.Substring(0, dot) + ".";
        }

        Match m = Regex.Match(page, "IG:\"([^\"]+)\"");
        if (!m.Success) throw new Exception("无法解析翻译服务页面（IG）");
        _ig = m.Groups[1].Value;

        m = Regex.Match(page, "data-iid=\"([^\"]+)\"");
        _iid = m.Success ? m.Groups[1].Value : "translator.5023";

        m = Regex.Match(page, "params_AbusePreventionHelper\\s*=\\s*\\[([^\\]]+)\\]");
        if (!m.Success) throw new Exception("无法解析翻译服务页面（token）");
        string[] parts = m.Groups[1].Value.Split(',');
        if (parts.Length < 3) throw new Exception("无法解析翻译服务页面（token 字段）");
        _key = long.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
        _token = parts[1].Trim().Trim('"');
        _intervalMs = long.Parse(parts[2].Trim(), CultureInfo.InvariantCulture);
        _sub = sub;
        _fetchedAt = DateTime.UtcNow;
    }

    static async Task<string> PostOnce(string text, string to)
    {
        string url = "https://" + _sub + "bing.com/ttranslatev3?isVertical=1&IG=" +
            Uri.EscapeDataString(_ig) + "&IID=" + Uri.EscapeDataString(_iid);

        List<KeyValuePair<string, string>> pairs = new List<KeyValuePair<string, string>>();
        pairs.Add(new KeyValuePair<string, string>("fromLang", "auto-detect"));
        pairs.Add(new KeyValuePair<string, string>("text", text));
        pairs.Add(new KeyValuePair<string, string>("to", to));
        pairs.Add(new KeyValuePair<string, string>("token", _token));
        pairs.Add(new KeyValuePair<string, string>("key", _key.ToString(CultureInfo.InvariantCulture)));
        pairs.Add(new KeyValuePair<string, string>("tryFetchingGenderDebiasedTranslations", "true"));

        using (FormUrlEncodedContent content = new FormUrlEncodedContent(pairs))
        using (HttpResponseMessage resp = await Http.PostAsync(url, content))
        {
            string body = await resp.Content.ReadAsStringAsync();
            int code = (int)resp.StatusCode;
            if (code == 400 || code == 401) return null; // token 失效 → 上层刷新后重试
            if (!resp.IsSuccessStatusCode) throw new Exception("翻译服务返回 " + code);
            if (body.IndexOf("\"translations\"", StringComparison.Ordinal) < 0)
            {
                if (body.IndexOf("ShowCaptcha", StringComparison.Ordinal) >= 0)
                    throw new Exception("翻译请求过于频繁，请稍后再试");
                return null;
            }

            JavaScriptSerializer ser = new JavaScriptSerializer();
            object[] root = ser.Deserialize<object[]>(body);
            if (root == null || root.Length == 0) return null;
            Dictionary<string, object> first = root[0] as Dictionary<string, object>;
            if (first == null) return null;

            object[] transArr = first.ContainsKey("translations") ? first["translations"] as object[] : null;
            if (transArr == null || transArr.Length == 0) return null;
            Dictionary<string, object> t0 = transArr[0] as Dictionary<string, object>;
            if (t0 == null || !t0.ContainsKey("text")) return null;

            _detected = "";
            if (first.ContainsKey("detectedLanguage"))
            {
                Dictionary<string, object> dl = first["detectedLanguage"] as Dictionary<string, object>;
                if (dl != null && dl.ContainsKey("language")) _detected = (dl["language"] as string) ?? "";
            }
            return (t0["text"] as string) ?? "";
        }
    }

    /// <summary>翻译文本。to 为目标语言代码（zh-Hans / en / ja / ko / ru / fr / de / es）。</summary>
    public static async Task<TransResult> TranslateAsync(string text, string to)
    {
        if (text == null) text = "";
        text = text.Trim();
        if (text.Length == 0)
        {
            TransResult empty = new TransResult();
            empty.Text = "";
            return empty;
        }
        if (text.Length > 4000) text = text.Substring(0, 4000);

        await EnsureConfigAsync(false);
        string result = await PostOnce(text, to);
        if (result == null)
        {
            await EnsureConfigAsync(true);   // token 失效 → 刷新重试一次
            result = await PostOnce(text, to);
        }
        if (result == null) throw new Exception("无法连接翻译服务，请检查网络");

        TransResult r = new TransResult();
        r.Text = result;
        r.Detected = _detected;
        return r;
    }
}

// ============================== 结果窗口静态入口 ==============================

// 弹窗入口（与 Main 同类拆分）：覆盖层销毁后仍可弹结果窗
static partial class Program
{
    public static void ShowOcrResult(string text, string status)
    {
        using (ResultForm f = new ResultForm(false, text, null, status))
        {
            f.ShowDialog();
        }
    }

    public static void ShowTranslateResult(string source, string target)
    {
        using (ResultForm f = new ResultForm(true, source, target, null))
        {
            f.ShowDialog();
        }
    }
}
