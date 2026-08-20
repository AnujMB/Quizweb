using QuizWeb.Data;

namespace QuizWeb.Services;

public sealed class SubmitRequest
{
    public string Name { get; set; } = "";
    public string Class { get; set; } = "";
    public string Section { get; set; } = "";
    public string ComputerName { get; set; } = "";
    public string IPAddress { get; set; } = "";
    /// <summary>Question number (string) -> selected letters, e.g. "B" or "B&D"; empty = not attempted.</summary>
    public Dictionary<string, string> Answers { get; set; } = new();
}

public sealed class SubmitResult
{
    public bool AlreadyTaken { get; set; }
    public double PreviousMarks { get; set; }
    public double Marks { get; set; }
    public int Correct { get; set; }
    public int Wrong { get; set; }
    public int Attempted { get; set; }
    public int Total { get; set; }
    public bool Saved { get; set; }
    public bool SavePending { get; set; }
}

/// <summary>
/// Server-side quiz logic: retake guard, scoring (with negative marking) and
/// result-row composition. Scoring is done here from the question bank so the
/// marks written to the CSV are authoritative.
/// </summary>
public static class QuizEngine
{
    public static double? FindPreviousMarks(string resultPath, ResultStore store,
        string name, string className, string section)
        => ResultFile.FindMarks(resultPath, name, className, section)
            ?? store.FindPendingMarks(name, className, section);

    public static SubmitResult Submit(SubmitRequest req, List<Question> questions,
        QuizInfo info, QuizConfig config, string dataDir, ResultStore store)
    {
        string name = Sanitize(req.Name);
        string className = Sanitize(req.Class);
        string section = Sanitize(req.Section);

        var prev = FindPreviousMarks(config.ResultPath(dataDir), store, name, className, section);
        if (prev.HasValue)
        {
            return new SubmitResult
            {
                AlreadyTaken = true,
                PreviousMarks = prev.Value,
                Total = questions.Count
            };
        }

        var answersByNumber = new Dictionary<int, string>();
        foreach (var kv in req.Answers)
            if (int.TryParse(kv.Key, out int n))
                answersByNumber[n] = kv.Value;

        var (marks, correct, wrong, attempted) = Score(questions, answersByNumber, config.NegativeMarkingPct / 100.0);

        string[] fields = ResultFile.Compose(
            name, className, section,
            info.ExamType, info.Subject, info.Class,
            marks.ToString("F2"),
            DateTime.Now.ToString("MM-dd-yyyy hh:mm"),
            req.ComputerName, req.IPAddress);

        string[] answers = BuildAnswerColumns(questions, answersByNumber);

        if (store.TryAppend(fields, answers))
        {
            return new SubmitResult
            {
                Marks = marks, Correct = correct, Wrong = wrong,
                Attempted = attempted, Total = questions.Count, Saved = true
            };
        }

        store.Save(fields, answers);
        return new SubmitResult
        {
            Marks = marks, Correct = correct, Wrong = wrong,
            Attempted = attempted, Total = questions.Count, Saved = false, SavePending = true
        };
    }

    private static (double Marks, int Correct, int Wrong, int Attempted) Score(
        List<Question> questions, Dictionary<int, string> answers, double negativeMarking)
    {
        int correct = 0;
        int attempted = 0;
        foreach (var q in questions)
        {
            if (!answers.TryGetValue(q.Number, out string? raw) || string.IsNullOrWhiteSpace(raw))
                continue;
            attempted++;
            var sel = ParseLetters(raw);
            if (sel.Count == q.CorrectIndices.Count && sel.SetEquals(q.CorrectIndices))
                correct++;
        }
        int wrong = attempted - correct;
        double marks = correct;
        if (negativeMarking > 0)
            marks = correct - (wrong * negativeMarking);
        return (marks, correct, wrong, attempted);
    }

    private static HashSet<int> ParseLetters(string raw)
    {
        var set = new HashSet<int>();
        foreach (string part in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string t = part.Trim().ToUpperInvariant();
            if (t.Length == 1 && t[0] >= 'A' && t[0] <= 'Z')
                set.Add(t[0] - 'A');
        }
        return set;
    }

    private static string[] BuildAnswerColumns(List<Question> questions, Dictionary<int, string> answers)
    {
        var cols = new string[questions.Count];
        for (int i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            int qno = q.Number > 0 ? q.Number : i + 1;
            if (qno < 1 || qno > cols.Length) continue;
            if (answers.TryGetValue(q.Number, out string? raw) && !string.IsNullOrWhiteSpace(raw))
                cols[qno - 1] = raw;
        }
        return cols;
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        value = value.Trim();
        return new string(value.Where(c =>
            char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '.' || c == '\'').ToArray());
    }
}
