using System.Collections.Concurrent;

namespace QuizWeb.Services;

/// <summary>
/// Tracks students currently inside the quiz (started but not yet submitted)
/// so the same name/class/section cannot begin a second session on another
/// computer while the first is still running — the retake guard would otherwise
/// only block at submit time, after the second student has already answered.
/// Entries expire shortly after the quiz timer would have finished, so a
/// crashed or abandoned session does not block a student forever.
/// </summary>
public sealed class ActiveSessions
{
    private readonly ConcurrentDictionary<string, DateTime> _map = new(StringComparer.OrdinalIgnoreCase);

    public bool IsActive(string name, string className, string section, DateTime now)
    {
        string key = Key(name, className, section);
        if (!_map.TryGetValue(key, out DateTime deadline)) return false;
        if (deadline > now) return true;
        _map.TryRemove(key, out _);
        return false;
    }

    public void Register(string name, string className, string section, DateTime deadline)
        => _map[Key(name, className, section)] = deadline;

    public void Remove(string name, string className, string section)
        => _map.TryRemove(Key(name, className, section), out _);

    private static string Key(string name, string className, string section)
        => (name ?? "").Trim() + "\u0001" + (className ?? "").Trim() + "\u0001" + (section ?? "").Trim();
}