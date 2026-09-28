using System.Collections.ObjectModel;
using System.Net.Http;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Services;
using Serilog;

namespace Foot_Tracker.ViewModels;

/// <summary>§239. One announcement as the window shows it: the post, and the
/// picture that came with it once it has been fetched.</summary>
public sealed partial class AnnouncementRow : ObservableObject
{
    public AnnouncementRow(Announcement announcement)
    {
        Source = announcement;
    }

    public Announcement Source { get; }

    public string Title => Source.Title;
    public string PublishedDisplay => Source.PublishedDisplay;
    // §343: the rendered post, not the raw one. Discord's timestamp markup
    // becomes a date in this reader's own timezone; Source.Body keeps the
    // original.
    public string Body => Source.Display;
    public bool HasImage => Source.HasImage;

    [ObservableProperty] private Bitmap? image;
}

/// <summary>
/// Backs AnnouncementsWindow (top-level menu item, next to Events).
///
/// §239. It used to fetch and parse PRO's "Update Logs" forum topic itself -
/// find the last page, read the rendered HTML, pull in the page before it.
/// None of that is here any more: the events server reads PRO's own
/// #announcements channel and this asks it for the result. No HTML is parsed
/// and no request goes to PRO at all.
///
/// The one thing this still fetches directly is the picture on a post, which
/// cannot travel through a JSON list. Those are checked against the same host
/// list the Worker used before it agreed to pass the address on - the server
/// filtering and the client refusing are not redundant, they are the feed
/// being untrusted at both ends - and are capped, because a picture from a
/// feed is exactly the sort of thing that arrives enormous one day.
/// </summary>
public sealed partial class AnnouncementsViewModel : ViewModelBase
{
    /// <summary>The same hosts the Worker will pass on. Repeated rather than
    /// assumed: this class is what actually opens the connection.</summary>
    private static readonly string[] AllowedImageHosts =
    {
        "cdn.discordapp.com",
        "media.discordapp.net",
        "pokemonrevolution.net",
        // §357: where the Worker now keeps the copy. Discord signs its
        // attachment URLs with a 24-hour expiry, so the address that arrived
        // with a post was a 404 by the time anybody scrolled back to it; the
        // picture is copied into the bucket at poll time and served from
        // here. Discord's own hosts stay on the list - a copy that could not
        // be made falls back to the original, which is right for the first
        // day and no worse than before after that.
        "dl.protrackerdb.com",
    };

    /// <summary>A post's picture is a screenshot, not a video. Anything past
    /// this is not one and is left unfetched.</summary>
    private const int MaxImageBytes = 8 * 1024 * 1024;

    private static readonly HttpClient Images = CreateImageClient();

    private static HttpClient CreateImageClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ProTracker (announcements images, v1)");
        return client;
    }

    public ObservableCollection<AnnouncementRow> Announcements { get; } = new();

    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private bool isBusy;

    public AnnouncementsViewModel()
    {
        _ = RefreshAsync();
    }

    private bool CanRefresh() => !IsBusy;

    /// <summary>§240. The generated command is RefreshCommand, not
    /// RefreshAsyncCommand: the toolkit's generator strips a trailing "Async"
    /// from the method name before appending "Command". Every other view model
    /// in this app already relies on that - BossScraperViewModel's FetchAsync
    /// is bound as FetchCommand - and this file spelled it out in full and
    /// broke the build.</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        RefreshCommand.NotifyCanExecuteChanged();
        StatusMessage = "Loading announcements...";

        try
        {
            IReadOnlyList<Announcement> fetched = await AnnouncementsService.FetchAsync();

            Announcements.Clear();

            foreach (Announcement announcement in fetched)
                Announcements.Add(new AnnouncementRow(announcement));

            StatusMessage = Announcements.Count > 0
                ? $"Showing {Announcements.Count} announcement{(Announcements.Count == 1 ? string.Empty : "s")}."
                : "Nothing has come through yet. New posts appear here within the hour.";
        }
        catch (EventsSyncException ex)
        {
            StatusMessage = $"Could not load announcements - {ex.Message}.";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Announcements: the list could not be loaded.");
            StatusMessage = "Could not load announcements - see today's log.";
        }
        finally
        {
            IsBusy = false;
            RefreshCommand.NotifyCanExecuteChanged();
        }

        // After the list is on screen, not before it: a post with no picture
        // should not wait behind one that has a slow picture.
        await LoadImagesAsync();
    }

    private async Task LoadImagesAsync()
    {
        foreach (AnnouncementRow row in Announcements)
        {
            if (!row.HasImage || row.Image is not null)
                continue;

            row.Image = await LoadImageAsync(row.Source.ImageUrl);
        }
    }

    /// <summary>Null for anything that is not an image this is willing to
    /// fetch, or that failed to arrive - the row then shows its text and no
    /// picture, which is the right outcome either way.</summary>
    private static async Task<Bitmap?> LoadImageAsync(string url)
    {
        if (!IsAllowedImage(url))
        {
            Log.Warning("Announcements: an image address was refused by the client's own host check.");
            return null;
        }

        try
        {
            using HttpResponseMessage response = await Images.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

            if (!response.IsSuccessStatusCode)
                return null;

            // Checked before reading, so an enormous one is never pulled down
            // in the first place; a missing length is checked again after.
            if (response.Content.Headers.ContentLength > MaxImageBytes)
                return null;

            byte[] bytes = await response.Content.ReadAsByteArrayAsync();

            if (bytes.Length == 0 || bytes.Length > MaxImageBytes)
                return null;

            using var stream = new MemoryStream(bytes);

            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Announcements: an image could not be loaded.");
            return null;
        }
    }

    internal static bool IsAllowedImage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttps)
            return false;

        foreach (string host in AllowedImageHosts)
        {
            if (uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
