using GlassDesk.Core.Input;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Input;
using System.Runtime.InteropServices;

namespace GlassDesk.AppServices;

public interface IHotkeyRegistry : IDisposable
{
    bool TryRegister(int id, HotkeyChord chord, out int errorCode);
    bool Unregister(int id);
}

public sealed class NativeHotkeyRegistry(nint hwnd, Func<int, bool>? canDispatchWheel = null) : IHotkeyRegistry
{
    private readonly GlobalHotkeyRegistration registry = new(hwnd);
    private readonly Dictionary<int, HotkeyChord> mouseBindings = [];
    private MouseSideButtonHook? mouseHook;
    private int wheelRemainder;
    private uint wheelModifiers;
    public bool TryRegister(int id, HotkeyChord chord, out int errorCode)
    {
        if (chord.VirtualKey is not (5 or 6 or 0x100 or 0x101))
            return registry.TryRegister(id, chord.Modifiers | 0x4000u, chord.VirtualKey, out errorCode);
        errorCode = 0;
        if (mouseBindings.ContainsKey(id) || mouseBindings.ContainsValue(chord)) { errorCode = 1409; return false; }
        if (mouseHook is null)
        {
            var hook = new MouseSideButtonHook(button => DispatchSideButton(button, KeyboardModifierState.Read()),
                delta => DispatchWheel(delta, KeyboardModifierState.Read()));
            if (!hook.TryStart(out _)) { errorCode = Marshal.GetLastWin32Error(); hook.Dispose(); return false; }
            mouseHook = hook;
        }
        mouseBindings.Add(id, chord);
        return true;
    }

    public bool DispatchSideButton(int button, uint modifiers)
    {
        if (button is not (1 or 2)) return false;
        var chord = new HotkeyChord(modifiers, button == 1 ? 5u : 6u);
        foreach (var binding in mouseBindings)
            if (binding.Value == chord)
            {
                if (canDispatchWheel?.Invoke(binding.Key) == false) return false;
                return PostMessage(hwnd, 0x0312, binding.Key, (nint)((chord.VirtualKey << 16) | modifiers));
            }
        return false;
    }

    public bool Unregister(int id)
    {
        if (!mouseBindings.Remove(id)) return registry.Unregister(id);
        wheelRemainder = 0;
        if (mouseBindings.Count == 0) { mouseHook?.Dispose(); mouseHook = null; }
        return true;
    }

    public bool DispatchWheel(int delta, uint modifiers)
    {
        if (delta == 0) return false;
        var chord = new HotkeyChord(modifiers, delta > 0 ? 0x100u : 0x101u);
        foreach (var binding in mouseBindings)
        {
            if (binding.Value != chord) continue;
            if (canDispatchWheel?.Invoke(binding.Key) == false) { wheelRemainder = 0; return false; }
            if (wheelModifiers != modifiers || Math.Sign(wheelRemainder) != Math.Sign(delta)) wheelRemainder = 0;
            wheelModifiers = modifiers;
            wheelRemainder += delta;
            while (Math.Abs(wheelRemainder) >= 120)
            {
                if (!PostMessage(hwnd, 0x0312, binding.Key, (nint)((chord.VirtualKey << 16) | modifiers)))
                { wheelRemainder = 0; return false; }
                wheelRemainder -= Math.Sign(wheelRemainder) * 120;
            }
            return true;
        }
        wheelRemainder = 0;
        return false;
    }

    public void Dispose() { mouseHook?.Dispose(); mouseHook = null; mouseBindings.Clear(); registry.Dispose(); }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hwnd, int message, nint wParam, nint lParam);
}

/// <summary>先申请新增热键，保存成功后才释放旧热键；冲突和写盘失败保留原配置。</summary>
public sealed class InteractionSettingsService(SettingsStore store, IHotkeyRegistry registry) : IDisposable
{
    private Dictionary<HotkeyChord, (int Id, InputAction Action)> active = [];
    private int nextId = 0x1000;
    private bool disposed;

    public InputAction ResolveAction(int id) => active.Values.FirstOrDefault(value => value.Id == id).Action;

    public string? Apply(ShortcutSettings shortcuts, DisplaySettings display, bool persist = true)
    {
        if (disposed) return "快捷操作服务已经关闭。";
        var next = SettingsPageEditor.Clone(store.Current);
        next.Shortcuts = shortcuts;
        next.Display = display;
        // Clone caller-owned mutable sections before committing them to shared state.
        next = SettingsPageEditor.Clone(next);
        next.Dock.TargetDisplayId = next.Display.DockDisplayId;
        next.Shortcuts.HotCornersEnabled = false;
        next.Shortcuts.MouseSideButtonsEnabled = false;
        var invalid = SettingsPageEditor.Validate(next);
        if (invalid is not null) return invalid;
        var desired = new Dictionary<HotkeyChord, (int Id, InputAction Action)>();
        var added = new List<int>();
        foreach (var (action, definition) in HotkeyText.Bindings(next.Shortcuts))
        {
            if (!definition.Enabled) continue;
            _ = HotkeyText.TryGetChord(definition, out var chord, out _);
            if (active.TryGetValue(chord, out var old)) { desired.Add(chord, (old.Id, action)); continue; }
            var id = nextId++;
            if (!registry.TryRegister(id, chord, out var error))
            {
                foreach (var registered in added) registry.Unregister(registered);
                return $"快捷键 {definition.Describe()} 注册失败（错误 {error}），可能已被其他程序占用；原设置已保留。";
            }
            added.Add(id);
            desired.Add(chord, (id, action));
        }

        var before = SettingsPageEditor.Clone(store.Current);
        try
        {
            SettingsPageEditor.Apply(store.Current, next);
            if (persist) store.Save();
        }
        catch (Exception ex) when (ex is StorageException or IOException or UnauthorizedAccessException)
        {
            SettingsPageEditor.Apply(store.Current, before);
            foreach (var registered in added) registry.Unregister(registered);
            return "保存失败，已恢复原设置及快捷键：" + ex.Message;
        }

        foreach (var removed in active.Where(pair => !desired.ContainsKey(pair.Key))) registry.Unregister(removed.Value.Id);
        active = desired;
        return null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        registry.Dispose();
        active.Clear();
    }
}
