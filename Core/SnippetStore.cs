using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Threading;

namespace Paster.Core;

/// <summary>
/// The in-memory view of snippets.txt, shared by the overlay and the manager.
/// Watches the file so hand-edits in Notepad show up without a restart.
/// </summary>
public sealed class SnippetStore
{
    private readonly FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _debounce;

    private SnippetFile _file;
    private string _lastWrittenByUs = "";

    /// <summary>Raised after the file changed on disk and was reloaded.</summary>
    public event Action? Reloaded;

    /// <summary>Raised when the in-memory list diverges from disk.</summary>
    public event Action? DirtyChanged;

    public string Path { get; }
    public ObservableCollection<Snippet> Items { get; } = new();

    private bool _dirty;
    public bool IsDirty
    {
        get => _dirty;
        private set
        {
            if (_dirty == value) return;
            _dirty = value;
            DirtyChanged?.Invoke();
        }
    }

    public string HotkeySpec => _file.HotkeySpec;

    public SnippetStore()
    {
        Path = SnippetFile.ResolvePath();
        _file = SnippetFile.Load(Path);

        foreach (var s in _file.Snippets) Track(s);
        Items.CollectionChanged += OnCollectionChanged;

        // Coalesce the burst of events a single editor save produces.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            ReloadFromDisk();
        };

        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir))
            {
                _watcher = new FileSystemWatcher(dir, System.IO.Path.GetFileName(Path))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };
                _watcher.Changed += OnFileTouched;
                _watcher.Created += OnFileTouched;
                _watcher.Renamed += OnFileTouched;
            }
        }
        catch
        {
            // Watching is a convenience; a missing or unwatchable folder must
            // not stop the app from working.
        }
    }

    private void OnFileTouched(object sender, FileSystemEventArgs e) =>
        _debounce.Dispatcher.BeginInvoke(() =>
        {
            _debounce.Stop();
            _debounce.Start();
        });

    private void Track(Snippet s)
    {
        Items.Add(s);
        s.PropertyChanged += OnSnippetChanged;
    }

    private void OnSnippetChanged(object? sender, PropertyChangedEventArgs e) => IsDirty = true;

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var s in e.NewItems?.OfType<Snippet>() ?? Enumerable.Empty<Snippet>())
            s.PropertyChanged += OnSnippetChanged;

        foreach (var s in e.OldItems?.OfType<Snippet>() ?? Enumerable.Empty<Snippet>())
            s.PropertyChanged -= OnSnippetChanged;

        IsDirty = true;
    }

    public void Save()
    {
        _file.Snippets.Clear();
        _file.Snippets.AddRange(Items);

        _lastWrittenByUs = _file.Serialize();
        _file.Save(Path);
        IsDirty = false;
    }

    /// <summary>
    /// Re-reads the file. Unsaved in-memory edits lose to disk, so callers with
    /// a dirty manager open should confirm first.
    /// </summary>
    public void ReloadFromDisk()
    {
        SnippetFile loaded;
        try
        {
            loaded = SnippetFile.Load(Path);
        }
        catch
        {
            return; // mid-write by another editor; the next event will catch it
        }

        // Our own Save() trips the watcher — ignore the echo.
        if (loaded.Serialize() == _lastWrittenByUs)
        {
            IsDirty = false;
            return;
        }

        _file = loaded;

        Items.CollectionChanged -= OnCollectionChanged;
        foreach (var s in Items) s.PropertyChanged -= OnSnippetChanged;
        Items.Clear();
        foreach (var s in loaded.Snippets)
        {
            Items.Add(s);
            s.PropertyChanged += OnSnippetChanged;
        }
        Items.CollectionChanged += OnCollectionChanged;

        IsDirty = false;
        Reloaded?.Invoke();
    }
}
