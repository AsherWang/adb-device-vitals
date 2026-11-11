using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AdbDeviceVitals.Controls
{
    /// <summary>
    /// 温度 / 信号历史曲线。WinForms 版本是 Control + GDI+，这里改成 WPF
    /// FrameworkElement + DrawingContext，采样上限和双轴计算保持一致。
    /// 颜色收敛到低饱和的一主一辅，避免旧的荧光黄 / 亮青抢视觉。
    /// </summary>
    public sealed class HistoryChart : FrameworkElement
    {
        private static readonly Brush TemperatureBrush = CreateBrush(0xC2, 0x6A, 0x12);
        private static readonly Brush SignalBrush = CreateBrush(0x00, 0x67, 0xC0);
        private static readonly Brush GridBrush = CreateBrush(0xE3, 0xE3, 0xE3);
        private static readonly Brush LabelBrush = CreateBrush(0x6E, 0x6E, 0x6E);
        private static readonly Pen GridPen = CreatePen(GridBrush, 1, null);
        private static readonly Pen TemperaturePen = CreatePen(TemperatureBrush, 2, null);
        private static readonly Pen SignalPen = CreatePen(SignalBrush, 1.5, new DashStyle(new double[] { 4, 3 }, 0));

        private readonly List<ChartSample> _samples = new List<ChartSample>();
        private readonly Typeface _typeface = new Typeface(new FontFamily("Segoe UI"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        public HistoryChart()
        {
            MinHeight = 100;
            ClipToBounds = true;
        }

        public void AddSample(double? temperature, int? signal)
        {
            _samples.Add(new ChartSample(temperature, signal));
            while (_samples.Count > 180) _samples.RemoveAt(0);
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);

            double width = RenderSize.Width;
            double height = RenderSize.Height;
            if (width < 80 || height < 60) return;

            // 顶部留出两行：第一行图例，第二行左/右轴最大值，避免文字叠在一起。
            Rect plot = new Rect(42, 32, Math.Max(10, width - 58), Math.Max(10, height - 54));
            AxisRange temperatureAxis = CalculateAxis(true);
            AxisRange signalAxis = CalculateAxis(false);

            for (int i = 0; i <= 4; i++)
            {
                double y = plot.Top + plot.Height * i / 4;
                drawing.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            }

            DrawText(drawing, FormatAxisValue(temperatureAxis.Maximum, true), LabelBrush, 8, plot.Top - 14);
            DrawText(drawing, FormatAxisValue(temperatureAxis.Minimum, true), LabelBrush, 8, plot.Bottom - 2);
            DrawRightLabel(drawing, plot, FormatAxisValue(signalAxis.Maximum, false), plot.Top - 14);
            DrawRightLabel(drawing, plot, FormatAxisValue(signalAxis.Minimum, false), plot.Bottom - 2);

            DrawSeries(drawing, plot, true, temperatureAxis, TemperaturePen, TemperatureBrush, false);
            DrawSeries(drawing, plot, false, signalAxis, SignalPen, SignalBrush, true);

            DrawText(drawing, "温度", TemperatureBrush, 10, 2);
            DrawText(drawing, "信号", SignalBrush, 52, 2);
        }

        private void DrawSeries(DrawingContext drawing, Rect plot, bool temperature, AxisRange axis,
            Pen pen, Brush marker, bool hollowMarker)
        {
            if (_samples.Count < 2) return;
            List<Point> points = new List<Point>();
            for (int i = 0; i < _samples.Count; i++)
            {
                double? value = temperature
                    ? _samples[i].Temperature
                    : (_samples[i].Signal.HasValue ? (double?)_samples[i].Signal.Value : null);
                if (!value.HasValue) continue;
                double normalized = Math.Max(0, Math.Min(1, (value.Value - axis.Minimum) / (axis.Maximum - axis.Minimum)));
                double x = plot.Left + i / (double)Math.Max(1, _samples.Count - 1) * plot.Width;
                double y = plot.Bottom - normalized * plot.Height;
                points.Add(new Point(x, y));
            }
            if (points.Count < 2) return;

            StreamGeometry geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                context.BeginFigure(points[0], false, false);
                for (int i = 1; i < points.Count; i++) context.LineTo(points[i], true, false);
            }
            geometry.Freeze();
            drawing.DrawGeometry(null, pen, geometry);

            Point latest = points[points.Count - 1];
            if (hollowMarker)
                drawing.DrawRectangle(null, new Pen(marker, 1.5), new Rect(latest.X - 3.5, latest.Y - 3.5, 7, 7));
            else
                drawing.DrawEllipse(marker, null, latest, 3.5, 3.5);
        }

        private AxisRange CalculateAxis(bool temperature)
        {
            double minimum = double.MaxValue;
            double maximum = double.MinValue;
            int count = 0;
            foreach (ChartSample sample in _samples)
            {
                double? value = temperature
                    ? sample.Temperature
                    : (sample.Signal.HasValue ? (double?)sample.Signal.Value : null);
                if (!value.HasValue) continue;
                minimum = Math.Min(minimum, value.Value);
                maximum = Math.Max(maximum, value.Value);
                count++;
            }

            if (count == 0) return temperature ? new AxisRange(20.0, 50.0) : new AxisRange(-130.0, -60.0);

            double span = maximum - minimum;
            if (span < 0.0001)
            {
                double minimumSpan = temperature ? 2.0 : 10.0;
                minimum -= minimumSpan / 2.0;
                maximum += minimumSpan / 2.0;
                span = minimumSpan;
            }

            double padding = span * 0.2;
            return new AxisRange(minimum - padding, maximum + padding);
        }

        private void DrawText(DrawingContext drawing, string text, Brush brush, double x, double y)
        {
            FormattedText formatted = new FormattedText(text, CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, _typeface, 11, brush, 1.0);
            drawing.DrawText(formatted, new Point(x, y));
        }

        private void DrawRightLabel(DrawingContext drawing, Rect plot, string text, double y)
        {
            FormattedText formatted = new FormattedText(text, CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, _typeface, 11, LabelBrush, 1.0);
            drawing.DrawText(formatted, new Point(plot.Right - formatted.Width, y));
        }

        private static string FormatAxisValue(double value, bool temperature)
        {
            return value.ToString(temperature ? "0.0'°'" : "0.#", CultureInfo.InvariantCulture);
        }

        private static Brush CreateBrush(byte r, byte g, byte b)
        {
            Brush brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private static Pen CreatePen(Brush brush, double thickness, DashStyle dash)
        {
            Pen pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round };
            if (dash != null) pen.DashStyle = dash;
            pen.Freeze();
            return pen;
        }

        private sealed class ChartSample
        {
            public double? Temperature { get; private set; }
            public int? Signal { get; private set; }
            public ChartSample(double? temperature, int? signal)
            {
                Temperature = temperature;
                Signal = signal;
            }
        }

        private sealed class AxisRange
        {
            public double Minimum { get; private set; }
            public double Maximum { get; private set; }
            public AxisRange(double minimum, double maximum)
            {
                Minimum = minimum;
                Maximum = maximum;
            }
        }
    }
}
