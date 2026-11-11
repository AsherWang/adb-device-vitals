using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using AdbDeviceVitals.Services;

namespace AdbDeviceVitals
{
    public sealed class SettingsForm : Form
    {
        private readonly TextBox _adbDirectory;
        private readonly CheckBox _runAtStartup;
        private readonly CheckBox _autoEnableUsbTethering;
        private readonly CheckBox _autoRecoverUsbTethering;
        private readonly CheckBox _autoStartPcHotspot;
        private readonly CheckBox _smsSyncEnabled;
        private readonly ComboBox _targetDevice;
        private readonly Label _deviceHint;
        private readonly Button _refreshDevices;
        private readonly AdbClient _adb;
        private readonly Func<IWin32Window, Task> _checkForUpdates;
        private bool _loadingDevices;
        private bool _suppressChoiceEvents;
        private int _authorizedCount;
        private int _unauthorizedCount;

        public SettingsForm(string detectedAdbPath, Func<IWin32Window, Task> checkForUpdates)
            : this(null, detectedAdbPath, checkForUpdates)
        {
        }

        public SettingsForm(AdbClient adb, Func<IWin32Window, Task> checkForUpdates)
            : this(adb, adb == null ? string.Empty : adb.AdbPath, checkForUpdates)
        {
        }

        private SettingsForm(AdbClient adb, string detectedAdbPath, Func<IWin32Window, Task> checkForUpdates)
        {
            _adb = adb;
            _checkForUpdates = checkForUpdates;
            Text = "设置";
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 496);
            Font = new Font("Segoe UI", 9f);

            Label heading = new Label
            {
                Text = "ADB 目录",
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 11f),
                Location = new Point(18, 16)
            };
            Label help = new Label
            {
                Text = "请选择包含 adb.exe 的 platform-tools 目录。留空时将从程序目录、Android SDK 和 PATH 自动查找。",
                AutoSize = false,
                Location = new Point(20, 47),
                Size = new Size(520, 40)
            };
            _adbDirectory = new TextBox
            {
                Location = new Point(20, 92),
                Size = new Size(425, 25),
                Text = AppSettings.AdbDirectory
            };
            Button browse = new Button { Text = "浏览…", Location = new Point(453, 90), Size = new Size(87, 29) };
            browse.Click += BrowseDirectory;

            string detected = string.IsNullOrEmpty(detectedAdbPath) ? "当前未检测到 ADB" : "当前使用：" + detectedAdbPath;
            Label current = new Label
            {
                Text = detected,
                AutoEllipsis = true,
                ForeColor = Color.DimGray,
                Location = new Point(20, 126),
                Size = new Size(520, 22)
            };

            Label deviceHeading = new Label
            {
                Text = "目标设备",
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 11f),
                Location = new Point(18, 160)
            };
            _targetDevice = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(20, 190),
                Size = new Size(425, 25)
            };
            _targetDevice.SelectedIndexChanged += delegate { if (!_suppressChoiceEvents) UpdateDeviceHint(); };
            _refreshDevices = new Button { Text = "刷新列表", Location = new Point(453, 188), Size = new Size(87, 29) };
            _refreshDevices.Click += async delegate { await ReloadDevicesAsync(); };
            _deviceHint = new Label
            {
                AutoSize = false,
                ForeColor = Color.DimGray,
                Location = new Point(20, 220),
                Size = new Size(520, 38)
            };

            _runAtStartup = new CheckBox
            {
                Text = "开机自启动",
                AutoSize = true,
                Location = new Point(20, 266),
                Checked = AppSettings.RunAtStartup
            };
            _autoEnableUsbTethering = new CheckBox
            {
                Text = "启动后自动开启 USB 网络共享",
                AutoSize = true,
                Location = new Point(20, 292),
                Checked = AppSettings.AutoEnableUsbTethering
            };
            _autoRecoverUsbTethering = new CheckBox
            {
                Text = "USB 网络共享断开后自动重新开启",
                AutoSize = true,
                Location = new Point(20, 318),
                Checked = AppSettings.AutoRecoverUsbTethering
            };
            _autoStartPcHotspot = new CheckBox
            {
                Text = "启动后自动开启电脑热点（把手机网络分享给其他设备）",
                AutoSize = true,
                Location = new Point(20, 344),
                Checked = AppSettings.AutoStartPcHotspot
            };

            _smsSyncEnabled = new CheckBox
            {
                Text = "同步新短信（程序启动后开始监听，不读取历史短信）",
                AutoSize = true,
                Location = new Point(20, 370),
                Checked = AppSettings.SmsSyncEnabled
            };

            Label version = new Label
            {
                Text = "当前版本 " + UpdateChecker.CurrentVersionText,
                AutoSize = true,
                ForeColor = Color.DimGray,
                Location = new Point(20, 404)
            };
            Button checkUpdates = new Button { Text = "检查更新", Location = new Point(148, 398), Size = new Size(96, 30) };
            checkUpdates.Click += async delegate { await CheckForUpdates(checkUpdates); };

            Button clear = new Button { Text = "恢复自动查找", Location = new Point(20, 440), Size = new Size(112, 30) };
            clear.Click += delegate { _adbDirectory.Text = string.Empty; };
            Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(358, 440), Size = new Size(86, 30) };
            Button save = new Button { Text = "保存", Location = new Point(454, 440), Size = new Size(86, 30) };
            save.Click += SaveSettings;

            Controls.Add(heading);
            Controls.Add(help);
            Controls.Add(_adbDirectory);
            Controls.Add(browse);
            Controls.Add(current);
            Controls.Add(deviceHeading);
            Controls.Add(_targetDevice);
            Controls.Add(_refreshDevices);
            Controls.Add(_deviceHint);
            Controls.Add(_runAtStartup);
            Controls.Add(_autoEnableUsbTethering);
            Controls.Add(_autoRecoverUsbTethering);
            Controls.Add(_autoStartPcHotspot);
            Controls.Add(_smsSyncEnabled);
            Controls.Add(version);
            Controls.Add(checkUpdates);
            Controls.Add(clear);
            Controls.Add(cancel);
            Controls.Add(save);
            AcceptButton = save;
            CancelButton = cancel;

            _suppressChoiceEvents = true;
            _targetDevice.Items.Add(new DeviceChoice(string.Empty, "自动选择（只连接一台时生效）", false));
            _targetDevice.SelectedIndex = 0;
            _suppressChoiceEvents = false;
            UpdateDeviceHint();

            Load += async delegate { await ReloadDevicesAsync(); };
        }

        private async Task ReloadDevicesAsync()
        {
            if (_loadingDevices) return;
            _loadingDevices = true;
            _refreshDevices.Enabled = false;
            try
            {
                IList<AdbDevice> devices = new List<AdbDevice>();
                string error = string.Empty;
                try
                {
                    if (_adb == null) error = "未检测到 ADB，无法读取设备列表。";
                    else if (!_adb.IsAvailable) error = "未找到 adb.exe，请先指定 ADB 目录并保存。";
                    else devices = await _adb.ListDevicesAsync();
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                if (IsDisposed) return;
                FillDeviceChoices(devices, error);
            }
            finally
            {
                if (!IsDisposed) _refreshDevices.Enabled = true;
                _loadingDevices = false;
            }
        }

        private void FillDeviceChoices(IList<AdbDevice> devices, string error)
        {
            string preferred = AppSettings.TargetDeviceSerial;
            _authorizedCount = 0;
            _unauthorizedCount = 0;

            _suppressChoiceEvents = true;
            _targetDevice.Items.Clear();
            _targetDevice.Items.Add(new DeviceChoice(string.Empty, "自动选择（只连接一台时生效）", false));
            int selected = 0;
            int missingIndex = -1;

            foreach (AdbDevice device in devices)
            {
                if (!DeviceSelection.IsAuthorized(device))
                {
                    _unauthorizedCount++;
                    continue;
                }
                _authorizedCount++;
                string label = (string.IsNullOrEmpty(device.Model) ? "Android 设备" : device.Model)
                    + "（" + DeviceSelection.ShortSerial(device.Serial) + "）";
                DeviceChoice choice = new DeviceChoice(device.Serial, label, false);
                _targetDevice.Items.Add(choice);
                if (preferred.Length > 0 && string.Equals(device.Serial, preferred, StringComparison.Ordinal))
                    selected = _targetDevice.Items.Count - 1;
            }

            if (preferred.Length > 0 && selected == 0)
            {
                // 记下来的设备不在当前列表里：把这一项显式摆到下拉框里并默认选中，
                // 用户一打开设置就能看见，且保存时会被拦下，必须重新选一台。
                missingIndex = _targetDevice.Items.Count;
                _targetDevice.Items.Add(new DeviceChoice(preferred,
                    "⚠ 预设设备 " + DeviceSelection.ShortSerial(preferred) + " 未连接，请重新选择", true));
                selected = missingIndex;
            }

            _targetDevice.SelectedIndex = selected;
            _suppressChoiceEvents = false;
            _deviceHint.Tag = error;
            UpdateDeviceHint();
        }

        private void UpdateDeviceHint()
        {
            if (_deviceHint == null) return;
            DeviceChoice choice = _targetDevice.SelectedItem as DeviceChoice;
            string error = _deviceHint.Tag as string;
            _deviceHint.ForeColor = Color.DimGray;

            if (choice != null && choice.Missing)
            {
                _deviceHint.ForeColor = Color.FromArgb(176, 72, 24);
                _deviceHint.Text = "记录的设备当前不在线。请从上面的列表重新选择一台已连接的设备，或选择「自动选择」，然后再保存。";
                return;
            }
            if (!string.IsNullOrEmpty(error))
            {
                _deviceHint.ForeColor = Color.FromArgb(176, 72, 24);
                _deviceHint.Text = "读取设备列表失败：" + error;
                return;
            }
            if (_authorizedCount > 1 && (choice == null || choice.Serial.Length == 0))
            {
                _deviceHint.ForeColor = Color.FromArgb(176, 72, 24);
                _deviceHint.Text = "当前有 " + _authorizedCount + " 台已授权设备，请明确指定其中一台，否则不会监控也不会开关共享。";
                return;
            }

            string text = "同时连接多台手机时，程序只对所选设备采集数据并开关 USB 网络共享；留「自动选择」时仅在只接一台设备的情况下生效。";
            if (_unauthorizedCount > 0)
                text += " 另有 " + _unauthorizedCount + " 台设备未授权 USB 调试。";
            _deviceHint.Text = text;
        }

        private async Task CheckForUpdates(Button button)
        {
            if (_checkForUpdates == null) return;
            button.Enabled = false;
            try
            {
                await _checkForUpdates(this);
            }
            catch (Exception ex)
            {
                if (!IsDisposed) MessageBox.Show(this, ex.Message, "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (!IsDisposed) button.Enabled = true;
            }
        }

        private void BrowseDirectory(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择 Android platform-tools 目录";
                dialog.ShowNewFolderButton = false;
                if (Directory.Exists(_adbDirectory.Text)) dialog.SelectedPath = _adbDirectory.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) _adbDirectory.Text = dialog.SelectedPath;
            }
        }

        private void SaveSettings(object sender, EventArgs e)
        {
            string directory = _adbDirectory.Text.Trim().Trim('"');
            if (directory.Length > 0 && !File.Exists(Path.Combine(directory, "adb.exe")))
            {
                MessageBox.Show(this, "所选目录中没有找到 adb.exe。", "ADB 目录无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DeviceChoice choice = _targetDevice.SelectedItem as DeviceChoice;
            if (choice != null && choice.Missing)
            {
                MessageBox.Show(this,
                    "之前记录的设备已不在线。请从下拉框中选择一台当前已连接的设备，或选择「自动选择」后再保存。",
                    "需要重新选择设备", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string serial = choice == null ? string.Empty : choice.Serial;
            if (serial.Length > 0 && !DeviceSelection.IsValidSerial(serial))
            {
                MessageBox.Show(this, "设备序列号不合法，请刷新列表后重新选择。", "设备无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                // 一次写入整份配置，避免五个属性各写一遍文件、留下中间状态。
                AppSettings.Save(new SettingsData
                {
                    AdbDirectory = directory,
                    TargetDeviceSerial = serial,
                    AutoEnableUsbTethering = _autoEnableUsbTethering.Checked,
                    AutoRecoverUsbTethering = _autoRecoverUsbTethering.Checked,
                    AutoStartPcHotspot = _autoStartPcHotspot.Checked,
                    SmsSyncEnabled = _smsSyncEnabled.Checked,
                    RunAtStartup = _runAtStartup.Checked,
                    LastUpdateCheckUtc = AppSettings.LastUpdateCheckUtc
                });
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法保存设置：" + ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private sealed class DeviceChoice
        {
            public DeviceChoice(string serial, string text, bool missing)
            {
                Serial = serial ?? string.Empty;
                Text = text ?? string.Empty;
                Missing = missing;
            }

            public string Serial { get; private set; }
            public string Text { get; private set; }
            public bool Missing { get; private set; }

            public override string ToString()
            {
                return Text;
            }
        }
    }
}
