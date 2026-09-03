namespace SimpleAranet4Client.Aranet
{
    public enum GoAqsLevel { Good, Moderate, Unhealthy }

    /// <summary>
    /// The GO IAQS (Global Open Indoor Air Quality Standards, https://goaqs.org) CO2 scale, as it is
    /// used on indoorco2map.com - boundaries and colours are taken from that site's js/legend.js and
    /// js/main.js, so the two stay in step.
    ///
    /// Only the level names are surfaced, not the 0-10 score ranges behind them: the site labels
    /// those 10-8 / 7-4 / 3-0 while the GO AQS conference deck gives 10-8 / 7-5 / 4-0. The ppm
    /// boundaries are the same either way.
    /// </summary>
    public static class GoAqsScale
    {
        /// <summary>Up to and including this is "Good" - the GO IAQS Ultimate CO2 threshold.</summary>
        public const int GoodMaxPpm = 800;

        /// <summary>Up to and including this is "Moderate"; anything above is "Unhealthy".</summary>
        public const int ModerateMaxPpm = 1400;

        public static readonly Color GoodColor = Color.FromArgb("#648EFF");
        public static readonly Color ModerateColor = Color.FromArgb("#FFB000");
        public static readonly Color UnhealthyColor = Color.FromArgb("#FF190C");

        // Same hues at low alpha, for the bands painted behind the chart line.
        public static readonly Color GoodBand = Color.FromArgb("#22648EFF");
        public static readonly Color ModerateBand = Color.FromArgb("#33FFB000");
        public static readonly Color UnhealthyBand = Color.FromArgb("#33FF190C");

        public static GoAqsLevel LevelFor(int ppm) => ppm switch
        {
            <= GoodMaxPpm => GoAqsLevel.Good,
            <= ModerateMaxPpm => GoAqsLevel.Moderate,
            _ => GoAqsLevel.Unhealthy
        };

        public static Color ColorFor(GoAqsLevel level) => level switch
        {
            GoAqsLevel.Good => GoodColor,
            GoAqsLevel.Moderate => ModerateColor,
            _ => UnhealthyColor
        };

        public static string TitleFor(GoAqsLevel level) => level switch
        {
            GoAqsLevel.Good => "Good",
            GoAqsLevel.Moderate => "Moderate",
            _ => "Unhealthy"
        };

        public static string RangeFor(GoAqsLevel level) => level switch
        {
            GoAqsLevel.Good => $"<= {GoodMaxPpm} ppm",
            GoAqsLevel.Moderate => $"{GoodMaxPpm + 1}-{ModerateMaxPpm} ppm",
            _ => $"> {ModerateMaxPpm} ppm"
        };
    }
}
