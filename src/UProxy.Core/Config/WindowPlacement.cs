namespace UProxy.Core.Config;

/// <summary>Helpers for remembering and restoring the main window size/position across runs.</summary>
public static class WindowPlacement
{
    public readonly record struct Bounds(int Left, int Top, int Width, int Height, bool Maximized);

    /// <summary>
    /// Returns saved bounds if they fit on at least one of <paramref name="screens"/>
    /// (a 40px title-bar sliver must remain visible). Otherwise null → caller should center.
    /// </summary>
    public static Bounds? TryRestore(
        int? left, int? top, int? width, int? height, bool maximized,
        IEnumerable<(int X, int Y, int Width, int Height)> screens,
        int minWidth = 400, int minHeight = 300)
    {
        if (width is null || height is null || width.Value < minWidth || height.Value < minHeight)
            return null;

        var w = width.Value;
        var h = height.Value;

        // No position saved → use size only; caller centers.
        if (left is null || top is null)
            return new Bounds(0, 0, w, h, maximized);

        var l = left.Value;
        var t = top.Value;
        var visible = screens.Any(s =>
            l + w > s.X + 40 &&
            t + 40 > s.Y &&
            l < s.X + s.Width - 40 &&
            t < s.Y + s.Height - 40);

        if (!visible)
            return new Bounds(0, 0, w, h, maximized); // size ok, recenter

        return new Bounds(l, t, w, h, maximized);
    }
}
