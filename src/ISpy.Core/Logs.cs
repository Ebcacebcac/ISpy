namespace ISpy.Core;

/// <summary>
/// Minimal append-only logging into the app's log directory. Every write is best-effort: a
/// diagnostic that can throw is worse than no diagnostic.
/// </summary>
public static class Logs
{
    private static readonly Lock Gate = new();

    public static void Append(string fileName, string message)
    {
        try
        {
            AppPaths.EnsureCreated();

            lock (Gate)
            {
                File.AppendAllText(
                    Path.Combine(AppPaths.LogDirectory, fileName),
                    $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Nothing useful left to do.
        }
    }
}
