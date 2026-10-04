using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Paster.Core;

/// <summary>
/// One entry from snippets.txt. Body always uses "\n" internally; "\r\n" is
/// produced only at the moment text goes onto the clipboard. Keeping one
/// newline char per line break means a character count is also a caret-move
/// count, which is what the placeholder maths relies on.
/// </summary>
public sealed class Snippet : INotifyPropertyChanged
{
    private string _name = "";
    private string _body = "";

    public string Name
    {
        get => _name;
        set => Set(ref _name, value ?? "");
    }

    public string Body
    {
        get => _body;
        set => Set(ref _body, Normalize(value));
    }

    /// <summary>Single-line preview for the overlay and the manager list.</summary>
    public string Preview
    {
        get
        {
            var flat = string.Join("  ⏎  ", Body.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
            return flat.Length <= 90 ? flat : flat[..89] + "…";
        }
    }

    public bool HasPlaceholder => Placeholders.Pattern.IsMatch(Body);

    public static string Normalize(string? s) =>
        (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    public Snippet Clone() => new() { Name = Name, Body = Body };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref string field, string value, [CallerMemberName] string? prop = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        if (prop == nameof(Body))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPlaceholder)));
        }
    }
}
