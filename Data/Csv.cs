using System.Text;

namespace QuizWeb.Data;

/// <summary>
/// Minimal, dependency-free RFC-4180-style CSV parser (handles quoted fields,
/// escaped quotes, and CRLF/LF line endings). Used by the CSV importer so the
/// web app has no external package or internet dependency.
/// </summary>
internal static class Csv
{
    public static List<List<string>> ReadRows(string path)
    {
        string text = File.ReadAllText(path, Encoding.UTF8);
        if (text.Length == 0) return new List<List<string>>();
        char delim = DetectDelimiter(text);
        return Parse(text, delim);
    }

    public static char DetectDelimiter(string text)
    {
        string sample = text.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
        char best = ',';
        int bestCount = -1;
        foreach (char d in new[] { ',', ';', '\t' })
        {
            int count = sample.Count(ch => ch == d);
            if (count > bestCount)
            {
                bestCount = count;
                best = d;
            }
        }
        return best;
    }

    public static List<List<string>> Parse(string text, char delim)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == delim)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = new List<string>();
            }
            else if (c != '\r')
            {
                field.Append(c);
            }
            i++;
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
