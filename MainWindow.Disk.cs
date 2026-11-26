using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SystemCare20.Services;
using WinForms = System.Windows.Forms;

namespace SystemCare20;

/// <summary>Disk Space page: where the space on a drive or folder goes.</summary>
public partial class MainWindow
{
    private DiskScanResult? _disk;
    private DiskNode? _diskCurrent;
    private CancellationTokenSource? _diskCts;
    private bool _diskBusy;
    private const int DiskRowLimit = 300;

    private void EnterDiskPage()
    {
        if (DiskTarget.Items.Count == 0) InitDiskTargets(keepSelection: false);
        RenderDisk();
    }

    private void InitDiskTargets(bool keepSelection)
    {
        string? selectedTag = keepSelection ? (DiskTarget.SelectedItem as ComboBoxItem)?.Tag as string : null;
        var custom = DiskTarget.Items.Cast<ComboBoxItem>().Where(i => i.Tag is string t && Path.GetPathRoot(t) != t).ToList();
        DiskTarget.Items.Clear();
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        foreach (var d in DriveInfo.GetDrives().Where(LocalDisks.IsReal))
        {
            string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? Loc.T("local_disk") : d.VolumeLabel;
            var item = new ComboBoxItem
            {
                Content = $"{label} ({d.Name.TrimEnd('\\')}) · {Loc.T("disk_free_short", SystemMetrics.FormatBytes(d.TotalFreeSpace))}",
                Tag = d.RootDirectory.FullName
            };
            DiskTarget.Items.Add(item);
            if (selectedTag == null && d.Name.Equals(systemRoot, StringComparison.OrdinalIgnoreCase)) DiskTarget.SelectedItem = item;
        }
        foreach (var c in custom) DiskTarget.Items.Add(new ComboBoxItem { Content = c.Content, Tag = c.Tag });
        if (selectedTag != null)
            DiskTarget.SelectedItem = DiskTarget.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selectedTag);
        if (DiskTarget.SelectedIndex < 0 && DiskTarget.Items.Count > 0) DiskTarget.SelectedIndex = 0;
    }

    private void BtnDiskBrowse_Click(object s, RoutedEventArgs e)
    {
        using var dlg = new WinForms.FolderBrowserDialog { Description = Loc.T("choose_folder"), UseDescriptionForTitle = true };
        if (dlg.ShowDialog() != WinForms.DialogResult.OK) return;
        var item = new ComboBoxItem { Content = dlg.SelectedPath, Tag = dlg.SelectedPath };
        DiskTarget.Items.Add(item);
        DiskTarget.SelectedItem = item;
    }

    private async void BtnDiskScan_Click(object s, RoutedEventArgs e)
    {
        if (_diskBusy || DiskTarget.SelectedItem is not ComboBoxItem { Tag: string root }) return;
        _diskCts = new CancellationTokenSource();
        var progress = new DiskAnalyzer.Progress();
        _diskBusy = true;
        RenderDisk();

        var ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        ticker.Tick += (_, _) =>
        {
            DiskBusyCount.Text = Loc.T("disk_scanning", Interlocked.Read(ref progress.Files), SystemMetrics.FormatBytes(Interlocked.Read(ref progress.Bytes)));
            DiskBusyPath.Text = progress.Current;
        };
        ticker.Start();
        try
        {
            _disk = await DiskAnalyzer.ScanAsync(root, progress, _diskCts.Token);
            _diskCurrent = _disk.Root;
        }
        catch (OperationCanceledException) { ShowToast(Loc.T("scan_cancelled"), ""); }
        catch (Exception ex) { ShowToast(Loc.T("failed", ex.Message), ""); }
        finally
        {
            ticker.Stop();
            _diskBusy = false;
            RenderDisk();
            DiskScroll.ScrollToTop();
        }
    }

    private void BtnDiskCancel_Click(object s, RoutedEventArgs e) => _diskCts?.Cancel();
    private void DiskView_Click(object s, RoutedEventArgs e)
    {
        if (DiskList == null || DiskScroll == null) return; // fires during InitializeComponent
        RenderDisk();
        DiskScroll.ScrollToTop();
    }

    private void BtnDiskUp_Click(object s, RoutedEventArgs e)
    {
        if (_diskCurrent?.Parent == null) return;
        _diskCurrent = _diskCurrent.Parent;
        RenderDisk();
        DiskScroll.ScrollToTop();
    }

    private void BtnDiskOpenHere_Click(object s, RoutedEventArgs e)
    {
        if (_diskCurrent != null) OpenInExplorer(_diskCurrent.Path, select: false);
    }

    private static void OpenInExplorer(string path, bool select)
    {
        try { Process.Start("explorer.exe", select ? $"/select,\"{path}\"" : $"\"{path}\""); } catch { }
    }

    // ------------------------------------------------------------------ rendering
    private void RenderDisk()
    {
        DiskBusy.Visibility = _diskBusy ? Visibility.Visible : Visibility.Collapsed;
        DiskScroll.Visibility = _diskBusy ? Visibility.Collapsed : Visibility.Visible;
        BtnDiskScan.IsEnabled = !_diskBusy;
        DiskList.Children.Clear();

        // drive usage summary (live)
        string targetRoot = (DiskTarget.SelectedItem as ComboBoxItem)?.Tag as string ?? Path.GetPathRoot(Environment.SystemDirectory)!;
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(_disk?.Root.Path ?? targetRoot)!);
            long used = d.TotalSize - d.TotalFreeSpace;
            DiskSummary.Text = Loc.T("disk_usage", d.Name.TrimEnd('\\'), SystemMetrics.FormatBytes(used), SystemMetrics.FormatBytes(d.TotalSize), SystemMetrics.FormatBytes(d.TotalFreeSpace));
            DiskUsageBar.Value = 100.0 * used / d.TotalSize;
        }
        catch { DiskSummary.Text = ""; }

        if (_diskBusy) { DiskNavBar.Visibility = Visibility.Collapsed; return; }

        if (_disk == null || _diskCurrent == null)
        {
            DiskScanInfo.Text = "";
            DiskNavBar.Visibility = Visibility.Collapsed;
            DiskList.Children.Add(new TextBlock
            {
                Text = Loc.T("disk_empty"), Style = (Style)FindResource("Muted"),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 60, 0, 60)
            });
            return;
        }

        // scan info: what was counted, and what could not be (system / protected areas)
        var r = _disk;
        string info = Loc.T("disk_scanned", SystemMetrics.FormatBytes(r.Root.Size), r.Root.FileCount, r.Elapsed.TotalSeconds);
        bool isDriveRoot = Path.GetPathRoot(r.Root.Path)?.Equals(r.Root.Path, StringComparison.OrdinalIgnoreCase) == true;
        long uncounted = (r.DriveTotal - r.DriveFree) - r.Root.Size;
        if (isDriveRoot && uncounted > 1L << 30) info += Loc.T("disk_uncounted", SystemMetrics.FormatBytes(uncounted));
        DiskScanInfo.Text = info;

        bool folders = DiskViewFolders.IsChecked == true;
        DiskNavBar.Visibility = folders ? Visibility.Visible : Visibility.Collapsed;
        if (folders)
        {
            var cur = _diskCurrent;
            BtnDiskUp.IsEnabled = cur.Parent != null;
            DiskPath.Text = cur.Path;
            var kids = cur.Children.OrderByDescending(c => c.Size).ToList();
            foreach (var c in kids.Take(DiskRowLimit)) AddDiskRow(c, cur.Size, showPath: false);
            if (cur.SmallFilesCount > 0)
                AddDiskRow(new DiskNode { Name = Loc.T("disk_small_files", cur.SmallFilesCount), Path = cur.Path, Size = cur.SmallFilesSize, FileCount = cur.SmallFilesCount },
                           cur.Size, showPath: false, pseudo: true);
            if (kids.Count == 0 && cur.SmallFilesCount == 0)
                DiskList.Children.Add(new TextBlock { Text = cur.Denied ? Loc.T("disk_denied") : Loc.T("disk_folder_empty"), Style = (Style)FindResource("Muted"), Margin = new Thickness(16) });
        }
        else
        {
            foreach (var f in r.LargestFiles) AddDiskRow(f, r.Root.Size, showPath: true);
            if (r.LargestFiles.Count == 0)
                DiskList.Children.Add(new TextBlock { Text = Loc.T("disk_no_large"), Style = (Style)FindResource("Muted"), Margin = new Thickness(16) });
        }
    }

    private void AddDiskRow(DiskNode n, long relativeTo, bool showPath, bool pseudo = false)
    {
        double pct = relativeTo > 0 ? Math.Clamp((double)n.Size / relativeTo, 0, 1) : 0;
        var grid = new Grid { Margin = new Thickness(12, 7, 12, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });

        string glyph = n.Denied ? "\uE72E" : pseudo ? "\uE8B7" : n.IsDir ? "\uE8B7" : "\uE7C3";
        grid.Children.Add(new TextBlock
        {
            Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15, VerticalAlignment = VerticalAlignment.Center,
            Foreground = n.Denied ? Res("AmberBrush") : n.IsDir ? Res("Accent2Brush") : Res("AccentBrush"), Opacity = pseudo ? 0.5 : 1
        });

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = n.Name, FontWeight = pseudo ? FontWeights.Normal : FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis,
            FlowDirection = pseudo ? Loc.Flow : FlowDirection.LeftToRight, TextAlignment = Loc.IsArabic && !pseudo ? TextAlignment.Right : TextAlignment.Left,
            Foreground = pseudo ? Res("MutedBrush") : Res("TextBrush")
        });
        string sub = showPath ? Path.GetDirectoryName(n.Path) ?? "" : n.IsDir ? Loc.T("disk_items", n.FileCount) : "";
        if (n.Denied) sub = Loc.T("disk_denied");
        if (sub.Length > 0)
            info.Children.Add(new TextBlock
            {
                Text = sub, Style = (Style)FindResource("Muted"), FontSize = 11.5, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
                FlowDirection = showPath ? FlowDirection.LeftToRight : Loc.Flow, TextAlignment = Loc.IsArabic && showPath ? TextAlignment.Right : TextAlignment.Left
            });
        if (!pseudo && DiskAnalyzer.Hint(n) is string hint)
            info.Children.Add(new TextBlock { Text = Loc.T(hint), Foreground = Res("AmberBrush"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        // share-of-parent bar
        var track = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = Res("TrackBrush"), Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        var fill = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = Res("AccentGradient"), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        track.Child = fill;
        track.SizeChanged += (_, ev) => fill.Width = Math.Max(pct > 0 ? 3 : 0, ev.NewSize.Width * pct);
        Grid.SetColumn(track, 2);
        grid.Children.Add(track);

        var size = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        size.Children.Add(new TextBlock { Text = SystemMetrics.FormatBytes(n.Size), FontWeight = FontWeights.SemiBold, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Right, FlowDirection = FlowDirection.LeftToRight });
        size.Children.Add(new TextBlock { Text = $"{pct * 100:0.#}%", Style = (Style)FindResource("Muted"), FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Right, FlowDirection = FlowDirection.LeftToRight });
        Grid.SetColumn(size, 3);
        grid.Children.Add(size);

        if (!pseudo)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(IconButton("\uE838", Loc.T("open_explorer"), () => OpenInExplorer(n.Path, select: !n.IsDir || showPath)));
            string? reason = DiskAnalyzer.ProtectedReason(n.Path);
            if (reason == null)
                actions.Children.Add(IconButton("\uE74D", Loc.T("recycle"), () => RecycleDiskNode(n)));
            else
                actions.Children.Add(new TextBlock
                {
                    Text = "\uE72E", FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = Res("DimBrush"), VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 8, 0), ToolTip = Loc.T(reason)
                });
            Grid.SetColumn(actions, 4);
            grid.Children.Add(actions);
        }

        var row = new Border { Child = grid, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent };
        row.MouseEnter += (_, _) => row.Background = Res("HoverBrush");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        if (n.IsDir && !pseudo && (n.Children.Count > 0 || n.SmallFilesCount > 0))
        {
            row.Cursor = Cursors.Hand;
            row.MouseLeftButtonUp += (_, _) =>
            {
                if (showPath) DiskViewFolders.IsChecked = true;
                _diskCurrent = n;
                RenderDisk();
                DiskScroll.ScrollToTop();
            };
        }
        DiskList.Children.Add(row);
    }

    private Button IconButton(string glyph, string tip, Action click)
    {
        var b = new Button
        {
            Style = (Style)FindResource("GhostButton"), Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(4, 0, 0, 0), ToolTip = tip,
            Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12 }
        };
        b.Click += (_, _) => click();
        return b;
    }

    private async void RecycleDiskNode(DiskNode n)
    {
        if (_disk == null || DiskAnalyzer.ProtectedReason(n.Path) != null) return;
        if (Msg(Loc.T(n.IsDir ? "disk_confirm_folder" : "disk_confirm_file", n.Name, SystemMetrics.FormatBytes(n.Size), n.FileCount),
                "Axiora SystemCare", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!WindowsNativeApi.IsSafeFileToDelete(n.Path)) { ShowToast(Loc.T("refused"), Loc.T("is_symlink")); return; }

        string? error = await Task.Run(() =>
        {
            try
            {
                if (n.IsDir)
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(n.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
                else
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(n.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
                return null;
            }
            catch (OperationCanceledException) { return Loc.T("wg_cancelled"); }
            catch (Exception ex) { return ex.Message; }
        });

        bool gone = n.IsDir ? !Directory.Exists(n.Path) : !File.Exists(n.Path);
        if (gone)
        {
            if (_diskCurrent == n || (_diskCurrent != null && DiskAnalyzer.IsUnder(_diskCurrent.Path, n.Path))) _diskCurrent = n.Parent ?? _disk.Root;
            DiskAnalyzer.Detach(n, _disk);
            try { _disk.DriveFree = new DriveInfo(Path.GetPathRoot(n.Path)!).TotalFreeSpace; } catch { }
            ShowToast(Loc.T("moved_recycle"), $"{n.Name} · {SystemMetrics.FormatBytes(n.Size)}");
        }
        else ShowToast(Loc.T("disk_delete_failed"), error ?? "");
        RenderDisk();
    }
}
