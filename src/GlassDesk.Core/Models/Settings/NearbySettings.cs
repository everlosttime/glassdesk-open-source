namespace GlassDesk.Core.Models.Settings;

public sealed class NearbySettings
{
    public bool Enabled { get; set; }
    public string DeviceName { get; set; } = "GlassDesk · " + Environment.MachineName;
    // Resolved by the Windows host through the Downloads known folder.
    public string? ReceiveDirectory { get; set; }

    public void Normalize()
    {
        DeviceName = string.IsNullOrWhiteSpace(DeviceName) ? "GlassDesk · " + Environment.MachineName : DeviceName.Trim();
        if (DeviceName.Length > 80) DeviceName = DeviceName[..80];
        if (string.IsNullOrWhiteSpace(ReceiveDirectory)) ReceiveDirectory = null;
    }
}
