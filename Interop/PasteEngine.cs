using System;
using System.Threading.Tasks;
using System.Windows;
using Paster.Core;

namespace Paster.Interop;

/// <summary>
/// Puts resolved snippet text into another application.
///
/// Clipboard + Ctrl+V rather than typing the text out character by character:
/// synthetic per-character input is slow, mangles non-ASCII, and trips
/// autocomplete in editors. The cost is that we borrow the clipboard, so the
/// previous contents are captured and put back afterwards.
/// </summary>
public static class PasteEngine
{
    /// <summary>Let the target window finish taking focus before Ctrl+V lands.</summary>
    private const int FocusSettleMs = 70;

    /// <summary>Give the target time to actually insert the text before moving the caret.</summary>
    private const int PasteSettleMs = 130;

    /// <summary>Hold the clipboard long enough that slow apps have read it.</summary>
    private const int ClipboardHoldMs = 400;

    /// <summary>Give up waiting on held modifiers rather than hanging forever.</summary>
    private const int ModifierWaitTimeoutMs = 1500;
    private const int ModifierPollMs = 15;

    /// <summary>Air around a synthesised Shift press so the target registers it.</summary>
    private const int ModifierSettleMs = 30;

    public static async Task PasteAsync(IntPtr target, ResolvedPaste paste)
    {
        var saved = ClipboardHelper.Capture();

        // "\r\n" is the clipboard convention; anything less confuses Notepad
        // and several native edit controls.
        if (!ClipboardHelper.TrySetText(paste.Text.Replace("\n", "\r\n")))
            return;

        Win32.Focus(target);

        // The hotkey modifiers are often still held when the digit is pressed.
        // Sending Ctrl+V while Win or Shift is down produces a different combo
        // entirely, so wait briefly for the keyboard to go quiet.
        await WaitForModifierReleaseAsync();

        await Task.Delay(FocusSettleMs);

        Win32.SendCtrlV();

        if (paste.MovesCaret)
        {
            await Task.Delay(PasteSettleMs);

            // Walk back from the end of the pasted text, then extend a
            // selection leftwards over the {{select:...}} payload. Selecting
            // right-to-left leaves the caret at the start, which behaves the
            // same for the next thing typed.
            Win32.SendKeyTaps(Win32.VK_LEFT, paste.LeftMoves);

            if (paste.SelectLength > 0)
            {
                // Shift has to land as its own event with air around it. Sent in
                // the same batch as the arrows, the target processes the keys
                // before the modifier state registers and just moves the caret.
                Win32.SendShift(down: true);
                await Task.Delay(ModifierSettleMs);

                Win32.SendKeyTaps(Win32.VK_LEFT, paste.SelectLength);
                await Task.Delay(ModifierSettleMs);

                Win32.SendShift(down: false);
            }
        }

        await Task.Delay(ClipboardHoldMs);
        ClipboardHelper.Restore(saved);
    }

    private static async Task WaitForModifierReleaseAsync()
    {
        for (var waited = 0; waited < ModifierWaitTimeoutMs && Win32.ModifiersHeld(); waited += ModifierPollMs)
            await Task.Delay(ModifierPollMs);
    }
}

internal static class ClipboardHelper
{
    private const int Attempts = 6;
    private const int RetryDelayMs = 25;

    /// <summary>
    /// Best-effort snapshot of the clipboard. Copies every format the current
    /// owner advertises; formats that refuse to marshal are skipped rather than
    /// failing the whole snapshot, so restore can be lossy for exotic payloads.
    /// </summary>
    public static IDataObject? Capture()
    {
        return Retry(() =>
        {
            var current = Clipboard.GetDataObject();
            if (current is null) return null;

            var copy = new DataObject();
            var kept = false;

            foreach (var format in current.GetFormats())
            {
                try
                {
                    var data = current.GetData(format);
                    if (data is null) continue;
                    copy.SetData(format, data);
                    kept = true;
                }
                catch
                {
                    // Unmarshalable format (live COM pointers, odd native
                    // handles) — nothing to do but leave it out.
                }
            }

            return kept ? copy : null;
        });
    }

    public static bool TrySetText(string text) =>
        Retry(() =>
        {
            Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: true);
            return (object?)true;
        }) is not null;

    public static void Restore(IDataObject? saved)
    {
        if (saved is null)
        {
            // Nothing worth restoring. Leaving the snippet on the clipboard is
            // friendlier than clearing it — a second Ctrl+V still works.
            return;
        }

        Retry(() =>
        {
            Clipboard.SetDataObject(saved, copy: true);
            return (object?)true;
        });
    }

    /// <summary>
    /// The clipboard is a shared, lock-based resource; another process holding
    /// it open makes these calls throw transiently. Retrying briefly is the
    /// documented remedy.
    /// </summary>
    private static T? Retry<T>(Func<T?> action) where T : class
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            try
            {
                return action();
            }
            catch
            {
                System.Threading.Thread.Sleep(RetryDelayMs);
            }
        }
        return null;
    }
}
