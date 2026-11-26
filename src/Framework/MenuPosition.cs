namespace Battlemarked.ModFramework;

// Pure pixel placement, independent of Unity. A negative saved axis means
// center on that axis; resizing clamps the preferred position without losing it.
internal sealed class MenuPosition
{
    private float preferredX, preferredY, offsetX, offsetY;
    private bool changed;
    internal bool Dragging { get; private set; }
    internal int SavedX => (int)Math.Round(preferredX);
    internal int SavedY => (int)Math.Round(preferredY);

    internal MenuPosition(int x, int y)
    {
        preferredX = x < 0 ? -1 : x;
        preferredY = y < 0 ? -1 : y;
    }
    private static float Clamp(float value, float screen, float panel)
        => Math.Clamp(value, 0f, Math.Max(0f, screen - panel));
    private static float Resolve(float saved, float screen, float panel)
        => Clamp(saved < 0 ? (screen - panel) / 2f : saved, screen, panel);
    internal float X(float screen, float panel) => Resolve(preferredX, screen, panel);
    internal float Y(float screen, float panel) => Resolve(preferredY, screen, panel);
    internal void Begin(float mouseX, float mouseY, float panelX, float panelY)
    {
        offsetX = mouseX - panelX;
        offsetY = mouseY - panelY;
        changed = false;
        Dragging = true;
    }
    internal void Move(float mouseX, float mouseY, float screenWidth, float screenHeight, float width, float height)
    {
        if (!Dragging || !float.IsFinite(mouseX) || !float.IsFinite(mouseY)) return;
        float x = Clamp(mouseX - offsetX, screenWidth, width);
        float y = Clamp(mouseY - offsetY, screenHeight, height);
        if (x == X(screenWidth, width) && y == Y(screenHeight, height)) return;
        preferredX = x;
        preferredY = y;
        changed = true;
    }
    internal bool End()
    {
        bool save = Dragging && changed;
        Dragging = false;
        changed = false;
        if (save)
        {
            preferredX = SavedX;
            preferredY = SavedY;
        }
        return save;
    }
}
