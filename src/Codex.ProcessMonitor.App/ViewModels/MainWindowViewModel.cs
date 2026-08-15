using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Codex.ProcessMonitor.App.Models;
using Codex.ProcessMonitor.App.Services;
using Microsoft.Win32;
using WpfSaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace Codex.ProcessMonitor.App.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly ProcessMonitorService _monitor;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _alertKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expandedProcessKeys = new(StringComparer.Ordinal);
    private Task? _consumerTask;
    private int _selectedPageIndex;
    private double _cpuPercent;
    private double _memoryPercent;
    private long _workingSetBytes;
    private int _processCount;
    private string _lastUpdatedText = "等待首个样本";
    private string _samplingStatus = "准备中";
    private bool _isMonitoring;
    private bool _minimizeToTray = true;
    private bool _startWithWindows;
    private string _noticeText = "只读观察模式 · 未启用自启动";

    public MainWindowViewModel(ProcessMonitorService monitor, Dispatcher dispatcher)
    {
        _monitor = monitor;
        _dispatcher = dispatcher;

        CpuHistory = new ObservableCollection<double>();
        MemoryHistory = new ObservableCollection<double>();
        History = new ObservableCollection<HistoryItem>();
        ProcessTree = new ObservableCollection<ProcessNode>();
        Windows = new ObservableCollection<WindowItem>();
        Capabilities = new ObservableCollection<CapabilityItem>(
            monitor.Capabilities.Select(static sample => new CapabilityItem(sample)));
        Alerts = new ObservableCollection<AlertItem>();

        NavigateCommand = new RelayCommand(parameter =>
        {
            if (parameter is int index)
            {
                SelectedPageIndex = index;
            }
            else if (parameter is string text && int.TryParse(text, out var parsed))
            {
                SelectedPageIndex = parsed;
            }
        });
        ExportHistoryCommand = new RelayCommand(ExportHistory);
        CopyDiagnosticReportCommand = new RelayCommand(CopyDiagnosticReport);
        RefreshCommand = new RelayCommand(() => NoticeText = "刷新请求已提交，数据由后台采样器更新。", () => IsMonitoring);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<double> CpuHistory { get; }
    public ObservableCollection<double> MemoryHistory { get; }
    public ObservableCollection<HistoryItem> History { get; }
    public ObservableCollection<ProcessNode> ProcessTree { get; }
    public ObservableCollection<WindowItem> Windows { get; }
    public ObservableCollection<CapabilityItem> Capabilities { get; }
    public ObservableCollection<AlertItem> Alerts { get; }

    public ICommand NavigateCommand { get; }
    public ICommand ExportHistoryCommand { get; }
    public ICommand CopyDiagnosticReportCommand { get; }
    public ICommand RefreshCommand { get; }

    public int SelectedPageIndex
    {
        get => _selectedPageIndex;
        set => SetField(ref _selectedPageIndex, Math.Clamp(value, 0, 4));
    }

    public double CpuPercent
    {
        get => _cpuPercent;
        private set
        {
            if (SetField(ref _cpuPercent, value))
            {
                OnPropertyChanged(nameof(CpuPercentText));
            }
        }
    }

    public string CpuPercentText => $"{CpuPercent:0.0}%";

    public double MemoryPercent
    {
        get => _memoryPercent;
        private set
        {
            if (SetField(ref _memoryPercent, value))
            {
                OnPropertyChanged(nameof(MemoryPercentText));
            }
        }
    }

    public string MemoryPercentText => $"{MemoryPercent:0.0}%";

    public long WorkingSetBytes
    {
        get => _workingSetBytes;
        private set
        {
            if (SetField(ref _workingSetBytes, value))
            {
                OnPropertyChanged(nameof(WorkingSetText));
            }
        }
    }

    public string WorkingSetText => FormatBytes(WorkingSetBytes);

    public int ProcessCount
    {
        get => _processCount;
        private set
        {
            if (SetField(ref _processCount, value))
            {
                OnPropertyChanged(nameof(ProcessCountText));
            }
        }
    }

    public string ProcessCountText => ProcessCount.ToString("N0");

    public string LastUpdatedText
    {
        get => _lastUpdatedText;
        private set => SetField(ref _lastUpdatedText, value);
    }

    public string SamplingStatus
    {
        get => _samplingStatus;
        private set => SetField(ref _samplingStatus, value);
    }

    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set
        {
            if (SetField(ref _isMonitoring, value))
            {
                OnPropertyChanged(nameof(MonitoringStatusText));
                if (RefreshCommand is RelayCommand relay)
                {
                    relay.RaiseCanExecuteChanged();
                }
            }
        }
    }

    public string MonitoringStatusText => IsMonitoring ? "实时监控中" : "监控已暂停";

    public string AlertSummaryText => $"最近 {Alerts.Count} 条";

    public string WindowSummaryText
    {
        get
        {
            var foreground = Windows.FirstOrDefault(static window => window.State == "前台");
            return foreground is null
                ? $"已关联 {Windows.Count} 个顶级窗口"
                : $"当前前台：{foreground.Label} → {foreground.OwnerProcessText}";
        }
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set => SetField(ref _minimizeToTray, value);
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            // The first release deliberately does not write Run registry keys.
            // Keeping the setting visible makes the non-self-start default clear
            // and leaves a stable seam for a future settings service.
            if (value)
            {
                NoticeText = "当前版本不注册系统自启动，设置保持关闭以避免改变系统配置。";
                value = false;
                OnPropertyChanged(nameof(StartWithWindows));
            }

            SetField(ref _startWithWindows, value);
        }
    }

    public string NoticeText
    {
        get => _noticeText;
        private set => SetField(ref _noticeText, value);
    }

    public async Task StartAsync()
    {
        if (IsMonitoring)
        {
            return;
        }

        IsMonitoring = true;
        SamplingStatus = "后台采样中";
        try
        {
            await _monitor.StartAsync(_lifetime.Token).ConfigureAwait(false);
            _consumerTask = Task.Run(ConsumeSnapshotsAsync, CancellationToken.None);
        }
        catch (Exception exception)
        {
            IsMonitoring = false;
            SamplingStatus = "启动失败";
            NoticeText = $"监控启动失败：{exception.Message}";
        }
    }

    public async ValueTask StopAsync()
    {
        if (!IsMonitoring && _consumerTask is null)
        {
            return;
        }

        IsMonitoring = false;
        SamplingStatus = "正在停止";
        _lifetime.Cancel();
        await _monitor.StopAsync().ConfigureAwait(false);
        if (_consumerTask is not null)
        {
            try
            {
                await _consumerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            _consumerTask = null;
        }
        SamplingStatus = "已停止";
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        await _monitor.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ConsumeSnapshotsAsync()
    {
        try
        {
            await foreach (var snapshot in _monitor.ReadSnapshotsAsync(_lifetime.Token).ConfigureAwait(false))
            {
                // Sampling never runs here. This dispatch only applies a completed
                // immutable snapshot to WPF-bound collections and properties.
                await _dispatcher.InvokeAsync(() => ApplySnapshot(snapshot), DispatcherPriority.DataBind, _lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                SamplingStatus = "采样异常";
                NoticeText = $"采样通道异常：{exception.Message}";
            });
        }
    }

    private void ApplySnapshot(MonitorSnapshot snapshot)
    {
        CpuPercent = snapshot.CpuPercent;
        MemoryPercent = snapshot.MemoryPercent;
        WorkingSetBytes = snapshot.WorkingSetBytes;
        ProcessCount = snapshot.ProcessCount;
        LastUpdatedText = $"最近更新：{snapshot.Timestamp.LocalDateTime:HH:mm:ss}";
        SamplingStatus = "后台采样中";

        AddBounded(CpuHistory, snapshot.CpuPercent, 60);
        AddBounded(MemoryHistory, snapshot.MemoryPercent, 60);
        var historyItem = new HistoryItem(new MetricSample(snapshot.Timestamp, snapshot.CpuPercent, snapshot.MemoryPercent));
        History.Insert(0, historyItem);
        while (History.Count > 120)
        {
            History.RemoveAt(History.Count - 1);
        }

        RebuildProcessTree(snapshot.Processes);
        RebuildWindows(snapshot.Windows ?? Array.Empty<WindowSample>());
        foreach (var alert in snapshot.Alerts)
        {
            var key = $"{alert.Timestamp.Ticks}:{alert.Title}";
            if (_alertKeys.Add(key))
            {
                Alerts.Insert(0, new AlertItem(alert));
                OnPropertyChanged(nameof(AlertSummaryText));
            }
        }

        while (Alerts.Count > 40)
        {
            var removed = Alerts[^1];
            Alerts.RemoveAt(Alerts.Count - 1);
            _alertKeys.Remove($"{removed.Timestamp.Ticks}:{removed.Title}");
            OnPropertyChanged(nameof(AlertSummaryText));
        }
    }

    private void RebuildProcessTree(IReadOnlyList<ProcessSample> samples)
    {
        // TreeView recreates its containers whenever the bound collection is
        // replaced. Capture expansion by the sampler's PID+start-time key so
        // a normal refresh does not collapse an item the user just opened, and
        // a reused PID cannot inherit the old process's expansion state.
        foreach (var node in Flatten(ProcessTree))
        {
            if (node.IsExpanded)
            {
                _expandedProcessKeys.Add(node.InstanceKey);
            }
        }

        var nodes = samples
            .Select(sample => new ProcessNode(sample))
            .ToDictionary(static node => node.InstanceKey, StringComparer.Ordinal);
        var nodesByPid = nodes.Values
            .GroupBy(static node => node.ProcessId)
            .ToDictionary(static group => group.Key, static group => group.ToArray());

        foreach (var node in nodes.Values)
        {
            node.IsExpanded = _expandedProcessKeys.Contains(node.InstanceKey);
        }

        ProcessTree.Clear();
        foreach (var node in nodes.Values.OrderByDescending(static node => node.CpuPercent))
        {
            // ParentProcessId alone is not an identity (PIDs can be reused),
            // so only attach when exactly one current process has that PID.
            if (node.ParentProcessId != 0
                && nodesByPid.TryGetValue(node.ParentProcessId, out var parents)
                && parents.Length == 1)
            {
                parents[0].Children.Add(node);
            }
            else
            {
                ProcessTree.Add(node);
            }
        }

        var currentKeys = nodes.Keys.ToHashSet(StringComparer.Ordinal);
        _expandedProcessKeys.RemoveWhere(key => !currentKeys.Contains(key));
    }

    private void RebuildWindows(IReadOnlyList<WindowSample> samples)
    {
        Windows.Clear();
        foreach (var sample in samples)
        {
            Windows.Add(new WindowItem(sample));
        }

        OnPropertyChanged(nameof(WindowSummaryText));
    }

    private static IEnumerable<ProcessNode> Flatten(IEnumerable<ProcessNode> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in Flatten(root.Children))
            {
                yield return child;
            }
        }
    }

    private void ExportHistory()
    {
        var dialog = new WpfSaveFileDialog
        {
            Title = "导出历史 CSV",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            FileName = $"codex-monitor-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            AddExtension = true,
            DefaultExt = ".csv"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, DiagnosticReportService.BuildCsv(History.Reverse()), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            NoticeText = $"CSV 已导出：{Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception exception)
        {
            NoticeText = $"CSV 导出失败：{exception.Message}";
        }
    }

    private void CopyDiagnosticReport()
    {
        var report = DiagnosticReportService.BuildReport(
            DateTimeOffset.Now,
            CpuPercent,
            MemoryPercent,
            ProcessCount,
            Capabilities,
            Alerts);
        try
        {
            System.Windows.Clipboard.SetText(report);
            NoticeText = "诊断报告已复制到剪贴板。";
        }
        catch (Exception exception)
        {
            NoticeText = $"复制诊断报告失败：{exception.Message}";
        }
    }

    private static void AddBounded(ObservableCollection<double> collection, double value, int maximum)
    {
        collection.Add(value);
        while (collection.Count > maximum)
        {
            collection.RemoveAt(0);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "—";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024):0.0} MB";
        return $"{bytes / (1024d * 1024 * 1024):0.0} GB";
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
