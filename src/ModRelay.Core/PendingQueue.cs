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

    public void Load()
    {
        lock (_gate)
        {
            _items.Clear();

            try
            {
                if (!File.Exists(filePath))
                    return;

                var stored = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(filePath)) ?? [];
                _items.AddRange(stored.Where(File.Exists));

                if (_items.Count > 0)
                    Log.Info($"{_items.Count} mod(s) waiting for Penumbra.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read the pending queue at {filePath}.", ex);
            }
        }
    }

    public void Add(string path)
    {
        lock (_gate)
        {
            if (_items.Contains(path, StringComparer.OrdinalIgnoreCase))
                return;

            _items.Add(path);
            Persist();
        }
    }

    public void Remove(string path)
    {
        lock (_gate)
        {
            if (_items.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0)
                Persist();
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
            return [.. _items];
    }

    private void Persist()
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(filePath, JsonSerializer.Serialize(_items));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save the pending queue to {filePath}.", ex);
        }
    }
}
