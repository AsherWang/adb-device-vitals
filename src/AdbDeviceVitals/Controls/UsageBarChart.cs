using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AdbDeviceVitals.Controls
{
    /// <summary>
    /// 广电用量「按小时」柱状图。自绘 FrameworkElement（和 HistoryChart 同思路），
    /// 不走 XAML 绑定 —— 上次详情窗卡死就是只读属性被 TwoWay 绑定坑过，这里直接代码喂数据。
    /// 24 根柱对应 0~23 点，高度按当小时用量 / 全日最大用量归一。
    /// </summary>
    public sealed class UsageBarChart : FrameworkElement
    {
        private static readonly Brush BarBrush = CreateBrush(0x00, 0x67, 0xC0);
        private static readonly Brush BarMutedBrush = CreateBrush(0xB8, 0xD4, 0xEC);
        private static readonly Brush GridBrush = CreateBrush(0xE3, 0xE3, 0xE3);
        private static readonly Brush LabelBrush = CreateBrush(0x6E, 0x6E, 0x6E);
        private static readonly Pen GridPen = CreatePen(GridBrush, 1);

        private readonly double[] _values = new double[24];
        private readonly Typeface _typeface = new Typeface(new FontFamily("Segoe UI"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        public UsageBarChart()
        {
            MinHeight = 92;
            ClipToBounds = true;
        }

        /// <summary>喂 24 个小时的用量（G）；不足 24 项补 0。</summary>
        public void SetValues(double[] hourlyG)
        {
            for (int i = 0; i < _values.Length; i++)
                _values[i] = hourlyG != null && i < hourlyG.Length && hourlyG[i] > 0 ? hourlyG[i] : 0;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            double width = RenderSize.Width;
            double height = RenderSize.Height;
            if (width < 80 || height < 50) return;

            double max = 0;
            for (int i = 0; i < _values.Length; i++) if (_values[i] > max) max = _values[i];

            Rect plot = new Rect(8, 4, Math.Max(10, width - 16), Math.Max(10, height - 24));
            // 三条水平参考线 + 顶部最大值标注。
            for (int i = 0; i <= 2; i++)
            {
                double y = plot.Top + plot.Height * i / 2;
                drawing.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            }
            DrawText(drawing, max > 0 ? max.ToString("0.##") + " G" : "无数据", LabelBrush,
                plot.Left, 3);

            double slot = plot.Width / 24.0;
            double barWidth = Math.Max(2, slot * 0.62);
            for (int i = 0; i < 24; i++)
            {
                double ratio = max > 0 ? _values[i] / max : 0;
                double barHeight = ratio * (plot.Height - 2);
                if (barHeight <= 0) continue;
                double x = plot.Left + slot * i + (slot - barWidth) / 2;
                drawing.DrawRectangle(ratio >= 0.999 ? BarBrush : BarMutedBrush, null,
                    new Rect(x, plot.Bottom - barHeight, barWidth, barHeight));
            }

            DrawText(drawing, "0", LabelBrush, plot.Left, plot.Bottom + 4);
            DrawText(drawing, "6", LabelBrush, plot.Left + slot * 6 - 3, plot.Bottom + 4);
            DrawText(drawing, "12", LabelBrush, plot.Left + slot * 12 - 6, plot.Bottom + 4);
            DrawText(drawing, "18", LabelBrush, plot.Left + slot * 18 - 6, plot.Bottom + 4);
            DrawText(drawing, "23", LabelBrush, plot.Right - 14, plot.Bottom + 4);
        }

        private void DrawText(DrawingContext drawing, string text, Brush brush, double x, double y)
        {
            FormattedText ft = new FormattedText(text, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, _typeface, 9, brush, 1.0);
            drawing.DrawText(ft, new Point(x, y));
        }

        private static Brush CreateBrush(byte r, byte g, byte b)
        {
            return new SolidColorBrush(Color.FromArgb(0xFF, r, g, b));
        }

        private static Pen CreatePen(Brush brush, double thickness)
        {
            return new Pen(brush, thickness);
        }
    }
}
