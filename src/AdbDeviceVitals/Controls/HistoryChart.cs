using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
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

            using (Pen grid = new Pen(Color.FromArgb(47, 56, 68)))
            {
                for (int i = 0; i <= 4; i++)
                {
                    int y = plot.Top + (plot.Height * i / 4);
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                }
            }

            g.DrawString("50°", _captionFont, Brushes.Gray, 8, plot.Top - 6);
            g.DrawString("20°", _captionFont, Brushes.Gray, 8, plot.Bottom - 10);
            g.DrawString("-60", _captionFont, Brushes.Gray, plot.Right - 24, plot.Top - 6);
            g.DrawString("-130", _captionFont, Brushes.Gray, plot.Right - 30, plot.Bottom - 10);

            DrawSeries(g, plot, true, Color.FromArgb(255, 174, 66));
            DrawSeries(g, plot, false, Color.FromArgb(61, 205, 255));
            using (Brush temperatureBrush = new SolidBrush(Color.FromArgb(255, 174, 66)))
            using (Brush signalBrush = new SolidBrush(Color.FromArgb(61, 205, 255)))
            {
                g.DrawString("温度", _captionFont, temperatureBrush, 10, 2);
                g.DrawString("信号", _captionFont, signalBrush, 55, 2);
            }
        }

        private void DrawSeries(Graphics g, Rectangle plot, bool temperature, Color color)
        {
            if (_samples.Count < 2) return;
            List<PointF> points = new List<PointF>();
            for (int i = 0; i < _samples.Count; i++)
            {
                double? value = temperature ? _samples[i].Temperature : (_samples[i].Signal.HasValue ? (double?)_samples[i].Signal.Value : null);
                if (!value.HasValue) continue;
                double min = temperature ? 20.0 : -130.0;
                double max = temperature ? 50.0 : -60.0;
                double normalized = Math.Max(0, Math.Min(1, (value.Value - min) / (max - min)));
                float x = plot.Left + (float)i / Math.Max(1, _samples.Count - 1) * plot.Width;
                float y = plot.Bottom - (float)normalized * plot.Height;
                points.Add(new PointF(x, y));
            }
            if (points.Count >= 2)
            {
                using (Pen pen = new Pen(color, 2f)) g.DrawLines(pen, points.ToArray());
            }
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
    }
}

