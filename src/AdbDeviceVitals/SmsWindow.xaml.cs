using System;
using System.Collections.Generic;
using System.Windows;
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
    public partial class SmsWindow
    {
        private const string HiddenPlaceholder = "••••••••";

        private readonly Func<SmsMessage[]> _source;
        private readonly Action _clear;
        private bool _showBody;

        public SmsWindow(Func<SmsMessage[]> source, Action clear)
        {
            InitializeComponent();
            _source = source;
            _clear = clear;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Reload();
        }

        /// <summary>新短信到达时由主窗口调用；保持「最新在最上」，并尽量保住当前选中的那条。</summary>
        public void Reload()
        {
            if (!IsLoaded && SmsGrid.ItemsSource == null) return;
            SmsRow current = SmsGrid.SelectedItem as SmsRow;
            long selectedId = current == null ? 0 : current.Message.Id;
            SmsMessage[] messages = _source == null ? new SmsMessage[0] : _source();

            List<SmsRow> rows = new List<SmsRow>();
            int restoreIndex = -1;
            for (int i = messages.Length - 1; i >= 0; i--)
            {
                SmsMessage message = messages[i];
                string time = message.ReceivedAt == DateTime.MinValue
                    ? "—" : message.ReceivedAt.ToString("MM-dd HH:mm:ss");
                rows.Add(new SmsRow(message, time, message.Address, PreviewOf(message)));
                if (selectedId != 0 && message.Id == selectedId) restoreIndex = rows.Count - 1;
            }
            SmsGrid.ItemsSource = rows;
            if (restoreIndex >= 0 && restoreIndex < rows.Count)
            {
                SmsGrid.SelectedIndex = restoreIndex;
                SmsGrid.ScrollIntoView(rows[restoreIndex]);
            }
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
            SmsRow row = SmsGrid.SelectedItem as SmsRow;
            return row == null ? null : row.Message;
        }

        private void SmsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            ShowSelectedBody();
        }

        private void ToggleBodyButton_Click(object sender, RoutedEventArgs e)
        {
            _showBody = !_showBody;
            ToggleBodyButton.Content = _showBody ? "隐藏正文" : "显示正文";
            Reload();
        }

        private void ShowSelectedBody()
        {
            SmsMessage message = Selected();
            string code = message == null ? string.Empty : SmsCodeExtractor.Extract(message.Body);
            CopyCodeButton.IsEnabled = code.Length > 0;
            CopyBodyButton.IsEnabled = message != null && _showBody;
            if (message == null)
            {
                DetailBox.Text = "选中上方任意一条短信，这里显示发件人与时间；正文默认隐藏。";
                return;
            }
            string time = message.ReceivedAt == DateTime.MinValue
                ? "未知" : message.ReceivedAt.ToString("yyyy-MM-dd HH:mm:ss");
            string head = "发件人：" + message.Address + "\r\n时间：" + time;
            if (!_showBody)
            {
                DetailBox.Text = head + "\r\n\r\n正文已隐藏。点击「显示正文」后才会展示内容。"
                    + (code.Length > 0 ? "\r\n检测到验证码，可用「复制验证码」直接复制，无需展开正文。" : string.Empty);
                return;
            }
            DetailBox.Text = head + "\r\n\r\n" + message.Body
                + (code.Length > 0 ? "\r\n\r\n验证码：" + code : string.Empty);
        }

        private void CopyCodeButton_Click(object sender, RoutedEventArgs e)
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

        private void CopyBodyButton_Click(object sender, RoutedEventArgs e)
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
            StatusText.Text = text ?? string.Empty;
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (_clear != null) _clear();
            SmsGrid.ItemsSource = new List<SmsRow>();
            _showBody = false;
            ToggleBodyButton.Content = "显示正文";
            DetailBox.Text = "选中上方任意一条短信，这里显示发件人与时间；正文默认隐藏。";
            SetStatus("暂无新短信");
            CopyBodyButton.IsEnabled = false;
            CopyCodeButton.IsEnabled = false;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private sealed class SmsRow
        {
            public SmsRow(SmsMessage message, string time, string address, string preview)
            {
                Message = message;
                Time = time;
                Address = address;
                Preview = preview;
            }

            public SmsMessage Message { get; private set; }
            public string Time { get; private set; }
            public string Address { get; private set; }
            public string Preview { get; private set; }
        }
    }
}
