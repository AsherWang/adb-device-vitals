# ADB Device Vitals

一个轻量的 Windows 托盘工具，通过 ADB 在本机查看 Android 设备的关键状态。

[下载最新版 Windows 便携包](https://github.com/AsherWang/adb-device-vitals/releases/latest/download/AdbDeviceVitals-win-x64.zip)

## 当前功能

- 自动发现 `adb.exe` 和已授权的 Android 设备
- 电量、充电来源、电池温度、电压和系统学习容量
- Android 热状态
- 4G/5G 网络类型及 RSRP、RSRQ、SINR
- 热点频段、频率、上游接口和已连接设备数
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

