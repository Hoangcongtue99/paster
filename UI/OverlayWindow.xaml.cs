using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Paster.Core;
using Paster.Interop;

namespace Paster.UI;

/// <summary>One row in the popup.</summary>
public sealed class OverlayRow
{
    public required Snippet Snippet { get; init; }

    /// <summary>The digit that pastes this row, or "" past the tenth entry.</summary>
    public required string Key { get; init; }

    public string Name => Snippet.Name;
    public string Preview => Snippet.Preview;

    public Visibility KeyVisibility => Key.Length > 0 ? Visibility.Visible : Visibility.Hidden;
    public Visibility PlaceholderVisibility =>
        Snippet.HasPlaceholder ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// The picker.
///
/// It deliberately never takes focus. Apps that manage focus internally - VS
/// Code and anything else built on Chromium is one window to Windows, with the
/// real focus living inside it - lose their caret when another window activates
/// and do not put it back. So the window is created non-activating and its keys
/// arrive from globally registered hotkeys instead of from WPF input routing.
/// The app underneath never notices the popup exists.
/// </summary>
public partial class OverlayWindow : Window
{
    /// <summary>Digits 1-9 then 0 address the first ten rows.</summary>
    private const string DigitOrder = "1234567890";

    /// <summary>How often to check whether the user has moved to another app.</summary>
    private static readonly TimeSpan ForegroundPoll = TimeSpan.FromMilliseconds(250);

    private readonly Action<Snippet> _chosen;
    private readonly IntPtr _target;
    private readonly List<OverlayRow> _rows = new();
    private readonly DispatcherTimer _foregroundWatch;

    private bool _closing;

    public OverlayWindow(IReadOnlyList<Snippet> snippets, IntPtr target, Action<Snippet> chosen)
    {
        InitializeComponent();

        _chosen = chosen;
        _target = target;

        for (var i = 0; i < snippets.Count; i++)
            _rows.Add(new OverlayRow
            {
                Snippet = snippets[i],
                Key = i < DigitOrder.Length ? DigitOrder[i].ToString() : ""
            });

        List.ItemsSource = _rows;
        if (_rows.Count > 0) List.SelectedIndex = 0;

        ContentRendered += (_, _) =>
        {
            PlaceOnActiveScreen();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(80)));
        };

        // Nothing deactivates this window, so the usual click-away dismissal is
        // not available. Watching the foreground window covers the same ground:
        // switch to another app and the popup goes away.
        _foregroundWatch = new DispatcherTimer { Interval = ForegroundPoll };
        _foregroundWatch.Tick += (_, _) =>
        {
            if (Win32.GetForegroundWindow() != _target) Dismiss();
        };
        _foregroundWatch.Start();

        Closed += (_, _) => _foregroundWatch.Stop();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // WS_EX_NOACTIVATE is what actually keeps focus where it was: without it
        // a click on the popup would steal it back. WS_EX_TOOLWINDOW keeps the
        // popup out of Alt+Tab.
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();

        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE,
            (IntPtr)(style | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW));
    }

    /// <summary>
    /// Handles one key, delivered as a virtual key code by the global hotkeys
    /// registered while the popup is open. Reading raw virtual keys also sides
    /// step WPF's Key.System remapping, which hides the real key whenever Alt
    /// is held - and Alt is usually held, being part of the trigger.
    /// </summary>
    public void HandleKey(ushort vk)
    {
        switch (vk)
        {
            case Win32.VK_ESCAPE:
                Dismiss();
                return;

            case Win32.VK_RETURN:
                Pick(List.SelectedIndex);
                return;

            case Win32.VK_DOWN:
                Move(1);
                return;

            case Win32.VK_UP:
                Move(-1);
                return;
        }

        var digit = DigitFor(vk);
        if (digit != NotADigit)
        {
            var index = DigitOrder.IndexOf(digit);
            if (index >= 0) Pick(index);
        }
    }

    private const char NotADigit = (char)0;

    /// <summary>
    /// The digit a virtual key stands for, from either the top row or the
    /// numpad. Numpad keys only report as VK_NUMPAD* while NumLock is on; with
    /// it off they arrive as the arrow and navigation keys printed on them,
    /// and numpad 2 and 8 then move the selection, which is no bad thing.
    /// </summary>
    private static char DigitFor(ushort vk) => vk switch
    {
        >= 0x30 and <= 0x39 => (char)vk,
        >= 0x60 and <= 0x69 => (char)('0' + (vk - 0x60)),
        _ => NotADigit
    };

    private void Move(int delta)
    {
        if (_rows.Count == 0) return;

        var next = List.SelectedIndex + delta;
        List.SelectedIndex = ((next % _rows.Count) + _rows.Count) % _rows.Count;
        List.ScrollIntoView(List.SelectedItem);
    }

    /// <summary>
    /// Resolved by hit-testing rather than reading SelectedIndex: ListBoxItem
    /// marks the button-down handled, so selection may not have moved yet.
    /// </summary>
    private void OnListClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (List.ContainerFromElement(source) is not ListBoxItem item) return;

        e.Handled = true;
        Pick(List.Items.IndexOf(item.DataContext));
    }

    /// <summary>
    /// Closes the popup exactly once. Several things can ask for it at the
    /// same moment - Esc, the foreground watch, picking a snippet, the trigger
    /// pressed again - and re-entering Close() mid-close makes WPF throw, which
    /// in a tray app with no window takes the whole process down.
    /// </summary>
    public void Dismiss()
    {
        if (_closing) return;

        _closing = true;
        Close();
    }

    private void Pick(int index)
    {
        if (index < 0 || index >= _rows.Count) return;

        var snippet = _rows[index].Snippet;

        Dismiss();
        _chosen(snippet);
    }

    /// <summary>
    /// Centres horizontally and sits a third of the way down the work area of
    /// whichever monitor the mouse is on - predictable, and never under the
    /// taskbar. Screen bounds are device pixels, so they go through the
    /// window's DPI transform first.
    /// </summary>
    private void PlaceOnActiveScreen()
    {
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Control.MousePosition);
        var area = screen.WorkingArea;

        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        if (transform is null) return;

        var topLeft = transform.Value.Transform(new Point(area.Left, area.Top));
        var bottomRight = transform.Value.Transform(new Point(area.Right, area.Bottom));

        Left = topLeft.X + ((bottomRight.X - topLeft.X) - ActualWidth) / 2;
        Top = topLeft.Y + Math.Max(0, ((bottomRight.Y - topLeft.Y) - ActualHeight) * 0.3);
    }
}
