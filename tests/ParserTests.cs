using System;
using System.Collections.Generic;
using AdbDeviceVitals.Models;
using AdbDeviceVitals.Parsing;
using AdbDeviceVitals.Services;

internal static class ParserTests
{
    private static int _failed;

    private static int Main()
    {
        Battery();
        Telephony();
        Hotspot();
        UsbTethering();
        NetworkStats();
        DeviceSelectionTests();
        Updates();
        Console.WriteLine(_failed == 0 ? "All parser tests passed." : _failed + " parser test(s) failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static void Battery()
    {
        DeviceSnapshot value = new DeviceSnapshot();
        AdbParsers.ParseBattery("USB powered: true\nstatus: 2\nhealth: 2\nlevel: 100\nvoltage: 4383\ntemperature: 350\n", value);
        Equal(100, value.BatteryLevel, "battery level");
        Equal(35.0, value.BatteryTemperatureC, "battery temperature");
        Equal(4.383, value.BatteryVoltageV, "battery voltage");
        Equal("充电中", value.BatteryStatus, "battery status");
        Equal(true, value.UsbPowered, "USB power");
    }

    private static void Telephony()
    {
        DeviceSnapshot value = new DeviceSnapshot();
        string sample = "mSignalStrength=SignalStrength:{mNr=CellSignalStrengthNr:{ csiRsrp = 2147483647 ssRsrp = -79 ssRsrq = -11 ssSinr = 18 miuiLevel = 5 level = 3 },primary=CellSignalStrengthNr}";
        AdbParsers.ParseTelephony(sample, value);
        Equal(-79, value.RsrpDbm, "NR RSRP");
        Equal(-11, value.RsrqDb, "NR RSRQ");
        Equal(18, value.SinrDb, "NR SINR");
        Equal(5, value.SignalLevel, "signal level");
    }

    private static void Hotspot()
    {
        DeviceSnapshot value = new DeviceSnapshot();
        AdbParsers.ParseTethering("wlan1 - TetheredState - lastError = 0\nCurrent upstream interface(s): [rmnet_data2]\n{name=wlan1 state=TetheredState={client: /192.168.1.2}}", value);
        AdbParsers.ParseWifi("mCurrentSoftApInfoMap {wlan1=SoftApInfo{bandwidth= 2, frequency= 2412}}", value);
        Equal(true, value.HotspotActive, "hotspot active");
        Equal(1, value.ConnectedClients, "hotspot clients");
        Equal("rmnet_data2", value.UpstreamInterface, "upstream");
        Equal("2.4 GHz", value.HotspotBand, "hotspot band");
    }

    private static void UsbTethering()
    {
        DeviceSnapshot value = new DeviceSnapshot();
        string sample = "rndis0 - TetheredState - lastError = 0\n" +
            "wlan1 - TetheredState - lastError = 0\n" +
            "Current upstream interface(s): [rmnet_data2]\n" +
            "{name=rndis0 state=TetheredState={client: /192.168.114.101}}\n" +
            "{name=wlan1 state=TetheredState={client: /192.168.148.2, client: /192.168.148.3}}";
        AdbParsers.ParseTethering(sample, value);
        Equal(true, value.UsbTetheringActive, "USB tethering active");
        Equal(1, value.UsbTetheringClients, "USB tethering clients");
        Equal(true, value.HotspotActive, "simultaneous hotspot active");
        Equal(2, value.ConnectedClients, "simultaneous hotspot clients");
        Equal("rmnet_data2", value.UpstreamInterface, "USB tethering upstream");

        AdbParsers.ParseTethering("Tether state:\n", value);
        Equal(false, value.UsbTetheringActive, "USB tethering reset");
        Equal(0, value.UsbTetheringClients, "USB tethering clients reset");
        Equal(false, value.HotspotActive, "hotspot reset");
        Equal(string.Empty, value.UpstreamInterface, "upstream reset");

        Equal(true, AdbParsers.IsUsbTetheringReady(sample), "usb tether ready");
        Equal(true, AdbParsers.IsUsbTetheringReady("  ncm0 - TetheredState - lastError = 0"), "ncm tether ready");
        Equal(false, AdbParsers.IsUsbTetheringReady("rndis0 - TetheredState - lastError = 11"), "usb tether error");
        Equal(false, AdbParsers.IsUsbTetheringReady("rndis0 - TetheredState"), "usb tether without error code");
        Equal(false, AdbParsers.IsUsbTetheringReady("wlan0 - TetheredState - lastError = 0"), "wifi is not usb tether");
    }

    private static void NetworkStats()
    {
        IDictionary<string, InterfaceCounters> values = AdbParsers.ParseNetworkStats("rmnet_data2: 295115263 149565 0 0 0 0 0 0 47949594 149720 0 0 0 0 0 0");
        Equal(295115263L, values["rmnet_data2"].ReceiveBytes, "rx bytes");
        Equal(47949594L, values["rmnet_data2"].TransmitBytes, "tx bytes");
    }

    private static void Updates()
    {
        Version current = new Version(0, 0, 3, 0);
        Equal(true, UpdateChecker.IsNewerVersion("v0.0.4", current), "newer tag");
        Equal(false, UpdateChecker.IsNewerVersion("v0.0.3", current), "same tag");
        Equal(false, UpdateChecker.IsNewerVersion("v0.0.2", current), "older tag");
        Equal(false, UpdateChecker.IsNewerVersion("nope", current), "invalid tag");
        Equal(true, UpdateChecker.HashMatches("abc  AdbDeviceVitals-win-x64.zip\n", UpdateChecker.PackageFileName, "abc"), "hash match");
        Equal(false, UpdateChecker.HashMatches("def  AdbDeviceVitals-win-x64.zip\n", UpdateChecker.PackageFileName, "abc"), "hash mismatch");
        Equal(true, UpdateChecker.IsDevelopmentBuild(@"D:\src\AdbDeviceVitals\bin\Release\AdbDeviceVitals.exe"), "dev release path");
        Equal(false, UpdateChecker.IsDevelopmentBuild(@"C:\Users\ash\Desktop\AdbDeviceVitals.exe"), "desktop path");
        Equal(true, UpdateChecker.ShouldCheckAutomatically(@"C:\Users\ash\Desktop\AdbDeviceVitals.exe"), "installed exe");
        Equal(false, UpdateChecker.ShouldCheckAutomatically(@"D:\src\AdbDeviceVitals\bin\Release\AdbDeviceVitals.exe"), "dev exe");
        Equal(false, UpdateChecker.ShouldCheckAutomatically(@"D:\artifacts\tests\RenderSmokeTest.exe"), "test exe");
        Equal(true, UsbTetheringService.IsUsbFunctionChangeAcknowledged("setCurrentFunctions opId:1", string.Empty), "usb function ack");
        Equal(false, UsbTetheringService.IsUsbFunctionChangeAcknowledged("setCurrentFunctions opId:1", "Error communicating with UsbManager"), "usb function error");
        Equal(false, UsbTetheringService.IsUsbFunctionChangeAcknowledged(string.Empty, "Unknown command"), "usb function unknown");
    }

    private static void DeviceSelectionTests()
    {
        AdbDevice phone = new AdbDevice("M2102K1AC", "device", "Xiaomi 11");
        AdbDevice tablet = new AdbDevice("emulator-5554", "device", "Pixel Tablet");
        AdbDevice pending = new AdbDevice("0123456789ABCDEF", "unauthorized", "Test Phone");

        // 单设备且没有预设：保持原来的零配置行为。
        DeviceSelection single = DeviceSelection.Resolve(new List<AdbDevice> { phone }, string.Empty);
        Equal("M2102K1AC", single.Device == null ? null : single.Device.Serial, "auto single device");
        Equal(false, single.NeedsSelection, "auto single does not need a choice");
        Equal(false, single.PreferredMissing, "auto single has no missing preference");

        // 多设备且没有预设：必须让用户先选，不能默认拿第一台。
        DeviceSelection many = DeviceSelection.Resolve(new List<AdbDevice> { phone, tablet }, string.Empty);
        Equal(null, many.Device, "ambiguous devices has no target");
        Equal(true, many.NeedsSelection, "ambiguous devices needs a choice");
        Equal(2, many.AuthorizedCount, "ambiguous authorized count");

        // 预设命中列表里的第二台：顺序变化时仍然锁定它。
        DeviceSelection pinned = DeviceSelection.Resolve(new List<AdbDevice> { phone, tablet }, "emulator-5554");
        Equal("emulator-5554", pinned.Device == null ? null : pinned.Device.Serial, "pinned device wins over first");
        Equal(false, pinned.NeedsSelection, "pinned device is ready");

        // 记录的设备不在当前列表（只剩一台别的手机）：绝不悄悄降级到那一台。
        DeviceSelection missing = DeviceSelection.Resolve(new List<AdbDevice> { tablet }, "M2102K1AC");
        Equal(null, missing.Device, "missing preference never falls back");
        Equal(true, missing.PreferredMissing, "missing preference flagged");
        Equal(true, missing.NeedsSelection, "missing preference needs re-selection");
        Equal(true, missing.Message.IndexOf("重新选择", StringComparison.Ordinal) >= 0, "missing preference asks to reselect");

        // 多设备且预设不命中，同样要求重新选。
        DeviceSelection missingMany = DeviceSelection.Resolve(new List<AdbDevice> { phone, tablet }, "gone-1234");
        Equal(null, missingMany.Device, "missing preference with several devices has no target");
        Equal(true, missingMany.PreferredMissing, "missing preference with several devices flagged");

        // 预设设备在线但只是未授权：不能当成可用目标。
        DeviceSelection notAuthorized = DeviceSelection.Resolve(new List<AdbDevice> { pending, tablet }, "0123456789ABCDEF");
        Equal(null, notAuthorized.Device, "unauthorized preference is not usable");
        Equal(true, notAuthorized.PreferredMissing, "unauthorized preference treated as missing");
        Equal(1, notAuthorized.UnauthorizedCount, "unauthorized count");

        // 没有已授权设备时给原始原因，不弹「需要选择设备」。
        DeviceSelection none = DeviceSelection.Resolve(new List<AdbDevice> { pending }, string.Empty);
        Equal(null, none.Device, "no authorized device");
        Equal(false, none.NeedsSelection, "unauthorized only is not a selection problem");
        Equal("请在手机上允许这台电脑进行 USB 调试。", none.Message, "unauthorized message");
        DeviceSelection empty = DeviceSelection.Resolve(new List<AdbDevice>(), string.Empty);
        Equal("未找到已授权的 Android 设备。", empty.Message, "empty device list message");

        Equal(true, DeviceSelection.IsAuthorized(phone), "device state authorized");
        Equal(false, DeviceSelection.IsAuthorized(pending), "unauthorized state rejected");
        Equal(true, DeviceSelection.IsValidSerial("emulator-5554"), "valid serial");
        Equal(false, DeviceSelection.IsValidSerial("bad serial; rm -rf"), "serial injection rejected");
        Equal(false, DeviceSelection.IsValidSerial(string.Empty), "empty serial rejected");
        Equal("01234567…EF00", DeviceSelection.ShortSerial("0123456789ABCDEF00"), "short serial");
        Equal("0123456789ABCDEF", DeviceSelection.ShortSerial("0123456789ABCDEF"), "short serial keeps 16 chars");
    }

    private static void Equal(object expected, object actual, string name)
    {
        if (!object.Equals(expected, actual))
        {
            Console.Error.WriteLine("FAIL " + name + ": expected " + expected + ", got " + actual);
            _failed++;
        }
    }
}
