using Microsoft.Extensions.Hosting;

namespace Radar.Worker.Tests;

/// <summary>
/// A no-op <see cref="IHostApplicationLifetime"/> so a composition test can build the Worker graph without
/// launching a host. Shared by every composition-root test class (it was duplicated per class before spec
/// 144 added a third caller).
/// </summary>
internal sealed class FakeLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;

    public CancellationToken ApplicationStopping => CancellationToken.None;

    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication()
    {
    }
}
