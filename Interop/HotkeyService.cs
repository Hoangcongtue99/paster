using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Interop;

namespace Paster.Interop;

/// <summary>
/// Owns the global hotkey. Hosts an invisible message-only window because
/// RegisterHotKey needs an HWND to deliver WM_HOTKEY to.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0xA17;

    /// <summary>Hotkey ids for the picker keys start here, one per key+modifier pair.</summary>
    private const int PickerIdBase = 0x1000;

    /// <summary>
    /// Keys the popup responds to. While it is open each of these is claimed as
    /// a global hotkey, which intercepts it without the popup ever taking focus
    /// - the point being that the app underneath keeps its own focus intact.
    /// </summary>
    private static readonly ushort[] PickerKeys =
    {
        0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,   // top row 0-9
        0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,   // numpad 0-9
        Win32.VK_ESCAPE, Win32.VK_RETURN, Win32.VK_UP, Win32.VK_DOWN
    };

    private readonly HwndSource _source;
    private bool _registered;
    private uint _triggerMods;
    private readonly Dictionary<int, ushort> _pickerIds = new();
    private Action<ushort>? _onPickerKey;

    public event Action? Pressed;

    /// <summary>Raised when something asks this process to shut down.
    /// Windows sends WM_CLOSE on logoff, on restart, and for a plain
    /// "taskkill /IM Paster.exe" - all of which should take the tray icon
    /// down with us rather than leave a dead one behind.</summary>
    public event Action? CloseRequested;

    /// <summary>The combo currently registered, for display in the tray menu.</summary>
    public string? ActiveSpec { get; private set; }

    public HotkeyService()
    {
        // Zero-sized, never shown, no WS_VISIBLE — it exists only to receive messages.
        _source = new HwndSource(new HwndSourceParameters("PasterHotkeySink")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0
        });

        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_CLOSE)
        {
            handled = true;
            CloseRequested?.Invoke();
            return IntPtr.Zero;
        }

        if (msg == Win32.WM_HOTKEY)
        {
            var id = wParam.ToInt32();

            if (id == HotkeyId)
            {
                handled = true;
                Pressed?.Invoke();
            }
            else if (_pickerIds.TryGetValue(id, out var vk))
            {
                handled = true;
                _onPickerKey?.Invoke(vk);
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Claims the picker keys globally until <see cref="EndPicking"/>.
    ///
    /// RegisterHotKey matches the modifier state exactly, so a key has to be
    /// claimed once per modifier combination the user might be holding. The
    /// trigger's own modifiers are the only ones in play - press Win+Alt+V and
    /// your thumb is probably still on Win, Alt or both - so every subset of
    /// those is registered, and nothing else.
    /// </summary>
    public void BeginPicking(Action<ushort> onKey)
    {
        EndPicking();
        _onPickerKey = onKey;

        var id = PickerIdBase;

        foreach (var mods in Subsets(_triggerMods))
        foreach (var vk in PickerKeys)
        {
            if (Win32.RegisterHotKey(_source.Handle, id, mods | Win32.MOD_NOREPEAT, vk))
                _pickerIds[id] = vk;

            // A combination another app already owns is simply skipped; the
            // remaining ones still make the popup usable.
            id++;
        }
    }

    public void EndPicking()
    {
        foreach (var id in _pickerIds.Keys)
            Win32.UnregisterHotKey(_source.Handle, id);

        _pickerIds.Clear();
        _onPickerKey = null;
    }

    /// <summary>Every combination of the bits set in <paramref name="mask"/>, including none.</summary>
    private static IEnumerable<uint> Subsets(uint mask)
    {
        var bits = new List<uint>();
        foreach (var bit in new[] { Win32.MOD_ALT, Win32.MOD_CONTROL, Win32.MOD_SHIFT, Win32.MOD_WIN })
            if ((mask & bit) != 0) bits.Add(bit);

        for (var combo = 0; combo < (1 << bits.Count); combo++)
        {
            uint mods = 0;
            for (var i = 0; i < bits.Count; i++)
                if ((combo & (1 << i)) != 0) mods |= bits[i];

            yield return mods;
        }
    }

    /// <summary>
    /// Registers <paramref name="spec"/> (e.g. "Win+Alt+V"), replacing any
    /// previous binding. Returns false with a human-readable reason on failure —
    /// a taken combo is the common case, not an exceptional one.
    /// </summary>
    public bool TryRegister(string spec, out string error)
    {
        Unregister();

        if (!TryParse(spec, out var mods, out var vk))
        {
            error = $"\"{spec}\" is not a hotkey I understand. Use something like Ctrl+Alt+V.";
            return false;
        }

        if (!Win32.RegisterHotKey(_source.Handle, HotkeyId, mods | Win32.MOD_NOREPEAT, vk))
        {
            error = $"{spec} is already taken by another app. Pick a different "
                  + "combination on the #!hotkey line in snippets.txt - the file "
                  + "is re-read as soon as you save it.";
            return false;
        }

        _registered = true;
        _triggerMods = mods;
        ActiveSpec = spec;
        error = "";
        return true;
    }

    private void Unregister()
    {
        EndPicking();

        if (!_registered) return;
        Win32.UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
        ActiveSpec = null;
    }

    private static readonly Dictionary<string, uint> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = Win32.MOD_CONTROL,
        ["control"] = Win32.MOD_CONTROL,
        ["alt"] = Win32.MOD_ALT,
        ["shift"] = Win32.MOD_SHIFT,
        ["win"] = Win32.MOD_WIN,
        ["windows"] = Win32.MOD_WIN,
        ["super"] = Win32.MOD_WIN,
    };

    private static bool TryParse(string spec, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;

        var parts = (spec ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!Modifiers.TryGetValue(parts[i], out var mod)) return false;
            mods |= mod;
        }

        var keyName = parts[^1];
        if (keyName.Length == 1 && char.IsLetterOrDigit(keyName[0]))
            keyName = char.IsDigit(keyName[0]) ? "D" + keyName : keyName.ToUpperInvariant();

        if (!Enum.TryParse<Key>(keyName, ignoreCase: true, out var key) || key == Key.None)
            return false;

        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return mods != 0 && vk != 0;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
