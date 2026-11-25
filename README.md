# ADB Device Vitals

一个轻量的 Windows 托盘工具，通过 ADB 在本机查看 Android 设备的关键状态。

[下载最新版 Windows 便携包](https://github.com/AsherWang/adb-device-vitals/releases/latest/download/AdbDeviceVitals-win-x64.zip)（需先安装 [Microsoft Windows Desktop Runtime 10 (x64)](https://dotnet.microsoft.com/download/dotnet/10.0/runtime)）

## 使用场景

电脑没有可用的宽带或 Wi-Fi，或者需要临时改用手机流量时，用数据线把 Android 手机接到这台电脑，由手机通过 USB 网络共享给电脑上网。程序运行在电脑上，用来确认这路连接是否正常，并自动维持这路连接。

使用时按这个顺序准备：

1. 手机打开开发者选项中的 USB 调试。使用能传输数据的 USB 线连接电脑，不要用只能充电的线。首次连接时，在手机上允许这台电脑进行 USB 调试。
2. 打开手机系统里的 USB 网络共享（有的系统叫 USB 绑定或 USB 网络共享）。手机把自己的蜂窝网络经 USB 发给电脑，Windows 会多出一张由手机提供的网卡，电脑此后的流量走手机的移动数据。
3. 程序通过本机 ADB 查看这条链路：蜂窝网络类型和信号、电量与温度、USB 共享有没有建立、上游是哪张蜂窝接口，以及该接口的实时上传和下载速度。窗口可以最小化到托盘，电脑开着时继续观察共享是否还在。
4. 手机侧 USB 网络共享由程序自动维持：设置里的「启动后自动开启 USB 网络共享」负责首次开启，「USB 网络共享断开后自动重新开启」负责掉线恢复。主窗口不再提供手动 USB 共享开关；切换时 USB 会短暂断开再重新连接，通常几秒内恢复，ADB 和电脑上的手机网卡也会跟着重现。
5. 设置中可以让程序开机自动启动。适合电脑重启后继续用同一部手机给电脑供网。
6. 同一台电脑还可以把这份网络再用 Wi-Fi 分给别的设备：主窗口下方「电脑热点」一行右侧的「开启热点」开关控制 Windows 自带的移动热点，也可以在设置里让它随开机自动开启。手机网络没就绪时不会硬开，会先等一段时间。

手机系统本身要提供 USB 网络共享。部分运营商会禁止或另计共享流量。程序不需要 Root，也不在手机上安装客户端。

## 当前功能

- 自动发现 `adb.exe` 和已授权的 Android 设备
- 电量、充电来源、电池温度、电压、电池健康和系统学习容量：主界面常驻电量与电池温度两个读数，其余在电池卡片右上角 ⓘ 的「电池详情」弹窗里
- Android 热状态
- 4G/5G 网络类型及 RSRP、RSRQ、SINR
- USB 网络共享与 Wi-Fi 热点状态、上游接口和各自连接设备数
- 可开机自启动，并在设备就绪后自动打开 USB 网络共享
- USB 网络共享掉线后自动重新开启（可选开关，带防抖与重试冷却）
- 电脑侧 Wi-Fi 热点：开关 Windows 移动热点，把手机网络转发给其他设备，可随开机自动开启
- 短信通知同步（默认关闭）：只监听启动后的新短信，正文默认隐藏；可提取验证码、按发件人过滤提醒，并可选 Windows 通知
- 中国广电套餐余量查询（首行「剩余流量」卡片或托盘「广电流量查询」）：首次弹出 WebView 登录窗口，账号 / 验证码 / 滑块全部人工完成，程序只检测登录态；检测到登录成功自动关窗，登录态快照保存在本地便于下次免登录。读到的余量常驻显示在第二张卡片（主读数为可用流量合计，副读数为话费，底部一行显示本月已用流量），点卡片可刷新，右上角 ⓘ 打开套餐明细弹窗（号码 / 话费 / 当月消费 / 本月已用 / 每个流量包）
- 多设备时在设置里指定目标设备并记住；记录的设备掉线后提示重新选择，不会误操作其它手机
- 蜂窝接口实时下载/上传速度：并入第一张「蜂窝网络」卡片底部，显示 ↓ / ↑ 两个数值，界面按字节速率显示，不足 1 MB/s 用 KB/s，以上用 MB/s（KB/MB 以 1024 进制）；CSV 日志仍记 Mbps。手机侧共享状态、上游接口和连接设备数收进该卡片右上角 ⓘ 打开的网络详情弹窗，电脑热点开关在下方「电脑热点」一行
- 首行三卡同构：统一 10px 内边距、外框等大，并固定 `MinHeight=132`，卡片高度不随内容多少收缩；标题贴左上角，主要读数在卡片内水平、垂直居中，鼠标悬停时右上角出现同款圆形 ⓘ。第一张「蜂窝网络」卡片合并了原「蜂窝网络 + 网速」两卡：只显示 5 格信号条图标（RSRP 与 SINR 取较差一档决定点亮格数与颜色，不再显示文字结论），↓ / ↑ 速率以小一号字附在下方，ⓘ 打开「网络详情」弹窗可看信号结论与判据；第二张「剩余流量」卡片显示广电套餐余量合计 + 话费 + 本月已用（三行读数），点卡片刷新、ⓘ 打开「广电套餐余量」明细弹窗；第三张「电池」卡片给电量百分比与电池温度两行读数，ⓘ 打开独立的「电池详情」弹窗。网络与电池弹窗随 2.5 秒采集实时刷新，广电明细在读到余量 / 手动刷新时更新
- 网络详情弹窗含网络类型、运营商、RSRP / RSRQ / SINR、系统信号等级、设备型号与序列号、上游接口、信号判据，以及电脑出口 IPv4 / IPv6（可一键复制）；电池详情弹窗含充电状态、供电方式、电压、电池健康、电池温度、系统热状态和学习容量
- 公网出口 IP：IPv4 由回显服务查询，IPv6 取本机出站全局地址；启动 / 网络变化 / 每 10 分钟刷新一次
- 最近状态曲线、CSV 本地日志和 Windows 异常提醒
- 最小化到系统托盘
- 单实例运行；重复启动时自动恢复已有窗口

设备数据均通过本机 ADB 读取。程序不会上传设备数据，也不会保存 ADB 原始输出、IMEI、MEID 或基站标识；短信正文只保留在内存中，不落盘，且默认不在界面上显示。对外请求只有 GitHub 更新检查和公网出口 IPv4 回显（ipinfo.io / api.ipify.org），请求中不含任何设备信息；IPv6 出口直接读本机网卡，不联网。

## 界面与技术栈

界面已整体迁移到 **WPF + .NET 10 + WPF-UI 4.3.0**，对齐 Windows 11「设置」的观感：`FluentWindow` + Mica 背景、卡片式分组、`ToggleSwitch`、侧栏导航，主面板 / 设置 / 近期短信 / 网络详情 / 电池详情五个窗口共用一套浅色主题。广电登录用 WebView2（Microsoft.Web.WebView2 4 月版，随单文件一起打包原生加载器），登录窗口外观沿用同一套 FluentWindow / Mica。

- 目标框架 `net10.0-windows10.0.19041.0`，SDK-style 工程（`UseWPF` + `UseWindowsForms`）。托盘图标仍用 WinForms 的 `NotifyIcon`，WPF-UI 没有提供托盘控件。
- 系统 API 改为 CsWinRT 强类型调用：`NetworkOperatorTetheringManager` 管电脑热点，`Windows.UI.Notifications` 发 Toast，不再靠 WinRT 反射。
- 历史曲线 `Controls/HistoryChart.cs` 改为 WPF `FrameworkElement` + `DrawingContext`。
- 采集、配置、短信、守护、多设备仲裁等业务逻辑与之前的 WinForms 版本保持一致，迁移只换 UI 与系统 API 层。
- 设置页左侧导航是自绘的单选按钮栏：WPF-UI 的 `NavigationView` 会自带一块内容区，塞进窄侧栏时菜单文字会被裁掉。

## 运行要求

- Windows 10（1809 / 内部版本 19041 或更高）或 Windows 11，x64
- **Microsoft Windows Desktop Runtime 10 (x64)**：发布包是框架依赖的单文件 exe，不含运行时。没装时 Windows 会弹窗提示下载，也可以从上面的链接手动安装
- **Microsoft Edge WebView2 Runtime**（仅广电查询需要）：Windows 11 与装了 Edge 的系统通常已内置；未安装时点此下载 [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/#download-section)。不点广电入口时不会初始化内核，不影响其余功能
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
- CSV 日志与 `tether.log` 仍在 `%LOCALAPPDATA%\AdbDeviceVitals\logs`，没有跟着迁移。两者都用进程级互斥 + `FileShare.ReadWrite` 追加写入并带短重试；即使文件正被记事本、杀毒软件或旧进程瞬时独占，也只会丢这一行日志，不会影响 USB 共享、电脑热点和状态采集，更不会弹出错误提示。

## USB 网络共享掉线自动恢复

设置里的「USB 网络共享断开后自动重新开启」默认关闭。打开后，程序在每 2.5 秒的状态采集里顺带看一眼手机侧的 USB 共享状态，发现掉线就尝试重新开启：

- **只保持已经开着的状态**。程序启动后共享本来就没开时，守护不会动手；首次开启由「启动后自动开启 USB 网络共享」负责。如果那个开关也打开了，就按「本来就该开着」处理，启动瞬间的掉线也能兜住。两个开关相互独立，只开守护也能保护已经开着的共享。
- **不靠单次读数下判断**：每 20 秒检查一次，连续 20 秒都读到未开启才认定断开，避免 `dumpsys` 输出抖动就切 USB 模式。真正执行前复用开启链路再读一次 `dumpsys tethering`，共享其实还在时只返回「已开启」，不会重复下发 `setFunctions rndis`。
- **失败不刷屏**：一次尝试之后至少隔 60 秒再试；托盘只在第一次失败时提醒。手机侧共享状态随每轮采集更新，但不占主界面——悬停第一张「蜂窝网络」卡片后点右上角 ⓘ 可查看；电脑侧热点状态在下方「电脑热点」一行。
- **不会在忙时插手**：设备掉线、或已有其它 USB 切换任务在跑时，守护一律停手。主窗口已不再提供手动 USB 共享开关，所以没有「手动关闭暂停守护」这个入口；在设置里把守护从关改成开仍会重置状态。

判定用的状态机、并发与重试细节见 `docs/roadmap.md` 的「实现说明：USB 网络共享掉线自动恢复」。

## 电脑热点

主窗口下方「电脑热点」行的「开启热点」开关控制的是 **Windows 自带的移动热点**，也就是「设置 → 网络和 Internet → 移动热点」那个开关；同一行显示它的状态（SSID、已连接设备数、上游）。注意它和首行「网速」卡片不是同一件事：「网速」卡片读的是**手机侧蜂窝接口**的实时速率，手机侧共享状态（`dumpsys tethering` / `dumpsys wifi`）在 ⓘ 打开的「网络详情」弹窗里，这一行反映的是**电脑侧**有没有把网络发给别人。手机侧 USB 共享不提供手动开关，由设置里的自动开启与掉线恢复负责。

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
- **权限**：部分 ROM 会拒绝 shell 读取短信（输出 `Permission Denial` / `READ_SMS`）。这类失败不会被静默当成「没有新短信」：主窗口「近期短信」卡片用橙色文字显示原因，并弹一次托盘气泡。此时需要在开发者选项里打开「USB 调试（安全设置）」，或执行 `adb shell appops set com.android.shell READ_SMS allow`。
- **实时性**：10 秒轮询，不是推送。USB 重枚举期间可能漏，靠下一次查询补上（只补本次运行期间、基线之后的）。要亚秒级推送得在手机装 App 监听 `SMS_RECEIVED`，与本项目「不装客户端」的原则冲突，暂不做。

多设备时沿用设置里的「目标设备」，只对那台手机查询；预设设备不在线就不查。判定与去重细节见 `docs/roadmap.md` 的「实现说明：短信通知同步」。

## 中国广电流量查询

点击首行第二张「剩余流量」卡片，或从托盘菜单「广电流量查询」进入。**首次使用**会弹出一个 WebView 窗口打开 `www.10099.com.cn/login.html`：

- 账号密码、图片验证码、滑块**全部由你手动完成**，程序不代填、不读取、也不保存密码；它只定时读页面 `sessionStorage` 判断登录是否成功。
- 检测到登录后窗口**自动关闭**；也可以手动点「我已登录」立即复查。
- 登录态快照（sessionStorage 的键值对）保存在 `~/adb-device-vitals/broadnet-session.json`，浏览器 profile 在同目录 `broadnet-webview\`；下次启动自动注回，服务端 session 仍有效就能免登录。程序会校验注回后页面是否真的离开登录页，若会话已过期则**删除失效快照并弹窗让你重新登录**，不会"看起来登录了、其实没登"。
- 登录成功后程序会自己导航到**个人中心页**（`personal-center-number-order.html`），用两条通道取套餐余量并给出托盘气泡摘要（只列剩余>0 的包 + 话费，过滤结转的过期包）：**① 接口优先**——拦截站点自己发出的 `qryUserRes` / `qryBillInfo` 响应，从 JSON 直接解析（字节精确换算成 G、话费直接是元）；**② DOM 兜底**——接口没抓到或解析失败时，回退到页面 CSS class 提取。两条通道拿到的数据格式一致，用户无感。

为什么不直接抓包重放接口：抓包调研确认 10099 的登录态**不在 cookie**，而在标签页级的 `sessionStorage`；业务 POST 每次都带页面脚本现算的动态 `Access` 头和加密请求体，导出 cookie 裸调会返回 `status=000001`。所以必须保留一个真实浏览器标签页，用 WebView2 承载是成本最低的离线方案。这也意味着：**登录成功后窗口是隐藏而不是销毁**——关掉标签页 sessionStorage 就没了，登录态会丢。

余量数据的两个来源（2026-11-16 抓包 + 自检实测）：

- **接口（主）**：个人中心页加载时站点会自动 POST `/contact-web/api/busi/qryUserRes`（流量包）和 `qryBillInfo`（话费）。这些请求带动态 `Access` 头和加密体，程序**不自己发请求**，而是用 WebView2 的 `WebResourceResponseReceived` 事件把站点自己拿到的响应缓存下来，从 JSON 解析：`data.intfResultBean.userResList[]` 里 `discntName` 名称、`highFee` 总量、`balance` 剩余（字节，除以 1048576 得 G）、`addupValue` 已用；`qryBillInfo.data.balance` 直接是元字符串。登录/恢复阶段导航个人中心页时就打开捕获，接口响应正好进缓存供读取。
- **DOM（兜底）**：接口没抓到（如重复导航同一 URL 站点不再发请求）或 JSON 结构变动解析失败时，回退到页面 `.number-order-package-detail` 区块的 CSS class 提取，钩子（`package-sort-name-text` / `package-sort-used-num` / `package-sort-remain-text`）稳定。

读到的数据**常驻显示在首行第二张「剩余流量」卡片**：主读数把所有剩余>0 的流量包按 G 求和，副读数是话费，底部一行是**本月已用流量**（所有包的 `addupValue` 按 G 求和，含上月结转包里本月消耗的部分——结转包虽然剩余为 0，但已用要计进来）；点卡片任意位置即可重新查询刷新，右上角 ⓘ 打开「广电套餐余量」明细弹窗（号码、话费、当月消费、本月已用、每个流量包的剩余 / 总量 / 已用，带「刷新」按钮）。托盘气泡摘要也带本月已用。站点目前不提供按天/按小时的用量曲线，只有本月累计值。

**自动刷新**：如果发现本地有登录快照，或当前已登录，程序启动时会用隐藏的 WebView 窗口**自动取一次**余量（不弹窗、不打扰），此后**每 10 分钟**静默刷新一次。刷新用带时间戳的 URL 强制站点重发业务接口，拿到的是实时数据。若会话过期，程序会删掉失效快照并把卡片退回「**点击查询**」，等你再次点击走人工登录；一次网络抖动导致的失败则保留上次读数，不会误清成过期态。没有任何登录快照时根本不会初始化 WebView2。站点改版时按实际变动更新 `BroadnetSessionService.cs` 的 `ParseApiTraffic`（接口）或 `TrafficExtractScript`（DOM）。其余功能完全不受影响。

## 查看电脑应用流量

打开「套餐余量」，选择运营商，在「用量统计」右侧点击「查看应用流量」。程序会打开 Windows「数据使用量」，定位到该运营商手机当前的 USB 共享网卡。同一运营商有多台手机正在共享时，可以选择设备。应用排行采用 Windows 的统计，走代理的流量可能归在代理进程名下。

## 多台设备时指定手机

同时通过 ADB 连接多台手机时（例如一台用来供网、另一台只是插着调试），程序**不会默认拿列表里的第一台**。设置里新增「目标设备」下拉框：

- 显示当前已授权设备列表，格式为 `型号（序列号）`，右侧「刷新列表」可重新读取 `adb devices`；
- 选中的序列号保存在 `settings.json` 的 `TargetDeviceSerial` 字段；
- **状态采集和 USB 网络共享的自动操作都以这台设备为准**，包括开机自动开启与掉线恢复。自动共享会一直等目标设备出现，不会把命令发给旁边的另一台手机；
- 选「自动选择（只连接一台时生效）」等于清空这个记录，回到只接一台手机时即插即用的行为。

如果记录下来的设备当前不在线，程序按「设备未连接」处理并明确提示：

1. 主窗口顶部显示「预设设备未连接 · 点此重新选择」，点击即可打开设置；
2. 托盘气泡给出完整原因；
3. 设置页的下拉框会多出一项 `⚠ 预设设备 xxx 未连接，请重新选择` 并默认选中它，下方是橙红色提示文字；
4. 此时点「保存」会被拦下，必须先从当前设备列表里重新选一台（或改回「自动选择」）。

这个限制是刻意的：换手机或换 USB 口之后，静默改用列表里第一台设备，可能会把另一台手机的 USB 模式切到 RNDIS。共享的自动开启与恢复会一直等待，直到重新选定设备。多台已授权设备但从未指定目标时，同样会提示先做选择。

## 构建

需要 **.NET 10 SDK**（脚本会优先找 `%USERPROFILE%\.dotnet\dotnet.exe`，因为系统 PATH 上的 `dotnet.exe` 有时只是 host、没装 SDK）和 NuGet 还原 WPF-UI 4.3.0。不需要 Visual Studio。

```powershell
.\build.ps1
```

`build.ps1` 用 `dotnet publish -p:PublishSingleFile=true -p:SelfContained=false` 生成**框架依赖的单文件 exe**：一个 `AdbDeviceVitals.exe` 里打包了托管 DLL 和 WPF-UI，运行时依赖系统里的 Desktop Runtime 10。输出位置 `src\AdbDeviceVitals\bin\Release\AdbDeviceVitals.exe`。

本地重新构建时，脚本先完成发布，再检查当前 Windows 会话。若程序正在运行（包括缩到托盘），脚本会退出旧实例、替换 Release 中的 exe 并启动新版。程序原先未运行时只更新文件。编译或发布失败时，已有实例继续运行。

因此发布包与自更新流程不用改：zip 里仍然只有 exe + README + LICENSE，程序内更新只替换这一个 exe，配置留在 `%USERPROFILE%\adb-device-vitals\settings.json`，不受影响。

跑完整验证（构建 + 逻辑单测 + 三个窗口的真实渲染截图）：

```powershell
.\test.ps1
```

- 逻辑测试：`tests\AdbDeviceVitals.Tests.csproj`，链接解析器 / 状态机等纯逻辑源码，输出 `All parser tests passed.`
- 渲染测试：`tests\AdbDeviceVitals.RenderTest.csproj`，在真实 `Application.Run()` 消息循环里显示三个窗口，用 Win32 `GetWindowRect` + `Graphics.CopyFromScreen` 截图（WPF 的 `RenderTargetBitmap` 截不到 DWM 画的 Mica 背景）。产物：`artifacts\wpf-main.png`、`artifacts\wpf-settings.png`、`artifacts\wpf-sms.png`。**屏幕锁定时** `CopyFromScreen` 只能抓到壁纸，`test.ps1` 会自动设 `ADV_RENDER_OFFSCREEN=1`，改用 `RenderTargetBitmap` + 白底渲染控件布局（没有 Mica，但文字和控件完整）；也可以手动设这个环境变量。
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

