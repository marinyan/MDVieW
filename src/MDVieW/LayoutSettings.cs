using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Controls;

namespace MDVieW;

internal sealed record LayoutSettings
{
    public PaneLayout PaneLayout { get; init; } = PaneLayout.SideBySide;
    public Orientation PaletteOrientation { get; init; } = Orientation.Horizontal;
    public bool PanesSwapped { get; init; }
    public string ViewMode { get; init; } = "Split";
    public double SideBySideRatio { get; init; } = 0.5;
    public double StackedRatio { get; init; } = 0.5;
    public bool SyncScrolling { get; init; } = true;
    public int ZoomPercent { get; init; } = 100;

    public LayoutSettings Validated() => this with
    {
        PaneLayout = Enum.IsDefined(PaneLayout) ? PaneLayout : PaneLayout.SideBySide,
        PaletteOrientation = Enum.IsDefined(PaletteOrientation) ? PaletteOrientation : Orientation.Horizontal,
        ViewMode = ViewMode is "Split" or "Source" or "Rich" ? ViewMode : "Split",
        SideBySideRatio = ValidRatio(SideBySideRatio),
        StackedRatio = ValidRatio(StackedRatio),
        ZoomPercent = ZoomPercent is >= 70 and <= 180 ? ZoomPercent : 100
    };

    private static double ValidRatio(double value) => double.IsFinite(value) && value is >= 0.05 and <= 0.95 ? value : 0.5;
}

internal sealed class LayoutSettingsStore(string? filePath)
{
    public static string DefaultFilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MDVieW", "layout.json");
    private readonly string? _filePath = filePath == null ? null : Path.GetFullPath(filePath);
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<PaneLayout>(allowIntegerValues: false), new JsonStringEnumConverter<Orientation>(allowIntegerValues: false) }
    };

    public LayoutSettings Load()
    {
        if (_filePath == null) return new LayoutSettings();
        try { return (JsonSerializer.Deserialize<LayoutSettings>(File.ReadAllBytes(_filePath), Options) ?? new LayoutSettings()).Validated(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException)
        { return new LayoutSettings(); }
    }

    public bool Save(LayoutSettings settings)
    {
        // A null path is an isolated, non-persistent store for native control tests.
        if (_filePath == null) return true;
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            temporary = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings.Validated(), Options), new UTF8Encoding(false));
            File.Move(temporary, _filePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return false; }
        finally
        {
            if (temporary != null)
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
    }
}
