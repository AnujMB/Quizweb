using System.IO;
using System.Text;

namespace QuizWeb.Data;

/// <summary>
/// Writes student results to a single shared CSV file in a way that stays
/// correct when many students submit at the same time (end-of-exam stampede).
///
/// Each append is done while holding an exclusive OS file lock
/// (FileAccess.Write + FileShare.Read). Because the lock is enforced by the
/// OS at the file level, it serializes writers even across different machines
/// writing to the same network share. Writers that cannot get the lock return
/// false immediately; the caller retries in the background until it succeeds,
/// so no result is ever lost. The header row is written only while holding the
/// lock and only when the file is empty, so it cannot be duplicated by two
/// simultaneous writers.
/// </summary>
internal static class ResultFile
{
    private const string BaseHeader =
        "Name,Class,Section,Exam Type,Subject,Quiz Class,Marks obtained,Exam date,ComputerName,IPAddress";

    public static string[] Compose(string name, string className, string section,
        string? examType, string? subject, string? quizClass, string marks, string date,
        string computerName = "", string ipAddress = "")
    {
        return new[]
        {
            Sanitize(name), Sanitize(className), Sanitize(section),
            Sanitize(examType ?? ""), Sanitize(subject ?? ""), Sanitize(quizClass ?? ""),
            Sanitize(marks), Sanitize(date),
            Sanitize(computerName), Sanitize(ipAddress)
        };
    }

    /// <summary>
    /// Strips characters that would corrupt the CSV layout (commas, newlines,
    /// carriage returns, tabs). Extra safety net on top of the input filtering.
    /// </summary>
    private static string Sanitize(string value)
    {
        if (value.IndexOfAny(new[] { ',', '\r', '\n', '\t' }) < 0)
            return value.Trim();
        return new string(value.Where(c => c != ',' && c != '\r' && c != '\n' && c != '\t').ToArray()).Trim();
    }

    /// <summary>
    /// Appends one result row under an exclusive file lock.
    /// The <paramref name="answers"/> array holds the student's selected options
    /// (letters such as "B" or "B&D", or "" when not attempted) for each question
    /// numbered 1..n. When the file is empty, a header row with "Q1".."Qn"
    /// columns is written first. If an existing file still has an older header
    /// (no Q columns, or a different Q count), the file is migrated to the new
    /// schema under the same lock: header rewritten, all existing rows preserved.
    /// Returns false (never throws) when the file is temporarily locked by
    /// another writer or the OS; the caller should retry.
    /// </summary>
    public static bool Append(string path, string[] fields, string[] answers, out string error)
    {
        error = "";
        try
        {
            string newHeader = BaseHeader;
            if (answers.Length > 0)
                newHeader += "," + string.Join(",", Enumerable.Range(1, answers.Length).Select(i => "Q" + i));
            string row = string.Join(",", fields);
            if (answers.Length > 0)
                row += "," + string.Join(",", answers);

            using (var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
            {
                if (fs.Length == 0)
                {
                    WriteLine(fs, newHeader);
                    WriteLine(fs, row);
                    return true;
                }

                fs.Position = 0;
                string existing;
                using (var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                    bufferSize: 4096, leaveOpen: true))
                    existing = sr.ReadToEnd();

                string[] lines = existing.Replace("\r", "").TrimEnd('\n').Split('\n');
                if (lines.Length > 0 && HeaderSchemaMatches(lines[0], answers.Length))
                {
                    WriteLine(fs, row);
                }
                else
                {
                    fs.SetLength(0);
                    fs.Position = 0;
                    WriteLine(fs, newHeader);
                    for (int i = 1; i < lines.Length; i++)
                    {
                        string l = lines[i].Trim();
                        if (l.Length > 0)
                            WriteLine(fs, l);
                    }
                    WriteLine(fs, row);
                }
            }
            return true;
        }
        catch (IOException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool HeaderSchemaMatches(string header, int answerCount)
    {
        int qCount = 0;
        foreach (string col in header.Split(','))
        {
            string h = col.Trim();
            if (h.Length > 1 && h[0] is 'Q' or 'q' && int.TryParse(h.AsSpan(1), out _))
                qCount++;
        }
        return qCount == answerCount;
    }

    private static void WriteLine(Stream fs, string line)
    {
        byte[] b = Encoding.UTF8.GetBytes(line + Environment.NewLine);
        fs.Write(b, 0, b.Length);
    }

    /// <summary>
    /// Finds the stored marks for a student (same name/class/section).
    /// Returns null when the student has not taken the test yet.
    /// The marks column is located from the header row, so it is robust to
    /// column reordering.
    /// </summary>
    public static double? FindMarks(string path, string name, string className, string section)
    {
        string key = name.Trim() + "," + className.Trim() + "," + section.Trim() + ",";
        try
        {
            string[] lines;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
                lines = sr.ReadToEnd().Replace("\r", "").Split('\n');

            int marksCol = 6;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (i == 0 && line.StartsWith("Name", StringComparison.Ordinal))
                {
                    marksCol = FindColumn(line, "Marks");
                    continue;
                }
                if (line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = line.Split(',');
                    if (parts.Length > marksCol && double.TryParse(parts[marksCol], out double m))
                        return m;
                }
            }
            return null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int FindColumn(string header, string columnStartsWith)
    {
        string[] cols = header.Split(',');
        for (int i = 0; i < cols.Length; i++)
        {
            if (cols[i].Trim().StartsWith(columnStartsWith, StringComparison.Ordinal))
                return i;
        }
        return 6;
    }

    /// <summary>
    /// Reads a result CSV back into structured rows. Column positions come from
    /// the header (which also carries the "Q1".."Qn" answer columns), so rows
    /// with fewer answer columns than the header are handled gracefully.
    /// </summary>
    public static List<StudentResult> LoadResults(string path)
    {
        var list = new List<StudentResult>();
        if (!File.Exists(path)) return list;

        string[] lines;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(fs, Encoding.UTF8))
            lines = sr.ReadToEnd().Replace("\r", "").Split('\n');
        if (lines.Length == 0) return list;

        string[] header = lines[0].Split(',');
        int colName = FindColumn(lines[0], "Name");
        int colClass = FindColumn(lines[0], "Class");
        int colSection = FindColumn(lines[0], "Section");
        int colExam = FindColumn(lines[0], "Exam Type");
        int colSubject = FindColumn(lines[0], "Subject");
        int colQClass = FindColumn(lines[0], "Quiz Class");
        int colMarks = FindColumn(lines[0], "Marks");
        int colDate = FindColumn(lines[0], "Exam date");
        int colComputer = FindColumn(lines[0], "ComputerName");
        int colIP = FindColumn(lines[0], "IPAddress");
        var qCols = new List<(int Col, int Number)>();
        for (int i = 0; i < header.Length; i++)
        {
            string h = header[i].Trim();
            if (h.Length > 1 && h[0] is 'Q' or 'q' && int.TryParse(h.AsSpan(1), out int qno))
                qCols.Add((i, qno));
        }

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',');
            if (colName < 0 || colName >= parts.Length && parts.Length < 8) continue;

            StudentResult s;
            if (parts.Length >= 8)
            {
                // New-schema row: 10 base fields in fixed order, then Q1..Qn by position.
                s = new StudentResult
                {
                    Name = parts[0].Trim(),
                    Class = parts[1].Trim(),
                    Section = parts[2].Trim(),
                    ExamType = parts[3].Trim(),
                    Subject = parts[4].Trim(),
                    QuizClass = parts[5].Trim(),
                    Date = parts.Length > 7 ? parts[7].Trim() : "",
                    ComputerName = parts.Length > 8 ? parts[8].Trim() : "",
                    IPAddress = parts.Length > 9 ? parts[9].Trim() : ""
                };
                if (double.TryParse(parts[6], out double m)) s.Marks = m;
                for (int col = 10; col < parts.Length; col++)
                    if (parts[col].Trim().Length > 0)
                        s.Answers[col - 9] = parts[col].Trim();
            }
            else
            {
                // Legacy row: the old app always wrote exactly 6 fields
                // (Name,Class,Section,Subject,Marks obtained,Exam date), so a
                // 6-column row maps by position regardless of the current header.
                if (parts.Length == 6)
                {
                    s = new StudentResult
                    {
                        Name = parts[0].Trim(),
                        Class = parts[1].Trim(),
                        Section = parts[2].Trim(),
                        Subject = parts[3].Trim(),
                        Date = parts[5].Trim()
                    };
                    if (double.TryParse(parts[4], out double m)) s.Marks = m;
                }
                else
                {
                    // Other short rows: map base fields by the header that was present.
                    if (colName < 0 || colName >= parts.Length) continue;
                    s = new StudentResult
                    {
                        Name = parts[colName].Trim(),
                        Class = colClass >= 0 && colClass < parts.Length ? parts[colClass].Trim() : "",
                        Section = colSection >= 0 && colSection < parts.Length ? parts[colSection].Trim() : "",
                        ExamType = colExam >= 0 && colExam < parts.Length ? parts[colExam].Trim() : "",
                        Subject = colSubject >= 0 && colSubject < parts.Length ? parts[colSubject].Trim() : "",
                        QuizClass = colQClass >= 0 && colQClass < parts.Length ? parts[colQClass].Trim() : "",
                        Date = colDate >= 0 && colDate < parts.Length ? parts[colDate].Trim() : "",
                        ComputerName = colComputer >= 0 && colComputer < parts.Length ? parts[colComputer].Trim() : "",
                        IPAddress = colIP >= 0 && colIP < parts.Length ? parts[colIP].Trim() : ""
                    };
                    if (colMarks >= 0 && colMarks < parts.Length && double.TryParse(parts[colMarks], out double m))
                        s.Marks = m;
                    foreach (var (col, qno) in qCols)
                        if (col < parts.Length && parts[col].Trim().Length > 0)
                            s.Answers[qno] = parts[col].Trim();
                }
            }
            list.Add(s);
        }
        return list;
    }
}

/// <summary>One student row read back from the result CSV.</summary>
internal sealed class StudentResult
{
    public string Name = "";
    public string Class = "";
    public string Section = "";
    public string ExamType = "";
    public string Subject = "";
    public string QuizClass = "";
    public double Marks;
    public string Date = "";
    public string ComputerName = "";
    public string IPAddress = "";
    /// <summary>Question number -> selected option letters (e.g. "B" or "B&D").</summary>
    public Dictionary<int, string> Answers = new();

    public override string ToString()
        => $"{Name}  |  Class {Class}  |  Section {Section}  |  Marks: {Marks:F2}";
}
