using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Codex.ProcessMonitor.App.Services;
using Codex.ProcessMonitor.App.ViewModels;
using DrawingIcon = System.Drawing.Icon;
using Forms = System.Windows.Forms;

namespace Codex.ProcessMonitor.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly DrawingIcon _trayIcon;
    private readonly Forms.NotifyIcon _notifyIcon;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel(
            MonitorCompositionRoot.CreateMonitor(),
            Dispatcher);
        DataContext = _viewModel;

        var menu = new Forms.ContextMenuStrip();
        var showItem = new Forms.ToolStripMenuItem("显示监控工作台");
        showItem.Click += (_, _) => ShowFromTray();
        var exitItem = new Forms.ToolStripMenuItem("退出监视器");
        exitItem.Click += (_, _) => ExitFromTray();
        menu.Items.Add(showItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = LoadTrayIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "Codex 进程监视器（只读）",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
        => await _viewModel.StartAsync();

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose && _viewModel.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayIcon.Dispose();
        await _viewModel.DisposeAsync();
    }

    private static DrawingIcon LoadTrayIcon()
    {
        try
        {
            var executablePath = Environment.ProcessPath
                ?? System.Windows.Application.ResourceAssembly.Location;
            if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
            {
                var icon = DrawingIcon.ExtractAssociatedIcon(executablePath);
                if (icon is not null)
                {
                    return icon;
                }
            }
        }
        catch (Exception)
        {
            // The monitor remains usable even if Windows cannot read the executable icon.
        }

        return (DrawingIcon)SystemIcons.Application.Clone();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _viewModel.MinimizeToTray)
        {
            Hide();
        }
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.MinimizeToTray)
        {
            Hide();
            return;
        }

        Close();
    }

    private void ShowFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        });
    }

    private void ExitFromTray()
    {
        _allowClose = true;
        Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
    }
}
