using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Tarjem.Services;

namespace Tarjem.Views;

/// <summary>
/// The support window.
///
/// It is written on the assumption that most people who open it will not donate, and that this is
/// completely fine - so the thank-you is unconditional and comes first, the ask is small and
/// appears once, and closing without doing anything is a perfectly good outcome the window never
/// makes anyone feel bad about. It also earns the ask: it opens by telling the user what they
/// have actually done with the app, which is both more honest and more affecting than a generic
/// plea.
///
/// While no payment link is configured (<see cref="DonationUrl"/> empty), the donate button is
/// shown disabled with a line saying why, rather than being live and going nowhere.
/// </summary>
public partial class SupportWindow : Wpf.Ui.Controls.FluentWindow
{
    /// <summary>Set this to the real donation link when there is one. Empty leaves the donate
    /// button disabled and says so underneath it.</summary>
    private const string DonationUrl = "";

    private const string ProjectUrl = "https://github.com/IdleBuilt/Tarjem";

    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    private readonly HistoryService? _history;

    public SupportWindow(HistoryService? history = null)
    {
        _history = history;
        InitializeComponent();
    }

    private void SupportWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ThanksBody.Text = BuildThanksMessage();

        var hasDonationLink = !string.IsNullOrWhiteSpace(DonationUrl);

        DonateButton.IsEnabled = hasDonationLink;
        DonateNote.Text = hasDonationLink
            ? "Opens in your browser. Any amount, once - never a subscription."
            : "Payment methods aren't available right now.";

        OtherWaysHeader.Text = hasDonationLink ? "Other ways to help" : "If you'd like to help";

        var duration = new Duration(TimeSpan.FromMilliseconds(220));
        RootGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = EaseOut });
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = EaseOut });
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = EaseOut });
    }

    /// <summary>
    /// Says something true about this specific user rather than something generic about users in
    /// general. Someone who has looked up two thousand words has a different relationship with
    /// the app than someone who installed it yesterday, and telling them what they've done is
    /// worth more than any amount of asking.
    /// </summary>
    private string BuildThanksMessage()
    {
        var lookups = SafeLookupCount();

        return lookups switch
        {
            >= 1000 => $"You've looked up {lookups:N0} words with Tarjem. That's a real amount of reading in a language that isn't your first - and it's the whole reason this app exists.",
            >= 100 => $"You've looked up {lookups:N0} words with Tarjem so far. Every one of those was a moment you kept reading instead of stopping.",
            >= 10 => $"You've looked up {lookups:N0} words so far. I hope it's been getting out of your way.",
            _ => "I hope it stays out of your way and just works when you need it.",
        };
    }

    /// <summary>History is optional and can be switched off entirely, so the count is best-effort
    /// - a failure here falls through to the generic message rather than taking the window with
    /// it.</summary>
    private int SafeLookupCount()
    {
        try
        {
            return _history?.Entries.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private void DonateButton_Click(object sender, RoutedEventArgs e) => OpenUrl(DonationUrl);
    private void StarButton_Click(object sender, RoutedEventArgs e) => OpenUrl(ProjectUrl);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not open {Url}", url);
        }
    }
}
