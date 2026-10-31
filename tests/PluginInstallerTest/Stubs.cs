namespace DalamudMCP;

internal sealed class GameServices
{
    public TestFramework Framework { get; } = new();
    public TestConfiguration Config { get; } = new();
}
internal sealed class TestFramework
{
    public Task<T> RunOnFrameworkThread<T>(Func<T> action) => Task.FromResult(action());
}
internal sealed class TestConfiguration
{
    public bool AllowMutatingTools { get; set; } = true;
}
