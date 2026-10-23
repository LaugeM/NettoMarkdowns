namespace NettoMarkdowns.Services;

/// <summary>
/// Bound to the "MarkdownLog" config section. Off by default so a local
/// `dotnet run` doesn't start a second logger next to the one on the server.
/// </summary>
public sealed class MarkdownLogOptions
{
    public const string SectionName = "MarkdownLog";

    /// <summary>Poll the API in the background and write every markdown to SQLite.</summary>
    public bool Enabled { get; set; }

    /// <summary>Time between polls. The time an item first appears is only as precise as this.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Postcodes to log. Empty means Salling:DefaultZips.</summary>
    public string[] Zips { get; set; } = [];

    /// <summary>SQLite file. A relative path is resolved against the app folder.</summary>
    public string DatabasePath { get; set; } = "data/markdowns.db";
}
