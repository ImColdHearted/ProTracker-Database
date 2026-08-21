using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// Ported from Forms/MegaStones/Test.cs (a local HTML "guide" viewer). WebView2's
/// SetVirtualHostNameToFolderMapping (guides.local / assets.local) is a WebView2-
/// specific feature; the original attempt at a cross-platform replacement used
/// WebViewControl-Avalonia (a CEF wrapper), but that requires bundling native CEF
/// runtime binaries alongside the app - easy to get wrong via a hand-authored
/// .csproj, and it fails hard (crashes the process) if the binaries aren't found
/// at runtime. Since the guide content is just static local HTML/CSS with no
/// interactivity, this now opens it in the user's default browser instead -
/// zero extra native dependencies, and a failure here can't crash the app.
///
/// Reusable for the other guide forms (LegendaryPokemon, EVZones, Excavations, etc.)
/// once you're ready to write actual guide HTML for them - just point guideFolderName
/// at their folder under DataFiles/Guides/&lt;name&gt;.
/// </summary>
public sealed partial class GuideViewModel : ViewModelBase
{
    [ObservableProperty] private string? guideUrl;
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private string guideTitle = "Guide";
    [ObservableProperty] private bool hasError;
    [ObservableProperty] private bool canOpen;

    partial void OnErrorMessageChanged(string? value) => HasError = !string.IsNullOrEmpty(value);
    partial void OnGuideUrlChanged(string? value) => CanOpen = !string.IsNullOrEmpty(value);

    public void LoadGuide(string guideFolderName)
    {
        string guideRoot = Path.Combine(AppContext.BaseDirectory, "DataFiles", "Guides", guideFolderName);
        string indexPath = Path.Combine(guideRoot, "index.html");

        if (!File.Exists(indexPath))
        {
            ErrorMessage = $"Guide was not found:\n{indexPath}";
            return;
        }

        GuideUrl = new Uri(indexPath).AbsoluteUri;
    }

    [RelayCommand]
    private void OpenInBrowser()
    {
        if (string.IsNullOrWhiteSpace(GuideUrl))
            return;

        try
        {
            // UseShellExecute is required here - without it, .NET tries to execute
            // the URL as a process directly instead of asking the OS to hand it to
            // the default browser, which throws Win32Exception.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = GuideUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not open the guide in a browser:\n{ex.Message}";
        }
    }
}
