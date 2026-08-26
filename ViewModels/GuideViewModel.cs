using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// Ported from Forms/MegaStones/Test.cs. Guide content is now rendered directly with
/// native Avalonia controls (see Views/GuideRendering/GuideHtmlRenderer.cs) instead of
/// an embedded browser - a prior attempt with WebViewControl-Avalonia (CEF) crashed on
/// native runtime packaging, and WebView2 specifically is Windows-only, which isn't
/// acceptable given how much work went into genuine Linux/macOS support elsewhere.
/// "Open in Browser" is kept as a manual fallback in case some future guide has
/// markup the custom renderer doesn't handle well.
/// </summary>
public sealed partial class GuideViewModel : ViewModelBase
{
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private string guideTitle = "Guide";
    [ObservableProperty] private bool hasError;
    [ObservableProperty] private bool canOpenInBrowser;

    public string? GuideFolderPath { get; private set; }
    private string? _guideHtmlPath;

    partial void OnErrorMessageChanged(string? value) => HasError = !string.IsNullOrEmpty(value);

    /// <summary>Returns the guide's raw HTML if found (with GuideFolderPath set for
    /// resolving relative assets), or null (with ErrorMessage set) otherwise.</summary>
    public string? LoadGuide(string guideFolderName)
    {
        string guideRoot = Path.Combine(AppContext.BaseDirectory, "DataFiles", "Guides", guideFolderName);
        string indexPath = Path.Combine(guideRoot, "index.html");

        if (!File.Exists(indexPath))
        {
            ErrorMessage = $"Guide was not found:\n{indexPath}";
            return null;
        }

        GuideFolderPath = guideRoot;
        _guideHtmlPath = indexPath;
        CanOpenInBrowser = true;

        try
        {
            return File.ReadAllText(indexPath);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"The guide could not be read:\n{ex.Message}";
            return null;
        }
    }

    [RelayCommand]
    private void OpenInBrowser()
    {
        if (string.IsNullOrWhiteSpace(_guideHtmlPath))
            return;

        try
        {
            string url = new Uri(_guideHtmlPath).AbsoluteUri;

            // UseShellExecute is required here - without it, .NET tries to execute
            // the URL as a process directly instead of asking the OS to hand it to
            // the default browser, which throws Win32Exception.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not open the guide in a browser:\n{ex.Message}";
        }
    }
}