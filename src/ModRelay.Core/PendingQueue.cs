using System.Text.Json;

namespace ModRelay.Core;

/// <summary>
/// Mods that are ready but could not be handed over because Penumbra was not running.
/// Survives a restart so a mod downloaded with the game closed is not lost.
/// </summary>
public sealed class PendingQueue(string filePath)
{
    private readonly object _gate = new();
    private readonly List<string> _items = [];
    private bool _needsPersistence;

    public PendingQueue() : this(AppPaths.PendingQueueFile)
    {
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _items.Count;
        }
    }

    public bool Load()
    {
        lock (_gate)
        {
            _items.Clear();
            _needsPersistence = false;

            try
            {
                if (!File.Exists(filePath))
                    return true;

                var stored = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(filePath)) ?? [];
                _items.AddRange(stored.Where(File.Exists));

                if (_items.Count != stored.Count)
                    Persist();

                if (_items.Count > 0)
                    Log.Info($"{_items.Count} mod(s) waiting for Penumbra.");

                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read the pending queue at {filePath}.", ex);
                BackupBrokenFile();
                return false;
            }
        }
    }

    public bool Add(string path)
    {
        lock (_gate)
        {
            if (_items.Contains(path, StringComparer.OrdinalIgnoreCase))
                return !_needsPersistence || Persist();

            _items.Add(path);
            return Persist();
        }
    }

    public bool Remove(string path)
    {
        lock (_gate)
        {
            if (_items.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0)
                return Persist();

            return !_needsPersistence || Persist();
        }
    }

    internal bool NeedsPersistence
    {
        get
        {
            lock (_gate)
                return _needsPersistence;
        }
    }

    internal bool Flush()
    {
        lock (_gate)
            return !_needsPersistence || Persist();
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
            return [.. _items];
    }

    private bool Persist()
    {
        var temp = filePath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(temp, JsonSerializer.Serialize(_items));
            File.Move(temp, filePath, overwrite: true);
            _needsPersistence = false;
            return true;
        }
        catch (Exception ex)
        {
            _needsPersistence = true;
            Log.Warn($"Could not save the pending queue to {filePath}.", ex);
            TryDelete(temp);
            return false;
        }
    }

    private void BackupBrokenFile()
    {
        try
        {
            File.Copy(filePath, filePath + ".broken", overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not back up the unreadable pending queue.", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* A stale temporary queue file is harmless. */ }
    }
}
