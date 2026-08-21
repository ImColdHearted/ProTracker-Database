using Avalonia.Controls;

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
        Title = title;
        var vm = new ViewModels.GuideViewModel();
        vm.LoadGuide(guideFolderName);
        DataContext = vm;
    }
}
