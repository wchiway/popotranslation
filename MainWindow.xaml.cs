using System;
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
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "PopoTranslation.ico");
        try
        {
            if (System.IO.File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        }
        catch (Exception)
        {
            // Keep the default window icon if the optional runtime icon cannot be loaded.
        }
        ResizeForContent();
        AppWindow.Closing += OnClosing;
        Settings.ExitForUpdate = ExitApplication;
        _controller = new ApplicationController(Settings, DispatcherQueue, ShowSettings, ExitApplication, iconPath);
        _ = _controller.InitializeAsync();
    }

    private void ResizeForContent()
    {
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        if (display is null) return;
        var width = Math.Clamp(display.WorkArea.Width - 96, 820, 1180);
        var height = Math.Clamp(display.WorkArea.Height - 96, 700, 900);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
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
