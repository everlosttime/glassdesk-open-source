using GlassDesk.Platform.Displays;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform;

/// <summary>Native policy for an independent, entirely input-transparent shadow window.</summary>
public static class WindowShadowStyles
{
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const int WsExLayered = 0x00080000;
    private const int WsExTopmost = 0x00000008;
    private const int RequiredStyles = WsExTransparent | WsExToolWindow | WsExLayered | User32.WsExNoactivate;
    private const uint NoActivate = 0x0010;
    private const uint NoOwnerZOrder = 0x0200;
    private const uint NoMoveOrSize = 0x0001 | 0x0002;

    /// <summary>
    /// A layered window with WS_EX_TRANSPARENT passes input through even its painted pixels.
    /// HTTRANSPARENT alone cannot provide that contract across process boundaries.
    /// </summary>
    public static bool MakeInputTransparent(nint window)
    {
        if (window == 0) return false;
        var style = User32.GetWindowLong(window, User32.GwlExStyle);
        _ = User32.SetWindowLong(window, User32.GwlExStyle, (style | RequiredStyles) & ~WsExAppWindow);
        return IsInputTransparent(window);
    }

    /// <summary>Reads back every style needed for no focus, taskbar entry, or pointer interception.</summary>
    public static bool IsInputTransparent(nint window)
    {
        if (window == 0) return false;
        var style = User32.GetWindowLong(window, User32.GwlExStyle);
        return (style & RequiredStyles) == RequiredStyles && (style & WsExAppWindow) == 0;
    }

    /// <summary>Whether an existing native owner can currently cast a visible shadow.</summary>
    public static bool IsOwnerVisible(nint owner) =>
        owner != 0 && User32.IsWindowVisible(owner) && !User32.IsIconic(owner);

    /// <summary>
    /// Places the shadow immediately below its owner without activating either window.
    /// Coordinates are physical pixels; the shared display service settles DPI transitions first.
    /// </summary>
    public static bool PositionBehind(nint shadow, nint owner, PixelRect bounds)
    {
        if (shadow == 0 || owner == 0 || shadow == owner) return false;
        if (!DisplayService.MoveWindowToPixelRect(shadow, bounds)) return false;

        var ownerTopmost = (User32.GetWindowLong(owner, User32.GwlExStyle) & WsExTopmost) != 0;
        var shadowTopmost = (User32.GetWindowLong(shadow, User32.GwlExStyle) & WsExTopmost) != 0;
        if (ownerTopmost != shadowTopmost)
        {
            if (!Dpi32.SetWindowPos(shadow, ownerTopmost ? new nint(-1) : new nint(-2),
                    0, 0, 0, 0, NoMoveOrSize | NoActivate | NoOwnerZOrder))
                return false;
        }

        // Do not establish Win32 ownership: owned windows are forced ABOVE their owner,
        // whereas translucent glass needs an unowned shadow BELOW its surface.
        return Dpi32.SetWindowPos(shadow, owner, 0, 0, 0, 0,
            NoMoveOrSize | NoActivate | NoOwnerZOrder);
    }
}
