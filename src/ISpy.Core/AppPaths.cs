namespace ISpy.Core;

/// <summary>
/// Resolves the per-user locations ISpy keeps its state in. Everything lives under
/// LocalApplicationData so the app never needs admin rights and never writes next to the exe
/// (which would break in-place updates).
/// </summary>
public static class AppPaths
{
    private static string? _rootOverride;

    /// <summary>Points every path at <paramref name="root"/>. Used by tests to stay off the real profile.</summary>
    public static void OverrideRoot(string? root) => _rootOverride = root;

    public static string Root =>
        _rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ISpy");

    public static string InventoryDatabase => Path.Combine(Root, "inventory.db");
    public static string ThumbnailDirectory => Path.Combine(Root, "thumbs");
    public static string LogDirectory => Path.Combine(Root, "logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ThumbnailDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
