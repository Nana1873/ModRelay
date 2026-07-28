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
        Assert.True(second.Load());

        Assert.Equal(1, second.Count);
        Assert.Equal(mod, second.Snapshot().Single(), ignoreCase: true);
    }

    [Fact]
    public void CorruptQueue_IsReportedAndBackedUp()
    {
        using var temp = new TestDirectory();
        var storage = temp.File("pending.json");
        File.WriteAllText(storage, "{not json");

        var queue = new PendingQueue(storage);

        Assert.False(queue.Load());
        Assert.True(File.Exists(storage + ".broken"));
        Assert.Equal(0, queue.Count);
    }
}
