namespace QuizWeb.Services;

/// <summary>
/// Loads config.txt (same format as the WinForms app):
/// Time, NegativeMarking, resultFile, allowResultViewing, allowImport.
/// Also reads an optional "Port" key so a teacher can pick the LAN port
/// without touching launch profiles.
/// </summary>
public sealed class QuizConfig
{
    public int TimeMinutes { get; private set; } = 25;
    public double NegativeMarkingPct { get; private set; }
    public string ResultFile { get; private set; } = "Result.txt";
    public bool AllowResultViewing { get; private set; } = true;
    public bool AllowImport { get; private set; } = true;
    public bool AllowReview { get; private set; } = true;
    public int? Port { get; private set; }

    public string ResultPath(string dataDir)
        => Path.IsPathRooted(ResultFile) ? ResultFile : Path.Combine(dataDir, ResultFile);

    public static QuizConfig Load(string dataDir)
    {
        var c = new QuizConfig();
        string path = Path.Combine(dataDir, "config.txt");
        if (!File.Exists(path)) return c;

        foreach (var raw in File.ReadAllLines(path))
        {
            var parts = raw.Split('=', 2);
            if (parts.Length != 2) continue;
            string key = parts[0].Trim();
            string val = parts[1].Trim();

            if (key.Equals("Time", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(val, out int minutes) && minutes > 0)
            {
                c.TimeMinutes = minutes;
            }
            else if (key.Equals("NegativeMarking", StringComparison.OrdinalIgnoreCase))
            {
                if (val.EndsWith('%')) val = val[..^1];
                if (double.TryParse(val, out double nm) && nm >= 0)
                    c.NegativeMarkingPct = nm;
            }
            else if (key.Equals("resultFile", StringComparison.OrdinalIgnoreCase) && val.Length > 0)
            {
                c.ResultFile = val;
            }
            else if (key.Equals("allowResultViewing", StringComparison.OrdinalIgnoreCase))
            {
                c.AllowResultViewing = !IsFalseValue(val);
            }
            else if (key.Equals("allowImport", StringComparison.OrdinalIgnoreCase))
            {
                c.AllowImport = !IsFalseValue(val);
            }
            else if (key.Equals("allowReview", StringComparison.OrdinalIgnoreCase))
            {
                c.AllowReview = !IsFalseValue(val);
            }
            else if (key.Equals("Port", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(val, out int port) && port is > 0 and < 65536)
            {
                c.Port = port;
            }
        }
        return c;
    }

    private static bool IsFalseValue(string val)
        => val.Trim().ToLowerInvariant() is "false" or "0" or "no" or "off";
}
