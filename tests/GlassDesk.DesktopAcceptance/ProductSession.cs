using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Automation;

namespace GlassDesk.DesktopAcceptance;

// Seeds only the dedicated test account after the existing ownership gate. Restores its
// original documents on disposal; all backup bytes also remain in this run's evidence folder.
internal sealed class ProductSession : IDisposable
{
    private readonly DriverContext context;
    private readonly Dictionary<string, byte[]?> original = [];
    private readonly List<string> ownedTargetImages = [];
    private ProcessLease? product;
    internal string Work { get; }
    internal string TargetExe { get; }
    internal string Marker { get; }
    internal AutomationElement Dock { get; private set; } = null!;
    internal int ProcessId => product!.ProcessId;
    internal JsonArray Entries { get; } = [];
    internal JsonObject Settings { get; } = new() { ["schemaVersion"] = 1, ["dock"] = new JsonObject() };

    internal ProductSession(DriverContext context, string label, int tileCount = 3)
    {
        this.context = context;
        var blocked = DesktopGate.ExistingInstanceReason();
        if (blocked != null) throw new InputBlockedException(blocked);
        if (ProductLibrary.ForeignEntries(ProductLibrary.DefaultRoot, DriverContext.WorkRootFamily).Count != 0)
            throw new InputBlockedException("The test account contains non-test library entries; seed refused.");
        Work = context.NewWorkDirectory(label);
        TargetExe = Path.Combine(Work, "target-0", "GlassDesk.DesktopAcceptance.exe");
        Marker = Path.Combine(Work, "launched.json");
        Directory.CreateDirectory(ProductLibrary.DefaultRoot);
        foreach (var name in new[] { "apps.json", "dock-layout.json", "settings.json", "launcher-layout.json" })
        foreach (var suffix in new[] { "", ".bak" })
        {
            var file = Path.Combine(ProductLibrary.DefaultRoot, name + suffix);
            original[file] = File.Exists(file) ? File.ReadAllBytes(file) : null;
            if (original[file] is { } bytes) File.WriteAllBytes(Path.Combine(Work, "before-" + name + suffix), bytes);
        }
        for (var i = 0; i < tileCount; i++)
        {
            var targetFolder = Path.Combine(Work, "target-" + i);
            Directory.CreateDirectory(targetFolder);
            foreach (var source in Directory.GetFiles(AppContext.BaseDirectory))
                File.Copy(source, Path.Combine(targetFolder, Path.GetFileName(source)));
            var image = Path.Combine(targetFolder, "GlassDesk.DesktopAcceptance.exe");
            ownedTargetImages.Add(image);
            var marker = i == 0 ? Marker : Path.Combine(Work, "target-" + i + ".json");
            Entries.Add(new JsonObject
            {
                ["id"] = "target-" + i, ["displayName"] = "Test target " + i, ["kind"] = "Program",
                ["target"] = image, ["arguments"] = $"--owned-target \"{marker}\" 1 normal",
                ["iconSource"] = image,
            });
        }
    }

    internal void Start(bool settings = false)
    {
        Write("apps.json", new JsonObject { ["schemaVersion"] = 1, ["items"] = Entries.DeepClone() });
        Write("dock-layout.json", new JsonObject { ["pinnedItemIds"] = new JsonArray(Entries.Select(e => JsonValue.Create(e!["id"]!.GetValue<string>())).ToArray()) });
        Write("settings.json", Settings);
        Launch(settings);
    }

    internal void Restart()
    {
        product?.Dispose();
        product = null;
        Launch(false);
    }

    private void Launch(bool settings)
    {
        product = ProcessLease.Launch(context.ProductPath, settings ? ["--settings"] : []);
        Dock = Uia.WaitForTopWindow(product.ProcessId, null, "程序坞", 30000)
            ?? throw new InputBlockedException("Product Dock did not appear in the test account.");
        Thread.Sleep(500);
    }

    internal AutomationElement Tile(string id)
    {
        AutomationElement? Find() => Uia.Descendants(Dock).FirstOrDefault(e =>
            e.Current.AutomationId == "dock-tile:" + id && !Uia.IsOffscreen(e));
        if (!Uia.Until(() => Find() != null, 5000)) throw new InvalidOperationException("Visible tile missing: " + id);
        return Find()!;
    }

    internal void ClickTile(string id)
    {
        var outcome = Uia.Click(Tile(id), id);
        if (!outcome.Clicked) throw new InputBlockedException(outcome.Detail);
    }

    internal static void Write(string name, JsonNode value) => File.WriteAllText(
        Path.Combine(ProductLibrary.DefaultRoot, name), value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    public void Dispose()
    {
        product?.Dispose();
        // Only targets with a receipt in this unique work directory receive a close request.
        foreach (var marker in Directory.GetFiles(Work, "*.json").Where(p => !Path.GetFileName(p).StartsWith("before-")))
        {
            try
            {
                var receipt = JsonNode.Parse(File.ReadAllText(marker));
                if (receipt?["image"]?.GetValue<string>() is { } image &&
                    ownedTargetImages.Contains(image, StringComparer.OrdinalIgnoreCase))
                    File.WriteAllText(marker + ".close", "close owned target");
            }
            catch (JsonException) { }
        }
        foreach (var (file, bytes) in original)
        {
            if (bytes != null) File.WriteAllBytes(file, bytes);
            else if (File.Exists(file)) File.Delete(file);
        }
    }
}
