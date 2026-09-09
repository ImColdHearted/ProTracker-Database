using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Foot_Tracker.Services;

namespace Foot_Tracker.Views;

/// <summary>
/// §228. Asks what happened, immediately before a report is sent.
///
/// §226 gave Report a Problem somewhere to deliver to. What that produced
/// was screenshots and a log with nothing attached saying what the player
/// had been doing, what they saw, or what they expected instead - which is
/// the half a log cannot supply. A log records what the app did; only the
/// person watching knows which part of it looked wrong.
///
/// Sending is blocked until something has been typed, because a blank
/// description is the exact outcome this exists to prevent - but there is
/// always a way past it that costs nothing: Just save locally leaves the
/// files in Downloads and sends none of them, which is where the button
/// stood before §226 and is still a perfectly good place to stop.
///
/// The capture happens BEFORE this window opens, in
/// ReportProblemButton_Click. That order is not incidental: the screenshot
/// has to show the problem, and a dialog asking about the problem sitting
/// on top of it would be the last thing anyone needed to see.
/// </summary>
public partial class ReportDescriptionWindow : Window
{
    public ReportDescriptionWindow()
    {
        InitializeComponent();
    }

    private void DescriptionBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        string text = DescriptionBox.Text ?? string.Empty;

        CountText.Text = $"{text.Length} of 400";

        // Whitespace is not a description.
        SendButton.IsEnabled = !string.IsNullOrWhiteSpace(text);
    }

    private void SendButton_Click(object? sender, RoutedEventArgs e) =>
        Close(DescriptionBox.Text ?? string.Empty);

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(null);

    /// <summary>Asks, and returns what was typed - or null when the player
    /// chose to keep the files locally, or closed the window through its
    /// chrome. Null means send nothing; the caller's existing
    /// files-are-in-Downloads message is the right thing to show.</summary>
    public static async Task<string?> AskAsync(Window owner, IReadOnlyList<string> fileNames)
    {
        var dialog = new ReportDescriptionWindow();

        // §229: what happens to the copies now depends on whether the send
        // works, and the window has to say so before the button is pressed
        // rather than after. Saying "copies stay in Downloads" stopped being
        // true the moment a delivered report started cleaning up after
        // itself.
        dialog.FilesText.Text =
            "Sending includes " + Describe(fileNames) +
            ". They are removed from your Downloads folder once they arrive, " +
            "and left there if the send does not work. Reports are limited to " +
            "one every " + BugReportUploadService.CooldownMinutes + " minutes.";

        return await dialog.ShowDialog<string?>(owner);
    }

    /// <summary>"a.png, b.txt and c.log" - the actual names, so what is
    /// about to leave the machine is readable rather than described.</summary>
    private static string Describe(IReadOnlyList<string> fileNames)
    {
        if (fileNames.Count == 0)
            return "nothing";

        if (fileNames.Count == 1)
            return fileNames[0];

        var head = new List<string>(fileNames.Count - 1);

        for (int i = 0; i < fileNames.Count - 1; i++)
            head.Add(fileNames[i]);

        return string.Join(", ", head) + " and " + fileNames[^1];
    }
}
