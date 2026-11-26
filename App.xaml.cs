using System.Windows;
using SystemCare20.Services;
using WinForms = System.Windows.Forms;

namespace SystemCare20;

/// <summary>
/// Application entry: main window + system tray icon (closing the window hides it to the tray).
/// </summary>
public partial class App : Application
{
    private WinForms.NotifyIcon? _tray;
    private MainWindow? _main;
    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Language: saved choice, otherwise follow the Windows display language
        var saved = SystemCare20.Database.RealmLocalDatabase.Instance.Find<SystemCare20.Models.AppSetting>("language")?.Value;
        string lang = saved ?? (System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar" ? "ar" : "en");
        Loc.SetLanguage(lang);
        ThemeManager.Apply(SystemCare20.Database.RealmLocalDatabase.Instance.Find<SystemCare20.Models.AppSetting>("theme")?.Value);
        Loc.Changed += BuildTrayMenu;

        _main = new MainWindow();
        _main.Show();
        CreateTray();
    }

    private void CreateTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "Axiora SystemCare",
            Visible = true
        };
        _tray.DoubleClick += (_, _) => ShowMain();
        BuildTrayMenu();
    }

    private void BuildTrayMenu()
    {
        if (_tray == null) return;
        var menu = new WinForms.ContextMenuStrip
        {
            RightToLeft = Loc.IsArabic ? WinForms.RightToLeft.Yes : WinForms.RightToLeft.No
        };
        menu.Items.Add(Loc.T("tray_open"), null, (_, _) => ShowMain());
        menu.Items.Add(Loc.T("tray_clean"), null, async (_, _) =>
        {
            long mb = await Task.Run(WindowsNativeApi.TrimOwnWorkingSet);
            _tray!.ShowBalloonTip(3000, Loc.T("mem_cleaned"), Loc.T("mem_freed", mb), WinForms.ToolTipIcon.Info);
        });
        menu.Items.Add(Loc.T("perf_monitor"), null, (_, _) => _main?.TogglePerformanceWidget());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(Loc.T("exit"), null, (_, _) => ExitApp());
        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = menu;
        old?.Dispose();
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var res = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (res != null) return new System.Drawing.Icon(res.Stream, new System.Drawing.Size(32, 32));
        }
        catch { /* fall back below */ }
        return System.Drawing.SystemIcons.Application;
    }

    public void ShowMain()
    {
        if (_main == null) return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public void NotifyHiddenToTray()
    {
        _tray?.ShowBalloonTip(2000, "Axiora SystemCare", Loc.T("tray_running"), WinForms.ToolTipIcon.None);
    }

    public void ExitApp()
    {
        IsExiting = true;
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Shutdown();
    }
}
