namespace ExoCinema.Sync;

/// <summary>
/// The profile's media as Cinema's page reaches it: kept by Exo.Identity's <see cref="Exo.Identity.ProfileMedia"/> in the
/// folder every Exo app shares (a file picked in Exo Launcher plays here too), served to the page from
/// <see cref="VirtualHost"/>. A test copy on its own data keeps its own folder there, so it never adds to the real one.
/// </summary>
internal static class ProfileMedia
{
    internal const string VirtualHost = "profile-media.cinema.exo";

    internal static string Folder => AppInfo.OwnData ? Path.Combine(AppInfo.Data, "profile-media") : Exo.Identity.ProfileMedia.Folder;
}

