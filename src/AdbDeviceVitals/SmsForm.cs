using System;
using System.Drawing;
using System.Windows.Forms;
using AdbDeviceVitals.Models;
using AdbDeviceVitals.Parsing;

namespace AdbDeviceVitals
{
    /// <summary>
    /// 「近期短信」列表。数据来自 SmsInboxService 的内存快照：只有本次运行期间
    /// 收到的短信，重启程序即清空，不提供翻页也不读历史。
    ///
    /// 隐私口径：窗口打开时默认不显示任何正文，必须点「显示正文」才展开；
    /// 该开关只在当前窗口有效，关掉窗口就复位。复制验证码是用户主动动作，
    /// 只把码放进剪贴板，不展示正文。
    /// </summary>
    public sealed class SmsForm : Form
    {
        private const string HiddenPlaceholder = "••••••••";

        private readonly Color _background = Color.FromArgb(16, 21, 28);
        private readonly Color _card = Color.FromArgb(24, 30, 39);
        private readonly Color _muted = Color.FromArgb(147, 160, 176);
        private readonly Color _green = Color.FromArgb(76, 217, 138);
        private readonly Func<SmsMessage[]> _source;
        private readonly Action _clear;
        private readonly DataGridView _grid;
        private readonly TextBox _detail;
        private readonly Label _status;
        private readonly Button _copyCode;
        private readonly Button _copy;
        private readonly Button _toggleBody;
        private readonly Button _clearButton;
        private readonly Button _closeButton;
        private bool _showBody;

        public SmsForm(Func<SmsMessage[]> source, Action clear)
        {
            _source = source;
            _clear = clear;
            Text = "近期短信";
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(760, 500);
            MinimumSize = new Size(640, 380);
            BackColor = _background;
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9f);
            ShowInTaskbar = false;

            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

            Panel header = new Panel { Dock = DockStyle.Fill };
            Label hint = new Label
            {
                Text = "仅本次运行期间的新短信 · 每 10 秒检查一次 · 不写入磁盘 · 默认隐藏正文",
                AutoSize = true,
                ForeColor = _muted,
                Location = new Point(0, 4)
            };
            _status = new Label { Text = "暂无新短信", AutoSize = true, ForeColor = _green };
            header.Controls.Add(hint);
            header.Controls.Add(_status);
            header.Resize += delegate { PositionStatus(); };
            root.Controls.Add(header, 0, 0);

            _grid = BuildGrid();
            root.Controls.Add(_grid, 0, 1);

            _detail = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                WordWrap = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = _card,
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Text = "选中上方任意一条短信，这里显示发件人与时间；正文默认隐藏。"
            };
            root.Controls.Add(_detail, 0, 2);

            Panel footer = new Panel { Dock = DockStyle.Fill };
            _toggleBody = new Button { Text = "显示正文", Size = new Size(96, 28) };
            _toggleBody.Click += delegate { ToggleBody(); };
            _copyCode = new Button { Text = "复制验证码", Size = new Size(104, 28), Enabled = false };
            _copyCode.Click += delegate { CopySelectedCode(); };
            _copy = new Button { Text = "复制正文", Size = new Size(96, 28), Enabled = false };
            _copy.Click += delegate { CopySelectedBody(); };
            _clearButton = new Button { Text = "清空列表", Size = new Size(96, 28) };
            _clearButton.Click += delegate { ClearList(); };
            _closeButton = new Button { Text = "关闭", Size = new Size(86, 28), DialogResult = DialogResult.Cancel };
            _closeButton.Click += delegate { Close(); };
            footer.Controls.Add(_toggleBody);
            footer.Controls.Add(_copyCode);
            footer.Controls.Add(_copy);
            footer.Controls.Add(_clearButton);
            footer.Controls.Add(_closeButton);
            footer.Resize += delegate
            {
                _closeButton.Location = new Point(Math.Max(0, footer.Width - _closeButton.Width), 4);
                _clearButton.Location = new Point(Math.Max(0, _closeButton.Left - _clearButton.Width - 8), 4);
                _copy.Location = new Point(Math.Max(0, _clearButton.Left - _copy.Width - 8), 4);
                _copyCode.Location = new Point(Math.Max(0, _copy.Left - _copyCode.Width - 8), 4);
                _toggleBody.Location = new Point(Math.Max(0, _copyCode.Left - _toggleBody.Width - 8), 4);
            };
            root.Controls.Add(footer, 0, 3);
            Controls.Add(root);
            CancelButton = _closeButton;

            Shown += delegate { Reload(); PositionStatus(); };
        }

        private DataGridView BuildGrid()
        {
            DataGridView grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.Margin = new Padding(0, 4, 0, 8);
            grid.BackgroundColor = _card;
            grid.BorderStyle = BorderStyle.None;
            grid.GridColor = Color.FromArgb(42, 50, 62);
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(32, 40, 51);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = _muted;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(32, 40, 51);
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = _muted;
            grid.DefaultCellStyle.BackColor = _card;
            grid.DefaultCellStyle.ForeColor = Color.White;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(38, 70, 86);
            grid.DefaultCellStyle.SelectionForeColor = Color.White;
            grid.RowHeadersVisible = false;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.ReadOnly = true;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            grid.RowTemplate.Height = 26;

            grid.Columns.Add("time", "时间");
            grid.Columns.Add("address", "发件人");
            grid.Columns.Add("preview", "内容");
            grid.Columns[0].Width = 108;
            grid.Columns[1].Width = 150;
            grid.Columns[2].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            grid.SelectionChanged += delegate { ShowSelectedBody(); };
            grid.CellDoubleClick += delegate { CopySelectedBody(); };
            return grid;
        }

        /// <summary>新短信到达时由主窗口调用；保持「最新在最上」，并尽量保住当前选中的那条。</summary>
        public void Reload()
        {
            if (IsDisposed) return;
            SmsMessage current = Selected();
            long selectedId = current == null ? 0 : current.Id;
            SmsMessage[] messages = _source == null ? new SmsMessage[0] : _source();
            _grid.Rows.Clear();
            int restoreIndex = -1;
            for (int i = messages.Length - 1; i >= 0; i--)
            {
                SmsMessage message = messages[i];
                string time = message.ReceivedAt == DateTime.MinValue
                    ? "—" : message.ReceivedAt.ToString("MM-dd HH:mm:ss");
                int index = _grid.Rows.Add(time, message.Address, PreviewOf(message));
                _grid.Rows[index].Tag = message;
                if (selectedId != 0 && message.Id == selectedId) restoreIndex = index;
            }
            if (restoreIndex >= 0 && restoreIndex < _grid.Rows.Count)
                _grid.CurrentCell = _grid.Rows[restoreIndex].Cells[0];
            SetStatus(messages.Length == 0
                ? "暂无新短信"
                : messages.Length + " 条 · " + (_showBody ? "正文已显示" : "正文已隐藏"));
            ShowSelectedBody();
        }

        private string PreviewOf(SmsMessage message)
        {
            if (!_showBody)
            {
                return SmsCodeExtractor.HasCode(message.Body)
                    ? HiddenPlaceholder + "  [含验证码]" : HiddenPlaceholder;
            }
            string preview = SmsRowParser.OneLine(message.Body);
            if (preview.Length > 160) preview = preview.Substring(0, 160) + "…";
            return preview.Length == 0 ? "（空正文）" : preview;
        }

        private SmsMessage Selected()
        {
            return _grid.CurrentRow == null ? null : _grid.CurrentRow.Tag as SmsMessage;
        }

        private void ToggleBody()
        {
            _showBody = !_showBody;
            _toggleBody.Text = _showBody ? "隐藏正文" : "显示正文";
            // 只改预览列与下方详情，不重建行，选中位置不会跳。
            foreach (DataGridViewRow row in _grid.Rows)
            {
                SmsMessage message = row.Tag as SmsMessage;
                if (message != null) row.Cells[2].Value = PreviewOf(message);
            }
            SetStatus(_grid.Rows.Count + " 条 · " + (_showBody ? "正文已显示" : "正文已隐藏"));
            ShowSelectedBody();
        }

        private void ShowSelectedBody()
        {
            SmsMessage message = Selected();
            string code = message == null ? string.Empty : SmsCodeExtractor.Extract(message.Body);
            _copyCode.Enabled = code.Length > 0;
            _copy.Enabled = message != null && _showBody;
            if (message == null)
            {
                _detail.Text = "选中上方任意一条短信，这里显示发件人与时间；正文默认隐藏。";
                return;
            }
            string time = message.ReceivedAt == DateTime.MinValue
                ? "未知" : message.ReceivedAt.ToString("yyyy-MM-dd HH:mm:ss");
            string head = "发件人：" + message.Address + "\r\n时间：" + time;
            if (!_showBody)
            {
                _detail.Text = head + "\r\n\r\n正文已隐藏。点击「显示正文」后才会展示内容。"
                    + (code.Length > 0 ? "\r\n检测到验证码，可用「复制验证码」直接复制，无需展开正文。" : string.Empty);
                return;
            }
            _detail.Text = head + "\r\n\r\n" + message.Body
                + (code.Length > 0 ? "\r\n\r\n验证码：" + code : string.Empty);
        }

        private void CopySelectedCode()
        {
            SmsMessage message = Selected();
            if (message == null) return;
            string code = SmsCodeExtractor.Extract(message.Body);
            if (code.Length == 0)
            {
                SetStatus("这条短信里没有检测到验证码");
                return;
            }
            CopyToClipboard(code, "验证码已复制到剪贴板");
        }

        private void CopySelectedBody()
        {
            if (!_showBody)
            {
                SetStatus("请先点「显示正文」再复制正文");
                return;
            }
            SmsMessage message = Selected();
            if (message == null) return;
            CopyToClipboard(message.Body, "正文已复制到剪贴板");
        }

        private void CopyToClipboard(string value, string success)
        {
            try
            {
                Clipboard.SetText(value);
                SetStatus(success);
            }
            catch (Exception ex)
            {
                SetStatus("复制失败：" + ex.Message);
            }
        }

        private void SetStatus(string text)
        {
            _status.Text = text ?? string.Empty;
            PositionStatus();
        }

        /// <summary>状态文字长度会变，每次改文字都要重新靠右，否则会跑出窗口。</summary>
        private void PositionStatus()
        {
            Control parent = _status.Parent;
            if (parent == null) return;
            _status.Location = new Point(Math.Max(300, parent.Width - _status.Width - 8), 4);
        }

        private void ClearList()
        {
            if (_clear != null) _clear();
            _grid.Rows.Clear();
            _showBody = false;
            _toggleBody.Text = "显示正文";
            _detail.Text = "选中上方任意一条短信，这里显示发件人与时间；正文默认隐藏。";
            SetStatus("暂无新短信");
            _copy.Enabled = false;
            _copyCode.Enabled = false;
        }
    }
}
