using Avalonia.Controls;
using Foot_Tracker.Views.GuideRendering;

namespace Foot_Tracker.Views;

public partial class GuideWindow : Window
{
    public GuideWindow()
    {
        InitializeComponent();
    }

    /// <summary>Call right after construction, before Show()/ShowDialog().</summary>
    public void LoadGuide(string guideFolderName, string title)
    {
        var vm = new ViewModels.GuideViewModel { GuideTitle = title };
        DataContext = vm;

        string? html = vm.LoadGuide(guideFolderName);

        if (html is null || vm.GuideFolderPath is null)
            return; // ErrorMessage is already set - the window shows that instead.

        try
        {
            HtmlNode body = SimpleHtmlParser.ParseBody(html);

            var renderer = new GuideHtmlRenderer(
                AppContext.BaseDirectory,
                vm.GuideFolderPath,
                onExternalLink: url =>
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = url,
                            UseShellExecute = true
                        });
                    }
                    catch
                    {
                        // A broken external link in guide content isn't worth
                        // surfacing as an app error - just do nothing.
                    }
                });

            var scrollViewer = renderer.Render(body);
            this.FindControl<ContentControl>("GuideContentHost")!.Content = scrollViewer;
        }
        catch (Exception ex)
        {
            vm.ErrorMessage = $"The guide could not be displayed:\n{ex.Message}";
        }
    }
}
