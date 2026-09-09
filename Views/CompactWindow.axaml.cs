using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;
using Serilog;

namespace Foot_Tracker.Views;

public partial class CompactWindow : Window
{
    private readonly Window? _owner;
    private bool _ownerRestored;

    public CompactWindow()
    {
        InitializeComponent();
    }

    public CompactWindow(Window owner) : this()
    {
        _owner = owner;

        // §191: restore the main window on EVERY close, not only the one the
        // X button below performs.
        //
        // Compact Mode HIDES the main window (MainWindow.axaml.cs's
        // CompactModeButton_Click - the only Hide() in the app) and relies on
        // this window's own close button to bring it back. Nothing forced the
        // user through that button. This window has WindowDecorations="None",
        // so it has no title bar of its own, but Alt+F4 still sends a close
        // on Windows and the taskbar entry still offers one - and either of
        // those left Pro Tracker running with no window on screen at all.
        //
        // An invisible running copy is not just confusing. It holds its own
        // executable open, so the next publish into that folder fails with
        // "Access to the path ... is denied" from the single-file bundler,
        // which is how this was found.
        //
        // The application-shutdown reasons are excluded deliberately: when
        // Avalonia is tearing the app down it closes every window, and
        // re-showing the main window in the middle of that would fight the
        // shutdown it is part of.
        Closing += (_, e) =>
        {
            if (e.CloseReason is WindowCloseReason.ApplicationShutdown
                              or WindowCloseReason.OSShutdown)
            {
                return;
            }

            RestoreOwner();
        };
    }

    /// <summary>§191. Puts the main window back and hands dialog ownership
    /// with it - see MainWindowViewModel.ActiveWindow. Idempotent, because
    /// the close button and the Closing handler can both reach it, and it
    /// swallows its own failure: this runs while a window is closing and a
    /// throw here would be a crash on the way out.</summary>
    private void RestoreOwner()
    {
        if (_owner is null || _ownerRestored)
            return;

        _ownerRestored = true;

        try
        {
            if (_owner.DataContext is MainWindowViewModel vm)
                vm.ActiveWindow = _owner;

            _owner.Show();
            _owner.Activate();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The main window could not be restored when Compact Mode closed");
        }
    }

    private void DragHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    // §191: just closes now. The restore happens in the Closing handler the
    // constructor wires up, which every way out of this window goes through.
    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}