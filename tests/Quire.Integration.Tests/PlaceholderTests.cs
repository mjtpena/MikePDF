namespace Quire.Integration.Tests;

// Keeps the test pipeline honest until WP4 lands real tests; delete with the first real test.
public sealed class PlaceholderTests
{
    [Fact]
    public void AssemblyLoads() => Assert.NotNull(typeof(PlaceholderTests).Assembly);
}
