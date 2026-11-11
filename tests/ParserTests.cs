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
        SettingsFileTests();
        SmsParserTests();
        SmsEnhancements();
        UsbTetherWatchdogTests();
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

    private static void SettingsFileTests()
    {
        // 完整往返：路径里的反斜杠、引号和中文都必须原样回来。
        SettingsData original = new SettingsData
        {
            Schema = 2,
            AdbDirectory = @"C:\工具\adb ""tools""\platform-tools",
            TargetDeviceSerial = "b522a8f6",
            RunAtStartup = true,
            AutoEnableUsbTethering = true,
            AutoRecoverUsbTethering = true,
            AutoStartPcHotspot = false,
            SmsSyncEnabled = true,
            SmsToastEnabled = false,
            SmsSenderFilter = "10086, 95555",
            LastUpdateCheckUtc = new DateTime(2026, 11, 8, 3, 0, 0, DateTimeKind.Utc)
        };
        string json = SettingsFile.Serialize(original);
        SettingsData parsed = SettingsFile.Parse(json);
        Equal(2, parsed.Schema, "settings schema");
        Equal(original.AdbDirectory, parsed.AdbDirectory, "settings adb directory escaped");
        Equal("b522a8f6", parsed.TargetDeviceSerial, "settings serial");
        Equal(true, parsed.RunAtStartup, "settings run at startup");
        Equal(true, parsed.AutoEnableUsbTethering, "settings auto usb tethering");
        Equal(true, parsed.AutoRecoverUsbTethering, "settings auto recover usb tethering");
        Equal(false, parsed.AutoStartPcHotspot, "settings auto hotspot");
        Equal(true, parsed.SmsSyncEnabled, "settings sms sync enabled");
        Equal(false, parsed.SmsToastEnabled, "settings toast flag");
        Equal("10086, 95555", parsed.SmsSenderFilter, "settings sender filter");
        Equal(true, json.IndexOf("\"SmsSyncEnabled\": true", StringComparison.Ordinal) >= 0,
            "settings sms flag serialized");
        Equal(true, json.IndexOf("\"AutoRecoverUsbTethering\": true", StringComparison.Ordinal) >= 0,
            "settings recover flag serialized");
        Equal(original.LastUpdateCheckUtc.Value.ToString("o"),
            parsed.LastUpdateCheckUtc.HasValue ? parsed.LastUpdateCheckUtc.Value.ToString("o") : null,
            "settings update timestamp");

        // 空文件 / 缺字段 / 未知字段：用默认值，不能被无关内容带偏。
        SettingsData empty = SettingsFile.Parse("{}");
        Equal(1, empty.Schema, "empty settings schema");
        Equal(string.Empty, empty.AdbDirectory, "empty settings adb directory");
        Equal(false, empty.RunAtStartup, "empty settings startup flag");
        Equal(false, empty.LastUpdateCheckUtc.HasValue, "empty settings timestamp");
        Equal(false, empty.SmsSyncEnabled, "empty settings sms sync off");
        Equal(true, empty.SmsToastEnabled, "empty settings toast on");
        Equal(string.Empty, empty.SmsSenderFilter, "empty settings sender filter");
        Equal(string.Empty, SettingsFile.Parse(string.Empty).TargetDeviceSerial, "null text is safe");
        Equal(true, SettingsFile.Parse("{\"Unknown\": \"x\", \"RunAtStartup\": true}").RunAtStartup,
            "unknown setting ignored");
        Equal(false, SettingsFile.Parse("{\"RunAtStartup\": true}").AutoRecoverUsbTethering,
            "missing recover flag defaults off in old files");
        Equal(false, SettingsFile.Parse("{\"RunAtStartup\": true}").SmsSyncEnabled,
            "missing sms flag defaults off in old files");
        Equal(false, SettingsFile.Parse("{\"SmsSyncEnabled\": false}").SmsSyncEnabled,
            "sms flag can be turned off");
        Equal(true, SettingsFile.Parse("{\"AutoRecoverUsbTethering\": true}").AutoRecoverUsbTethering,
            "recover flag parsed");

        // 写坏一项不能丢掉整份配置。
        SettingsData broken = SettingsFile.Parse("{\"AutoStartPcHotspot\": true, broken");
        Equal(true, broken.AutoStartPcHotspot, "tolerant parse keeps good fields");
        Equal(false, SettingsFile.Parse("{\"RunAtStartup\": \"yes\"}").RunAtStartup,
            "non-boolean value rejected");
        Equal(false, SettingsFile.Parse("{\"LastUpdateCheckUtc\": \"nope\"}").LastUpdateCheckUtc.HasValue,
            "bad timestamp ignored");
        Equal(true, SettingsFile.Serialize(null).IndexOf("\"schema\": 1", StringComparison.Ordinal) >= 0,
            "serialize null uses defaults");

        // \u 转义与控制字符。
        SettingsData unicode = SettingsFile.Parse("{\"TargetDeviceSerial\": \"ab\\u0063d\", \"AdbDirectory\": \"a\\tb\"}");
        Equal("abcd", unicode.TargetDeviceSerial, "unicode escape");
        Equal("a\tb", unicode.AdbDirectory, "tab escape");
        Equal(true, SettingsFile.Serialize(unicode).IndexOf("a\\tb", StringComparison.Ordinal) >= 0,
            "control character re-escaped");
    }

    private static void SmsParserTests()
    {
        string normal = "Row: 0 _id=10632, address=10099, date=1793353114426, body=您的验证码是 123456\n";
        IList<SmsMessage> rows = SmsRowParser.Parse(normal);
        Equal(1, rows.Count, "sms single row");
        Equal(10632L, rows[0].Id, "sms id");
        Equal("10099", rows[0].Address, "sms address");
        Equal(1793353114426L, rows[0].DateMs, "sms date");
        Equal("您的验证码是 123456", rows[0].Body, "sms body");
        Equal(true, rows[0].ReceivedAt != DateTime.MinValue, "sms local time");

        // 正文里的逗号、等号、甚至 body= 都不能把记录切断。
        string tricky = "Row: 0 _id=5, address=+8613800000000, date=1793353114426, body=金额=12, 剩余 3, body=abc\n"
            + "Row: 1 _id=6, address=95555, date=1793353114427, body=第二条\n";
        IList<SmsMessage> trickyRows = SmsRowParser.Parse(tricky);
        Equal(2, trickyRows.Count, "sms two rows with punctuation");
        Equal("金额=12, 剩余 3, body=abc", trickyRows[0].Body, "sms body keeps commas and equals");
        Equal("第二条", trickyRows[1].Body, "sms second row body");

        // 实测有短信正文带换行：一条 Row 会横跨多个物理行，必须并回同一条记录。
        string multiline = "Row: 0 _id=7, address=10086, date=1793353114426, body=第一行\n第二行\n"
            + "Row: 1 _id=8, address=10010, date=1793353114427, body=下一条\n";
        IList<SmsMessage> multilineRows = SmsRowParser.Parse(multiline);
        Equal(2, multilineRows.Count, "sms rows with multiline body");
        Equal("第一行\n第二行", multilineRows[0].Body, "sms multiline body preserved");
        Equal("下一条", multilineRows[1].Body, "sms row after multiline body");

        Equal(0, SmsRowParser.Parse("No result found.\n").Count, "sms no-result output");
        Equal(true, SmsRowParser.IsEmptyResult("No result found."), "sms empty result marker");
        Equal(0, SmsRowParser.Parse(string.Empty).Count, "sms empty output");
        Equal(0, SmsRowParser.Parse("Noise before rows\nRow: 0 address=1, date=2, body=x\n").Count,
            "sms row without id is skipped");
        Equal("ok", SmsRowParser.Parse("Noise before rows\nRow: 0 _id=9, address=1, date=2, body=ok\n")[0].Body,
            "sms leading noise ignored");

        Equal(true, SmsRowParser.IsPermissionDenied(
            "Permission Denial: reading com.android.providers.telephony requires android.permission.READ_SMS", string.Empty),
            "sms permission denial detected");
        Equal(true, SmsRowParser.IsPermissionDenied(string.Empty, "java.lang.SecurityException: READ_SMS"),
            "sms security exception detected");
        Equal(false, SmsRowParser.IsPermissionDenied(normal, string.Empty), "sms normal output is not a permission error");

        Equal("a b c", SmsRowParser.OneLine("a\r\nb   c\n"), "sms one-line preview");
        Equal(DateTime.MinValue, SmsRowParser.ToLocalTime(0), "sms invalid date is safe");
    }

    private static void SmsEnhancements()
    {
        // 默认值：读短信要显式开启，Windows 通知默认尝试但允许回退。
        SettingsData defaults = new SettingsData();
        Equal(false, defaults.SmsSyncEnabled, "sms sync defaults off");
        Equal(true, defaults.SmsToastEnabled, "toast defaults on");
        Equal(string.Empty, defaults.SmsSenderFilter, "sender filter defaults empty");

        string encoded = SettingsFile.Serialize(new SettingsData
        {
            SmsToastEnabled = false,
            SmsSenderFilter = "a\"b\\c"
        });
        SettingsData roundTrip = SettingsFile.Parse(encoded);
        Equal("a\"b\\c", roundTrip.SmsSenderFilter, "sender filter escaped round trip");
        Equal(false, roundTrip.SmsToastEnabled, "toast flag parsed");

        // 验证码提取：关键词优先，纯数字只在唯一数字块时认，余额/订单不算。
        Equal("123456", SmsCodeExtractor.Extract("【XX】您的验证码是 123456，10 分钟内有效。"), "sms code zh");
        Equal("482913", SmsCodeExtractor.Extract("Your code: 482913. Do not share it."), "sms code en");
        Equal("654321", SmsCodeExtractor.Extract("654321"), "sms code standalone");
        Equal(string.Empty, SmsCodeExtractor.Extract("您的订单 12345678 已发货"), "sms code skips long number");
        Equal(string.Empty, SmsCodeExtractor.Extract("账户余额 123456 元"), "sms code skips balance");
        Equal(string.Empty, SmsCodeExtractor.Extract("订单 1234，金额 5678"), "sms code skips multiple numbers");
        Equal(true, SmsCodeExtractor.HasCode("验证码 4321"), "sms code has code");
        Equal(false, SmsCodeExtractor.HasCode("纯文字通知"), "sms code plain text");

        // 发件人过滤只影响提醒，不决定收不收短信。
        Equal(4, SmsSenderFilter.Keywords("10086, 95555; 1069|abc").Length, "sender filter keyword count");
        Equal(true, SmsSenderFilter.Matches(string.Empty, "任意"), "empty filter matches all");
        Equal(true, SmsSenderFilter.Matches("10086,95555", "+8610086"), "sender filter substring");
        Equal(false, SmsSenderFilter.Matches("10086", "95555"), "sender filter excludes others");
        Equal(false, SmsSenderFilter.Matches("10086", string.Empty), "sender filter needs address");

        List<SmsMessage> batch = new List<SmsMessage>
        {
            new SmsMessage(1, "99999", "x", 1, DateTime.MinValue),
            new SmsMessage(2, "10086", "y", 2, DateTime.MinValue)
        };
        Equal(true, SmsSenderFilter.ShouldNotify("10086", batch), "sender filter notifies on match");
        Equal(false, SmsSenderFilter.ShouldNotify("95555", batch), "sender filter suppresses unmatched batch");
        Equal(false, SmsSenderFilter.ShouldNotify(string.Empty, new List<SmsMessage>()), "sender filter empty batch");
    }

    private static void UsbTetherWatchdogTests()
    {
        DateTime t = new DateTime(2026, 11, 11, 5, 0, 0, DateTimeKind.Utc);
        DeviceSnapshot on = new DeviceSnapshot { Connected = true, UsbTetheringActive = true, DeviceSerial = "device-a" };
        DeviceSnapshot off = new DeviceSnapshot { Connected = true, UsbTetheringActive = false, DeviceSerial = "device-a" };
        DeviceSnapshot offline = new DeviceSnapshot { Connected = false, UsbTetheringActive = false, DeviceSerial = "device-a" };

        // 启动后本来就没开过：守护不主动开，首次开启归「启动后自动开启」那个开关管。
        UsbTetherWatchdog cold = new UsbTetherWatchdog();
        Equal(false, cold.Observe(off, t, true, false, false), "cold start does not enable");
        Equal(false, cold.Observe(off, t.AddSeconds(120), true, false, false), "cold start stays quiet");

        // 开过之后掉线：连续确认一段时间才动手，失败后按冷却间隔重试。
        UsbTetherWatchdog warm = new UsbTetherWatchdog();
        Equal(false, warm.Observe(on, t, true, false, false), "active state is quiet");
        Equal(false, warm.Observe(off, t.AddSeconds(2), true, false, false), "first inactive sample waits");
        Equal(false, warm.Observe(off, t.AddSeconds(10), true, false, false), "check interval throttles");
        Equal(true, warm.Observe(off, t.AddSeconds(22), true, false, false), "recovers after confirm window");
        Equal(false, warm.Observe(off, t.AddSeconds(42), true, false, false), "retry cooldown blocks second attempt");
        Equal(false, warm.Observe(off, t.AddSeconds(62), true, false, false), "still cooling down");
        Equal(true, warm.Observe(off, t.AddSeconds(82), true, false, false), "retry allowed after cooldown");

        // 开关关闭、设备掉线、别的 USB 切换任务在跑：一律不插手。
        UsbTetherWatchdog guarded = new UsbTetherWatchdog();
        Equal(false, guarded.Observe(on, t, true, false, false), "guarded sees active");
        Equal(false, guarded.Observe(off, t.AddSeconds(30), false, false, false), "disabled setting blocks recovery");
        Equal(false, guarded.Observe(offline, t.AddSeconds(60), true, false, false), "offline device blocks recovery");
        Equal(false, guarded.Observe(off, t.AddSeconds(90), true, false, true), "busy switch blocks recovery");

        // 手动关过共享就暂停，重新手动打开（或在设置里重新启用守护）才恢复。
        guarded.NoteManualOff();
        Equal(false, guarded.Observe(off, t.AddSeconds(150), true, false, false), "manual off suspends recovery");
        guarded.NoteManualOn();
        Equal(false, guarded.Observe(off, t.AddSeconds(170), true, false, false), "manual on still needs confirm");
        Equal(true, guarded.Observe(off, t.AddSeconds(190), true, false, false), "manual on resumes recovery");

        // 开了「启动后自动开启」时，即使还没观察到共享开着，也按本来就该开着处理。
        UsbTetherWatchdog assumed = new UsbTetherWatchdog();
        Equal(false, assumed.Observe(off, t, true, true, false), "assumed active still waits");
        Equal(true, assumed.Observe(off, t.AddSeconds(20), true, true, false), "startup auto-enable covers a cold drop");

        // 恢复成功后重新读到已开启，不再继续折腾。
        UsbTetherWatchdog recovered = new UsbTetherWatchdog();
        Equal(false, recovered.Observe(on, t, true, false, false), "recovered sees active");
        Equal(false, recovered.Observe(off, t.AddSeconds(20), true, false, false), "recovered waits");
        Equal(true, recovered.Observe(off, t.AddSeconds(40), true, false, false), "recovered triggers");
        Equal(false, recovered.Observe(on, t.AddSeconds(60), true, false, false), "active again is quiet");
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
