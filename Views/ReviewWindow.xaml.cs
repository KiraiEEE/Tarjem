using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Tarjem.Core.Learning;
using Tarjem.Models;
using Tarjem.Services;

namespace Tarjem.Views;

/// <summary>
/// Spaced-repetition review of the words in History: see the word, try to recall it, reveal the
/// translation, definition and the sentence it came from, then say how well you knew it. The
/// schedule is <see cref="ReviewScheduler"/> (SM-2, Anki's buttons); everything is keyboard-
/// driven - Space to reveal, 1-4 to answer - so a session takes as long as the remembering does.
/// </summary>
public partial class ReviewWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly HistoryService _history;
    private readonly Queue<HistoryEntry> _queue;
    private readonly HashSet<HistoryEntry> _missedThisSession = new();
    private readonly int _total;
    private HistoryEntry? _current;
    private int _done;
    private bool _revealed;

    /// <summary>Raised after every answer, so History and the tray can update their counts.</summary>
    public event EventHandler? Reviewed;

    public ReviewWindow(HistoryService history)
    {
        InitializeComponent();
        _history = history;

        var due = history.DueForReview(DateTime.Now);
        _queue = new Queue<HistoryEntry>(due);
        _total = due.Count;

        ShowNext();
    }

    private void ShowNext()
    {
        if (_queue.Count == 0)
        {
            ShowDone();
            return;
        }

        _current = _queue.Dequeue();
        _revealed = false;

        var entry = _current;
        ProgressText.Text = $"{Math.Min(_done + 1, _total)} of {_total}";
        Progress.Maximum = Math.Max(1, _total);
        Progress.Value = _done;
        KindText.Text = entry.ReviewCount == 0 ? "New word" : _missedThisSession.Contains(entry) ? "Again" : "Review";

        WordText.Text = entry.Word;
        PhoneticText.Text = entry.Phonetic;
        PhoneticText.Visibility = string.IsNullOrWhiteSpace(entry.Phonetic) ? Visibility.Collapsed : Visibility.Visible;
        SpeakButton.Visibility = PronunciationService.CanSpeak(SourceOf(entry)) ? Visibility.Visible : Visibility.Collapsed;

        PromptText.Visibility = Visibility.Visible;
        AnswerPanel.Visibility = Visibility.Collapsed;
        RevealButton.Visibility = Visibility.Visible;
        GradeButtons.Visibility = Visibility.Collapsed;

        FadeIn(WordText);
    }

    private void Reveal()
    {
        if (_current == null || _revealed) return;
        _revealed = true;

        var entry = _current;
        TranslationText.Text = entry.ArabicTranslation;
        TranslationText.FlowDirection = entry.TranslationFlowDirection;
        TranslationText.FontFamily = entry.TranslationFont;
        TranslationText.Visibility = string.IsNullOrWhiteSpace(entry.ArabicTranslation) ? Visibility.Collapsed : Visibility.Visible;

        var definition = entry.Definition == "No definition found" ? "" : entry.Definition;
        DefinitionText.Text = definition;
        DefinitionText.Visibility = string.IsNullOrWhiteSpace(definition) ? Visibility.Collapsed : Visibility.Visible;

        SentenceText.Text = string.IsNullOrWhiteSpace(entry.Sentence) ? "" : $"“{entry.Sentence.Trim()}”";
        SentenceText.Visibility = string.IsNullOrWhiteSpace(entry.Sentence) ? Visibility.Collapsed : Visibility.Visible;

        var now = DateTime.Now;
        var state = entry.Review;
        AgainButton.Content = Label("Again", "1", ReviewScheduler.Describe(state, ReviewGrade.Again, now));
        HardButton.Content = Label("Hard", "2", ReviewScheduler.Describe(state, ReviewGrade.Hard, now));
        GoodButton.Content = Label("Good", "3", ReviewScheduler.Describe(state, ReviewGrade.Good, now));
        EasyButton.Content = Label("Easy", "4", ReviewScheduler.Describe(state, ReviewGrade.Easy, now));

        PromptText.Visibility = Visibility.Collapsed;
        AnswerPanel.Visibility = Visibility.Visible;
        RevealButton.Visibility = Visibility.Collapsed;
        GradeButtons.Visibility = Visibility.Visible;
        FadeIn(AnswerPanel);
    }

    private void Answer(ReviewGrade grade)
    {
        if (_current == null || !_revealed) return;

        var entry = _current;
        _history.RecordReview(entry, grade, DateTime.Now);

        // A miss comes back before the session ends - the point of "Again" is to see it again
        // while it still matters - but only once, so one stubborn word can't hold the session.
        if (grade == ReviewGrade.Again && _missedThisSession.Add(entry))
            _queue.Enqueue(entry);
        else
            _done++;

        Reviewed?.Invoke(this, EventArgs.Empty);
        ShowNext();
    }

    private void ShowDone()
    {
        _current = null;
        ReviewPanel.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Visible;

        var next = _history.Entries
            .Where(e => e.ReviewCount > 0 && e.ReviewDue > DateTime.Now)
            .Select(e => e.ReviewDue!.Value)
            .DefaultIfEmpty()
            .Min();

        if (_total == 0)
        {
            DoneTitle.Text = _history.Entries.Count == 0 ? "Nothing to review yet" : "All caught up";
            DoneText.Text = _history.Entries.Count == 0
                ? "Words you look up with Alt+Q are saved to History and turn up here for review."
                : DescribeNext(next);
        }
        else
        {
            DoneTitle.Text = "All caught up";
            DoneText.Text = $"You reviewed {_total} word{(_total == 1 ? "" : "s")}. {DescribeNext(next)}";
        }
    }

    private static string DescribeNext(DateTime next)
    {
        if (next == default) return "Look up more words and they'll show up here.";
        var wait = next - DateTime.Now;
        return wait.TotalHours < 1 ? "The next words are due in a few minutes."
            : wait.TotalDays < 1 ? $"The next words are due in about {Math.Max(1, (int)Math.Round(wait.TotalHours))} hours."
            : $"The next words are due {(wait.TotalDays < 2 ? "tomorrow" : $"in {(int)Math.Round(wait.TotalDays)} days")}.";
    }

    private static string SourceOf(HistoryEntry entry) => entry.SourceLanguage.Length > 0 ? entry.SourceLanguage : "en";

    private static object Label(string name, string key, string interval)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = $"{interval}  ·  {key}", FontSize = 11, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center });
        return panel;
    }

    private static void FadeIn(UIElement element) =>
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

    // ── Input ──

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space or Key.Enter when !_revealed:
                Reveal();
                e.Handled = true;
                break;
            case Key.D1 or Key.NumPad1 when _revealed: Answer(ReviewGrade.Again); e.Handled = true; break;
            case Key.D2 or Key.NumPad2 when _revealed: Answer(ReviewGrade.Hard); e.Handled = true; break;
            case Key.D3 or Key.NumPad3 or Key.Space or Key.Enter when _revealed: Answer(ReviewGrade.Good); e.Handled = true; break;
            case Key.D4 or Key.NumPad4 when _revealed: Answer(ReviewGrade.Easy); e.Handled = true; break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void Reveal_Click(object sender, RoutedEventArgs e) => Reveal();

    private void Grade_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<ReviewGrade>(tag, out var grade))
            Answer(grade);
    }

    private async void Speak_Click(object sender, RoutedEventArgs e)
    {
        if (_current != null)
            await PronunciationService.SpeakAsync(_current.Word, SourceOf(_current));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
