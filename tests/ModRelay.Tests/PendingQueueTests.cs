using ModRelay.Core;

namespace ModRelay.Tests;

public sealed class PendingQueueTests
{
    [Fact]
    public void Queue_PersistsExistingFilesAndDoesNotDuplicate()
    {
        using var temp = new TestDirectory();
        var mod = temp.File("mod.ttmp2");
        var storage = temp.File("pending.json");
        File.WriteAllText(mod, "mod");

        var first = new PendingQueue(storage);
        first.Add(mod);
        first.Add(mod.ToUpperInvariant());

        var second = new PendingQueue(storage);
        second.Load();

        Assert.Equal(1, second.Count);
        Assert.Equal(mod, second.Snapshot().Single(), ignoreCase: true);
    }
}
