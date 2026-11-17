using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AdbDeviceVitals;
using AdbDeviceVitals.Models;
using AdbDeviceVitals.Services;

/// <summary>
/// WPF 版渲染冒烟：在真实的 Application.Run 消息循环里构造三个窗口并注入样例数据。
/// Mica 背景由 DWM 绘制，RenderTargetBitmap 只能拿到 WPF 可视树（会截成黑图），
/// 因此窗口放到屏幕内、置顶后用 CopyFromScreen 截真实画面。
/// </summary>
internal static class RenderSmokeTest
{
    private static string[] _args;
    private static int _result = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out RECT rectangle);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, int flags, int extraInfo);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(int flags, int dx, int dy, int data, int extraInfo);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 5) return 2;
        _args = args;
        App application = new App();
        application.InitializeComponent();
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        application.Startup += OnStartup;
        application.Run();
        return _result;
    }

    private static void OnStartup(object sender, StartupEventArgs e)
    {
        try
        {
            RenderMainWindow(_args[0]);
            RenderSettings(_args[1]);
            RenderSms(_args[2]);
            RenderNetworkDetails(_args[3]);
            if (_args.Length > 6) RenderBatteryDetails(_args[6]);
            _result = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("RENDER FAILED: " + ex);
            _result = 1;
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }

    private static void RenderMainWindow(string path)
    {
        MainWindow window = Prepare(new MainWindow());
        DeviceSnapshot sample = BuildSample();

        MethodInfo update = typeof(MainWindow).GetMethod("UpdateUi", BindingFlags.Instance | BindingFlags.NonPublic);
        if (update == null) throw new MissingMethodException("MainWindow.UpdateUi");
        for (int i = 0; i < 12; i++)
        {
            sample.Timestamp = DateTime.Now.AddSeconds(i);
            sample.BatteryTemperatureC = 32.0 + i * 0.4;
            sample.RsrpDbm = -96 + i;
            update.Invoke(window, new object[] { sample });
        }

        MethodInfo applyHotspot = typeof(MainWindow).GetMethod("ApplyPcHotspotSnapshot", BindingFlags.Instance | BindingFlags.NonPublic);
        if (applyHotspot != null)
            applyHotspot.Invoke(window, new object[] { PcHotspotSnapshot.Available(PcHotspotState.On, "VITALS-TEST", 2, 8, "以太网 2") });

        MethodInfo applyPublicIp = typeof(MainWindow).GetMethod("ApplyPublicIp", BindingFlags.Instance | BindingFlags.NonPublic);
        if (applyPublicIp != null)
            applyPublicIp.Invoke(window, new object[]
            {
                "223.160.130.43",
                "240a:4282:9000:24b2:dd06:1741:277f:f02c",
                DateTime.Now
            });

        // 第三张卡片只留电量 + 温度两个结论，供电 / 电压 / 容量等旧控件必须只在 ⓘ 弹窗里。
        System.Windows.Controls.TextBlock battery = NamedElement(window, "CardBatteryValue") as System.Windows.Controls.TextBlock;
        if (battery == null || battery.Text != "100%")
            throw new InvalidOperationException("Battery card should show the level conclusion.");
        System.Windows.Controls.TextBlock temperature = NamedElement(window, "CardTemperatureValue") as System.Windows.Controls.TextBlock;
        if (temperature == null || !temperature.Text.EndsWith(" °C", StringComparison.Ordinal))
            throw new InvalidOperationException("Battery card should show the temperature conclusion.");
        if (NamedElement(window, "CardThermalValue") != null ||
            NamedElement(window, "CardCapacityText") != null)
            throw new InvalidOperationException("Battery detail controls should live in the ⓘ window only.");

        // 广电余量卡：注入样例读数，确认第二张卡展示剩余流量 + 话费。
        MethodInfo applyBroadnet = typeof(MainWindow).GetMethod("ApplyBroadnetCard",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (applyBroadnet == null) throw new MissingMethodException("MainWindow.ApplyBroadnetCard");
        applyBroadnet.Invoke(window, new object[] { "162.4 G", "话费 85.40 元" });
        System.Windows.Controls.TextBlock remain = NamedElement(window, "BroadnetRemainValue")
            as System.Windows.Controls.TextBlock;
        if (remain == null || remain.Text != "162.4 G")
            throw new InvalidOperationException("Broadnet card should show remaining traffic.");
        System.Windows.Controls.TextBlock balance = NamedElement(window, "BroadnetBalanceValue")
            as System.Windows.Controls.TextBlock;
        if (balance == null || !balance.Text.Contains("话费"))
            throw new InvalidOperationException("Broadnet card should show phone balance.");

        Save(window, path);

        // 按 Alt 会触发 WPF 的键盘焦点框；铺满客户区的容器被聚焦时只在标题栏下
        // 露出上边，看起来就是一条黑线。ADV_ALT_BASELINE=1 时跳过焦点样式断言，
        // 便于临时回退修复做对照实验（只保留 Alt 截图的像素统计）。
        if (!string.Equals(Environment.GetEnvironmentVariable("ADV_ALT_BASELINE"), "1",
            StringComparison.Ordinal))
            VerifyFocusVisualPolicy(window);

        // 三张卡片都靠真实 MouseEnter/MouseLeave 控制右上角 ⓘ，逐个验证并同时截图。
        if (_args.Length > 4) VerifyInfoHover(window, _args[4]);

        // 真实按下 Alt 并保持，复现用户反馈的「顶部黑条」；截图后逐像素检查客户区第一行。
        if (_args.Length > 5) VerifyAltKey(window, _args[5]);
    }

    private static UIElement NamedElement(object target, string name)
    {
        FieldInfo field = target.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return field == null ? null : field.GetValue(target) as UIElement;
    }

    private static void VerifyInfoHover(MainWindow window, string path)
    {
        string[] cardNames = { "NetworkCard", "BroadnetCard", "BatteryCard" };
        string[] buttonNames = { "NetworkInfoButton", "BroadnetInfoButton", "BatteryInfoButton" };
        UIElement[] cards = new UIElement[cardNames.Length];
        UIElement[] buttons = new UIElement[cardNames.Length];
        for (int i = 0; i < cardNames.Length; i++)
        {
            cards[i] = NamedElement(window, cardNames[i]);
            buttons[i] = NamedElement(window, buttonNames[i]);
            if (cards[i] == null || buttons[i] == null)
                throw new InvalidOperationException(cardNames[i] + " / " + buttonNames[i] + " not found.");
            if (buttons[i].Visibility != Visibility.Hidden)
                throw new InvalidOperationException(buttonNames[i] + " should start hidden.");
        }
        for (int i = 0; i < cardNames.Length; i++)
        {
            cards[i].RaiseEvent(new System.Windows.Input.MouseEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0)
            {
                RoutedEvent = UIElement.MouseEnterEvent
            });
            if (buttons[i].Visibility != Visibility.Visible)
                throw new InvalidOperationException(buttonNames[i] + " did not show on hover.");
        }
        Save(window, path); // 三个 ⓘ 同时可见
        for (int i = 0; i < cardNames.Length; i++)
        {
            cards[i].RaiseEvent(new System.Windows.Input.MouseEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0)
            {
                RoutedEvent = UIElement.MouseLeaveEvent
            });
            if (buttons[i].Visibility != Visibility.Hidden)
                throw new InvalidOperationException(buttonNames[i] + " did not hide on leave.");
        }
    }

    private static void VerifyAltKey(Window window, string path)
    {
        window.Activate();
        DoEvents();
        // 用户反馈的路径是「先点过界面，再按 Alt」：先真实点击客户区空白处，
        // 让 WPF 的焦点落到一个铺满窗口的容器上，再模拟按键。
        RECT rect;
        if (GetWindowRect(new WindowInteropHelper(window).Handle, out rect))
        {
            SetCursorPos(rect.Left + 200, rect.Top + 900);
            mouse_event(0x02, 0, 0, 0, 0); // left down
            mouse_event(0x04, 0, 0, 0, 0); // left up
            DoEvents();
            Thread.Sleep(80);
            DoEvents();
        }
        keybd_event(0x12, 0, 0, 0); // VK_MENU 按下
        try
        {
            DoEvents();
            Thread.Sleep(150);
            DoEvents();
            Save(window, path);
        }
        finally
        {
            keybd_event(0x12, 0, 2, 0); // KEYEVENTF_KEYUP
            DoEvents();
        }

        // 离屏渲染拿不到 DWM / 键盘焦点装饰，只在真实截屏下做像素断言。
        if (string.Equals(Environment.GetEnvironmentVariable("ADV_RENDER_OFFSCREEN"), "1",
            StringComparison.Ordinal)) return;
        using (Bitmap bitmap = new Bitmap(path))
        {
            int row = 72; // 150% DPI 下 48px 标题栏 = 72 物理像素，客户区从这里开始
            if (row >= bitmap.Height) return;
            int dark = 0;
            int samples = 0;
            for (int x = 4; x < bitmap.Width - 4; x += 4)
            {
                samples++;
                if (bitmap.GetPixel(x, row).R < 120) dark++;
            }
            Console.WriteLine("ALT KEY: dark pixels in client row " + row + " = " + dark + "/" + samples);
            if (samples > 0 && dark * 3 > samples * 2)
                throw new InvalidOperationException("Alt key still draws a dark bar under the title bar.");
        }
    }

    private static void RenderNetworkDetails(string path)
    {
        NetworkDetailsWindow window = Prepare(new NetworkDetailsWindow());
        DeviceSnapshot sample = BuildSample();
        sample.NetworkType = "NR_SA";
        sample.OperatorName = "中国广电";
        sample.RsrpDbm = -82;
        sample.RsrqDb = -10;
        sample.SinrDb = 13;
        sample.SignalLevel = 3;
        sample.DeviceModel = "M2102K1AC";
        sample.DeviceSerial = "vice-a8f6";
        sample.UpstreamInterface = "rmnet_data2";
        window.Apply(sample, "223.160.130.43",
            "240a:4282:9000:24b2:dd06:1741:277f:f02c", DateTime.Now);
        // 「网速」卡片的原悬停内容必须完整搬进弹窗。
        System.Windows.Controls.TextBlock mode = NamedElement(window, "TetherModeText") as System.Windows.Controls.TextBlock;
        System.Windows.Controls.TextBlock detail = NamedElement(window, "TetherDetailText") as System.Windows.Controls.TextBlock;
        if (mode == null || detail == null || mode.Text != "USB + Wi-Fi" || !detail.Text.Contains("上游"))
            throw new InvalidOperationException("Tethering details missing from network details window.");
        Save(window, path);
        window.FocusTethering(); // 从「网速」ⓘ 进入时的滚动定位不应抛异常
        DoEvents();
    }

    private static void RenderBatteryDetails(string path)
    {
        BatteryDetailsWindow window = Prepare(new BatteryDetailsWindow());
        DeviceSnapshot sample = BuildSample();
        sample.BatteryTemperatureC = 32.0;
        sample.LearnedCapacityMah = 4093;
        window.Apply(sample);

        // 从电池卡片移走的细节一条都不能丢。
        string[,] expected =
        {
            { "LevelText", "100%" },
            { "StatusText", "充电中" },
            { "PowerText", "USB 供电" },
            { "VoltageText", "4.392 V" },
            { "HealthText", "良好" },
            { "TemperatureText", "32.0 °C" },
            { "ThermalText", "正常" },
            { "CapacityText", "4093 mAh" }
        };
        for (int i = 0; i < expected.GetLength(0); i++)
        {
            System.Windows.Controls.TextBlock block =
                NamedElement(window, expected[i, 0]) as System.Windows.Controls.TextBlock;
            if (block == null || block.Text != expected[i, 1])
                throw new InvalidOperationException(expected[i, 0] + " missing from battery details window.");
        }
        Save(window, path);
    }

    private static void RenderSettings(string path)
    {
        // 传 null ADB：Loaded 里只显示「未检测到 ADB」，不会真的启动 adb.exe。
        SettingsWindow window = Prepare(new SettingsWindow(null, delegate { return Task.CompletedTask; }));
        Save(window, path);
    }

    private static void RenderSms(string path)
    {
        DateTime now = DateTime.Now;
        SmsMessage[] messages =
        {
            new SmsMessage(1, "10086", "本月账单已出，回复 CX 查询。", 0, now.AddMinutes(-8)),
            new SmsMessage(2, "95588", "验证码 482516，5 分钟内有效，请勿转发。", 0, now.AddMinutes(-3)),
            new SmsMessage(3, "10690", "您的快递已到达\n请在 24 小时内取件。", 0, now)
        };
        SmsWindow window = Prepare(new SmsWindow(delegate { return messages; }, delegate { }));
        Save(window, path);
    }

    private static void VerifyFocusVisualPolicy(Window window)
    {
        MethodInfo register = typeof(App).GetMethod("RegisterFocusVisualPolicy",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        MethodInfo suppress = typeof(App).GetMethod("SuppressFocusVisuals",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if (register == null || suppress == null)
            throw new MissingMethodException("App focus-visual policy methods not found.");
        register.Invoke(null, null);
        suppress.Invoke(null, new object[] { window });
        FrameworkElement rootGrid = window.Content as FrameworkElement;
        if (rootGrid == null) throw new InvalidOperationException("Main window content missing.");
        // 人为挂一个焦点样式，再模拟一次键盘焦点事件，class handler 必须把它清成 null。
        rootGrid.FocusVisualStyle = new Style(typeof(FrameworkElement));
        rootGrid.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(
            System.Windows.Input.Keyboard.PrimaryDevice, 0, null, rootGrid)
        {
            RoutedEvent = System.Windows.Input.Keyboard.PreviewGotKeyboardFocusEvent
        });
        if (rootGrid.FocusVisualStyle != null)
            throw new InvalidOperationException("Keyboard focus visual is not suppressed.");
        int elements = AssertNoFocusVisual(window);
        if (elements < 10)
            throw new InvalidOperationException("Visual tree too small for focus-visual check: " + elements);
    }

    private static int AssertNoFocusVisual(DependencyObject root)
    {
        int count = 0;
        FrameworkElement element = root as FrameworkElement;
        if (element != null)
        {
            count++;
            if (element.FocusVisualStyle != null)
                throw new InvalidOperationException("FocusVisualStyle should be null on " + element.GetType().Name);
        }
        int children = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < children; i++)
            count += AssertNoFocusVisual(VisualTreeHelper.GetChild(root, i));
        return count;
    }

    private static T Prepare<T>(T window) where T : Window
    {
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 90;
        window.Top = 60;
        window.Topmost = true;
        window.Show();
        window.Activate();
        DoEvents();
        return window;
    }

    private static void Save(Window window, string path)
    {
        window.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Render, new Action(delegate { }));
        DoEvents();
        Thread.Sleep(250);
        DoEvents();

        // 锁屏 / 会话不可见时 CopyFromScreen 只能抓到壁纸。设置环境变量
        // ADV_RENDER_OFFSCREEN=1 改用 RenderTargetBitmap 验证控件布局（拿不到 Mica 背景）。
        if (string.Equals(Environment.GetEnvironmentVariable("ADV_RENDER_OFFSCREEN"), "1",
            StringComparison.Ordinal))
        {
            SaveOffscreen(window, path);
            return;
        }

        IntPtr handle = new WindowInteropHelper(window).Handle;
        RECT rectangle;
        if (!GetWindowRect(handle, out rectangle)) throw new InvalidOperationException("GetWindowRect failed.");
        int width = Math.Max(1, rectangle.Right - rectangle.Left);
        int height = Math.Max(1, rectangle.Bottom - rectangle.Top);

        using (Bitmap bitmap = new Bitmap(width, height))
        {
            using (Graphics graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(rectangle.Left, rectangle.Top, 0, 0, new System.Drawing.Size(width, height));
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            bitmap.Save(path, ImageFormat.Png);
        }
        Console.WriteLine("RENDERED: " + path + " (" + width + "x" + height + ")");
    }

    private static void SaveOffscreen(Window window, string path)
    {
        // Mica 是 DWM 画的，离屏渲染里给窗口一个实色底，保证文字与卡片可读。
        window.Background = System.Windows.Media.Brushes.White;
        window.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Render, new Action(delegate { }));
        DoEvents();

        int width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);

        PngBitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            encoder.Save(stream);
        Console.WriteLine("RENDERED (offscreen): " + path + " (" + width + "x" + height + ")");
    }

    private static void DoEvents()
    {
        DispatcherFrame frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new DispatcherOperationCallback(delegate(object state)
            {
                ((DispatcherFrame)state).Continue = false;
                return null;
            }), frame);
        Dispatcher.PushFrame(frame);
    }

    private static DeviceSnapshot BuildSample()
    {
        DeviceSnapshot sample = new DeviceSnapshot();
        sample.Timestamp = DateTime.Now;
        sample.Connected = true;
        sample.DeviceModel = "M2102K1AC";
        sample.DeviceSerial = "device-a";
        sample.BatteryLevel = 100;
        sample.BatteryTemperatureC = 35.0;
        sample.BatteryVoltageV = 4.392;
        sample.LearnedCapacityMah = 4272;
        sample.BatteryStatus = "充电中";
        sample.BatteryHealth = "良好";
        sample.UsbPowered = true;
        sample.ThermalStatus = 0;
        sample.OperatorName = "中国广电";
        sample.NetworkType = "NR_SA";
        sample.RsrpDbm = -79;
        sample.RsrqDb = -11;
        sample.SinrDb = 18;
        sample.HotspotActive = true;
        sample.HotspotBand = "2.4 GHz";
        sample.HotspotFrequencyMhz = 2412;
        sample.ConnectedClients = 1;
        sample.UsbTetheringActive = true;
        sample.UsbTetheringClients = 1;
        sample.UpstreamInterface = "rmnet_data2";
        sample.DownloadMbps = 25.31;
        sample.UploadMbps = 3.20;
        return sample;
    }
}
