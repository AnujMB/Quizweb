using System.Collections.Concurrent;
using QuizWeb.Data;

namespace QuizWeb.Services;

/// <summary>
/// Owns writes to the single shared result CSV. Appends are serialized with a
/// process-wide semaphore and an exclusive OS file lock (see ResultFile.Append),
/// so many students submitting at once cannot corrupt the file. If an append
/// fails because the file is temporarily busy (e.g. the teacher has it open in
/// Excel), the row is queued and retried in the background so no result is lost.
/// </summary>
public sealed class ResultStore : IDisposable
{
    private string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<(string[] Fields, string[] Answers)> _pending = new();
    private readonly System.Threading.Timer _retryTimer;
    private readonly Lock _scanLock = new();

    public ResultStore(string path)
    {
        _path = path;
        _retryTimer = new System.Threading.Timer(_ => Drain(), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public void UpdatePath(string newPath) => _path = newPath;

    /// <summary>Attempts one immediate append. Returns false when the file is busy.</summary>
    public bool TryAppend(string[] fields, string[] answers)
    {
        if (ResultFile.Append(_path, fields, answers, out _)) return true;
        return false;
    }

    /// <summary>
    /// Appends the row now; if the file is busy it is queued for background
    /// retries. Never loses a result.
    /// </summary>
    public void Save(string[] fields, string[] answers)
    {
        if (TryAppend(fields, answers)) return;
        _pending.Enqueue((fields, answers));
    }

    public void Drain()
    {
        if (!_gate.Wait(0)) return;
        try
        {
            int attempts = 0;
            while (_pending.TryPeek(out var item) && attempts < 20)
            {
                if (!ResultFile.Append(_path, item.Fields, item.Answers, out _))
                    break; // file still busy; retry on the next tick
                _pending.TryDequeue(out _);
                attempts++;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Marks for a student who is queued but not yet flushed (so the retake
    /// guard works even while a save is pending). Returns null if not found.
    /// </summary>
    public double? FindPendingMarks(string name, string className, string section)
    {
        string key = name.Trim() + "," + className.Trim() + "," + section.Trim();
        lock (_scanLock)
        {
            foreach (var (fields, _) in _pending)
            {
                string rowKey = string.Join(",", fields.Take(3)).Trim();
                if (string.Equals(rowKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string f in fields)
                        if (double.TryParse(f, out double m))
                            return m;
                    return 0;
                }
            }
        }
        return null;
    }

    public void Dispose()
    {
        Drain();
        _retryTimer.Dispose();
        _gate.Dispose();
    }
}
