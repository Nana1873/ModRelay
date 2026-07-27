using ModRelay.Core;

namespace ModRelay.Tests;

public sealed class PortableLayoutTests
{
    [Fact]
    public void SettingsAndRuntimeDataStayBesideTheExecutable()
    {
        Assert.Equal(
            Path.Combine(AppPaths.ApplicationDirectory, "settings.json"),
            AppPaths.ConfigFile);
        Assert.Equal(
            Path.Combine(AppPaths.ApplicationDirectory, "data"),
            AppPaths.DataDirectory);
        Assert.Equal(
            Path.Combine(AppPaths.ApplicationDirectory, "data", "pending.json"),
            AppPaths.PendingQueueFile);
    }
}
