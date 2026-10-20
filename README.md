# ADB Device Vitals

一个轻量的 Windows 托盘工具，通过 ADB 在本机查看 Android 设备的关键状态。

[下载最新版 Windows 便携包](https://github.com/AsherWang/adb-device-vitals/releases/latest/download/AdbDeviceVitals-win-x64.zip)

## 使用场景

电脑没有可用的宽带或 Wi-Fi，或者需要临时改用手机流量时，用数据线把 Android 手机接到这台电脑，由手机通过 USB 网络共享给电脑上网。程序运行在电脑上，用来确认这路连接是否正常，并在需要时打开或关闭 USB 网络共享。

使用时按这个顺序准备：

1. 手机打开开发者选项中的 USB 调试。使用能传输数据的 USB 线连接电脑，不要用只能充电的线。首次连接时，在手机上允许这台电脑进行 USB 调试。
2. 打开手机系统里的 USB 网络共享（有的系统叫 USB 绑定或 USB 网络共享）。手机把自己的蜂窝网络经 USB 发给电脑，Windows 会多出一张由手机提供的网卡，电脑此后的流量走手机的移动数据。
3. 程序通过本机 ADB 查看这条链路：蜂窝网络类型和信号、电量与温度、USB 共享有没有建立、上游是哪张蜂窝接口，以及该接口的实时上传和下载速度。窗口可以最小化到托盘，电脑开着时继续观察共享是否还在。
4. 主窗口「网络共享」卡片右上角的「USB 共享」可以手动开关。打开后手机切换到 USB 网络共享；关闭后退出共享。切换时 USB 会短暂断开再重新连接，通常几秒内恢复，ADB 和电脑上的手机网卡也会跟着重现。
5. 设置中可以让程序开机自动启动，并在发现已授权的手机后自动打开 USB 网络共享。适合电脑重启后继续用同一部手机给电脑供网。

手机系统本身要提供 USB 网络共享。部分运营商会禁止或另计共享流量。程序不需要 Root，也不在手机上安装客户端。

## 当前功能

- 自动发现 `adb.exe` 和已授权的 Android 设备
- 电量、充电来源、电池温度、电压和系统学习容量
- Android 热状态
- 4G/5G 网络类型及 RSRP、RSRQ、SINR
- USB 网络共享与 Wi-Fi 热点状态、上游接口和各自连接设备数
- 手动开关 USB 网络共享；可开机自启动，并在设备就绪后自动打开 USB 网络共享
- 蜂窝接口实时下载/上传速度
- 最近状态曲线、CSV 本地日志和 Windows 异常提醒
- 最小化到系统托盘
- 单实例运行；重复启动时自动恢复已有窗口

所有数据均通过本机 ADB 读取。程序不会上传数据，也不会保存 ADB 原始输出、IMEI、MEID 或基站标识。

## 运行要求

- Windows 10 或 Windows 11
- Android 手机已开启 USB 调试，并授权当前电脑
- ADB 已加入 PATH，或位于以下常见位置之一：
  - 程序目录
  - `%LOCALAPPDATA%\Android\Sdk\platform-tools`

也可以在程序右上角打开“设置”，手动选择包含 `adb.exe` 的 `platform-tools` 目录。该设置保存在当前用户的注册表 `HKCU\Software\AdbDeviceVitals`，无需管理员权限。

程序不需要 Root，也不需要在手机安装客户端。

## 构建

仓库提供了不依赖 Visual Studio 的构建脚本。Windows 自带的 .NET Framework 编译器即可完成构建：

```powershell
.\build.ps1
```

输出文件：`src\AdbDeviceVitals\bin\Release\AdbDeviceVitals.exe`

运行解析测试：

```powershell
.\test.ps1
```

## 数据兼容性

Android 厂商和系统版本可能隐藏部分字段。应用会按可用程度降级显示：

- 电池电量、温度、电压通常可稳定读取。
- 蜂窝信号优先读取 5G NR，未提供时回退到 LTE。
- 瞬时充电电流和循环次数在部分 MIUI 设备上受系统权限限制。

## License

MIT

