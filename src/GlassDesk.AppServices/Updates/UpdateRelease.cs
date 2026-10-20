namespace GlassDesk.AppServices.Updates;

public sealed record UpdateRelease(
    Version Version, string FileName, Uri DownloadUrl, string Sha256, long Size, string Notes, Uri ReleasePage);
