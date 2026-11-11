namespace GlassDesk.AppServices.Updates;

/// <summary>Version discovery contains no installer or download channel.</summary>
public sealed record UpdateNotice(Version Version, string Notes);
