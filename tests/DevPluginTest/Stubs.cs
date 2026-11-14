namespace DalamudMCP;

internal sealed class GameServices
{
    public TestFramework Framework { get; } = new();
    public TestConfiguration Config { get; } = new();
}
internal sealed class TestFramework
{
    public bool IsFrameworkUnloading => false;
    public Task<T> RunOnFrameworkThread<T>(Func<T> action) => Task.FromResult(action());
    public Task RunOnFrameworkThread(Action action) { action(); return Task.CompletedTask; }
}
internal sealed class TestConfiguration
{
    public bool AllowMutatingTools { get; set; }
}
