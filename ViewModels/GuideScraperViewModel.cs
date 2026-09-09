using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Services;
using Serilog;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// §194. Backs GuideScraperWindow, the admin-only tool that turns a PRO forum
/// topic into a draft guide. ForumGuideScraperService does the fetching and
/// the conversion; this is the fetch, look at it, then save workflow around
/// it, which is deliberately the same one BossScraperViewModel already uses.
/// Nothing reaches disk until Save is pressed.
///
/// The one rule that is not in the service: a guide that already has a real
/// index.html is never overwritten. The draft goes to index.scraped.html
/// beside it instead, and the status line says which of the two it wrote. The
/// Mega Stones guide has a hand-written Gyaradosite section in it, and a tool
/// that can silently replace an afternoon's writing is a tool nobody should
/// run twice.
/// </summary>
public sealed partial class GuideScraperViewModel : ViewModelBase
{
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // Same courtesy as the wiki scraper and the announcements reader: say
        // what this is, so the forum's operators can see it in their logs and
        // tell it apart from a browser.
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("ProTracker-ForumGuideScraper", "1.0"));

        return client;
    }

    public ObservableCollection<string> Warnings { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand))]
    private string topicUrl = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string guideFolderName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand))]
    private string guideTitle = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool hasResult;

    [ObservableProperty] private string statusMessage =
        "Paste a pokemonrevolution.net topic address, name the guide folder, then Fetch & Preview.";

    [ObservableProperty] private string? previewHtml;
    [ObservableProperty] private bool targetGuideExists;

    private ForumGuideScraperService.GuideScrapeResult? _lastResult;

    private bool CanFetch() =>
        !IsBusy
        && !string.IsNullOrWhiteSpace(TopicUrl)
        && !string.IsNullOrWhiteSpace(GuideFolderName)
        && !string.IsNullOrWhiteSpace(GuideTitle);

    [RelayCommand(CanExecute = nameof(CanFetch))]
    private async Task FetchAsync()
    {
        IsBusy = true;
        HasResult = false;
        Warnings.Clear();
        PreviewHtml = null;
        StatusMessage = "Fetching...";

        try
        {
            string? folderError = ValidateFolderName(GuideFolderName);

            if (folderError is not null)
            {
                StatusMessage = folderError;
                return;
            }

            ForumGuideScraperService.GuideScrapeResult result =
                await ForumGuideScraperService.ScrapeAsync(
                    TopicUrl.Trim(), GuideTitle.Trim(), SharedHttpClient);

            _lastResult = result;
            PreviewHtml = result.Html;
            HasResult = true;

            foreach (string warning in result.Warnings)
                Warnings.Add(warning);

            TargetGuideExists = ExistingGuideHasContent(GuideFolderName.Trim());

            StatusMessage =
                $"Read the first post of {result.SourceUrl}. Check the preview and the warnings, "
                + "then Save. Nothing has been written yet.";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Forum guide scrape failed for {Url}", TopicUrl);
            StatusMessage = "Could not read that topic: " + ex.GetBaseException().Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSave() => !IsBusy && HasResult && _lastResult is not null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (_lastResult is null)
            return;

        try
        {
            string folderName = GuideFolderName.Trim();
            string? folderError = ValidateFolderName(folderName);

            if (folderError is not null)
            {
                StatusMessage = folderError;
                return;
            }

            string folder = Path.Combine(GuidesRoot, folderName);
            Directory.CreateDirectory(folder);

            // Never over the top of a guide somebody wrote by hand.
            bool exists = ExistingGuideHasContent(folderName);
            string fileName = exists ? "index.scraped.html" : "index.html";
            string path = Path.Combine(folder, fileName);

            DurableFile.WriteAllText(path, _lastResult.Html);
            CopyStylesheetIfMissing(folder);

            StatusMessage = exists
                ? $"Saved DataFiles/Guides/{folderName}/{fileName}. The existing index.html was left "
                  + "alone - copy the sections you want across by hand."
                : $"Saved DataFiles/Guides/{folderName}/{fileName}. Open it from Game Information > "
                  + "Game Guides once a menu entry points at this folder.";

            Log.Information("Forum guide draft saved to {Path} from {Source}", path, _lastResult.SourceUrl);
        }
        catch (Exception ex)
        {
            StatusMessage = "Save failed: " + ex.GetBaseException().Message;
        }
    }

    private static string GuidesRoot =>
        Path.Combine(AppContext.BaseDirectory, "DataFiles", "Guides");

    /// <summary>A folder name, not a path. This writes into the application's
    /// own folder, so a name that can climb out of it is refused rather than
    /// sanitised - quietly rewriting what someone typed is how a tool ends up
    /// writing somewhere nobody expected.</summary>
    private static string? ValidateFolderName(string name)
    {
        string trimmed = name.Trim();

        if (trimmed.Length == 0)
            return "Give the guide a folder name.";

        if (trimmed is "." or ".."
            || trimmed.Contains('/') || trimmed.Contains('\\')
            || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "The folder name has to be a plain name, with no slashes or dots for parent folders.";
        }

        return null;
    }

    /// <summary>True when the folder already holds an index.html with
    /// something in it. An empty file does not count - that is a placeholder,
    /// not somebody's work.</summary>
    private static bool ExistingGuideHasContent(string folderName)
    {
        try
        {
            var file = new FileInfo(Path.Combine(GuidesRoot, folderName, "index.html"));
            return file.Exists && file.Length > 0;
        }
        catch
        {
            // If it cannot be inspected, assume it matters.
            return true;
        }
    }

    /// <summary>The renderer draws with Avalonia controls and never reads the
    /// stylesheet, but Open in Browser does, so a new guide folder gets a copy
    /// of the one the existing guide uses. Best effort - a guide without it
    /// still renders in the app.</summary>
    private static void CopyStylesheetIfMissing(string folder)
    {
        try
        {
            string target = Path.Combine(folder, "guide.css");

            if (File.Exists(target))
                return;

            string source = Path.Combine(GuidesRoot, "Test", "guide.css");

            if (File.Exists(source))
                File.Copy(source, target);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The guide stylesheet could not be copied into {Folder}", folder);
        }
    }
}
