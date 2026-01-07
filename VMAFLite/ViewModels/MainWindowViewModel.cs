#pragma warning disable SYSLIB1045, SYSLIB1054, CA1822 

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VMAFLite.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private const string AppVersion = "1.0.0";
    private const string AppFolderName = "VMAFLite";
    private const string ModelStandard = "vmaf_v0.6.1neg";
    private const string Model4k = "vmaf_4k_v0.6.1neg";
    private const string ColorPristine = "#00C853";
    private const string ColorHigh = "#C6FF00";
    private const string ColorOk = "#FFAB00";
    private const string ColorBad = "#D50000";
    private const string ColorNeutral = "#3E3E42";
    private const string ColorResultGray = "LightGray";

    private static readonly FilePickerFileType[] _videoFileFilters = [new("Videos") { Patterns = ["*.mkv", "*.mp4", "*.avi", "*.mov", "*.webm", "*.mts", "*.m2ts", "*.mxf", "*.wmv", "*.mpg", "*.mpeg", "*.vob", "*.flv", "*.m4v", "*.ts"] }];
    private static readonly Regex _timeRegex = new(@"time=(\d{2}:\d{2}:\d{2}\.\d{2})", RegexOptions.Compiled);
    private static readonly Regex _fpsRegex = new(@"fps=\s*([\d\.]+)", RegexOptions.Compiled);
    private static readonly Regex _resolutionRegex = new(@"Video:.*,\s+(\d+)x(\d+)", RegexOptions.Compiled);
    private static readonly Regex _durationRegex = new(@"Duration: (\d{2}:\d{2}:\d{2}\.\d{2})", RegexOptions.Compiled);
    private static readonly Regex _metaFramesRegex = new(@"NUMBER_OF_FRAMES.*:\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex _probeFpsRegex = new(@",\s*([\d\.]+)\s*fps", RegexOptions.Compiled);
    private static readonly Regex _videoStreamRegex = new(@"Stream\s*#\d+:\d+.*Video:.*", RegexOptions.Compiled);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint SetThreadExecutionState(uint esFlags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessInformation(IntPtr hProcess, int ProcessInformationClass, IntPtr ProcessInformation, uint ProcessInformationSize);

    private struct PROCESS_POWER_THROTTLING_STATE { public uint Version; public uint ControlMask; public uint StateMask; }
    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 1;
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    private Process? _ffmpegProcess;
    private string? _cachedFFmpegPath;
    private TaskCompletionSource<bool>? _tcs;
    private double _totalDuration;
    private readonly Stopwatch _stopWatch = new();
    private readonly int _threads = Environment.ProcessorCount;
    private readonly DispatcherTimer _loadingTimer;
    private int _loadingTickCount;

    [ObservableProperty] private string _windowTitle = $"VMAFLite v{AppVersion}";
    public string Version => AppVersion;

    [ObservableProperty] private string _statusMessage = "Select Reference Video";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _overallScore = "----";
    [ObservableProperty] private string _stableMinScore = "----";
    [ObservableProperty] private string _worstFrameScore = "----";
    [ObservableProperty] private string _overallColor = ColorNeutral;
    [ObservableProperty] private string _stableMinColor = ColorNeutral;
    [ObservableProperty] private string _lowestMinColor = ColorNeutral;
    [ObservableProperty] private string _resultTextColor = "Gray";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsDistortedBrowseEnabled))] private string _referencePath = "";
    [ObservableProperty] private string _distortedPath = "";
    [ObservableProperty] private string _referenceName = "Select Reference Video";
    [ObservableProperty] private string _distortedName = "Select Distorted Video";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsDistortedBrowseEnabled))] private bool _isRunning;
    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private bool _isStandardModel = true;
    [ObservableProperty] private bool _is4kModel;
    [ObservableProperty] private bool _isAboutVisible;
    [ObservableProperty] private bool _isResultsAvailable;
    [ObservableProperty] private bool _isCopyEnabled = true;

    public bool IsDistortedBrowseEnabled => !IsRunning && !string.IsNullOrEmpty(ReferencePath);

    public MainWindowViewModel()
    {
        _loadingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _loadingTimer.Tick += (s, e) => AnimateLoadingText();
        Task.Run(PrepareFFmpeg);
    }

    private void StartLoading() { _loadingTickCount = 0; _loadingTimer.Start(); }
    private void StopLoading() => _loadingTimer.Stop();

    private void AnimateLoadingText()
    {
        _loadingTickCount++;
        string dots = (_loadingTickCount % 5) switch { 1 => ".    ", 2 => "..   ", 3 => "...  ", 4 => "....  ", _ => "     " };
        OverallScore = StableMinScore = WorstFrameScore = dots;
    }

    public void SetReferenceFile(string path)
    {
        DistortedPath = "";
        DistortedName = "Select Distorted Video";
        ReferencePath = path;
        ReferenceName = Path.GetFileName(path);
        Task.Run(async () =>
        {
            var (width, _) = await GetVideoDimensions(path);
            Is4kModel = width > 2000;
            IsStandardModel = !Is4kModel;
        });
        CheckRunState();
    }

    public void SetDistortedFile(string path)
    {
        if (string.IsNullOrEmpty(ReferencePath)) return;
        if (string.Equals(path, ReferencePath, StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "Error: Reference and Distorted files cannot be the same";
            DistortedPath = "";
            DistortedName = "Select Distorted Video";
            CheckRunState();
            return;
        }
        DistortedPath = path;
        DistortedName = Path.GetFileName(path);
        CheckRunState();
    }

    [RelayCommand] private void ToggleAbout() => IsAboutVisible = !IsAboutVisible;

    [RelayCommand]
    private async Task CopyResults()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(desktop.MainWindow);
            if (topLevel?.Clipboard != null)
            {
                await topLevel.Clipboard.SetTextAsync($"{DistortedName} = OVERALL RESULT: {OverallScore} | STABLE MINIMUM: {StableMinScore} | WORST FRAME: {WorstFrameScore}");
                string oldStatus = StatusMessage;
                StatusMessage = "Results Copied";
                IsCopyEnabled = false;
                _ = Task.Run(async () => { await Task.Delay(2000); Dispatcher.UIThread.Post(() => { IsCopyEnabled = true; if (!IsRunning) StatusMessage = oldStatus; }); });
            }
        }
    }

    [RelayCommand]
    private async Task SelectReferenceFile()
    {
        if (await OpenFilePickerAsync("Select Reference Video") is { } path)
        {
            SetReferenceFile(path);
            _ = Task.Run(async () => { Dispatcher.UIThread.Post(() => StatusMessage = "Reference Video Selected"); await Task.Delay(2000); Dispatcher.UIThread.Post(() => StatusMessage = "Select Distorted Video"); });
        }
    }

    [RelayCommand]
    private async Task SelectDistortedFile()
    {
        if (await OpenFilePickerAsync("Select Distorted Video") is not { } path) return;
        if (string.Equals(path, ReferencePath, StringComparison.OrdinalIgnoreCase)) { SetDistortedFile(path); await SelectDistortedFile(); return; }

        string refOutput = await RunProbe(ReferencePath);
        string distOutput = await RunProbe(path);
        if (Math.Abs(ParseFrameCount(refOutput) - ParseFrameCount(distOutput)) > 10)
        {
            StatusMessage = "Error: Content mismatch (Frames)";
            DistortedPath = ""; DistortedName = "Select Distorted Video"; CheckRunState(); await SelectDistortedFile(); return;
        }
        if (IsHdr(refOutput) != IsHdr(distOutput))
        {
            StatusMessage = "Error: Content mismatch (HDR vs SDR)";
            DistortedPath = ""; DistortedName = "Select Distorted Video"; CheckRunState(); await SelectDistortedFile(); return;
        }

        SetDistortedFile(path);
        _ = Task.Run(async () => { Dispatcher.UIThread.Post(() => StatusMessage = "Distorted Video Selected"); await Task.Delay(2000); Dispatcher.UIThread.Post(() => { if (!IsRunning) StatusMessage = "Ready"; }); });
    }

    [RelayCommand]
    private async Task RunAnalysis()
    {
        if (IsRunning) return;
        IsRunning = true; IsResultsAvailable = false; CheckRunState();
        StatusMessage = "Initializing...";
        OverallColor = StableMinColor = LowestMinColor = ColorNeutral;
        ResultTextColor = "Gray";
        OverallScore = StableMinScore = WorstFrameScore = "     ";
        ProgressValue = 0; StartLoading(); PreventSleep(); await PrepareFFmpeg();

        try
        {
            _totalDuration = await GetVideoDuration(ReferencePath);
            if (_totalDuration <= 0) throw new Exception("Could not read video duration");
            var (refW, refH) = await GetVideoDimensions(ReferencePath);
            var (distW, distH) = await GetVideoDimensions(DistortedPath);

            string filterChain = (refW == distW && refH == distH) ? "[0:v]setpts=PTS-STARTPTS[dist];[1:v]setpts=PTS-STARTPTS[ref]" :
                                 (refW == distW && refH > distH) ? $"[0:v]setpts=PTS-STARTPTS[dist];[1:v]crop={distW}:{distH}:0:{(refH - distH) / 2},setpts=PTS-STARTPTS[ref]" :
                                 $"[0:v]scale={refW}:{refH}:flags=bicubic,setpts=PTS-STARTPTS[dist];[1:v]setpts=PTS-STARTPTS[ref]";

            _stopWatch.Restart();
            string model = Is4kModel ? Model4k : ModelStandard;
            string args = $"-loglevel quiet -stats -y -i \"{DistortedPath}\" -i \"{ReferencePath}\" -lavfi \"{filterChain};[dist][ref]libvmaf=model=version={model}:log_path='vmaf.json':log_fmt=json:n_threads={_threads}\" -an -f null -";
            await RunFFmpegProcessAsync(args);

            if (IsRunning)
            {
                await Task.Delay(300);
                string logPath = Path.Combine(Path.GetTempPath(), "vmaf.json");
                if (File.Exists(logPath)) ParseJsonResult(logPath);
            }
        }
        catch (Exception ex) { if (IsRunning) StatusMessage = $"Error: {ex.Message}"; }
        finally { RestoreSleep(); if (IsRunning) { StopLoading(); IsRunning = false; _stopWatch.Stop(); CheckRunState(); } }
    }

    private static void PreventSleep() => _ = SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
    private static void RestoreSleep() => _ = SetThreadExecutionState(ES_CONTINUOUS);

    private static void DisablePowerThrottling(IntPtr handle)
    {
        try
        {
            var ts = new PROCESS_POWER_THROTTLING_STATE { Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION, ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED, StateMask = 0 };
            int size = Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>(); IntPtr pState = Marshal.AllocHGlobal(size);
            try { Marshal.StructureToPtr(ts, pState, false); _ = SetProcessInformation(handle, ProcessPowerThrottling, pState, (uint)size); } finally { Marshal.FreeHGlobal(pState); }
        }
        catch { }
    }

    private static long ParseFrameCount(string output)
    {
        var m = _metaFramesRegex.Match(output);
        if (m.Success && long.TryParse(m.Groups[1].Value, out long frames)) return frames;
        var dm = _durationRegex.Match(output); var fm = _probeFpsRegex.Match(output);
        if (dm.Success && fm.Success) return (long)(TimeSpan.Parse(dm.Groups[1].Value).TotalSeconds * double.Parse(fm.Groups[1].Value));
        return 0;
    }

    private static bool IsHdr(string output) => _videoStreamRegex.Match(output) is { Success: true } m && m.Value.Contains("smpte2084");

    [RelayCommand]
    private void CancelAnalysis()
    {
        StopLoading(); IsRunning = false; IsResultsAvailable = false; StatusMessage = "Cancelled";
        ProgressValue = 0; OverallScore = StableMinScore = WorstFrameScore = "----";
        OverallColor = StableMinColor = LowestMinColor = ColorNeutral; ResultTextColor = "Gray";
        CheckRunState();
        Task.Run(() => { _tcs?.TrySetResult(false); KillFFmpeg(); });
    }

    private void ParseJsonResult(string path)
    {
        try
        {
            StopLoading();
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var pooled = root.GetProperty("pooled_metrics").GetProperty("vmaf");
            double harmonicMean = pooled.GetProperty("harmonic_mean").GetDouble();
            double minScore = pooled.GetProperty("min").GetDouble();

            List<double> scores = [];
            foreach (var f in root.GetProperty("frames").EnumerateArray())
                if (f.TryGetProperty("metrics", out var m) && m.TryGetProperty("vmaf", out var v)) scores.Add(v.GetDouble());

            double stableMin = scores.Count > 0 ? scores.OrderBy(x => x).ElementAt(Math.Clamp((int)Math.Ceiling(0.05 * scores.Count) - 1, 0, scores.Count - 1)) : 0;

            OverallScore = $"{harmonicMean:F2}"; StableMinScore = $"{stableMin:F2}"; WorstFrameScore = $"{minScore:F2}";
            OverallColor = harmonicMean >= 95 ? ColorPristine : harmonicMean >= 93 ? ColorHigh : harmonicMean >= 85 ? ColorOk : ColorBad;
            StableMinColor = ColorResultGray; ResultTextColor = "FloralWhite";
            StatusMessage = $"Done | Time: {_stopWatch.Elapsed:hh\\:mm\\:ss}";
            ProgressValue = 100; IsResultsAvailable = true; IsCopyEnabled = true;
            try { File.Delete(path); } catch { }
        }
        catch (Exception ex) { StopLoading(); StatusMessage = "Parsing Error: " + ex.Message; }
    }

    private void CheckRunState() => CanStart = !IsRunning && !string.IsNullOrEmpty(ReferencePath) && !string.IsNullOrEmpty(DistortedPath);

    private async Task PrepareFFmpeg()
    {
        try
        {
            string localDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);
            string ffmpegPath = Path.Combine(localDataPath, "ffmpeg.exe");
            if (!Directory.Exists(localDataPath)) Directory.CreateDirectory(localDataPath);
            using var rs = Assembly.GetExecutingAssembly().GetManifestResourceStream("VMAFLite.Assets.ffmpeg.exe");
            if (rs == null) return;

            bool overwrite = true;
            if (File.Exists(ffmpegPath)) { using var fs = File.OpenRead(ffmpegPath); if (fs.Length == rs.Length) overwrite = false; }
            if (overwrite) { using var fs = new FileStream(ffmpegPath, FileMode.Create); await rs.CopyToAsync(fs); }
            _cachedFFmpegPath = ffmpegPath;
        }
        catch { Dispatcher.UIThread.Post(() => StatusMessage = "Engine Error"); }
    }

    private async Task RunFFmpegProcessAsync(string arguments)
    {
        string workingDir = Path.GetTempPath();
        _tcs = new TaskCompletionSource<bool>();
        _ffmpegProcess = new Process { StartInfo = new ProcessStartInfo { FileName = _cachedFFmpegPath ?? "ffmpeg.exe", Arguments = arguments, WorkingDirectory = workingDir, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true }, EnableRaisingEvents = true };
        _ffmpegProcess.ErrorDataReceived += (s, e) => { if (e.Data != null) Dispatcher.UIThread.Post(() => UpdateProgress(e.Data)); };
        _ffmpegProcess.Exited += (s, e) => _tcs.TrySetResult(true);
        _ffmpegProcess.Start();
        DisablePowerThrottling(_ffmpegProcess.Handle);
        _ffmpegProcess.BeginErrorReadLine();
        await _tcs.Task;
    }

    private void UpdateProgress(string data)
    {
        if (!IsRunning) return;
        var tm = _timeRegex.Match(data);
        if (tm.Success && TimeSpan.TryParse(tm.Groups[1].Value, out var ct))
        {
            ProgressValue = Math.Clamp((ct.TotalSeconds / _totalDuration) * 100, 0, 100);
            var fm = _fpsRegex.Match(data);
            StatusMessage = $"FPS: {(fm.Success ? fm.Groups[1].Value : "0")} | Process Time: {ct:hh\\:mm\\:ss} | Elapsed: {_stopWatch.Elapsed:hh\\:mm\\:ss}";
        }
    }

    private async Task<string> RunProbe(string path)
    {
        if (string.IsNullOrEmpty(_cachedFFmpegPath)) await PrepareFFmpeg();
        return await Task.Run(() => {
            using var p = Process.Start(new ProcessStartInfo(_cachedFFmpegPath!, $"-i \"{path}\"") { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            return p?.StandardError.ReadToEnd() ?? "";
        });
    }

    private async Task<double> GetVideoDuration(string path) => TimeSpan.TryParse(_durationRegex.Match(await RunProbe(path)).Groups[1].Value, out var ts) ? ts.TotalSeconds : 0;

    private async Task<(int Width, int Height)> GetVideoDimensions(string path)
    {
        var m = _resolutionRegex.Match(await RunProbe(path));
        return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : (0, 0);
    }

    public void KillFFmpeg()
    {
        if (_ffmpegProcess == null) return;
        try { if (!_ffmpegProcess.HasExited) { using var kp = Process.Start(new ProcessStartInfo("taskkill", $"/F /T /PID {_ffmpegProcess.Id}") { CreateNoWindow = true, UseShellExecute = false }); kp?.WaitForExit(500); } }
        catch { }
        finally { _ffmpegProcess.Dispose(); _ffmpegProcess = null; }
    }

    private static async Task<string?> OpenFilePickerAsync(string title)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
        {
            var f = await Avalonia.Controls.TopLevel.GetTopLevel(d.MainWindow)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, FileTypeFilter = _videoFileFilters });
            return f.Count > 0 ? f[0].Path.LocalPath : null;
        }
        return null;
    }
}