using System.Globalization;
using System.Windows;
using SuperCommander.Models;
using SuperCommander.Services;

namespace SuperCommander.Views.Dialogs;

/// <summary>Ctrl+M multi-rename tool with a live preview.</summary>
public partial class MultiRenameWindow : ThemedWindow
{
    private readonly IReadOnlyList<FileItem> _items;
    private List<RenamePreview> _previews = new();
    private bool _ready;

    public MultiRenameWindow(IReadOnlyList<FileItem> items)
    {
        InitializeComponent();

        _items = items;
        CaseBox.ItemsSource = new[] { "Unchanged", "lower case", "UPPER CASE", "Title Case", "First upper" };
        CaseBox.SelectedIndex = 0;

        _ready = true;
        Refresh();
    }

    /// <summary>True when files were actually renamed, so the caller can reload.</summary>
    public bool Applied { get; private set; }

    private RenameRule BuildRule() => new()
    {
        NamePattern = NameMask.Text,
        ExtensionPattern = ExtensionMask.Text,
        SearchFor = SearchFor.Text,
        ReplaceWith = ReplaceWith.Text,
        UseRegex = RegexCheck.IsChecked == true,
        CaseSensitive = CaseCheck.IsChecked == true,
        Case = (NameCase)Math.Max(0, CaseBox.SelectedIndex),
        CounterStart = ParseInt(CounterStart.Text, 1),
        CounterStep = ParseInt(CounterStep.Text, 1),
        CounterDigits = Math.Clamp(ParseInt(CounterDigits.Text, 1), 1, 10)
    };

    private static int ParseInt(string text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private void Refresh()
    {
        if (!_ready) return;

        _previews = MultiRenameService.BuildPreview(_items, BuildRule());
        PreviewList.ItemsSource = _previews;

        int changed = _previews.Count(p => p.Changed && p.Error is null);
        int problems = _previews.Count(p => p.Error is not null);

        SummaryText.Text = problems > 0
            ? $"{changed} file(s) will be renamed, {problems} need attention."
            : $"{changed} of {_previews.Count} file(s) will be renamed.";

        ApplyButton.IsEnabled = changed > 0;
    }

    private void OnRuleChanged(object sender, RoutedEventArgs e) => Refresh();

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _ready = false;
        NameMask.Text = "[N]";
        ExtensionMask.Text = "[E]";
        SearchFor.Text = string.Empty;
        ReplaceWith.Text = string.Empty;
        RegexCheck.IsChecked = false;
        CaseCheck.IsChecked = false;
        CaseBox.SelectedIndex = 0;
        CounterStart.Text = "1";
        CounterStep.Text = "1";
        CounterDigits.Text = "1";
        _ready = true;
        Refresh();
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        var pending = _previews.Where(p => p.Changed && p.Error is null).ToList();
        if (pending.Count == 0) return;

        if (!MessageDialog.ShowConfirm(this, "Multi-rename",
                $"Rename {pending.Count} file(s)?", okText: "Rename"))
            return;

        var (renamed, errors) = MultiRenameService.Apply(pending);
        Applied = renamed > 0;

        if (errors.Count > 0)
        {
            MessageDialog.ShowError(this, "Multi-rename",
                $"{renamed} renamed, {errors.Count} failed.",
                string.Join(Environment.NewLine, errors.Take(50)));
        }

        DialogResult = Applied;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
