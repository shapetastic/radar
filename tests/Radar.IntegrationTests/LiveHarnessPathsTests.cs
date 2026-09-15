namespace Radar.IntegrationTests;

/// <summary>
/// SPEC 229 — the shared live-harness directory guard is a path-BOUNDARY check. Spec 227's harness used a text-prefix
/// check that rejected a sibling such as <c>data-cache</c> for root <c>data</c>. Offline, deterministic, never skipped.
/// </summary>
public sealed class LiveHarnessPathsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "radar-229-guard", "data");

    [Fact]
    public void ASiblingWhoseNameStartsWithTheRoot_IsOutside()
    {
        Assert.True(LiveHarnessPaths.IsOutside(Root, Path.GetFullPath(Root + "-cache")));
    }

    [Fact]
    public void ADirectoryUnderTheRoot_OrTheRootItself_IsNotOutside()
    {
        Assert.False(LiveHarnessPaths.IsOutside(Root, Path.Combine(Root, "cache")));
        Assert.False(LiveHarnessPaths.IsOutside(Root, Root));
    }

    [Fact]
    public void AnUnrelatedDirectory_IsOutside()
    {
        Assert.True(LiveHarnessPaths.IsOutside(Root, Path.Combine(Path.GetTempPath(), "radar-229-guard", "elsewhere")));
    }
}
