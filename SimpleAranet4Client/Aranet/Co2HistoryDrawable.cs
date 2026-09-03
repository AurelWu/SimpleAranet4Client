namespace SimpleAranet4Client.Aranet
{
    /// <summary>
    /// Draws the CO2 history as a line chart with GO IAQS bands, a labelled grid, and a window into
    /// the data that can be zoomed and panned.
    /// </summary>
    public sealed class Co2HistoryDrawable : IDrawable
    {
        static readonly Color Line = Color.FromArgb("#1E88E5");
        static readonly Color Fill = Color.FromArgb("#331E88E5");
        static readonly Color Axis = Color.FromArgb("#9E9E9E");
        static readonly Color Grid = Color.FromArgb("#22000000");
        static readonly Color Text = Color.FromArgb("#616161");

        /// <summary>Never zoom in past this many measurements.</summary>
        const int MinVisiblePoints = 5;

        // Steps the y-axis is allowed to snap to, so labels stay round numbers.
        static readonly float[] PpmSteps = [50, 100, 200, 250, 500, 1000, 2000];

        // Steps the x-axis is allowed to snap to. All divide a day, so ticks land on clock
        // boundaries rather than at arbitrary offsets.
        static readonly TimeSpan[] TimeSteps =
        [
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1),
            TimeSpan.FromHours(3), TimeSpan.FromHours(6), TimeSpan.FromHours(12),
            TimeSpan.FromDays(1), TimeSpan.FromDays(2), TimeSpan.FromDays(7),
        ];

        IReadOnlyList<Aranet4HistoryPoint> _points = [];

        public IReadOnlyList<Aranet4HistoryPoint> Points
        {
            get => _points;
            set { _points = value; Reset(); }
        }

        /// <summary>Index of the leftmost visible point. Fractional, so zooming is smooth.</summary>
        public double VisibleStart { get; private set; }

        /// <summary>How many points the window spans.</summary>
        public double VisibleCount { get; private set; }

        public bool CanZoomOut => VisibleCount < _points.Count;

        /// <summary>Show everything again.</summary>
        public void Reset()
        {
            VisibleStart = 0;
            VisibleCount = _points.Count;
        }

        /// <summary>
        /// Zoom by <paramref name="factor"/> (below 1 zooms in) around <paramref name="anchor01"/>,
        /// a 0..1 position across the plot that stays put.
        /// </summary>
        public void ZoomAt(double anchor01, double factor)
        {
            if (_points.Count < MinVisiblePoints) return;

            double anchorIndex = VisibleStart + anchor01 * VisibleCount;
            double wanted = Math.Clamp(VisibleCount * factor, MinVisiblePoints, _points.Count);

            VisibleStart = anchorIndex - anchor01 * wanted;
            VisibleCount = wanted;
            ClampWindow();
        }

        /// <summary>Slide the window by a fraction of its own width. Positive moves forward in time.</summary>
        public void PanBy(double fractionOfWindow)
        {
            VisibleStart += fractionOfWindow * VisibleCount;
            ClampWindow();
        }

        void ClampWindow()
        {
            VisibleCount = Math.Clamp(VisibleCount, Math.Min(MinVisiblePoints, _points.Count), _points.Count);
            VisibleStart = Math.Clamp(VisibleStart, 0, Math.Max(0, _points.Count - VisibleCount));
        }

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            canvas.FontSize = 11;

            if (_points.Count < 2)
            {
                canvas.FontColor = Text;
                canvas.DrawString("no history loaded", dirtyRect, HorizontalAlignment.Center, VerticalAlignment.Center);
                return;
            }

            const float left = 46f, right = 10f, top = 10f, bottom = 24f;

            var plot = new RectF(
                dirtyRect.X + left,
                dirtyRect.Y + top,
                Math.Max(1f, dirtyRect.Width - left - right),
                Math.Max(1f, dirtyRect.Height - top - bottom));

            // The visible slice, as whole indices covering the fractional window.
            int first = Math.Max(0, (int)Math.Floor(VisibleStart));
            int last = Math.Min(_points.Count - 1, (int)Math.Ceiling(VisibleStart + VisibleCount) - 1);
            if (last <= first) return;

            int min = int.MaxValue, max = int.MinValue;
            for (int i = first; i <= last; i++)
            {
                min = Math.Min(min, _points[i].Co2Ppm);
                max = Math.Max(max, _points[i].Co2Ppm);
            }

            // Round the range outwards to whole steps so gridlines sit on labelled values.
            float step = NicePpmStep(min, max, plot.Height);
            float lower = Math.Max(0, (float)Math.Floor((min - step * 0.3f) / step) * step);
            float upper = (float)Math.Ceiling((max + step * 0.3f) / step) * step;
            if (upper - lower < step * 2) upper = lower + step * 2;

            float YFor(float ppm) => plot.Bottom - (ppm - lower) / (upper - lower) * plot.Height;

            // Position within the window, not within the whole series, so panning moves the line.
            float XFor(double index) => plot.Left + (float)((index - VisibleStart) / VisibleCount) * plot.Width;

            DrawBands(canvas, plot, YFor, lower, upper);
            DrawYGrid(canvas, plot, YFor, dirtyRect.X, left, lower, upper, step);
            DrawXGrid(canvas, plot, XFor, first, last);

            canvas.StrokeColor = Axis;
            canvas.StrokeSize = 1;
            canvas.DrawLine(plot.Left, plot.Bottom, plot.Right, plot.Bottom);
            canvas.DrawLine(plot.Left, plot.Top, plot.Left, plot.Bottom);

            var path = new PathF();
            for (int i = first; i <= last; i++)
            {
                float x = XFor(i);
                float y = YFor(_points[i].Co2Ppm);
                if (i == first) path.MoveTo(x, y);
                else path.LineTo(x, y);
            }

            var area = new PathF(path);
            area.LineTo(XFor(last), plot.Bottom);
            area.LineTo(XFor(first), plot.Bottom);
            area.Close();

            // first and last are rounded outwards from the fractional window, so when zoomed in the
            // line runs past both edges - clip it to the plot instead of over the axis labels.
            canvas.SaveState();
            canvas.ClipRectangle(plot);

            canvas.FillColor = Fill;
            canvas.FillPath(area);

            canvas.StrokeColor = Line;
            canvas.StrokeSize = 2;
            canvas.DrawPath(path);

            canvas.RestoreState();
        }

        static void DrawBands(ICanvas canvas, RectF plot, Func<float, float> yFor, float lower, float upper)
        {
            DrawBand(canvas, plot, yFor, lower, upper, 0, GoAqsScale.GoodMaxPpm, GoAqsScale.GoodBand);
            DrawBand(canvas, plot, yFor, lower, upper,
                GoAqsScale.GoodMaxPpm, GoAqsScale.ModerateMaxPpm, GoAqsScale.ModerateBand);
            DrawBand(canvas, plot, yFor, lower, upper,
                GoAqsScale.ModerateMaxPpm, upper, GoAqsScale.UnhealthyBand);
        }

        void DrawYGrid(ICanvas canvas, RectF plot, Func<float, float> yFor,
            float originX, float gutter, float lower, float upper, float step)
        {
            canvas.StrokeSize = 1;

            for (float ppm = lower; ppm <= upper + 0.5f; ppm += step)
            {
                float y = yFor(ppm);

                if (ppm > lower)
                {
                    canvas.StrokeColor = Grid;
                    canvas.DrawLine(plot.Left, y, plot.Right, y);
                }

                // Keep the lowest label clear of the axis, where the time labels start.
                float labelY = Math.Clamp(y - 7, plot.Top - 7, plot.Bottom - 15);

                canvas.FontColor = Text;
                canvas.DrawString($"{ppm:0}", originX, labelY, gutter - 5, 14,
                    HorizontalAlignment.Right, VerticalAlignment.Center);
            }
        }

        void DrawXGrid(ICanvas canvas, RectF plot, Func<double, float> xFor, int first, int last)
        {
            DateTime from = _points[first].Timestamp;
            DateTime to = _points[last].Timestamp;
            var span = to - from;
            if (span <= TimeSpan.Zero) return;

            // Roughly 70 px per label, so a narrow screen simply gets fewer of them.
            int maxLabels = Math.Max(2, (int)(plot.Width / 70f));
            var stepSpan = NiceTimeStep(span, maxLabels);

            // Start at the first tick on or after the left edge.
            DateTime tick = Floor(from, stepSpan);
            if (tick < from) tick += stepSpan;

            canvas.StrokeSize = 1;

            for (; tick <= to; tick += stepSpan)
            {
                // Where that instant falls between the two measurements around it.
                double index = IndexAt(tick, first, last);
                float x = xFor(index);
                if (x < plot.Left || x > plot.Right) continue;

                canvas.StrokeColor = Grid;
                canvas.DrawLine(x, plot.Top, x, plot.Bottom);

                // Nudge labels at the edges inwards so they are not half cut off.
                const float boxWidth = 64f;
                float boxX = Math.Clamp(x - boxWidth / 2, plot.Left - 6, plot.Right - boxWidth + 6);

                canvas.FontColor = Text;
                canvas.DrawString(TickLabel(tick, span), boxX, plot.Bottom + 5, boxWidth, 14,
                    HorizontalAlignment.Center, VerticalAlignment.Top);
            }
        }

        /// <summary>Fractional index of an instant, interpolated between the surrounding points.</summary>
        double IndexAt(DateTime when, int first, int last)
        {
            // Binary search for the last point at or before "when".
            int lo = first, hi = last;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (_points[mid].Timestamp <= when) lo = mid;
                else hi = mid - 1;
            }

            if (lo >= last) return last;

            double gap = (_points[lo + 1].Timestamp - _points[lo].Timestamp).TotalSeconds;
            if (gap <= 0) return lo;

            return lo + (when - _points[lo].Timestamp).TotalSeconds / gap;
        }

        static float NicePpmStep(int min, int max, float height)
        {
            // Aim for a gridline about every 45 px.
            int wanted = Math.Max(2, (int)(height / 45f));
            float rough = Math.Max(1, max - min) / (float)wanted;

            foreach (float candidate in PpmSteps)
                if (candidate >= rough) return candidate;

            return PpmSteps[^1];
        }

        static TimeSpan NiceTimeStep(TimeSpan span, int maxLabels)
        {
            var rough = span / maxLabels;

            foreach (var candidate in TimeSteps)
                if (candidate >= rough) return candidate;

            return TimeSteps[^1];
        }

        /// <summary>
        /// Times, except at midnight, where the date marks the day boundary instead. Repeating the
        /// date on every label would just be noise.
        /// </summary>
        static string TickLabel(DateTime tick, TimeSpan span) =>
            span.TotalDays >= 2 || tick.TimeOfDay == TimeSpan.Zero
                ? tick.ToString("dd.MM")
                : tick.ToString("HH:mm");

        /// <summary>Rounds an instant down to a whole multiple of the step, from midnight.</summary>
        static DateTime Floor(DateTime value, TimeSpan step)
        {
            if (step >= TimeSpan.FromDays(1))
            {
                var days = (long)step.TotalDays;
                var date = value.Date;
                return date.AddDays(-(date.DayOfYear % days));
            }

            var midnight = value.Date;
            long ticks = (value - midnight).Ticks / step.Ticks * step.Ticks;
            return midnight.AddTicks(ticks);
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
