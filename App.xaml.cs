using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using Paster.Core;
using Paster.Interop;
using Paster.UI;
using Forms = System.Windows.Forms;

namespace Paster;

public partial class App : Application
{
    private const string MutexName = @"Local\Paster.SingleInstance";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Paster";

    /// <summary>
    /// Where Task Manager's Startup tab records its own enable/disable state.
    /// Disabling there leaves the Run value untouched and writes a flag here
    /// instead, so the Run value alone is not proof that the app will start.
    /// </summary>
    private const string ApprovedKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private Mutex? _instanceLock;
    private SnippetStore? _store;
    private HotkeyService? _hotkeys;
    private Forms.NotifyIcon? _tray;
    private ManagerWindow? _manager;
    private OverlayWindow? _overlay;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        InstallCrashGuards();

        _instanceLock = new Mutex(initiallyOwned: true, MutexName, out var isFirst);
        if (!isFirst)
        {
            // Already running — the tray icon is the way in, so just leave.
            Shutdown();
            return;
        }

        try
        {
            _store = new SnippetStore();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not read the snippets file.\n\n{ex.Message}",
                "Paster", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        BuildTray();

        _hotkeys = new HotkeyService();
        _hotkeys.Pressed += ShowOverlay;
        _hotkeys.CloseRequested += Shutdown;
        ApplyHotkey();

        // A hand-edit may have changed the #!hotkey line.
        _store.Reloaded += () =>
        {
            ApplyHotkey();
            RefreshTrayTooltip();
        };
    }

    /// <summary>
    /// A tray app has no window to carry an error dialog, so an unhandled
    /// exception would end the process with nothing to show for it - the tray
    /// icon simply vanishes. Keep running, say so, and leave a line in the log.
    /// </summary>
    private void InstallCrashGuards()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log("dispatcher", args.Exception);
            _tray?.ShowBalloonTip(10000, "Paster hit an error",
                args.Exception.Message, Forms.ToolTipIcon.Error);
            args.Handled = true;
        };

        // Not recoverable, but worth recording before the process goes down.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log("fatal", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log("task", args.Exception);
            args.SetObserved();
        };
    }

    internal static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Paster", "paster.log");

    private static void Log(string kind, Exception? ex)
    {
        try
        {
            var path = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTime.Now:s}  {kind}  {ex}" + Environment.NewLine);
        }
        catch
        {
            // Diagnostics must never be the thing that breaks the app.
        }
    }

    // ---- tray ----------------------------------------------------------

    private void BuildTray()
    {
        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ForeColor = System.Drawing.Color.FromArgb(0xE8, 0xE8, 0xEA),
            BackColor = System.Drawing.Color.FromArgb(0x2B, 0x2D, 0x31),
            // The check mark for "Start with Windows" is drawn in the image
            // margin, so hiding the margin hides the tick along with it.
            ShowImageMargin = true
        };

        menu.Items.Add("Manage snippets…", null, (_, _) => ShowManager());
        menu.Items.Add("Open snippets.txt", null, (_, _) => OpenSnippetsFile());
        menu.Items.Add("Reload from file", null, (_, _) => _store?.ReloadFromDisk());
        menu.Items.Add(new Forms.ToolStripSeparator());

        var startup = new Forms.ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = IsAutostartEnabled()
        };

        startup.Click += (_, _) =>
        {
            // CheckOnClick has already flipped Checked by the time this runs,
            // so it carries the state being asked for.
            SetAutostart(startup.Checked);

            // Re-read rather than trust the click: if the write failed, the
            // tick must not claim otherwise.
            startup.Checked = IsAutostartEnabled();
        };

        // The setting can change behind our back - Task Manager's Startup tab,
        // or another copy of this app - so the tick is refreshed every time the
        // menu is opened rather than only at startup.
        menu.Opening += (_, _) => startup.Checked = IsAutostartEnabled();

        menu.Items.Add(startup);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        _tray = new Forms.NotifyIcon
        {
            Icon = TrayIconFactory.Create(),
            Visible = true,
            ContextMenuStrip = menu
        };

        _tray.DoubleClick += (_, _) => ShowManager();
        RefreshTrayTooltip();
    }

    private void RefreshTrayTooltip()
    {
        if (_tray is null || _store is null) return;

        var hotkey = _hotkeys?.ActiveSpec ?? "(hotkey unavailable)";
        // NotifyIcon truncates past 63 chars, so keep this short.
        _tray.Text = $"Paster — {hotkey} · {_store.Items.Count} snippets";
    }

    private void ApplyHotkey()
    {
        if (_hotkeys is null || _store is null) return;

        if (_hotkeys.TryRegister(_store.HotkeySpec, out var error))
        {
            RefreshTrayTooltip();
            return;
        }

        _tray?.ShowBalloonTip(12000, "Paster could not grab its hotkey", error, Forms.ToolTipIcon.Warning);
        RefreshTrayTooltip();
    }

    // ---- overlay -------------------------------------------------------

    private void ShowOverlay()
    {
        if (_store is null) return;

        // Pressing the hotkey while the popup is up dismisses it.
        if (_overlay is not null)
        {
            _overlay.Dismiss();
            return;
        }

        if (_store.Items.Count == 0)
        {
            _tray?.ShowBalloonTip(6000, "No snippets yet",
                "Right-click the Paster tray icon and choose \"Manage snippets…\".",
                Forms.ToolTipIcon.Info);
            return;
        }

        // The window the user is actually typing in. The popup never activates,
        // so this stays the foreground window throughout - it is captured to paste
        // into, and to notice when the user switches away.
        var target = Win32.GetForegroundWindow();

        // Snapshot the list so a mid-flight reload cannot shuffle it under the popup.
        var snapshot = new List<Snippet>(_store.Items);

        _overlay = new OverlayWindow(snapshot, target, snippet => _ = PasteAsync(target, snippet));

        _overlay.Closed += (_, _) =>
        {
            _hotkeys?.EndPicking();
            _overlay = null;
        };

        _overlay.Show();

        // Claim the picker keys only while the popup is up, so they are not
        // taken away from the rest of the system the rest of the time.
        _hotkeys?.BeginPicking(vk => _overlay?.HandleKey(vk));
    }

    /// <summary>
    /// Fire-and-forget paste. Wrapped so a failure surfaces as a balloon tip
    /// rather than an unobserved exception on the dispatcher.
    /// </summary>
    private async Task PasteAsync(IntPtr target, Snippet snippet)
    {
        try
        {
            await PasteEngine.PasteAsync(target, Placeholders.Resolve(snippet.Body));
        }
        catch (Exception ex)
        {
            _tray?.ShowBalloonTip(6000, "Paster could not paste", ex.Message, Forms.ToolTipIcon.Error);
        }
    }

    // ---- manager -------------------------------------------------------

    private void ShowManager()
    {
        if (_store is null) return;

        if (_manager is not null)
        {
            if (_manager.WindowState == WindowState.Minimized)
                _manager.WindowState = WindowState.Normal;

            _manager.Activate();
            return;
        }

        _manager = new ManagerWindow(_store);
        _manager.Closed += (_, _) =>
        {
            _manager = null;
            RefreshTrayTooltip();
        };
        _manager.Show();
        _manager.Activate();
    }

    private void OpenSnippetsFile()
    {
        if (_store is null) return;

        try
        {
            Process.Start(new ProcessStartInfo(_store.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open {_store.Path}\n\n{ex.Message}",
                "Paster", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---- autostart -----------------------------------------------------

    private static string ExecutablePath => Environment.ProcessPath ?? "";

    private static bool IsAutostartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);

            var registered = key?.GetValue(RunValue) is string existing &&
                             existing.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase);

            return registered && !IsBlockedByWindows();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>First byte 2 means approved, 3 means the user switched it off.</summary>
    private static bool IsBlockedByWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return key?.GetValue(RunValue) is byte[] { Length: > 0 } flag && (flag[0] & 1) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static void SetAutostart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return;

            if (enabled)
            {
                key.SetValue(RunValue, $"\"{ExecutablePath}\"");

                // Clear any Task Manager veto, or the entry would be written
                // and still not run.
                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
                approved?.DeleteValue(RunValue, throwOnMissingValue: false);
            }
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not change the startup setting.\n\n{ex.Message}",
                "Paster", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Icon?.Dispose();
            _tray.Dispose();
        }

        _hotkeys?.Dispose();
        _instanceLock?.Dispose();

        base.OnExit(e);
    }
}
