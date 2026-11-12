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

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 3) return 2;
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
