# ADB Device Vitals

一个轻量的 Windows 托盘工具，通过 ADB 在本机查看 Android 设备的关键状态。

[下载最新版 Windows 便携包](https://github.com/AsherWang/adb-device-vitals/releases/latest/download/AdbDeviceVitals-win-x64.zip)（需先安装 [Microsoft Windows Desktop Runtime 10 (x64)](https://dotnet.microsoft.com/download/dotnet/10.0/runtime)）

## 使用场景

电脑没有可用的宽带或 Wi-Fi，或者需要临时改用手机流量时，用数据线把 Android 手机接到这台电脑，由手机通过 USB 网络共享给电脑上网。程序运行在电脑上，用来确认这路连接是否正常，并在需要时打开或关闭 USB 网络共享。

使用时按这个顺序准备：

1. 手机打开开发者选项中的 USB 调试。使用能传输数据的 USB 线连接电脑，不要用只能充电的线。首次连接时，在手机上允许这台电脑进行 USB 调试。
2. 打开手机系统里的 USB 网络共享（有的系统叫 USB 绑定或 USB 网络共享）。手机把自己的蜂窝网络经 USB 发给电脑，Windows 会多出一张由手机提供的网卡，电脑此后的流量走手机的移动数据。
3. 程序通过本机 ADB 查看这条链路：蜂窝网络类型和信号、电量与温度、USB 共享有没有建立、上游是哪张蜂窝接口，以及该接口的实时上传和下载速度。窗口可以最小化到托盘，电脑开着时继续观察共享是否还在。
4. 主窗口「网络共享」卡片右上角的「USB 共享」可以手动开关。打开后手机切换到 USB 网络共享；关闭后退出共享。切换时 USB 会短暂断开再重新连接，通常几秒内恢复，ADB 和电脑上的手机网卡也会跟着重现。
5. 设置中可以让程序开机自动启动，并在发现已授权的手机后自动打开 USB 网络共享。适合电脑重启后继续用同一部手机给电脑供网。
6. 同一台电脑还可以把这份网络再用 Wi-Fi 分给别的设备：底部「电脑热点」一行可以直接开关 Windows 自带的移动热点，也可以在设置里让它随开机自动开启。手机网络没就绪时不会硬开，会先等一段时间。

手机系统本身要提供 USB 网络共享。部分运营商会禁止或另计共享流量。程序不需要 Root，也不在手机上安装客户端。

## 当前功能

- 自动发现 `adb.exe` 和已授权的 Android 设备
- 电量、充电来源、电池温度、电压和系统学习容量
- Android 热状态
- 4G/5G 网络类型及 RSRP、RSRQ、SINR
- USB 网络共享与 Wi-Fi 热点状态、上游接口和各自连接设备数
- 手动开关 USB 网络共享；可开机自启动，并在设备就绪后自动打开 USB 网络共享
- USB 网络共享掉线后自动重新开启（可选开关，带防抖与重试冷却，手动关闭会暂停）
- 电脑侧 Wi-Fi 热点：开关 Windows 移动热点，把手机网络转发给其他设备，可随开机自动开启
- 短信通知同步（默认关闭）：只监听启动后的新短信，正文默认隐藏；可提取验证码、按发件人过滤提醒，并可选 Windows 通知
- 多设备时在设置里指定目标设备并记住；记录的设备掉线后提示重新选择，不会误操作其它手机
- 蜂窝接口实时下载/上传速度
- 最近状态曲线、CSV 本地日志和 Windows 异常提醒
- 最小化到系统托盘
- 单实例运行；重复启动时自动恢复已有窗口

所有数据均通过本机 ADB 读取。程序不会上传数据，也不会保存 ADB 原始输出、IMEI、MEID 或基站标识；短信正文只保留在内存中，不落盘，且默认不在界面上显示。

## 界面与技术栈

界面已整体迁移到 **WPF + .NET 10 + WPF-UI 4.3.0**，对齐 Windows 11「设置」的观感：`FluentWindow` + Mica 背景、卡片式分组、`ToggleSwitch`、侧栏导航，主面板 / 设置 / 近期短信三个窗口共用一套浅色主题。

- 目标框架 `net10.0-windows10.0.19041.0`，SDK-style 工程（`UseWPF` + `UseWindowsForms`）。托盘图标仍用 WinForms 的 `NotifyIcon`，WPF-UI 没有提供托盘控件。
- 系统 API 改为 CsWinRT 强类型调用：`NetworkOperatorTetheringManager` 管电脑热点，`Windows.UI.Notifications` 发 Toast，不再靠 WinRT 反射。
- 历史曲线 `Controls/HistoryChart.cs` 改为 WPF `FrameworkElement` + `DrawingContext`。
- 采集、配置、短信、守护、多设备仲裁等业务逻辑与之前的 WinForms 版本保持一致，迁移只换 UI 与系统 API 层。
- 设置页左侧导航是自绘的单选按钮栏：WPF-UI 的 `NavigationView` 会自带一块内容区，塞进窄侧栏时菜单文字会被裁掉。

## 运行要求

- Windows 10（1809 / 内部版本 19041 或更高）或 Windows 11，x64
- **Microsoft Windows Desktop Runtime 10 (x64)**：发布包是框架依赖的单文件 exe，不含运行时。没装时 Windows 会弹窗提示下载，也可以从上面的链接手动安装
- Android 手机已开启 USB 调试，并授权当前电脑
- ADB 已加入 PATH，或位于以下常见位置之一：
  - 程序目录
  - `%LOCALAPPDATA%\Android\Sdk\platform-tools`

也可以在程序右上角打开“设置”，手动选择包含 `adb.exe` 的 `platform-tools` 目录。所有设置保存在用户主目录下的 `adb-device-vitals\settings.json`，无需管理员权限。

程序不需要 Root，也不需要在手机安装客户端。

## 配置文件

所有设置保存在 `~/adb-device-vitals/settings.json`（Windows 上即 `%USERPROFILE%\adb-device-vitals\settings.json`），首次启动时自动创建。配置不跟着 exe 目录走，移动程序、用程序内更新替换 exe 都不会丢。

```json
{
  "schema": 1,
  "AdbDirectory": "",
  "TargetDeviceSerial": "",
  "RunAtStartup": false,
  "AutoEnableUsbTethering": false,
  "AutoRecoverUsbTethering": false,
  "AutoStartPcHotspot": false,
  "SmsSyncEnabled": false,
  "SmsToastEnabled": true,
  "SmsSenderFilter": "",
  "LastUpdateCheckUtc": null
}
```

程序只在启动、打开设置和保存设置时读写文件；`DeviceMonitor` 每 2.5 秒的轮询读的是内存缓存，不会反复碰磁盘。写入时先写同目录临时文件再整体替换，避免留下半截 JSON。设置页保存后立即生效；程序运行时手改文件，下次打开设置会重新读取。

从 0.0.6 及更早版本升级时，首次启动会把 `HKCU\Software\AdbDeviceVitals` 里的旧值迁移过来，并在旧键写一个 `MigratedTo` 标记，保证只迁移一次：之后删掉 `settings.json` 就等于重置，不会被旧值重新灌回。旧键的值保留不动，回滚到旧版本仍可用。某字段写坏了只影响那一项，其余配置照常读取。

两处例外：

- **开机自启**仍要写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，那是 Windows 的启动登记机制，不是配置。开关的真值在 `settings.json` 里，程序启动时会按它校正登记路径。
- CSV 日志与 `tether.log` 仍在 `%LOCALAPPDATA%\AdbDeviceVitals\logs`，没有跟着迁移。

## USB 网络共享掉线自动恢复

设置里的「USB 网络共享断开后自动重新开启」默认关闭。打开后，程序在每 2.5 秒的状态采集里顺带看一眼手机侧的 USB 共享状态，发现掉线就尝试重新开启：

- **只保持已经开着的状态**。程序启动后共享本来就没开时，守护不会动手；首次开启由「启动后自动开启 USB 网络共享」负责。如果那个开关也打开了，就按「本来就该开着」处理，启动瞬间的掉线也能兜住。两个开关相互独立，只开守护也能保护手动开起来的共享。
- **不靠单次读数下判断**：每 20 秒检查一次，连续 20 秒都读到未开启才认定断开，避免 `dumpsys` 输出抖动就切 USB 模式。真正执行前复用手动开启那条链路再读一次 `dumpsys tethering`，共享其实还在时只返回「已开启」，不会重复下发 `setFunctions rndis`。
- **失败不刷屏**：一次尝试之后至少隔 60 秒再试；托盘只在第一次失败时提醒，界面状态栏始终显示最新结果。
- **手动关闭优先**：在主窗口手动关掉共享后，自动恢复会暂停，直到你再次手动打开；在设置里把守护从关改成开也会解除暂停。设备掉线、或已有其它 USB 切换任务在跑时，同样不会插手。

判定用的状态机、并发与重试细节见 `docs/后续计划.md` 的「实现说明：USB 网络共享掉线自动恢复」。

## 电脑热点

主窗口底部单独有一行「电脑热点」，控制的是 **Windows 自带的移动热点**，也就是「设置 → 网络和 Internet → 移动热点」那个开关。它和上面「网络共享」卡片不是同一件事：那张卡片读的是**手机侧**的共享状态（`dumpsys tethering` / `dumpsys wifi`），这一行反映的是**电脑侧**有没有把网络发给别人。

实现方式与设置里的开关完全一致，都是 CsWinRT 强类型调用的 `Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager`，所以：

- 使用的是系统里已经配置好的热点名称和密码，程序不会去改它们；
- 热点由 Windows 的 ICS 服务提供，客户端会拿到 `192.168.137.x` 网段的地址；
- 需要无线网卡驱动支持承载网络。用 `netsh wlan show drivers` 看不到「支持的承载网络：是」时，这个开关会显示「不可用」并给出原因。

### 上游连接的选择

程序不会直接用「默认路由」当上游。默认路由可能是别的网络（比如电脑同时连着办公室 Wi-Fi），那样会把那个网络共享出去，而不是手机流量。实际做法是遍历所有连接配置文件，按网卡驱动描述找到手机共享出来的那块网卡（`Remote NDIS Compatible Device`、`NCM` 这类 USB 共享网卡），再以它为上游创建热点管理器。找不到时会提示「未找到手机共享出来的网卡」。

### 自动开启的时机

USB 网络共享切换后网卡会重新枚举，Windows 还要做一轮连通性探测，这期间上游是不可用的。因此自动流程不会立即开热点，而是先等手机网络就绪（最长 120 秒），确认上游拿到地址后再开；USB 共享本身已经在运行时则直接开。

热点开启后由系统接管，程序退出不会关掉它；需要关闭时用界面上的开关。

## 短信通知同步

默认关闭：读短信需要显式授权，要用的话在设置里勾选「同步新短信」。它只关心**程序启动之后**收到的短信：启动时读一次手机时钟作为基线，之前的短信一条都不查询；之后每 10 秒查一次 `date > 基线` 的收件箱记录，按 `_id` 去重。程序退出、换了一台设备、或者在设置里把开关从关改成开，都会重新建立基线，期间到达的短信不补报。

- **入口**：主窗口底部「近期短信」一行只显示时间、发件人和「正文已隐藏」，点「查看列表」或托盘菜单「近期短信」打开列表窗口。列表按最新在上排列，**默认不显示任何正文**，要看就点窗口里的「显示正文」；这个开关只在当前窗口有效，关掉窗口就复位。
- **验证码**：含验证码的短信在列表里标 `[含验证码]`，但不显示码值；「复制验证码」和托盘菜单「复制最新验证码」只把码放进剪贴板，不需要展开正文。提取规则是关键词邻近的 4–8 位数字，或全文唯一的一个 4–6 位数字块；余额、订单号、日期这类数字会被排除。
- **发件人过滤**：设置里的「只提醒这些发件人」填关键字（逗号、分号、竖线或空格分隔），留空表示全部。它只决定要不要弹提醒，**不决定收不收短信**，列表始终保留全部消息。
- **提醒方式**：默认用 Windows 通知中心样式的 Toast；系统不支持（未注册 AppUserModelID、策略限制等）会自动回退到托盘气泡，设置里也能关掉这个开关。提醒文案只有数量、发件人和「含验证码」，绝不带正文或码值。Toast 不支持点击直接打开列表，请用托盘菜单。
- **隐私边界**：短信只存在内存里，最多保留最近 100 条；不写 CSV、不写 `tether.log`、不落盘、不上传，也不读取历史短信库。「复制正文 / 复制验证码」会把明文放进 Windows 剪贴板，Toast 会留在 Windows 通知中心历史里，介意就别用这两处。
- **实现**：走 `adb shell content query --uri content://sms/inbox`，不需要 Root、不在手机装 App；短信通道本身不引入额外依赖（UI 用的 WPF-UI 与它无关）。`body` 放在 `--projection` 最后一项，按 `Row:` 分段合并，正文里的逗号、等号和换行都不会被截断。
- **权限**：部分 ROM 会拒绝 shell 读取短信（输出 `Permission Denial` / `READ_SMS`）。这类失败不会被静默当成「没有新短信」：主窗口底部显示原因、托盘提示一次。此时需要在开发者选项里打开「USB 调试（安全设置）」，或执行 `adb shell appops set com.android.shell READ_SMS allow`。
- **实时性**：10 秒轮询，不是推送。USB 重枚举期间可能漏，靠下一次查询补上（只补本次运行期间、基线之后的）。要亚秒级推送得在手机装 App 监听 `SMS_RECEIVED`，与本项目「不装客户端」的原则冲突，暂不做。

多设备时沿用设置里的「目标设备」，只对那台手机查询；预设设备不在线就不查。判定与去重细节见 `docs/后续计划.md` 的「实现说明：短信通知同步」。

## 多台设备时指定手机

同时通过 ADB 连接多台手机时（例如一台用来供网、另一台只是插着调试），程序**不会默认拿列表里的第一台**。设置里新增「目标设备」下拉框：

- 显示当前已授权设备列表，格式为 `型号（序列号）`，右侧「刷新列表」可重新读取 `adb devices`；
- 选中的序列号保存在 `settings.json` 的 `TargetDeviceSerial` 字段；
- **状态采集和 USB 网络共享开关都以这台设备为准**，包括开机自动开启共享。自动共享会一直等目标设备出现，不会把命令发给旁边的另一台手机；
- 选「自动选择（只连接一台时生效）」等于清空这个记录，回到只接一台手机时即插即用的行为。

如果记录下来的设备当前不在线，程序按「设备未连接」处理并明确提示：

1. 主窗口顶部显示「预设设备未连接 · 点此重新选择」，点击即可打开设置；
2. 托盘气泡给出完整原因；
3. 设置页的下拉框会多出一项 `⚠ 预设设备 xxx 未连接，请重新选择` 并默认选中它，下方是橙红色提示文字；
4. 此时点「保存」会被拦下，必须先从当前设备列表里重新选一台（或改回「自动选择」）。

这个限制是刻意的：换手机或换 USB 口之后，静默改用列表里第一台设备，可能会把另一台手机的 USB 模式切到 RNDIS。USB 共享开关禁用，直到重新选定设备。多台已授权设备但从未指定目标时，同样会提示先做选择。

## 构建

需要 **.NET 10 SDK**（脚本会优先找 `%USERPROFILE%\.dotnet\dotnet.exe`，因为系统 PATH 上的 `dotnet.exe` 有时只是 host、没装 SDK）和 NuGet 还原 WPF-UI 4.3.0。不需要 Visual Studio。

```powershell
.\build.ps1
```

`build.ps1` 用 `dotnet publish -p:PublishSingleFile=true -p:SelfContained=false` 生成**框架依赖的单文件 exe**：一个 `AdbDeviceVitals.exe` 里打包了托管 DLL 和 WPF-UI，运行时依赖系统里的 Desktop Runtime 10。输出位置 `src\AdbDeviceVitals\bin\Release\AdbDeviceVitals.exe`。

因此发布包与自更新流程不用改：zip 里仍然只有 exe + README + LICENSE，程序内更新只替换这一个 exe，配置留在 `%USERPROFILE%\adb-device-vitals\settings.json`，不受影响。

跑完整验证（构建 + 逻辑单测 + 三个窗口的真实渲染截图）：

```powershell
.\test.ps1
```

- 逻辑测试：`tests\AdbDeviceVitals.Tests.csproj`，链接解析器 / 状态机等纯逻辑源码，输出 `All parser tests passed.`
- 渲染测试：`tests\AdbDeviceVitals.RenderTest.csproj`，在真实 `Application.Run()` 消息循环里显示三个窗口，用 Win32 `GetWindowRect` + `Graphics.CopyFromScreen` 截图（WPF 的 `RenderTargetBitmap` 截不到 DWM 画的 Mica 背景）。产物：`artifacts\wpf-main.png`、`artifacts\wpf-settings.png`、`artifacts\wpf-sms.png`。
- CI 发布：`.github\workflows\release.yml` 在推 `v*` 标签时先用 `actions/setup-dotnet@v4` 装 .NET 10 SDK，再执行同一个 `.\build.ps1`。

## 数据兼容性

Android 厂商和系统版本可能隐藏部分字段。应用会按可用程度降级显示：

- 电池电量、温度、电压通常可稳定读取。
- 蜂窝信号优先读取 5G NR，未提供时回退到 LTE。
- 瞬时充电电流和循环次数在部分 MIUI 设备上受系统权限限制。

### USB 功能组合

开启 USB 网络共享时，程序执行 `svc usb setFunctions rndis`，只切换 RNDIS，不显式携带 adb。这与早期计划中的 `rndis,adb` 不同，属于有意保持的现状：

- Android 的 `setCurrentFunctions` 是"替换整个组合"语义，不带 adb 就可能把调试通道摘掉。小米等 ROM 会自动把 adb 并回组合（`sys.usb.state` 显示为 `rndis,adb`），USB 调试不受影响；本项目验证的机型属于这种情况。
- 严格按 AOSP 语义实现的系统如果只保留 `rndis`，切换后 ADB 会掉线。此时程序读不到设备，会把成功的切换报成失败，也无法再通过界面关闭共享，需要到手机上操作。
- 在已验证机型上改回 `rndis,adb` 没有任何收益，反而多一条可能失败的命令，因此维持 `rndis`。若在其他机型遇到切换后 ADB 断连，再改为 `rndis,adb`。

另外，`setFunctions` 只改运行时组合，不写入 `persist.sys.usb.config`。拔插数据线或重启手机后，USB 功能会回到系统持久化的组合（通常只含 adb 或 `mtp,adb`），USB 网络共享不会自动恢复，需要重新开启。

## License

MIT

