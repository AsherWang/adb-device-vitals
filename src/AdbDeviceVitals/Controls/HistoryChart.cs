using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace AdbDeviceVitals.Controls
{
    public sealed class HistoryChart : Control
    {
        private readonly List<ChartSample> _samples = new List<ChartSample>();
        private readonly Font _captionFont = new Font("Segoe UI", 8.5f, FontStyle.Regular);

        public HistoryChart()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(24, 30, 39);
            ForeColor = Color.FromArgb(188, 198, 210);
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        public void AddSample(double? temperature, int? signal)
        {
            _samples.Add(new ChartSample(temperature, signal));
            while (_samples.Count > 180) _samples.RemoveAt(0);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle plot = new Rectangle(42, 20, Math.Max(10, Width - 58), Math.Max(10, Height - 42));
            AxisRange temperatureAxis = CalculateAxis(true);
            AxisRange signalAxis = CalculateAxis(false);

            using (Pen grid = new Pen(Color.FromArgb(47, 56, 68)))
            {
                for (int i = 0; i <= 4; i++)
                {
                    int y = plot.Top + (plot.Height * i / 4);
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                }
            }

            g.DrawString(FormatAxisValue(temperatureAxis.Maximum, true), _captionFont, Brushes.Gray, 8, plot.Top - 6);
            g.DrawString(FormatAxisValue(temperatureAxis.Minimum, true), _captionFont, Brushes.Gray, 8, plot.Bottom - 10);
            DrawRightAxisLabel(g, plot, FormatAxisValue(signalAxis.Maximum, false), plot.Top - 6);
            DrawRightAxisLabel(g, plot, FormatAxisValue(signalAxis.Minimum, false), plot.Bottom - 10);

            DrawSeries(g, plot, true, temperatureAxis, Color.FromArgb(255, 174, 66), false);
            DrawSeries(g, plot, false, signalAxis, Color.FromArgb(61, 205, 255), true);
            using (Brush temperatureBrush = new SolidBrush(Color.FromArgb(255, 174, 66)))
            using (Brush signalBrush = new SolidBrush(Color.FromArgb(61, 205, 255)))
            {
                g.DrawString("温度", _captionFont, temperatureBrush, 10, 2);
                g.DrawString("信号", _captionFont, signalBrush, 55, 2);
            }
        }

        private void DrawSeries(Graphics g, Rectangle plot, bool temperature, AxisRange axis, Color color, bool dashed)
        {
            if (_samples.Count < 2) return;
            List<PointF> points = new List<PointF>();
            for (int i = 0; i < _samples.Count; i++)
            {
                double? value = temperature ? _samples[i].Temperature : (_samples[i].Signal.HasValue ? (double?)_samples[i].Signal.Value : null);
                if (!value.HasValue) continue;
                double normalized = Math.Max(0, Math.Min(1, (value.Value - axis.Minimum) / (axis.Maximum - axis.Minimum)));
                float x = plot.Left + (float)i / Math.Max(1, _samples.Count - 1) * plot.Width;
                float y = plot.Bottom - (float)normalized * plot.Height;
                points.Add(new PointF(x, y));
            }
            if (points.Count >= 2)
            {
                using (Pen pen = new Pen(color, temperature ? 3f : 2f))
                {
                    pen.LineJoin = LineJoin.Round;
                    if (dashed)
                    {
                        pen.DashStyle = DashStyle.Dash;
                        pen.DashCap = DashCap.Round;
                        pen.DashPattern = new float[] { 4f, 3f };
                    }
                    g.DrawLines(pen, points.ToArray());
                }

                PointF latest = points[points.Count - 1];
                if (temperature)
                {
                    using (Brush marker = new SolidBrush(color))
                        g.FillEllipse(marker, latest.X - 3.5f, latest.Y - 3.5f, 7f, 7f);
                }
                else
                {
                    using (Pen marker = new Pen(color, 1.5f))
                        g.DrawRectangle(marker, latest.X - 4f, latest.Y - 4f, 8f, 8f);
                }
            }
        }

        private AxisRange CalculateAxis(bool temperature)
        {
            double minimum = double.MaxValue;
            double maximum = double.MinValue;
            int count = 0;
            foreach (ChartSample sample in _samples)
            {
                double? value = temperature ? sample.Temperature : (sample.Signal.HasValue ? (double?)sample.Signal.Value : null);
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

        private void DrawRightAxisLabel(Graphics graphics, Rectangle plot, string text, float y)
        {
            SizeF size = graphics.MeasureString(text, _captionFont);
            graphics.DrawString(text, _captionFont, Brushes.Gray, plot.Right - size.Width, y);
        }

        private static string FormatAxisValue(double value, bool temperature)
        {
            return value.ToString(temperature ? "0.0'°'" : "0.#", CultureInfo.InvariantCulture);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _captionFont.Dispose();
            base.Dispose(disposing);
        }

        private sealed class ChartSample
        {
            public double? Temperature { get; private set; }
            public int? Signal { get; private set; }
            public ChartSample(double? temperature, int? signal) { Temperature = temperature; Signal = signal; }
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

