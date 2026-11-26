using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SystemCare20.Database;
using SystemCare20.Models;
using SystemCare20.Services;
using SystemCare20.Views;
using WinForms = System.Windows.Forms;

namespace SystemCare20;

public partial class MainWindow : Window
{
    // ------------------------------------------------------------------ module catalogue
    private sealed class CareModule
    {
        public string Id = "", Glyph = "";
        public bool Real;                 // backed by a real measurement / action
        public CheckBox? Check;
        public int Issues;
        public long Bytes;
        public Func<string> DetailFn = () => "";
        public string Name => Loc.T("m." + Id);
        public string Description => Loc.T("m." + Id + ".d");
        public string Detail => DetailFn();
        public bool AiDefault => Real && Id != "recycle";
    }

    private readonly List<CareModule> _modules = new()
    {
        new() { Id = "junk",     Glyph = "\uE74D", Real = true },
        new() { Id = "browser",  Glyph = "\uE774", Real = true },
        new() { Id = "recycle",  Glyph = "\uE107", Real = true },
        new() { Id = "privacy",  Glyph = "\uE72E", Real = true },
        new() { Id = "shortcut", Glyph = "\uE71B", Real = true },
        new() { Id = "startup",  Glyph = "\uE7E8", Real = true },
        new() { Id = "security", Glyph = "\uE83D", Real = true },
        new() { Id = "spyware",  Glyph = "\uEA18", Real = true },
        new() { Id = "diskerr",  Glyph = "\uEDA2", Real = true },
        new() { Id = "ram",      Glyph = "\uE964", Real = true },
        new() { Id = "updates",  Glyph = "\uE895", Real = true },
    };

    // real scan results kept for FIX
    private PrivacyTraces.Report? _privacy;
    private List<string> _brokenLinks = new();
    private (long bytes, long items) _recycle;
    private SecurityReport? _security;

    private readonly RealmLocalDatabase _db = RealmLocalDatabase.Instance;
    private readonly DispatcherTimer _metricsTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private PerformanceWidgetWindow? _widget;

    // scan state
    private CancellationTokenSource? _scanCts;
    private bool _paused;
    private List<FileInfo> _junkFiles = new();
    private List<FileInfo> _browserFiles = new();
    private DateTime _scanStart;

    // custom clean state
    private List<FileGroup> _groups = new();
    private CancellationTokenSource? _cleanCts;

    private List<StartupEntry> _startup = new();
    private bool _hiddenOnce;

    public MainWindow()
    {
        InitializeComponent();
        FitToWorkArea();
        FlowDirection = Loc.Flow;
        Loc.Changed += ApplyLanguage;
        ThemeManager.Changed += ApplyLanguage;
        StateChanged += (_, _) => BtnMax.Content = WindowState == WindowState.Maximized ? "" : "";

        BuildModuleGrid();
        LoadSettings();
        InitCleanLocations();
        RefreshLastScan();

        _metricsTimer.Tick += (_, _) => UpdateMetrics();
        _metricsTimer.Start();
        UpdateMetrics();
    }

    // ================================================================== chrome / navigation
    private void BtnMin_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void BtnMax_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void BtnClose_Click(object s, RoutedEventArgs e)
    {
        Hide();
        if (!_hiddenOnce) { _hiddenOnce = true; ((App)Application.Current).NotifyHiddenToTray(); }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!((App)Application.Current).IsExiting) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }

    private void BtnWidget_Click(object s, RoutedEventArgs e) => TogglePerformanceWidget();

    // ================================================================== language
    private void BtnLang_Click(object s, RoutedEventArgs e)
    {
        string lang = Loc.IsArabic ? "en" : "ar";
        SetSetting("language", lang);
        Loc.SetLanguage(lang);
    }

    /// <summary>Re-renders everything built in code after a language switch.</summary>
    private void ApplyLanguage()
    {
        FlowDirection = Loc.Flow;
        if (_widget != null) _widget.FlowDirection = Loc.Flow;

        var states = _modules.ToDictionary(m => m.Id, m => m.Check?.IsChecked);
        BuildModuleGrid();
        foreach (var m in _modules) m.Check!.IsChecked = states[m.Id];
        ApplyScanMode(ModeAi.IsChecked == true);

        RefreshLastScan();
        ApplyTurbo(save: false);
        UpdateMetrics();

        // rebuild location list (translated labels) but keep browsed folders and the selection
        int sel = CleanLocation.SelectedIndex;
        var custom = CleanLocation.Items.Cast<ComboBoxItem>().Where(i => i.Tag is string t && t == (string)i.Content).ToList();
        InitCleanLocations();
        foreach (var c in custom) { CleanLocation.Items.Add(new ComboBoxItem { Content = c.Content, Tag = c.Tag }); }
        CleanLocation.SelectedIndex = Math.Clamp(sel, 0, CleanLocation.Items.Count - 1);
        UpdateCleanSelection();

        if (CareResults.Visibility == Visibility.Visible) ShowResults(_lastScanned, keepTime: true);
        if (CleanPanel.Visibility == Visibility.Visible) RenderDupTree();
        if (DiskPanel.Visibility == Visibility.Visible) { InitDiskTargets(keepSelection: true); RenderDisk(); }
        if (SpeedPanel.Visibility == Visibility.Visible) RefreshStartup();
        if (ProtectPanel.Visibility == Visibility.Visible) RenderShields();
        if (UpdaterPanel.Visibility == Visibility.Visible) RenderUpdates();
        if (ActionPanel.Visibility == Visibility.Visible) RenderActionCenter();
        if (SettingsPanel.Visibility == Visibility.Visible) RenderSettings();
    }

    public void TogglePerformanceWidget()
    {
        if (_widget != null) { _widget.Close(); _widget = null; return; }
        _widget = new PerformanceWidgetWindow();
        _widget.OnMemoryCleaned += (t, b) => ShowToast(t, b);
        _widget.Closed += (_, _) => _widget = null;
        _widget.Show();
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (CarePanel == null) return; // during InitializeComponent
        string tag = (string)((FrameworkElement)sender).Tag;
        CarePanel.Visibility = tag == "care" ? Visibility.Visible : Visibility.Collapsed;
        CleanPanel.Visibility = tag == "clean" ? Visibility.Visible : Visibility.Collapsed;
        DiskPanel.Visibility = tag == "disk" ? Visibility.Visible : Visibility.Collapsed;
        SpeedPanel.Visibility = tag == "speed" ? Visibility.Visible : Visibility.Collapsed;
        ProtectPanel.Visibility = tag == "protect" ? Visibility.Visible : Visibility.Collapsed;
        UpdaterPanel.Visibility = tag == "updater" ? Visibility.Visible : Visibility.Collapsed;
        ActionPanel.Visibility = tag == "action" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        switch (tag)
        {
            case "clean": RenderDupTree(); break;
            case "disk": EnterDiskPage(); break;
            case "speed": RefreshStartup(); break;
            case "protect": RenderShields(); break;
            case "updater":
                RenderUpdates(keepScroll: false);
                UpdaterPanel.ScrollToTop();
                if (_wingetOk == true && WingetService.LastResult == null && !_updBusy) _ = CheckUpdatesAsync();
                break;
            case "action": RenderActionCenter(); break;
            case "settings": RenderSettings(); break;
        }
    }

    private void FitToWorkArea()
    {
        var wa = SystemParameters.WorkArea;
        if (Height > wa.Height - 20) Height = Math.Max(MinHeight = Math.Min(MinHeight, wa.Height - 20), wa.Height - 20);
        if (Width > wa.Width - 20) Width = Math.Max(MinWidth = Math.Min(MinWidth, wa.Width - 20), wa.Width - 20);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + (wa.Height - Height) / 2;
    }

    // ================================================================== metrics
    private void UpdateMetrics()
    {
        var m = SystemMetrics.Instance;
        m.Sample();
        SideCpuText.Text = $"{m.CpuPercent:0}%"; SideCpuBar.Value = m.CpuPercent;
        SideRamText.Text = $"{m.RamPercent:0}%"; SideRamBar.Value = m.RamPercent;
        SideDiskLabel.Text = Loc.T("disk_label", m.DiskLabel);
        SideDiskText.Text = $"{m.DiskPercent:0}%"; SideDiskBar.Value = m.DiskPercent;
        SpeedRamPct.Text = $"{m.RamPercent:0}%";
        SpeedRamBar.Value = m.RamPercent;
        SpeedRamDetail.Text = Loc.T("ram_in_use", m.UsedRamGb, m.TotalRamGb);
    }

    // ================================================================== settings
    private string GetSetting(string key, string fallback) => _db.Find<AppSetting>(key)?.Value ?? fallback;
    private void SetSetting(string key, string value) =>
        _db.Write(() => _db.Add(new AppSetting { Key = key, Value = value, UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }));

    private void LoadSettings()
    {
        bool ai = GetSetting("scanMode", "AI") == "AI";
        ModeAi.IsChecked = ai; ModeManual.IsChecked = !ai;
        ApplyScanMode(ai);

        TurboToggle.IsChecked = GetSetting("turboBoostActive", "False") == "True";
        bool game = GetSetting("turboBoostMode", "Game") == "Game";
        TurboGame.IsChecked = game; TurboWork.IsChecked = !game;
        ApplyTurbo(save: false);
    }

    // ================================================================== CARE
    private void BuildModuleGrid()
    {
        ModuleGrid.Children.Clear();
        foreach (var m in _modules)
        {
            var check = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Right };
            check.Click += (_, _) => SyncSelectAll();
            m.Check = check;

            var icon = new Border
            {
                Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(8), Background = Res("AccentSoftBrush"),
                Child = new TextBlock { Text = m.Glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15, Foreground = Res("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            var text = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            text.Children.Add(new TextBlock { Text = m.Name, FontWeight = FontWeights.SemiBold, FontSize = 13 });
            text.Children.Add(new TextBlock { Text = m.Description, Style = (Style)FindResource("Muted"), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0) });

            var grid = new Grid();
            var top = new Grid(); top.Children.Add(icon); top.Children.Add(check);
            var sp = new StackPanel(); sp.Children.Add(top); sp.Children.Add(text);
            grid.Children.Add(sp);

            var card = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(14), Margin = new Thickness(0, 0, 12, 12), Child = grid, Cursor = System.Windows.Input.Cursors.Hand };
            card.MouseLeftButtonUp += (_, ev) => { if (check.IsEnabled && ev.OriginalSource is not CheckBox) { check.IsChecked = !check.IsChecked; SyncSelectAll(); } };
            ModuleGrid.Children.Add(card);
        }
        SyncSelectAll();
    }

    private void SyncSelectAll() => ChkSelectAll.IsChecked = _modules.All(m => m.Check!.IsChecked == true);

    private void ChkSelectAll_Click(object s, RoutedEventArgs e)
    {
        bool on = ChkSelectAll.IsChecked == true;
        foreach (var m in _modules) m.Check!.IsChecked = on;
    }

    private void ScanMode_Checked(object s, RoutedEventArgs e)
    {
        if (ModuleGrid == null) return;
        bool ai = ModeAi.IsChecked == true;
        ApplyScanMode(ai);
        SetSetting("scanMode", ai ? "AI" : "Manual");
    }

    private void ApplyScanMode(bool ai)
    {
        foreach (var m in _modules)
        {
            if (ai) m.Check!.IsChecked = m.AiDefault;
            m.Check!.IsEnabled = !ai;
        }
        ChkSelectAll.IsEnabled = !ai;
        SyncSelectAll();
    }

    private void RefreshLastScan()
    {
        var last = _db.All<ScanRecord>().OrderByDescending(r => r.Timestamp).FirstOrDefault();
        LastScanText.Text = last == null
            ? Loc.T("no_scans")
            : Loc.T("last_scan", Loc.Date(DateTimeOffset.FromUnixTimeMilliseconds(last.Timestamp).LocalDateTime), last.TotalJunkCleanedFormatted);
    }

    private void ShowCareView(FrameworkElement view)
    {
        CareIdle.Visibility = CareProgress.Visibility = CareResults.Visibility = CareSuccess.Visibility = Visibility.Collapsed;
        view.Visibility = Visibility.Visible;
    }

    private async void BtnScan_Click(object s, RoutedEventArgs e)
    {
        var selected = _modules.Where(m => m.Check!.IsChecked == true).ToList();
        if (selected.Count == 0) { ShowToast(Loc.T("nothing_selected"), Loc.T("choose_module")); return; }

        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        _paused = false; BtnPause.SetResourceReference(ContentControl.ContentProperty, "S.pause");
        _junkFiles = new(); _browserFiles = new();
        _privacy = null; _brokenLinks = new(); _recycle = (0, 0); _security = null;
        _scanStart = DateTime.Now;
        ScanLog.Children.Clear();
        SetProgress(0);
        ShowCareView(CareProgress);

        try
        {
            for (int i = 0; i < selected.Count; i++)
            {
                var m = selected[i];
                ProgressModule.Text = Loc.T("scanning_x", m.Name);
                ProgressPath.Text = m.Description;
                await RunModuleAsync(m, ct);
                await WaitWhilePaused(ct);
                SetProgress((i + 1) * 100.0 / selected.Count);
                AddLog(m);
            }
        }
        catch (OperationCanceledException)
        {
            ShowCareView(CareIdle);
            ShowToast(Loc.T("scan_stopped"), Loc.T("scan_stopped_d"));
            return;
        }
        ShowResults(selected);
    }

    private async Task WaitWhilePaused(CancellationToken ct)
    {
        while (_paused) await Task.Delay(150, ct);
        ct.ThrowIfCancellationRequested();
    }

    private async Task RunModuleAsync(CareModule m, CancellationToken ct)
    {
        m.Issues = 0; m.Bytes = 0; m.DetailFn = () => "";
        switch (m.Id)
        {
            case "junk":
                _junkFiles = await Task.Run(() => FileScanner.FindJunk(ct), ct);
                m.Issues = _junkFiles.Count;
                m.Bytes = _junkFiles.Sum(f => SafeLen(f));
                int jn = m.Issues; m.DetailFn = () => Loc.T("d.junk", jn);
                break;
            case "browser":
                _browserFiles = await Task.Run(() => FindBrowserCache(ct), ct);
                m.Issues = _browserFiles.Count;
                m.Bytes = _browserFiles.Sum(f => SafeLen(f));
                int bn = m.Issues; m.DetailFn = () => Loc.T("d.browser", bn);
                break;
            case "startup":
                var entries = await Task.Run(StartupManager.GetEntries, ct);
                int enabled = entries.Count(x => x.Enabled);
                int folder = entries.Count(x => x.ApprovedSubKey == "StartupFolder");
                m.Issues = 0;
                m.DetailFn = () => Loc.T("d.startup", enabled, folder);
                break;
            case "ram":
                var sm = SystemMetrics.Instance; sm.Sample(force: true);
                m.Issues = 0;
                double ru = sm.UsedRamGb, rt = sm.TotalRamGb, rp = sm.RamPercent; m.DetailFn = () => Loc.T("d.ram", ru, rt, rp);
                break;
            case "diskerr":
                var bad = DriveInfo.GetDrives().Where(d => LocalDisks.IsReal(d) && d.TotalFreeSpace < d.TotalSize * 0.1).ToList();
                m.Issues = bad.Count;
                string badNames = string.Join(", ", bad.Select(d => d.Name.TrimEnd('\\'))); m.DetailFn = () => badNames.Length == 0 ? Loc.T("d.disk_ok") : Loc.T("d.disk_low", badNames);
                break;
            case "updates":
                if (_wingetOk ??= await Task.Run(WingetService.IsAvailable))
                {
                    bool fresh = WingetService.LastChecked is DateTime lc && DateTime.Now - lc < TimeSpan.FromHours(6) && WingetService.LastResult != null;
                    var pk = fresh ? WingetService.LastResult! : await WingetService.CheckAsync(ct);
                    int upCount = pk.Count;
                    m.Issues = upCount;
                    m.DetailFn = () => upCount == 0 ? Loc.T("all_up_to_date") : Loc.T("d.updates", upCount);
                }
                else m.DetailFn = () => Loc.T("winget_missing_short");
                break;
            case "recycle":
                _recycle = await Task.Run(RecycleBin.Query, ct);
                m.Issues = (int)Math.Min(int.MaxValue, _recycle.items);
                m.Bytes = _recycle.bytes;
                long rbItems = _recycle.items; m.DetailFn = () => rbItems == 0 ? Loc.T("d.recycle_empty") : Loc.T("d.recycle", rbItems);
                break;
            case "privacy":
                var pr = await Task.Run(PrivacyTraces.Scan, ct);
                _privacy = pr;
                m.Issues = pr.Total;
                m.DetailFn = () => pr.Total == 0 ? Loc.T("d.none") : Loc.T("d.privacy", pr.RecentFiles.Count, pr.RunHistory, pr.TypedPaths, pr.RecentDocs);
                break;
            case "shortcut":
                _brokenLinks = await Task.Run(() => BrokenShortcuts.Find(ct), ct);
                int bl = _brokenLinks.Count;
                m.Issues = bl;
                m.DetailFn = () => bl == 0 ? Loc.T("d.shortcut_ok") : Loc.T("d.shortcut", bl);
                break;
            case "security":
            case "spyware":
                _security ??= await SecurityCheck.RunAsync(ct);
                var sec = _security;
                if (m.Id == "security")
                {
                    var problems = SecurityProblems(sec);
                    m.Issues = sec.Failed ? 0 : problems.Count;
                    m.DetailFn = () => sec.Failed ? Loc.T("d.sec_failed")
                        : SecurityProblems(sec) is { Count: > 0 } p ? string.Join(" · ", p)
                        : Loc.T("d.sec_ok", sec.ActiveAv?.Name ?? "Windows Defender");
                }
                else
                {
                    m.Issues = sec.DefenderIsActive ? sec.ActiveThreats : 0;
                    int threats = m.Issues;
                    m.DetailFn = () => sec.Failed ? Loc.T("d.sec_failed")
                        : !sec.DefenderIsActive && sec.ActiveAv != null ? Loc.T("d.threats_other", sec.ActiveAv.Name)
                        : threats == 0 ? Loc.T("d.threats_none") : Loc.T("d.threats", threats);
                }
                break;
            default:
                m.Issues = 0;
                m.DetailFn = () => Loc.T("d.unavailable");
                break;
        }
    }

    /// <summary>Human-readable problems in the security report (re-evaluated so it follows the language).</summary>
    private static List<string> SecurityProblems(SecurityReport r)
    {
        var list = new List<string>();
        if (r.Failed) return list;
        var av = r.ActiveAv;
        if (av == null) list.Add(Loc.T("d.sec_noav"));
        else if (!av.UpToDate && !av.IsDefender) list.Add(Loc.T("d.sec_avold", av.Name));
        if (!r.FirewallOk) list.Add(Loc.T("d.sec_fw", string.Join(", ", r.WindowsFirewallOff)));
        if (r.DefenderIsActive)
        {
            if (!r.DefenderRealtime) list.Add(Loc.T("d.sec_rtp"));
            if (r.DefenderSignatureAgeDays > 3) list.Add(Loc.T("d.sec_sig", r.DefenderSignatureAgeDays));
            if (r.DefenderQuickScanAgeDays < 0) list.Add(Loc.T("d.sec_noscan"));
            else if (r.DefenderQuickScanAgeDays > 14) list.Add(Loc.T("d.sec_scan", r.DefenderQuickScanAgeDays));
        }
        return list;
    }

    private static long SafeLen(FileInfo f) { try { return f.Length; } catch { return 0; } }

    private static List<FileInfo> FindBrowserCache(CancellationToken ct)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dirs = new List<string>();
        foreach (var vendor in new[] { @"Google\Chrome", @"Microsoft\Edge", @"BraveSoftware\Brave-Browser" })
        {
            string ud = Path.Combine(local, vendor, "User Data");
            if (!Directory.Exists(ud)) continue;
            foreach (var profile in SafeDirs(ud))
                foreach (var c in new[] { "Cache", "Code Cache", "GPUCache" })
                    dirs.Add(Path.Combine(profile, c));
        }
        string ff = Path.Combine(local, @"Mozilla\Firefox\Profiles");
        foreach (var p in SafeDirs(ff)) dirs.Add(Path.Combine(p, "cache2"));

        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var list = new List<FileInfo>();
        foreach (var d in dirs.Where(Directory.Exists))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(d, "*", opts))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var fi = new FileInfo(f);
                        if (fi.LastWriteTime < DateTime.Now.AddHours(-24)) list.Add(fi);
                    }
                    catch { }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        return list;
    }

    private static IEnumerable<string> SafeDirs(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private void AddLog(CareModule m)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(new TextBlock { Text = (m.Issues > 0 ? "" : ""), FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = m.Issues > 0 ? Res("AmberBrush") : Res("GreenBrush"), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = m.Name, Margin = new Thickness(26, 0, 0, 0), FontSize = 13 });
        row.Children.Add(new TextBlock { Text = m.Issues > 0 ? Loc.T("log_issues", m.Issues) + (m.Bytes > 0 ? " · " + SystemMetrics.FormatBytes(m.Bytes) : "") : Loc.T("ok"), HorizontalAlignment = HorizontalAlignment.Right, Foreground = Res("MutedBrush"), FontSize = 12.5 });
        ScanLog.Children.Add(row);
        ScanLogScroll.ScrollToEnd();
    }

    private void SetProgress(double pct)
    {
        ProgressPct.Text = $"{pct:0}%";
        const double r = 103, c = 110; // radius, centre (220x220 box, stroke 14)
        if (pct >= 99.99)
        {
            ProgressArc.Data = new EllipseGeometry(new Point(c, c), r, r);
            return;
        }
        double angle = pct / 100 * 360;
        double rad = (angle - 90) * Math.PI / 180;
        var start = new Point(c, c - r);
        var end = new Point(c + r * Math.Cos(rad), c + r * Math.Sin(rad));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, angle > 180, SweepDirection.Clockwise, true));
        ProgressArc.Data = new PathGeometry(new[] { fig });
    }

    private void BtnPause_Click(object s, RoutedEventArgs e)
    {
        _paused = !_paused;
        BtnPause.SetResourceReference(ContentControl.ContentProperty, _paused ? "S.resume" : "S.pause");
        if (_paused) ProgressModule.Text = Loc.T("paused");
    }

    private void BtnStop_Click(object s, RoutedEventArgs e) { _paused = false; _scanCts?.Cancel(); }

    private List<CareModule> _lastScanned = new();

    private double _lastScanSeconds;

    private void ShowResults(List<CareModule> scanned, bool keepTime = false)
    {
        _lastScanned = scanned;
        if (!keepTime) _lastScanSeconds = (DateTime.Now - _scanStart).TotalSeconds;
        var actionable = scanned.Where(m => m.Real).ToList();
        long bytes = actionable.Sum(m => m.Bytes);
        int issues = actionable.Sum(m => m.Issues);
        ResultsJunk.Text = SystemMetrics.FormatBytes(bytes);
        ResultsIssues.Text = Loc.T("issues_found", issues);
        ResultsSubtitle.Text = Loc.T("modules_scanned", scanned.Count, _lastScanSeconds);

        ResultsList.Children.Clear();
        foreach (var m in scanned.OrderByDescending(m => m.Issues > 0).ThenByDescending(m => m.Bytes))
        {
            var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new TextBlock { Text = m.Glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16, Foreground = m.Issues > 0 ? Res("AmberBrush") : Res("GreenBrush"), VerticalAlignment = VerticalAlignment.Center };
            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = m.Name + (m.Real ? "" : Loc.T("analysis_only")), FontWeight = FontWeights.SemiBold, FontSize = 13 });
            info.Children.Add(new TextBlock { Text = m.Detail, Style = (Style)FindResource("Muted"), FontSize = 12 });
            var right = new TextBlock { Text = m.Bytes > 0 ? SystemMetrics.FormatBytes(m.Bytes) : (m.Issues > 0 ? $"{m.Issues}" : Loc.T("ok")), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = m.Issues > 0 ? Res("AmberBrush") : Res("GreenBrush") };
            Grid.SetColumn(info, 1); Grid.SetColumn(right, 2);
            row.Children.Add(icon); row.Children.Add(info); row.Children.Add(right);
            ResultsList.Children.Add(row);
            ResultsList.Children.Add(new Border { Height = 1, Background = Res("BorderBrush") });
        }
        BtnFix.IsEnabled = true;
        ShowCareView(CareResults);
    }

    private async void BtnFix_Click(object s, RoutedEventArgs e)
    {
        BtnFix.IsEnabled = false;
        BtnFix.Content = Loc.T("fixing");
        var scanned = _lastScanned.Where(m => m.Real).ToList();
        long freedDisk = 0, freedRamMb = 0;
        var notes = new List<string>();
        bool Has(string id) => scanned.Any(m => m.Id == id && m.Issues > 0);

        // Emptying the Recycle Bin cannot be undone: ask first
        bool emptyBin = Has("recycle");
        if (emptyBin && Msg(Loc.T("confirm_empty_bin", _recycle.items, SystemMetrics.FormatBytes(_recycle.bytes)), "Axiora SystemCare",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            emptyBin = false;

        if (scanned.Any(m => m.Id == "junk")) freedDisk += await Task.Run(() => FileScanner.DeleteJunk(_junkFiles));
        if (scanned.Any(m => m.Id == "browser")) freedDisk += await Task.Run(() => FileScanner.DeleteJunk(_browserFiles));
        if (scanned.Any(m => m.Id == "ram")) freedRamMb = await Task.Run(WindowsNativeApi.TrimOwnWorkingSet);

        if (emptyBin)
        {
            var before = _recycle;
            if (await Task.Run(RecycleBin.Empty)) { freedDisk += before.bytes; notes.Add(Loc.T("fx_recycle", SystemMetrics.FormatBytes(before.bytes))); }
        }
        if (Has("privacy") && _privacy != null)
        {
            var pr = _privacy;
            int n = await Task.Run(() => PrivacyTraces.Clean(pr));
            notes.Add(Loc.T("fx_privacy", n));
        }
        if (Has("shortcut") && _brokenLinks.Count > 0)
        {
            var links = _brokenLinks;
            var (moved, skipped) = await Task.Run(() => BrokenShortcuts.Recycle(links));
            notes.Add(Loc.T("fx_shortcut", moved) + (skipped > 0 ? Loc.T("fx_shortcut_admin", skipped) : ""));
        }
        if ((Has("security") || Has("spyware")) && _security is { DefenderIsActive: true })
            notes.Add(SecurityCheck.StartDefenderQuickScan() ? Loc.T("fx_defender") : Loc.T("fx_defender_fail"));
        if (Has("security") && _security != null && SecurityProblems(_security).Count > 0 && !_security.DefenderIsActive)
            notes.Add(Loc.T("fx_security_manual"));

        var record = new ScanRecord
        {
            Mode = ModeAi.IsChecked == true ? "AI" : "Manual",
            TotalJunkCleanedBytes = freedDisk,
            TotalJunkCleanedFormatted = SystemMetrics.FormatBytes(freedDisk),
            StartupItemsOptimized = 0,
            PrivacyTracesCount = _privacy?.Total ?? 0,
            SecurityIssuesCount = _security == null ? 0 : SecurityProblems(_security).Count,
            ModulesScanned = scanned.Select(m => m.Id).ToList(),
            DurationMs = (int)(DateTime.Now - _scanStart).TotalMilliseconds,
        };
        _db.Write(() => _db.Add(record));

        BtnFix.SetResourceReference(ContentControl.ContentProperty, "S.fix");
        SuccessDetail.Text = Loc.T("success_disk", SystemMetrics.FormatBytes(freedDisk)) +
                             (freedRamMb > 0 ? Loc.T("success_ram", freedRamMb) : "") +
                             Loc.T("success_tail") +
                             (notes.Count > 0 ? "\n\n• " + string.Join("\n• ", notes) : "");
        RefreshLastScan();
        ShowCareView(CareSuccess);
    }

    private void BtnBackToIdle_Click(object s, RoutedEventArgs e) => ShowCareView(CareIdle);

    // ================================================================== CUSTOM CLEAN
    private void InitCleanLocations()
    {
        CleanLocation.Items.Clear();
        CleanLocation.Items.Add(new ComboBoxItem { Content = Loc.T("my_folders"), Tag = "user" });
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? Loc.T("local_disk") : d.VolumeLabel;
            CleanLocation.Items.Add(new ComboBoxItem { Content = $"{label} ({d.Name.TrimEnd('\\')})", Tag = d.RootDirectory.FullName });
        }
        CleanLocation.SelectedIndex = 0;
    }

    private void BtnBrowseFolder_Click(object s, RoutedEventArgs e)
    {
        using var dlg = new WinForms.FolderBrowserDialog { Description = Loc.T("choose_folder"), UseDescriptionForTitle = true };
        if (dlg.ShowDialog() != WinForms.DialogResult.OK) return;
        var item = new ComboBoxItem { Content = dlg.SelectedPath, Tag = dlg.SelectedPath };
        CleanLocation.Items.Add(item);
        CleanLocation.SelectedItem = item;
    }

    private IEnumerable<string> CleanRoots()
    {
        string tag = (string)((ComboBoxItem)CleanLocation.SelectedItem).Tag;
        if (tag != "user") return new[] { tag };
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(home, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private async void BtnCleanScan_Click(object s, RoutedEventArgs e)
    {
        _cleanCts = new CancellationTokenSource();
        var ct = _cleanCts.Token;
        var roots = CleanRoots().ToList();
        bool large = CleanLargeMode.IsChecked == true;
        var method = (MatchMethod)CleanMatch.SelectedIndex;
        var progress = new Progress<string>(t => CleanBusyText.Text = t);

        BtnCleanScan.IsEnabled = false;
        DupTree.Visibility = Visibility.Collapsed;
        CleanBusy.Visibility = Visibility.Visible;
        CleanBusyText.Text = Loc.T("indexing");
        try
        {
            _groups = await Task.Run(() => large
                ? FileScanner.FindLargeFiles(roots, progress, ct)
                : FileScanner.FindDuplicates(roots, method, progress, ct), ct);
            ShowToast(Loc.T("scan_complete"), _groups.Count == 0 ? Loc.T("clean_none") : Loc.T("clean_found", _groups.Sum(g => g.Files.Count), _groups.Count, SystemMetrics.FormatBytes(SelectedBytes(all: true))));
        }
        catch (OperationCanceledException) { ShowToast(Loc.T("scan_cancelled"), ""); }
        finally
        {
            BtnCleanScan.IsEnabled = true;
            CleanBusy.Visibility = Visibility.Collapsed;
            DupTree.Visibility = Visibility.Visible;
            RenderDupTree();
        }
    }

    private void BtnCleanCancel_Click(object s, RoutedEventArgs e) => _cleanCts?.Cancel();

    private long SelectedBytes(bool all = false) =>
        _groups.SelectMany(g => g.Files).Where(f => all ? !f.IsOriginal : f.IsSelected).Sum(f => f.Size);

    private void RenderDupTree()
    {
        DupTree.Children.Clear();
        if (_groups.Count == 0)
        {
            DupTree.Children.Add(new TextBlock
            {
                Text = Loc.T("clean_empty"),
                Style = (Style)FindResource("Muted"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 60, 0, 0)
            });
            UpdateCleanSelection();
            return;
        }

        foreach (var g in _groups)
        {
            var header = new Grid { Margin = new Thickness(0, 6, 0, 2), Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent };
            var chevron = new TextBlock { Text = g.IsCollapsed ? "" : "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11, Foreground = Res("MutedBrush"), VerticalAlignment = VerticalAlignment.Center };
            var groupCheck = new CheckBox
            {
                Margin = new Thickness(22, 0, 0, 0),
                IsChecked = g.Files.Where(f => !f.IsOriginal).All(f => f.IsSelected) && g.Files.Any(f => f.IsSelected) ? true : g.Files.Any(f => f.IsSelected) ? null : false,
                Content = new TextBlock { Text = GroupTitle(g), FontWeight = FontWeights.SemiBold, FontSize = 13 }
            };
            groupCheck.Click += (_, _) =>
            {
                bool on = groupCheck.IsChecked == true;
                foreach (var f in g.Files) f.IsSelected = on && !f.IsOriginal;
                if (on && g.Files.All(f => f.IsOriginal)) foreach (var f in g.Files) f.IsSelected = true;
                RenderDupTree();
            };
            var size = new TextBlock { Text = SystemMetrics.FormatBytes(g.TotalSize), Foreground = Res("MutedBrush"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            header.Children.Add(chevron); header.Children.Add(groupCheck); header.Children.Add(size);
            header.MouseLeftButtonUp += (_, ev) => { if (ev.OriginalSource is not CheckBox && !IsInside<CheckBox>(ev.OriginalSource)) { g.IsCollapsed = !g.IsCollapsed; RenderDupTree(); } };
            DupTree.Children.Add(header);

            if (!g.IsCollapsed)
            {
                foreach (var f in g.Files)
                {
                    var row = new Grid { Margin = new Thickness(44, 3, 0, 3) };
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var name = new StackPanel { Orientation = Orientation.Horizontal };
                    name.Children.Add(new TextBlock { Text = Path.GetFileName(f.Path), FontSize = 12.5 });
                    if (f.IsOriginal)
                        name.Children.Add(new Border { Background = Res("GreenSoftBrush"), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(8, 0, 0, 0), Child = new TextBlock { Text = Loc.T("original"), FontSize = 10.5, Foreground = Res("GreenBrush") } });
                    var stack = new StackPanel();
                    stack.Children.Add(name);
                    stack.Children.Add(new TextBlock { Text = f.Path, FlowDirection = FlowDirection.LeftToRight, TextAlignment = Loc.IsArabic ? TextAlignment.Right : TextAlignment.Left, FontSize = 11, Foreground = Res("DimBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
                    var fc = new CheckBox { IsChecked = f.IsSelected, Content = stack };
                    fc.Click += (_, _) => { f.IsSelected = fc.IsChecked == true; UpdateCleanSelection(); };
                    var sz = new TextBlock { Text = SystemMetrics.FormatBytes(f.Size), Foreground = Res("MutedBrush"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                    Grid.SetColumn(sz, 1);
                    row.Children.Add(fc); row.Children.Add(sz);
                    row.MouseRightButtonUp += (_, _) => { try { Process.Start("explorer.exe", $"/select,\"{f.Path}\""); } catch { } };
                    row.ToolTip = Loc.T("show_in_explorer");
                    DupTree.Children.Add(row);
                }
            }
            DupTree.Children.Add(new Border { Height = 1, Background = Res("BorderBrush"), Margin = new Thickness(0, 6, 0, 0) });
        }
        UpdateCleanSelection();
    }

    private static string GroupTitle(FileGroup g) =>
        g.IsLarge ? Loc.T("large_title", g.Files.Count)
        : g.Verified ? Loc.T("group_title", g.Index, g.Files.Count)
        : Loc.T("group_title_name", g.Index, g.Files.Count);

    private static bool IsInside<T>(object src) where T : DependencyObject
    {
        var d = src as DependencyObject;
        while (d != null) { if (d is T) return true; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d); }
        return false;
    }

    private void UpdateCleanSelection()
    {
        var sel = _groups.SelectMany(g => g.Files).Where(f => f.IsSelected).ToList();
        CleanSelectedText.Text = Loc.T("selected", sel.Count, SystemMetrics.FormatBytes(sel.Sum(f => f.Size)));
    }

    private void BtnRecycle_Click(object s, RoutedEventArgs e) => DeleteSelected(permanent: false);
    private void BtnDeletePermanent_Click(object s, RoutedEventArgs e) => DeleteSelected(permanent: true);

    private void DeleteSelected(bool permanent)
    {
        var sel = _groups.SelectMany(g => g.Files).Where(f => f.IsSelected).ToList();
        if (sel.Count == 0) { ShowToast(Loc.T("nothing_selected"), Loc.T("tick_files")); return; }

        // Never delete the last remaining copy of a duplicate group
        foreach (var g in _groups.Where(g => !g.IsLarge))
            if (g.Files.All(f => f.IsSelected))
            {
                Msg(Loc.T("keep_one", GroupTitle(g)), "Axiora SystemCare", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

        // Name + size matches were never compared byte for byte: only allow the reversible path
        if (permanent && _groups.Any(g => !g.IsLarge && !g.Verified && g.Files.Any(f => f.IsSelected)))
        {
            Msg(Loc.T("unverified_no_perm"), "Axiora SystemCare", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (Msg(Loc.T(permanent ? "confirm_delete" : "confirm_recycle", sel.Count, SystemMetrics.FormatBytes(sel.Sum(f => f.Size))),
                "Axiora SystemCare", MessageBoxButton.YesNo, permanent ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        int ok = 0, skipped = 0; long freed = 0;
        foreach (var f in sel)
        {
            try
            {
                if (!File.Exists(f.Path) || !WindowsNativeApi.IsSafeFileToDelete(f.Path)) { skipped++; continue; }
                if (permanent) File.Delete(f.Path);
                else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(f.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                freed += f.Size; ok++;
                foreach (var g in _groups) g.Files.Remove(f);
            }
            catch { skipped++; }
        }
        _groups.RemoveAll(g => g.Files.Count < (g.IsLarge ? 1 : 2));
        RenderDupTree();
        ShowToast(Loc.T(permanent ? "files_deleted" : "moved_recycle"), Loc.T("delete_result", ok, SystemMetrics.FormatBytes(freed)) + (skipped > 0 ? Loc.T("delete_skipped", skipped) : ""));
    }

    // ================================================================== SPEED UP
    private void Turbo_Changed(object s, RoutedEventArgs e) => ApplyTurbo(save: true);

    private void ApplyTurbo(bool save)
    {
        bool on = TurboToggle.IsChecked == true;
        bool game = TurboGame.IsChecked == true;
        TurboWork.IsEnabled = TurboGame.IsEnabled = on;
        TurboDesc.Text = !on
            ? Loc.T("turbo_off")
            : game ? Loc.T("turbo_game") : Loc.T("turbo_work");
        if (on) WindowsNativeApi.SetTurboBoostMode(game);
        else
        {
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Normal; } catch { }
        }
        if (save)
        {
            SetSetting("turboBoostActive", on ? "True" : "False");
            SetSetting("turboBoostMode", game ? "Game" : "Work");
        }
    }

    private async void BtnCleanRam_Click(object s, RoutedEventArgs e)
    {
        BtnCleanRam.IsEnabled = false;
        BtnCleanRam.Content = Loc.T("cleaning");
        long mb = await Task.Run(WindowsNativeApi.TrimOwnWorkingSet);
        BtnCleanRam.SetResourceReference(ContentControl.ContentProperty, "S.clean_ram");
        BtnCleanRam.IsEnabled = true;
        RamCleanResult.Text = Loc.T("freed_now", mb);
        UpdateMetrics();
        ShowToast(Loc.T("mem_cleaned"), Loc.T("mem_freed", mb));
    }

    private void BtnStartupRefresh_Click(object s, RoutedEventArgs e) => RefreshStartup();

    private void RefreshStartup()
    {
        _startup = StartupManager.GetEntries();
        StartupSummary.Text = Loc.T("startup_summary", _startup.Count(x => x.Enabled), _startup.Count);
        StartupList.Children.Clear();
        foreach (var item in _startup)
        {
            var row = new Grid { Margin = new Thickness(16, 10, 16, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold, FontSize = 13 });
            info.Children.Add(new TextBlock { Text = item.Command, FlowDirection = FlowDirection.LeftToRight, TextAlignment = Loc.IsArabic ? TextAlignment.Right : TextAlignment.Left, FontSize = 11, Foreground = Res("DimBrush"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = item.Command });
            var scope = new TextBlock { Text = Loc.D(item.Scope), Style = (Style)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center };
            var impactColor = item.Impact switch { "High" => "RedBrush", "Low" => "GreenBrush", _ => "AmberBrush" };
            var impact = new TextBlock { Text = Loc.D(item.Impact), Foreground = Res(impactColor), FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var toggle = new CheckBox { Style = (Style)FindResource("ToggleSwitch"), IsChecked = item.Enabled, VerticalAlignment = VerticalAlignment.Center };
            toggle.Click += (_, _) =>
            {
                bool want = toggle.IsChecked == true;
                try
                {
                    StartupManager.SetEnabled(item, want);
                    ShowToast(Loc.T(want ? "startup_enabled" : "startup_disabled"), item.Name);
                    StartupSummary.Text = Loc.T("startup_summary", _startup.Count(x => x.Enabled), _startup.Count);
                }
                catch (Exception ex)
                {
                    toggle.IsChecked = !want;
                    ShowToast(Loc.T("startup_fail"), ex is UnauthorizedAccessException ? Loc.T("need_admin") : ex.Message);
                }
            };
            Grid.SetColumn(scope, 1); Grid.SetColumn(impact, 2); Grid.SetColumn(toggle, 3);
            row.Children.Add(info); row.Children.Add(scope); row.Children.Add(impact); row.Children.Add(toggle);
            StartupList.Children.Add(row);
            StartupList.Children.Add(new Border { Height = 1, Background = Res("BorderBrush") });
        }
        if (_startup.Count == 0)
            StartupList.Children.Add(new TextBlock { Text = Loc.T("no_startup"), Style = (Style)FindResource("Muted"), Margin = new Thickness(16) });
    }

    // ================================================================== PROTECT
    private void RenderShields()
    {
        var shields = _db.All<ProtectionShield>().OrderBy(x => x.Id).ToList();
        ProtectSummary.Text = Loc.T("protect_summary", shields.Count(x => x.IsEnabled), shields.Count);
        ShieldList.Children.Clear();
        foreach (var cat in shields.GroupBy(x => x.Category))
        {
            ShieldList.Children.Add(new TextBlock { Text = Loc.D(cat.Key).ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Res("DimBrush"), Margin = new Thickness(0, 8, 0, 8) });
            var grid = new UniformGrid { Columns = 2 };
            foreach (var sh in cat)
            {
                var g = new Grid();
                var info = new StackPanel { Margin = new Thickness(0, 0, 56, 0) };
                info.Children.Add(new TextBlock { Text = Loc.D(sh.Name), FontWeight = FontWeights.SemiBold, FontSize = 13.5 });
                info.Children.Add(new TextBlock { Text = Loc.T("shield_pref"), Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 3, 0, 0) });
                var t = new CheckBox { Style = (Style)FindResource("ToggleSwitch"), IsChecked = sh.IsEnabled, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
                var captured = sh;
                t.Click += (_, _) =>
                {
                    _db.Write(() => captured.IsEnabled = t.IsChecked == true);
                    RenderShields();
                };
                g.Children.Add(info); g.Children.Add(t);
                grid.Children.Add(new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 12, 12), Child = g });
            }
            ShieldList.Children.Add(grid);
        }
    }

    private async void BtnShred_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("shred_dialog"), CheckFileExists = true };
        if (dlg.ShowDialog(this) != true) return;
        string path = dlg.FileName;
        if (!WindowsNativeApi.IsSafeFileToDelete(path)) { ShowToast(Loc.T("refused"), Loc.T("is_symlink")); return; }
        if (Msg(Loc.T("shred_confirm", path), Loc.T("shredder"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        BtnShred.IsEnabled = false;
        try
        {
            await WindowsNativeApi.ShredFileAsync(path, new Progress<string>(t => ShredStatus.Text = t));
            ShowToast(Loc.T("shredded"), Path.GetFileName(path));
        }
        catch (Exception ex) { ShredStatus.Text = Loc.T("failed", ex.Message); }
        finally { BtnShred.IsEnabled = true; }
    }

    // ================================================================== UPDATER (winget)
    private CancellationTokenSource? _updCts;
    private bool _updBusy;
    private string _updMode = "";          // "check" | "one" | "all"
    private bool _updStopRequested;
    private bool? _wingetOk;
    private readonly Dictionary<string, (bool ok, string text)> _updOutcome = new();
    private readonly List<WingetPackage> _updatedThisSession = new();

    private bool _wingetProbing;

    private async void EnsureWingetProbed()
    {
        if (_wingetOk != null || _wingetProbing) return;
        _wingetProbing = true;
        _wingetOk = await Task.Run(WingetService.IsAvailable);
        _wingetProbing = false;
        RenderUpdates();
        if (_wingetOk == true && WingetService.LastResult == null && UpdaterPanel.Visibility == Visibility.Visible) _ = CheckUpdatesAsync();
    }

    private void RenderUpdates() => RenderUpdates(keepScroll: true);

    private void RenderUpdates(bool keepScroll)
    {
        if (keepScroll)
        {
            double offset = UpdaterPanel.VerticalOffset;
            Dispatcher.BeginInvoke(() => UpdaterPanel.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
        }
        UpdateList.Children.Clear();
        if (_wingetOk == null)
        {
            UpdaterSummary.Text = Loc.T("checking");
            UpdaterStatus.Text = "";
            BtnCheckUpdates.IsEnabled = BtnUpdateAll.IsEnabled = false;
            UpdaterProgress.Visibility = Visibility.Visible;
            EnsureWingetProbed();
            return;
        }
        BtnCheckUpdates.IsEnabled = _wingetOk == true && (!_updBusy || (_updMode != "one" && !_updStopRequested));
        BtnCheckUpdates.SetResourceReference(ContentControl.ContentProperty,
            !_updBusy || _updMode == "one" ? "S.check_updates" : _updMode == "all" ? "S.stop_after" : "S.cancel");
        UpdaterProgress.Visibility = _updBusy ? Visibility.Visible : Visibility.Collapsed;

        if (_wingetOk != true)
        {
            UpdaterSummary.Text = Loc.T("winget_missing");
            UpdaterStatus.Text = "";
            BtnUpdateAll.IsEnabled = false;
            return;
        }

        var list = WingetService.LastResult;
        if (!_updBusy)
            UpdaterStatus.Text = WingetService.LastChecked is DateTime t ? Loc.T("last_checked", Loc.Date(t)) : Loc.T("not_checked");

        if (list == null)
        {
            if (!_updBusy) UpdaterSummary.Text = Loc.T("not_checked");
            BtnUpdateAll.IsEnabled = false;
            return;
        }

        var normal = list.Where(x => !x.ExplicitOnly).ToList();
        if (!_updBusy)
            UpdaterSummary.Text = list.Count == 0 ? Loc.T("all_up_to_date") : Loc.T("updates_avail", list.Count);
        BtnUpdateAll.IsEnabled = !_updBusy && normal.Count > 0;

        foreach (var u in _updatedThisSession.Concat(list.OrderBy(x => x.ExplicitOnly).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)))
            AddUpdateRow(u);
        if (list.Count == 0 && _updatedThisSession.Count == 0)
            UpdateList.Children.Add(new TextBlock { Text = Loc.T("all_up_to_date"), Style = (Style)FindResource("Muted"), Margin = new Thickness(16) });
    }

    private void AddUpdateRow(WingetPackage u)
    {
        bool done = _updatedThisSession.Contains(u);
        var row = new Grid { Margin = new Thickness(16, 11, 16, 11) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });

        var info = new StackPanel();
        info.Children.Add(new TextBlock { Text = u.Name, FontWeight = FontWeights.SemiBold, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis, FlowDirection = FlowDirection.LeftToRight, TextAlignment = Loc.IsArabic ? TextAlignment.Right : TextAlignment.Left });
        string sub = u.Id + (u.ExplicitOnly ? "  ·  " + Loc.T("pinned") : u.VersionUnknown ? "  ·  " + Loc.T("ver_unknown") : "");
        info.Children.Add(new TextBlock { Text = sub, Style = (Style)FindResource("Muted"), FontSize = 11.5, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
        if (_updOutcome.TryGetValue(u.Id, out var oc) && !oc.ok)
            info.Children.Add(new TextBlock { Text = oc.text, Foreground = Res("RedBrush"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });

        var ver = new TextBlock
        {
            Text = done ? u.Available : $"{u.Version}  →  {u.Available}",
            FlowDirection = FlowDirection.LeftToRight, TextAlignment = Loc.IsArabic ? TextAlignment.Right : TextAlignment.Left,
            Foreground = done ? Res("GreenBrush") : Res("AmberBrush"), VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 8, 0)
        };

        FrameworkElement action;
        if (done)
            action = new TextBlock { Text = "  " + Loc.T("updated"), Foreground = Res("GreenBrush"), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        else
        {
            var b = new Button { Style = (Style)FindResource("PrimaryButton"), Content = Loc.T("update"), HorizontalAlignment = HorizontalAlignment.Right, IsEnabled = !_updBusy, MinWidth = 96 };
            b.Click += async (_, _) => await UpdateOneAsync(u);
            action = b;
        }
        if (action is TextBlock tb && done) { tb.Inlines.Clear(); tb.Inlines.Add(new System.Windows.Documents.Run("") { FontFamily = new FontFamily("Segoe MDL2 Assets") }); tb.Inlines.Add(new System.Windows.Documents.Run("  " + Loc.T("updated"))); }

        Grid.SetColumn(ver, 1); Grid.SetColumn(action, 2);
        row.Children.Add(info); row.Children.Add(ver); row.Children.Add(action);
        UpdateList.Children.Add(row);
        UpdateList.Children.Add(new Border { Height = 1, Background = Res("BorderBrush") });
    }

    private void SetUpdBusy(bool busy, string status = "")
    {
        _updBusy = busy;
        if (busy) UpdaterStatus.Text = status;
        RenderUpdates();
        if (busy) UpdaterStatus.Text = status;
    }

    private async void BtnCheckUpdates_Click(object s, RoutedEventArgs e)
    {
        if (!_updBusy) { await CheckUpdatesAsync(); return; }
        if (_updMode == "check") _updCts?.Cancel();          // safe: only reading
        else if (_updMode == "all") { _updStopRequested = true; RenderUpdates(); } // never kill an installer midway
    }

    private async Task CheckUpdatesAsync()
    {
        if (_updBusy) return;
        _updCts = new CancellationTokenSource();
        _updOutcome.Clear();
        _updatedThisSession.Clear();
        _updMode = "check";
        UpdaterSummary.Text = Loc.T("checking");
        SetUpdBusy(true, Loc.T("checking_d"));
        try
        {
            await WingetService.CheckAsync(_updCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowToast(Loc.T("check_failed"), ex.Message); }
        finally { SetUpdBusy(false); }
    }

    private async Task UpdateOneAsync(WingetPackage u)
    {
        if (_updBusy) return;
        _updCts = new CancellationTokenSource();
        _updMode = "one";
        UpdaterSummary.Text = Loc.T("updating_x", u.Name);
        SetUpdBusy(true, Loc.T("updating"));
        try
        {
            var r = await WingetService.UpgradeAsync(u, _updCts.Token, new Progress<string>(t => UpdaterStatus.Text = t));
            RecordOutcome(u, r);
            ShowToast(r.Success ? Loc.T("updated") : Loc.T("upd_failed"), r.Success ? Loc.T("updated_to", u.Name, u.Version, u.Available) : $"{u.Name}: {r.Message}");
        }
        catch (OperationCanceledException) { }
        finally { SetUpdBusy(false); }
    }

    private void RecordOutcome(WingetPackage u, WingetResult r)
    {
        _updOutcome[u.Id] = (r.Success, r.Message);
        if (r.Success && !_updatedThisSession.Contains(u)) _updatedThisSession.Add(u);
    }

    private async void BtnUpdateAll_Click(object s, RoutedEventArgs e)
    {
        var todo = WingetService.LastResult?.Where(x => !x.ExplicitOnly).ToList() ?? new();
        if (todo.Count == 0 || _updBusy) return;
        if (Msg(Loc.T("confirm_update_all", todo.Count), "Axiora SystemCare", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _updCts = new CancellationTokenSource();
        _updMode = "all";
        _updStopRequested = false;
        int ok = 0, failed = 0;
        try
        {
            for (int i = 0; i < todo.Count; i++)
            {
                if (_updStopRequested) break;
                var u = todo[i];
                UpdaterSummary.Text = Loc.T("updating_n", i + 1, todo.Count, u.Name);
                SetUpdBusy(true, Loc.T("updating"));
                UpdaterSummary.Text = Loc.T("updating_n", i + 1, todo.Count, u.Name);
                var r = await WingetService.UpgradeAsync(u, _updCts.Token, new Progress<string>(t => UpdaterStatus.Text = t));
                RecordOutcome(u, r);
                if (r.Success) ok++; else failed++;
            }
        }
        catch (OperationCanceledException) { }
        finally { _updStopRequested = false; SetUpdBusy(false); }
        ShowToast(Loc.T("updates_done"), Loc.T("update_all_done", ok, failed));
    }

    // ================================================================== ACTION CENTER
    private void RenderActionCenter()
    {
        HistoryList.Children.Clear();
        var history = _db.All<ScanRecord>().OrderByDescending(r => r.Timestamp).Take(15).ToList();
        if (history.Count == 0) HistoryList.Children.Add(new TextBlock { Text = Loc.T("no_maint"), Style = (Style)FindResource("Muted") });
        foreach (var r in history)
            HistoryList.Children.Add(ListRow("", "GreenBrush",
                Loc.T("hist_title", Loc.T("mode_" + r.Mode), r.TotalJunkCleanedFormatted),
                Loc.T("hist_sub", Loc.Date(DateTimeOffset.FromUnixTimeMilliseconds(r.Timestamp).LocalDateTime), r.ModulesScanned.Count, r.DurationMs / 1000.0), null));
        DbJson.Text = _db.ExportJsonSnapshot();
    }

    private Grid ListRow(string glyph, string colorKey, string title, string sub, FrameworkElement? right)
    {
        var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = Res(colorKey), VerticalAlignment = VerticalAlignment.Center });
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
        sp.Children.Add(new TextBlock { Text = sub, Style = (Style)FindResource("Muted"), FontSize = 11.5 });
        Grid.SetColumn(sp, 1); row.Children.Add(sp);
        if (right != null) { Grid.SetColumn(right, 2); right.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(right); }
        return row;
    }

    private void BtnDbRefresh_Click(object s, RoutedEventArgs e) => RenderActionCenter();

    private void BtnDbFolder_Click(object s, RoutedEventArgs e)
    {
        string dir = RealmLocalDatabase.DataFolder;
        try { Process.Start("explorer.exe", dir); } catch { }
    }

    // ================================================================== SETTINGS
    private void RenderSettings()
    {
        var ver = typeof(MainWindow).Assembly.GetName().Version;
        AboutVersion.Text = Loc.T("version", ver == null ? "1.0" : $"{ver.Major}.{ver.Minor}.{ver.Build}");
        LangEn.IsChecked = !Loc.IsArabic;
        LangAr.IsChecked = Loc.IsArabic;

        ThemeList.Children.Clear();
        foreach (var t in ThemeManager.All)
        {
            bool active = t.Id == ThemeManager.Current.Id;
            var sw = ThemeManager.Swatches(t);
            var strip = new Grid { Height = 64 };
            for (int i = 0; i < sw.Length; i++)
            {
                strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(i == 2 ? 2 : 1, GridUnitType.Star) });
                var cell = new Border { Background = sw[i] };
                Grid.SetColumn(cell, i);
                strip.Children.Add(cell);
            }
            var clip = new Border { CornerRadius = new CornerRadius(8), Child = strip, ClipToBounds = true, Margin = new Thickness(0, 0, 0, 10) };
            clip.Clip = new RectangleGeometry(new Rect(0, 0, 196, 64), 8, 8);

            var title = new Grid();
            title.Children.Add(new TextBlock { Text = Loc.T(t.NameKey), FontWeight = FontWeights.SemiBold, FontSize = 13 });
            if (active)
                title.Children.Add(new TextBlock { Text = "\uE73E", FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = Res("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
            var tag = new TextBlock { Text = Loc.T(t.IsLight ? "theme_lighttag" : "theme_dark"), Style = (Style)FindResource("Muted"), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0) };

            var body = new StackPanel { Width = 196 };
            body.Children.Add(clip); body.Children.Add(title); body.Children.Add(tag);
            var card = new Border
            {
                Background = Res("CardBrush"), CornerRadius = new CornerRadius(12), Padding = new Thickness(12),
                BorderThickness = new Thickness(active ? 2 : 1), BorderBrush = active ? Res("AccentBrush") : Res("BorderBrush"),
                Margin = new Thickness(0, 0, 14, 14), Cursor = System.Windows.Input.Cursors.Hand, Child = body,
                ToolTip = Loc.T(t.NameKey)
            };
            var id = t.Id;
            card.MouseLeftButtonUp += (_, _) =>
            {
                if (id == ThemeManager.Current.Id) return;
                SetSetting("theme", id);
                ThemeManager.Apply(id);
                ShowToast(Loc.T("theme_applied"), Loc.T(ThemeManager.Current.NameKey));
            };
            ThemeList.Children.Add(card);
        }
    }

    private void LangRadio_Click(object s, RoutedEventArgs e)
    {
        string lang = LangAr.IsChecked == true ? "ar" : "en";
        if (lang == Loc.Lang) return;
        SetSetting("language", lang);
        Loc.SetLanguage(lang);
    }

    // ================================================================== helpers
    private DispatcherTimer? _toastTimer;

    public void ShowToast(string title, string body)
    {
        ToastTitle.Text = title;
        ToastBody.Text = body;
        ToastBody.Visibility = string.IsNullOrEmpty(body) ? Visibility.Collapsed : Visibility.Visible;
        Toast.Visibility = Visibility.Visible;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer!.Stop(); };
        _toastTimer.Start();
    }

    private MessageBoxResult Msg(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon) =>
        MessageBox.Show(this, text, caption, buttons, icon, MessageBoxResult.None,
            Loc.IsArabic ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None);

    private Brush Res(string key) => (Brush)FindResource(key);
    private static Brush Brush(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
}
