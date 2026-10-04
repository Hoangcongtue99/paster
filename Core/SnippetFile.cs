using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Paster.Core;

/// <summary>
/// Reads and writes the one portable text file.
///
/// Layout:
///   everything before the first "===" line is a preamble (comments +
///   "#!hotkey" directive) and is preserved verbatim across saves;
///   each "=== Name" line starts an entry, whose body runs to the next
///   "===" line or end of file.
///
/// A body line that genuinely begins with "===" is escaped with a leading
/// backslash on write and unescaped on read, so bodies round-trip losslessly.
/// </summary>
public sealed class SnippetFile
{
    private static readonly Regex HeaderLine = new(@"^===\s?(?<name>.*)$");
    /// <summary>Escape character for a body line that would read as a header.</summary>
    private const char Backslash = (char)92;

    public string Preamble { get; set; } = DefaultPreamble;
    public List<Snippet> Snippets { get; } = new();

    public static SnippetFile Parse(string content)
    {
        var file = new SnippetFile();
        var lines = Snippet.Normalize(content).Split('\n');

        var preamble = new List<string>();
        Snippet? current = null;
        var body = new List<string>();

        void Flush()
        {
            if (current is null) return;
            // Drop the blank separator line(s) we write between entries.
            while (body.Count > 0 && body[^1].Trim().Length == 0) body.RemoveAt(body.Count - 1);
            current.Body = string.Join("\n", body);
            file.Snippets.Add(current);
            body.Clear();
        }

        foreach (var line in lines)
        {
            var header = HeaderLine.Match(line);
            if (header.Success)
            {
                Flush();
                current = new Snippet { Name = header.Groups["name"].Value.Trim() };
                continue;
            }

            if (current is null) preamble.Add(line);
            else body.Add(Unescape(line));
        }

        Flush();

        var text = string.Join("\n", preamble).Trim('\n');
        file.Preamble = text.Length > 0 ? text : DefaultPreamble;
        return file;
    }

    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.Append(Preamble.Trim('\n')).Append('\n');

        foreach (var s in Snippets)
        {
            var name = s.Name.Trim();
            if (name.Length == 0) name = "(unnamed)";

            sb.Append('\n').Append("=== ").Append(name).Append('\n');

            foreach (var line in s.Body.Split('\n'))
                sb.Append(Escape(line)).Append('\n');
        }

        return sb.ToString().Replace("\n", "\r\n");
    }

    /// <summary>
    /// Hotkey override from the preamble, e.g. "#!hotkey Ctrl+Alt+V".
    /// Lives in the snippets file on purpose: one file stays the whole config.
    /// </summary>
    public string HotkeySpec
    {
        get
        {
            foreach (var line in Preamble.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("#!hotkey", StringComparison.OrdinalIgnoreCase))
                {
                    var spec = t[8..].Trim();
                    if (spec.Length > 0) return spec;
                }
            }
            return "Win+Alt+V";
        }
    }

    // ---- escaping ------------------------------------------------------

    /// <summary>
    /// True when a body line would be read back as a header: one starting
    /// with "===", and also an already-escaped one, whose escape character
    /// itself has to be escaped. A backslash anywhere else is left alone.
    /// </summary>
    private static bool NeedsEscape(string line)
    {
        var i = 0;
        while (i < line.Length && line[i] == Backslash) i++;

        return line.AsSpan(i).StartsWith("===");
    }

    private static string Escape(string line) =>
        NeedsEscape(line) ? Backslash + line : line;

    private static string Unescape(string line) =>
        line.Length > 0 && line[0] == Backslash && NeedsEscape(line) ? line[1..] : line;

    // ---- disk ----------------------------------------------------------

    /// <summary>
    /// Portable first: snippets.txt next to the exe when that folder is
    /// writable, otherwise %APPDATA%\Paster\snippets.txt.
    /// </summary>
    public static string ResolvePath()
    {
        // AppContext.BaseDirectory (not Assembly.Location, which is empty in
        // a single-file publish) is the folder the exe actually sits in.
        var portable = Path.Combine(AppContext.BaseDirectory, "snippets.txt");

        if (File.Exists(portable) || IsWritable(AppContext.BaseDirectory))
            return portable;

        var roaming = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Paster");
        Directory.CreateDirectory(roaming);
        return Path.Combine(roaming, "snippets.txt");
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".paster-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static SnippetFile Load(string path)
    {
        if (!File.Exists(path))
        {
            var seeded = Parse(SampleContent);
            seeded.Save(path);
            return seeded;
        }

        // StreamReader honours a BOM when present and falls back to UTF-8.
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Parse(reader.ReadToEnd());
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Write via a temp file so a crash mid-save cannot truncate the only
        // copy of the user's snippets.
        var temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        File.Copy(temp, path, overwrite: true);
        File.Delete(temp);
    }

    public const string DefaultPreamble = """
        # Paster snippets — this one file is the entire config, so you can copy
        # it (or the whole folder) to another machine.
        #
        # Each entry starts with a "=== " line holding its name. Everything up to
        # the next "===" line is the body, blank lines included.
        #
        # Placeholders — the FIRST one in a body decides where the caret lands:
        #   {{cursor}}        caret is left here after pasting
        #   {{select:TEXT}}   TEXT is pasted and left selected, ready to type over
        #
        # Order here is the order in the popup: 1-9 then 0 pick the first ten.
        # Change the trigger by editing the line below (Ctrl / Alt / Shift / Win).
        #
        #!hotkey Win+Alt+V
        """;

    /// <summary>Entries written on a first run, under the preamble above.</summary>
    private const string SampleEntries = """
        === Email signature
        Best regards,
        Tue

        === Standup update
        Yesterday: {{cursor}}
        Today:
        Blockers: none

        === Review request
        Hi {{select:NAME}}, could you take a look at this when you get a chance?

        === Shrug
        ¯\_(ツ)_/¯
        """;

    private static string SampleContent => DefaultPreamble + "\n\n" + SampleEntries;
}
