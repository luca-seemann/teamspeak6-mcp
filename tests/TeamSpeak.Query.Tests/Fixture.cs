namespace TeamSpeak.Query.Tests;

/// <summary>
/// Reads the raw server responses captured under <c>Fixtures/</c>.
/// </summary>
internal static class Fixture
{
    /// <summary>Reads a captured line-based (SSH) response.</summary>
    /// <param name="name">The fixture file name without extension.</param>
    /// <returns>The raw response text.</returns>
    public static string Ssh(string name) => Read(Path.Combine("Fixtures", "ssh", name + ".txt"));

    /// <summary>Reads a captured WebQuery JSON response.</summary>
    /// <param name="name">The fixture file name without extension.</param>
    /// <returns>The raw response body.</returns>
    public static string Http(string name) => Read(Path.Combine("Fixtures", "http", name + ".json"));

    private static string Read(string relativePath)
    {
        var path = Path.Combine(AppContext.BaseDirectory, relativePath);
        return File.Exists(path)
            ? File.ReadAllText(path)
            : throw new FileNotFoundException($"Missing fixture '{relativePath}'.", path);
    }
}