using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MacroHid.Core;
using MacroHid.Runtime;
using MacroStudio.Services;

namespace MacroStudio.Controls;

/// <summary>Chooses which release plays macros (PlaybackVersions). Separate from 精度控制 so both stay short.</summary>
public partial class PlaybackVersionDialog : Window
{
    private readonly RuntimePrecisionSettings source;

    public PlaybackVersionDialog(RuntimePrecisionSettings settings)
    {
        InitializeComponent();
        ThemedDialogChrome.Apply(this);
        source = settings;
        Title = L("PlaybackVersion");
        IntroText.Text = L("PlaybackVersionHelp");
        NextRunNoteText.Text = L("PlaybackVersionNextRun");
        ChangesLabelText.Text = L("PlaybackVersionChanges");
        CancelButton.Content = L("Cancel");
        ConfirmButton.Content = L("Confirm");

        foreach (var version in PlaybackVersions.All) VersionList.Items.Add(CreateRow(version));
        VersionList.SelectionChanged += (_, _) => ShowSelected();
        VersionList.SelectedItem = VersionList.Items.OfType<ListBoxItem>()
            .FirstOrDefault(item => ReferenceEquals(item.Tag, settings.Version)) ?? VersionList.Items[0];
        // After layout, so an earlier release further down the list is shown selected.
        Loaded += (_, _) => VersionList.ScrollIntoView(VersionList.SelectedItem);
        ContentRendered += (_, _) => VersionList.ScrollIntoView(VersionList.SelectedItem);
    }

    public RuntimePrecisionSettings Settings { get; private set; } = RuntimePrecisionSettings.Default;

    private PlaybackVersionInfo Selected => (VersionList.SelectedItem as ListBoxItem)?.Tag as PlaybackVersionInfo ?? PlaybackVersions.All[0];

    private ListBoxItem CreateRow(PlaybackVersionInfo version)
    {
        var title = new TextBlock { Text = version.Label, FontSize = 14, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryText");
        var header = new DockPanel { LastChildFill = false };
        header.Children.Add(title);
        if (version.Folder is null)
        {
            var badgeText = new TextBlock { Text = L("PlaybackVersionCurrentBadge"), FontSize = 10.5 };
            badgeText.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            var badge = new Border { Child = badgeText, Style = (Style)FindResource("SmallBadge") };
            header.Children.Add(badge);
        }
        var subtitle = new TextBlock { Text = Summary(version), FontSize = 11, Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(subtitle);
        return new ListBoxItem { Content = stack, Tag = version };
    }

    // First item of the notes ("2.0.2：循环末尾…") without the version prefix, for the list subtitle.
    private static string Summary(PlaybackVersionInfo version) => Changes(version).FirstOrDefault() ?? "";

    /// <summary>The version notes split into one line per change; the "x.y.z：" prefix is dropped (the title shows it).</summary>
    internal static IReadOnlyList<string> Changes(PlaybackVersionInfo version)
    {
        var text = L(version.NotesKey);
        var colon = text.IndexOfAny(['：', ':']);
        if (colon >= 0 && colon < 40) text = text[(colon + 1)..];
        var parts = text.Split(['；', ';', '。'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Where(p => p.Length > 0).Select(p => p.TrimEnd('.', '。')).ToArray();
    }

    private void ShowSelected()
    {
        var version = Selected;
        SelectedTitleText.Text = "MacroHID " + version.Label;
        SelectedBadge.Visibility = version.Folder is null ? Visibility.Visible : Visibility.Collapsed;
        SelectedBadgeText.Text = L("PlaybackVersionCurrentBadge");
        SelectedEngineText.Text = version.Folder is null ? L("PlaybackVersionEngineCurrent") : L("PlaybackVersionEngineLegacy");
        ChangeList.ItemsSource = Changes(version);
        var missing = version.Folder is not null && !LegacyPlaybackRuntimes.IsAvailable(version);
        MissingNotice.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
        MissingNoticeText.Text = L("PlaybackVersionMissing");
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        Settings = source with { PlaybackVersion = Selected.Id, Generation = PrecisionGeneration.Latest };
        DialogResult = true;
    }

    private static string L(string key) => LocalizationService.Get(key);
}
