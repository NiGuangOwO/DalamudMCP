using System.Runtime.CompilerServices;
public class ManagedFixture
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Sum(int a, int b) => a + b;
}
