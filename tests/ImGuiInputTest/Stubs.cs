namespace DalamudMCP;
public sealed class GameServices
{
    public PluginInterface PluginInterface { get; } = new();
    public Configuration Config { get; } = new();
}
public sealed class Configuration { public bool AllowMutatingTools { get; set; } = true; }
public sealed class PluginInterface { public UiBuilder UiBuilder { get; } = new(); }
public sealed class UiBuilder
{
    public event Action? Draw;
    public void FireDraw() => Draw?.Invoke();
}
