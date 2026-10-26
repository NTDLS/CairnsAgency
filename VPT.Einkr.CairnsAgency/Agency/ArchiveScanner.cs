namespace VPT.Einkr.CairnsAgency.Agency;

/// <summary>Reads the archive folder (never writes it) for the one fact the nudge reports about it: what someone other than a wake
/// changed since the last wake.</summary>
public static class ArchiveScanner
{
    public static void Fill(WakeContext context, string archiveRoot, DateTime? lastWakeEndUtc)
    {
        if (!Directory.Exists(archiveRoot)) return;

        var files = new DirectoryInfo(archiveRoot).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        string Rel(FileInfo f) => Path.GetRelativePath(Path.GetDirectoryName(archiveRoot.TrimEnd('\\', '/'))!, f.FullName).Replace('\\', '/');

        if (lastWakeEndUtc is { } since)
        {
            context.ChangedByOthers = files
                .Where(f => f.LastWriteTimeUtc > since.AddSeconds(5)
                            && !f.Name.Equals("AGENCY.md", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(Rel)
                .ToList();
        }
    }
}
