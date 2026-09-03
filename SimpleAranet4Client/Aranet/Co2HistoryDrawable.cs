namespace SimpleAranet4Client.Aranet
{
    /// <summary>Draws the CO2 history as a simple line chart.</summary>
    public sealed class Co2HistoryDrawable : IDrawable
    {
        static readonly Color Line = Color.FromArgb("#1E88E5");
        static readonly Color Fill = Color.FromArgb("#331E88E5");
        static readonly Color Axis = Color.FromArgb("#9E9E9E");
        static readonly Color Text = Color.FromArgb("#616161");
        static readonly Color Warn = Color.FromArgb("#66FB8C00");
        static readonly Color Bad = Color.FromArgb("#66E53935");

        public IReadOnlyList<Aranet4HistoryPoint> Points { get; set; } = [];

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            const float left = 44f, right = 8f, top = 8f, bottom = 20f;

            var plot = new RectF(
                dirtyRect.X + left,
                dirtyRect.Y + top,
                Math.Max(1f, dirtyRect.Width - left - right),
                Math.Max(1f, dirtyRect.Height - top - bottom));

            canvas.FontSize = 11;

            if (Points.Count < 2)
            {
                canvas.FontColor = Text;
                canvas.DrawString("no history loaded", dirtyRect, HorizontalAlignment.Center, VerticalAlignment.Center);
                return;
            }

            int min = Points.Min(p => p.Co2Ppm);
            int max = Points.Max(p => p.Co2Ppm);

            // Round to a readable range with a little headroom.
            float lower = Math.Max(0, (float)Math.Floor((min - 50) / 100.0) * 100);
            float upper = (float)Math.Ceiling((max + 50) / 100.0) * 100;
            if (upper - lower < 200) upper = lower + 200;

            float YFor(float ppm) => plot.Bottom - (ppm - lower) / (upper - lower) * plot.Height;
            float XFor(int i) => plot.Left + (float)i / (Points.Count - 1) * plot.Width;

            // Threshold bands (1000 / 1400 ppm) for orientation.
            DrawBand(canvas, plot, YFor, lower, upper, 1000, 1400, Warn);
            DrawBand(canvas, plot, YFor, lower, upper, 1400, upper, Bad);

            canvas.StrokeColor = Axis;
            canvas.StrokeSize = 1;
            canvas.DrawLine(plot.Left, plot.Bottom, plot.Right, plot.Bottom);
            canvas.DrawLine(plot.Left, plot.Top, plot.Left, plot.Bottom);

            canvas.FontColor = Text;
            canvas.DrawString($"{upper:0}", dirtyRect.X, YFor(upper) - 7, left - 4, 14,
                HorizontalAlignment.Right, VerticalAlignment.Center);
            canvas.DrawString($"{lower:0}", dirtyRect.X, YFor(lower) - 7, left - 4, 14,
                HorizontalAlignment.Right, VerticalAlignment.Center);

            var path = new PathF();
            for (int i = 0; i < Points.Count; i++)
            {
                float x = XFor(i);
                float y = YFor(Points[i].Co2Ppm);
                if (i == 0) path.MoveTo(x, y);
                else path.LineTo(x, y);
            }

            var area = new PathF(path);
            area.LineTo(plot.Right, plot.Bottom);
            area.LineTo(plot.Left, plot.Bottom);
            area.Close();
            canvas.FillColor = Fill;
            canvas.FillPath(area);

            canvas.StrokeColor = Line;
            canvas.StrokeSize = 2;
            canvas.DrawPath(path);

            canvas.FontColor = Text;
            canvas.DrawString(Points[0].Timestamp.ToString("HH:mm"),
                plot.Left, plot.Bottom + 4, 60, 14, HorizontalAlignment.Left, VerticalAlignment.Top);
            canvas.DrawString(Points[^1].Timestamp.ToString("HH:mm"),
                plot.Right - 60, plot.Bottom + 4, 60, 14, HorizontalAlignment.Right, VerticalAlignment.Top);
        }

        static void DrawBand(ICanvas canvas, RectF plot, Func<float, float> yFor,
            float lower, float upper, float from, float to, Color color)
        {
            float bandFrom = Math.Max(from, lower);
            float bandTo = Math.Min(to, upper);
            if (bandTo <= bandFrom) return;

            canvas.FillColor = color;
            canvas.FillRectangle(plot.Left, yFor(bandTo), plot.Width, yFor(bandFrom) - yFor(bandTo));
        }
    }
}
