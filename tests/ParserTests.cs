using System;
using System.Collections.Generic;
using AdbDeviceVitals.Models;
using AdbDeviceVitals.Parsing;

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
        Equal("Charging", value.BatteryStatus, "battery status");
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

    private static void Equal(object expected, object actual, string name)
    {
        if (!object.Equals(expected, actual))
        {
            Console.Error.WriteLine("FAIL " + name + ": expected " + expected + ", got " + actual);
            _failed++;
        }
    }
}
