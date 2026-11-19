namespace ExoCinema.Library;

/// <summary>What the library needs from the player.</summary>
internal interface IPlayerSurface
{
    void Play(string url, string? authHeader, double startSeconds, string? title);
    void Stop();
}

/// <summary>A failure the page shows as it is.</summary>
internal class LibraryException(string message) : Exception(message);
