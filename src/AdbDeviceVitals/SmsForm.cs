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
    /// </summary>
    public sealed class SmsForm : Form
    {
        private readonly Color _background = Color.FromArgb(16, 21, 28);
        private readonly Color _card = Color.FromArgb(24, 30, 39);
        private readonly Color _muted = Color.FromArgb(147, 160, 176);
        private readonly Color _green = Color.FromArgb(76, 217, 138);
        private readonly Func<SmsMessage[]> _source;
        private readonly Action _clear;
        private readonly DataGridView _grid;
        private readonly TextBox _detail;
        private readonly Label _status;
        private readonly Button _copy;
        private readonly Button _clearButton;
        private readonly Button _closeButton;

        public SmsForm(Func<SmsMessage[]> source, Action clear)
        {
            _source = source;
            _clear = clear;
            Text = "近期短信";
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(760, 500);
            MinimumSize = new Size(560, 380);
            BackColor = _background;
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9f);
            ShowInTaskbar = false;

            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

            Panel header = new Panel { Dock = DockStyle.Fill };
            Label hint = new Label
            {
                Text = "仅显示本次运行期间收到的短信 · 每 10 秒检查一次 · 不写入磁盘",
                AutoSize = true,
                ForeColor = _muted,
                Location = new Point(0, 4)
            };
            _status = new Label { Text = "暂无新短信", AutoSize = true, ForeColor = _green, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            header.Controls.Add(hint);
            header.Controls.Add(_status);
            header.Resize += delegate { _status.Location = new Point(Math.Max(360, header.Width - _status.Width), 4); };
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
                Text = "选中上方任意一条短信，这里显示完整正文。"
            };
            root.Controls.Add(_detail, 0, 2);

            Panel footer = new Panel { Dock = DockStyle.Fill };
            _copy = new Button { Text = "复制正文", Size = new Size(96, 28), Enabled = false };
            _copy.Click += delegate { CopySelectedBody(); };
            _clearButton = new Button { Text = "清空列表", Size = new Size(96, 28) };
            _clearButton.Click += delegate { ClearList(); };
            _closeButton = new Button { Text = "关闭", Size = new Size(86, 28), DialogResult = DialogResult.Cancel };
            _closeButton.Click += delegate { Close(); };
            footer.Controls.Add(_copy);
            footer.Controls.Add(_clearButton);
            footer.Controls.Add(_closeButton);
            footer.Resize += delegate
            {
                _closeButton.Location = new Point(Math.Max(0, footer.Width - _closeButton.Width), 4);
                _clearButton.Location = new Point(Math.Max(0, _closeButton.Left - _clearButton.Width - 8), 4);
                _copy.Location = new Point(Math.Max(0, _clearButton.Left - _copy.Width - 8), 4);
            };
            root.Controls.Add(footer, 0, 3);
            Controls.Add(root);
            CancelButton = _closeButton;

            Shown += delegate { Reload(); };
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

        /// <summary>新短信到达时由主窗口调用；保持「最新在最上」。</summary>
        public void Reload()
        {
            if (IsDisposed) return;
            SmsMessage[] messages = _source == null ? new SmsMessage[0] : _source();
            _grid.Rows.Clear();
            for (int i = messages.Length - 1; i >= 0; i--)
            {
                SmsMessage message = messages[i];
                string time = message.ReceivedAt == DateTime.MinValue
                    ? "—" : message.ReceivedAt.ToString("MM-dd HH:mm:ss");
                string preview = SmsRowParser.OneLine(message.Body);
                if (preview.Length > 160) preview = preview.Substring(0, 160) + "…";
                int index = _grid.Rows.Add(time, message.Address, preview);
                _grid.Rows[index].Tag = message;
            }
            _status.Text = messages.Length == 0 ? "暂无新短信" : messages.Length + " 条新短信";
            ShowSelectedBody();
        }

        private SmsMessage Selected()
        {
            return _grid.CurrentRow == null ? null : _grid.CurrentRow.Tag as SmsMessage;
        }

        private void ShowSelectedBody()
        {
            SmsMessage message = Selected();
            _copy.Enabled = message != null;
            if (message == null)
            {
                _detail.Text = "选中上方任意一条短信，这里显示完整正文。";
                return;
            }
            string time = message.ReceivedAt == DateTime.MinValue
                ? "未知" : message.ReceivedAt.ToString("yyyy-MM-dd HH:mm:ss");
            _detail.Text = "发件人：" + message.Address + "\r\n时间：" + time
                + "\r\n\r\n" + message.Body;
        }

        private void CopySelectedBody()
        {
            SmsMessage message = Selected();
            if (message == null) return;
            try
            {
                Clipboard.SetText(message.Body);
                _status.Text = "正文已复制到剪贴板";
            }
            catch (Exception ex)
            {
                _status.Text = "复制失败：" + ex.Message;
            }
        }

        private void ClearList()
        {
            if (_clear != null) _clear();
            _grid.Rows.Clear();
            _detail.Text = "选中上方任意一条短信，这里显示完整正文。";
            _status.Text = "暂无新短信";
            _copy.Enabled = false;
        }
    }
}
