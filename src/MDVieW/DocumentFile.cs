using System.IO;
using System.Text;

namespace MDVieW;

public sealed class DocumentFile
{
    public string? FilePath { get; private set; }
    public string SavedText { get; private set; } = "";
    public bool HasBom { get; private set; }
    public string NewLine { get; private set; } = "\n";
    public string DisplayName => FilePath == null ? "無題.md" : Path.GetFileName(FilePath);

    public string Open(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var text = new UTF8Encoding(false, true).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        // Commit document identity only after decoding succeeds.
        FilePath = Path.GetFullPath(path); HasBom = bom; NewLine = newline; SavedText = text;
        return text;
    }

    public void Reset(string text = "") { FilePath = null; HasBom = false; NewLine = "\n"; SavedText = text; }

    public void Save(string path, string text)
    {
        var fullPath = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(fullPath)!, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(HasBom));
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
            else File.Move(temporary, fullPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        FilePath = fullPath; SavedText = text;
    }

    public string NormalizeNewLines(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", NewLine);
}
