using System.Text;

namespace QuizWeb.Data;

internal static class CsvImporter
{
    public sealed class Result
    {
        public int QuestionsWritten;
        public int RowsSkipped;
        public List<string> Warnings = new();
    }

    public static Result Import(string csvPath, string outPath)
    {
        var rows = ReadRows(csvPath);
        if (rows.Count == 0)
            throw new InvalidDataException("The CSV file is empty.");

        var parsed = QuestionGridParser.Parse(rows);
        if (parsed.Questions.Count == 0)
            throw new InvalidDataException("No valid questions were found in the CSV file.");

        var lines = new List<string>();
        int qno = 0;
        string? lastGroupId = null;
        foreach (var q in parsed.Questions)
        {
            qno++;
            if (lastGroupId != null && q.GroupId != lastGroupId)
                lines.Add("#EndPassage");
            if (q.Passage != null && q.GroupId != null && q.GroupId != lastGroupId)
                lines.Add("#Passage: " + q.Passage);
            lastGroupId = q.GroupId;
            lines.Add($"#Q{qno}. {q.Text}");
            if (!string.IsNullOrWhiteSpace(q.ImagePath))
                lines.Add($"#Image: {q.ImagePath}");
            lines.Add("#Options#");
            for (int o = 0; o < q.Options.Count; o++)
                lines.Add(q.CorrectIndices.Contains(o) ? q.Options[o] + "**" : q.Options[o]);
            lines.Add("");
        }

        if (File.Exists(outPath))
            File.Copy(outPath, outPath + ".backup", overwrite: true);

        File.WriteAllLines(outPath, lines, new UTF8Encoding(true));

        return new Result
        {
            QuestionsWritten = parsed.Questions.Count,
            RowsSkipped = parsed.RowsSkipped,
            Warnings = parsed.Warnings
        };
    }

    private static List<List<string>> ReadRows(string path) => Csv.ReadRows(path);
}
