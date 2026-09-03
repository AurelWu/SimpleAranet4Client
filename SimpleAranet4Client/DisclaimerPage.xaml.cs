namespace SimpleAranet4Client
{
    /// <summary>
    /// Shown once, before the app is used for the first time. The user has to tick the box to get
    /// past it; the answer is remembered in <see cref="Preferences"/>.
    /// </summary>
    public partial class DisclaimerPage : ContentPage
    {
        // Bumping the key shows the disclaimer again, should its wording ever change materially.
        const string AcceptedKey = "risk-accepted-v1";

        public static bool Accepted => Preferences.Default.Get(AcceptedKey, false);

        public DisclaimerPage()
        {
            InitializeComponent();
        }

        void OnAcceptCheckedChanged(object? sender, CheckedChangedEventArgs e) =>
            ContinueButton.IsEnabled = e.Value;

        async void OnContinueClicked(object? sender, EventArgs e)
        {
            Preferences.Default.Set(AcceptedKey, true);
            await Navigation.PopModalAsync();
        }

        void OnCloseClicked(object? sender, EventArgs e) => Application.Current?.Quit();

        /// <summary>Back must not dismiss this - accepting or closing the app are the only ways out.</summary>
        protected override bool OnBackButtonPressed() => true;
    }
}
