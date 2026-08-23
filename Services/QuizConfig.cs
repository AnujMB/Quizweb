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
    public bool AllowAnswerDetails { get; private set; } = true;
    public int? Port { get; private set; }
    public string QuizFolder { get; private set; } = "";
    public string AdminPasswordHash { get; private set; } = "";

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
            else if (key.Equals("allowAnswerDetails", StringComparison.OrdinalIgnoreCase))
            {
                c.AllowAnswerDetails = !IsFalseValue(val);
            }
            else if (key.Equals("Port", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(val, out int port) && port is > 0 and < 65536)
            {
                c.Port = port;
            }
            else if (key.Equals("quizFolder", StringComparison.OrdinalIgnoreCase)
                  || key.Equals("folder", StringComparison.OrdinalIgnoreCase)
                  || key.Equals("dataFolder", StringComparison.OrdinalIgnoreCase))
            {
                string sanitized = SanitizeFolderName(val);
                if (!string.IsNullOrWhiteSpace(sanitized))
                    c.QuizFolder = sanitized;
            }
            else if (key.Equals("adminPassword", StringComparison.OrdinalIgnoreCase) && val.Length > 0)
            {
                c.AdminPasswordHash = ComputeHash(val);
            }
            else if (key.Equals("adminPasswordHash", StringComparison.OrdinalIgnoreCase) && val.Length > 0)
            {
                c.AdminPasswordHash = val.Trim().ToLowerInvariant();
            }
        }
        return c;
    }

    private static bool IsFalseValue(string val)
        => val.Trim().ToLowerInvariant() is "false" or "0" or "no" or "off";

    private static string SanitizeFolderName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        raw = raw.Trim().TrimStart('/', '\\');
        int slash = raw.IndexOfAny(new[] { '/', '\\' });
        if (slash >= 0) raw = raw.Substring(0, slash);
        raw = raw.Trim();
        if (raw == "." || raw == "..") return "";
        var sb = new System.Text.StringBuilder();
        foreach (char ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == ' ')
                sb.Append(ch);
            // drop everything else (including '.' to prevent hidden folders)
        }
        string s = sb.ToString().Trim();
        if (s.Length > 50) s = s.Substring(0, 50).Trim();
        if (s == "." || s == "..") return "";
        return s;
    }

    public static string ComputeHash(string input)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(input);
        byte[] hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public bool VerifyAdminPassword(string password)
    {
        if (string.IsNullOrEmpty(AdminPasswordHash)) return false;
        return ComputeHash(password) == AdminPasswordHash;
    }
}
