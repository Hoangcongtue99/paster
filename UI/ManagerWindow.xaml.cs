using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Paster.Core;

namespace Paster.UI;

public partial class ManagerWindow : Window
{
    private readonly SnippetStore _store;

    public ManagerWindow(SnippetStore store)
    {
        InitializeComponent();

        _store = store;
        DataContext = store;

        PathLabel.Text = store.Path;

        store.DirtyChanged += OnDirtyChanged;
        store.Reloaded += OnStoreReloaded;
        Closed += (_, _) =>
        {
            store.DirtyChanged -= OnDirtyChanged;
            store.Reloaded -= OnStoreReloaded;
        };

        RefreshHotkeyLabel();
        OnDirtyChanged();

        if (_store.Items.Count > 0) List.SelectedIndex = 0;
        UpdateEditorVisibility();
    }

    private void RefreshHotkeyLabel() =>
        HotkeyLabel.Text = $"Trigger: {_store.HotkeySpec} — change it with the #!hotkey line at the top of this file.";

    private void OnDirtyChanged() =>
        DirtyLabel.Visibility = _store.IsDirty ? Visibility.Visible : Visibility.Collapsed;

    private void OnStoreReloaded()
    {
        // The file changed underneath us; resync the panes.
        RefreshHotkeyLabel();
        if (_store.Items.Count > 0 && List.SelectedIndex < 0) List.SelectedIndex = 0;
        UpdateEditorVisibility();
    }

    private Snippet? Selected => List.SelectedItem as Snippet;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_store is null) return;

        Editor.DataContext = Selected;
        UpdateEditorVisibility();
    }

    private void UpdateEditorVisibility()
    {
        var has = Selected is not null;
        Editor.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        NoSelection.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- list commands -------------------------------------------------

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var snippet = new Snippet { Name = "New snippet", Body = "" };
        var at = List.SelectedIndex < 0 ? _store.Items.Count : List.SelectedIndex + 1;

        _store.Items.Insert(at, snippet);
        List.SelectedIndex = at;

        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;

        var copy = Selected.Clone();
        copy.Name += " (copy)";

        var at = List.SelectedIndex + 1;
        _store.Items.Insert(at, copy);
        List.SelectedIndex = at;
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } snippet) return;

        var answer = MessageBox.Show(
            this,
            $"Delete \"{snippet.Name}\"?",
            "Paster",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK) return;

        var at = List.SelectedIndex;
        _store.Items.RemoveAt(at);
        List.SelectedIndex = Math.Min(at, _store.Items.Count - 1);
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int delta)
    {
        var from = List.SelectedIndex;
        var to = from + delta;

        if (from < 0 || to < 0 || to >= _store.Items.Count) return;

        _store.Items.Move(from, to);
        List.SelectedIndex = to;
        List.ScrollIntoView(List.SelectedItem);
    }

    // ---- editor commands -----------------------------------------------

    private void OnInsertCursor(object sender, RoutedEventArgs e) =>
        InsertAtCaret(Placeholders.CursorToken, selectPayload: false);

    private void OnInsertSelect(object sender, RoutedEventArgs e) =>
        InsertAtCaret(Placeholders.SelectToken, selectPayload: true);

    /// <summary>
    /// Drops a marker in at the caret. For {{select:TEXT}} the TEXT part is left
    /// selected so the next keystroke replaces the placeholder word.
    /// </summary>
    private void InsertAtCaret(string token, bool selectPayload)
    {
        if (Selected is null) return;

        BodyBox.Focus();

        var at = BodyBox.SelectionStart;
        BodyBox.SelectedText = token;

        if (selectPayload)
        {
            var offset = token.IndexOf("TEXT", StringComparison.Ordinal);
            BodyBox.Select(at + offset, "TEXT".Length);
        }
        else
        {
            BodyBox.Select(at + token.Length, 0);
        }
    }

    // ---- file commands -------------------------------------------------

    private void OnSave(object sender, RoutedEventArgs e) => Save();

    private bool Save()
    {
        try
        {
            _store.Save();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not write {_store.Path}\n\n{ex.Message}",
                "Paster", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void OnReload(object sender, RoutedEventArgs e)
    {
        if (_store.IsDirty)
        {
            var answer = MessageBox.Show(this,
                "Discard your unsaved changes and reload from the file?",
                "Paster", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.OK) return;
        }

        _store.ReloadFromDisk();
    }

    private void OnOpenFolder(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_store.Path}\"")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // Explorer missing or blocked by policy — not worth interrupting for.
        }
    }

    // ---- shortcuts -----------------------------------------------------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

        if (ctrl && e.Key == Key.S)
        {
            e.Handled = true;
            Save();
        }
        else if (ctrl && e.Key == Key.N)
        {
            e.Handled = true;
            OnAdd(this, new RoutedEventArgs());
        }
        else if (alt && e.SystemKey is Key.Up or Key.Down)
        {
            e.Handled = true;
            Move(e.SystemKey == Key.Up ? -1 : 1);
        }
        else
        {
            base.OnPreviewKeyDown(e);
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_store.IsDirty)
        {
            var answer = MessageBox.Show(this,
                "Save your changes before closing?",
                "Paster", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            switch (answer)
            {
                case MessageBoxResult.Yes when !Save():
                    e.Cancel = true;
                    return;
                case MessageBoxResult.No:
                    _store.ReloadFromDisk();
                    break;
                case MessageBoxResult.Cancel:
                    e.Cancel = true;
                    return;
            }
        }

        base.OnClosing(e);
    }
}
