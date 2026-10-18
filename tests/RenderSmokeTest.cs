using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using AdbDeviceVitals;
using AdbDeviceVitals.Models;

internal static class RenderSmokeTest
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using (MainForm form = new MainForm())
        {
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(20000, 20000);
            form.Show();
            Application.DoEvents();

            DeviceSnapshot sample = new DeviceSnapshot();
            sample.Timestamp = DateTime.Now;
            sample.Connected = true;
            sample.DeviceModel = "M2102K1AC";
            sample.DeviceSerial = "b522a8f6";
            sample.BatteryLevel = 100;
            sample.BatteryTemperatureC = 35.0;
            sample.BatteryVoltageV = 4.392;
            sample.LearnedCapacityMah = 4272;
            sample.BatteryStatus = "Charging";
            sample.BatteryHealth = "Good";
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

            MethodInfo update = typeof(MainForm).GetMethod("UpdateUi", BindingFlags.Instance | BindingFlags.NonPublic);
            if (update == null) return 3;
            for (int i = 0; i < 12; i++)
            {
                sample.Timestamp = DateTime.Now.AddSeconds(i);
                sample.BatteryTemperatureC = 32.0 + i * 0.4;
                sample.RsrpDbm = -96 + i;
                update.Invoke(form, new object[] { sample });
            }
            Application.DoEvents();

            using (Bitmap bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(args[0], ImageFormat.Png);
            }
            form.Close();
        }
        return 0;
    }
}
