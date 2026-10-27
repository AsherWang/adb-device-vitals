using System;
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
        private readonly CheckBox _autoStartPcHotspot;
        private readonly Func<IWin32Window, Task> _checkForUpdates;

        public SettingsForm(string detectedAdbPath, Func<IWin32Window, Task> checkForUpdates)
        {
            _checkForUpdates = checkForUpdates;
            Text = "设置";
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 360);
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

            _runAtStartup = new CheckBox
            {
                Text = "开机自启动",
                AutoSize = true,
                Location = new Point(20, 156),
                Checked = AppSettings.RunAtStartup
            };
            _autoEnableUsbTethering = new CheckBox
            {
                Text = "启动后自动开启 USB 网络共享",
                AutoSize = true,
                Location = new Point(20, 184),
                Checked = AppSettings.AutoEnableUsbTethering
            };
            _autoStartPcHotspot = new CheckBox
            {
                Text = "启动后自动开启电脑热点（把手机网络分享给其他设备）",
                AutoSize = true,
                Location = new Point(20, 212),
                Checked = AppSettings.AutoStartPcHotspot
            };

            Label version = new Label
            {
                Text = "当前版本 " + UpdateChecker.CurrentVersionText,
                AutoSize = true,
                ForeColor = Color.DimGray,
                Location = new Point(20, 250)
            };
            Button checkUpdates = new Button { Text = "检查更新", Location = new Point(148, 244), Size = new Size(96, 30) };
            checkUpdates.Click += async delegate { await CheckForUpdates(checkUpdates); };

            Button clear = new Button { Text = "恢复自动查找", Location = new Point(20, 310), Size = new Size(112, 30) };
            clear.Click += delegate { _adbDirectory.Text = string.Empty; };
            Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(358, 310), Size = new Size(86, 30) };
            Button save = new Button { Text = "保存", Location = new Point(454, 310), Size = new Size(86, 30) };
            save.Click += SaveSettings;

            Controls.Add(heading);
            Controls.Add(help);
            Controls.Add(_adbDirectory);
            Controls.Add(browse);
            Controls.Add(current);
            Controls.Add(_runAtStartup);
            Controls.Add(_autoEnableUsbTethering);
            Controls.Add(_autoStartPcHotspot);
            Controls.Add(version);
            Controls.Add(checkUpdates);
            Controls.Add(clear);
            Controls.Add(cancel);
            Controls.Add(save);
            AcceptButton = save;
            CancelButton = cancel;
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

            try
            {
                AppSettings.AdbDirectory = directory;
                AppSettings.AutoEnableUsbTethering = _autoEnableUsbTethering.Checked;
                AppSettings.AutoStartPcHotspot = _autoStartPcHotspot.Checked;
                AppSettings.RunAtStartup = _runAtStartup.Checked;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法保存设置：" + ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
