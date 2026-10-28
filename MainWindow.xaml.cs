using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using popotranslation.Services;

namespace popotranslation;

public sealed partial class MainWindow : Window
{
    private readonly ApplicationController _controller;
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Closing += OnClosing;
        _controller = new ApplicationController(Settings, DispatcherQueue, ShowSettings, ExitApplication);
        _ = _controller.InitializeAsync();
    }

    public void ShowSettings()
    {
        AppWindow.Show();
        Activate();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting) return;
        if (!_controller.CanHide)
        {
            _exiting = true;
            _controller.Dispose();
            return;
        }
        args.Cancel = true;
        sender.Hide();
    }

    private void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        _controller.Dispose();
        Close();
    }
}
