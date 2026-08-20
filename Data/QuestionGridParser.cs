namespace QuizWeb.Data;

internal sealed class GridParseResult
{
    public List<Question> Questions = new();
    public List<string> Warnings = new();
    public int RowsSkipped;
    public QuizInfo Info = new();
}

internal static class QuestionGridParser
{
    public static GridParseResult Parse(List<List<string>> rows)
    {
        var result = new GridParseResult();
        if (rows.Count == 0) return result;

        int headerRow = FindHeaderRow(rows);
        var map = headerRow >= 0 ? BuildMapFromHeader(rows[headerRow]) : BuildFixedMap();
        int startRow = headerRow >= 0 ? headerRow + 1 : 0;
        result.Info = headerRow >= 0 ? ParseInfoAboveHeader(rows, headerRow) : new QuizInfo();

        string? passage = null;
        string? groupId = null;
        var passageGroups = new Dictionary<string, string>(StringComparer.Ordinal);

        for (int r = startRow; r < rows.Count; r++)
        {
            var cells = rows[r];
            if (cells.All(string.IsNullOrWhiteSpace)) continue;

            string cellPassage = Get(cells, map.Passage);
            if (!string.IsNullOrWhiteSpace(cellPassage))
            {
                passage = cellPassage.Trim();
                if (!passageGroups.TryGetValue(passage, out var gid))
                {
                    gid = "P" + (passageGroups.Count + 1);
                    passageGroups[passage] = gid;
                }
                groupId = gid;
            }
            else
            {
                passage = null;
                groupId = null;
            }

            string question = Get(cells, map.Question);
            if (string.IsNullOrWhiteSpace(question))
            {
                result.RowsSkipped++;
                result.Warnings.Add($"Row {r + 1}: no question text, skipped.");
                continue;
            }

            string image = Get(cells, map.Image);
            var options = new List<string>();
            foreach (int idx in map.Options)
            {
                string opt = Get(cells, idx);
                if (!string.IsNullOrWhiteSpace(opt)) options.Add(opt.Trim());
            }
            if (options.Count < 2)
            {
                result.RowsSkipped++;
                result.Warnings.Add($"Row {r + 1}: question needs at least 2 options, skipped.");
                continue;
            }

            var correct = ParseCorrect(Get(cells, map.Correct), options.Count);
            if (correct.Count == 0)
            {
                correct.Add(0);
                result.Warnings.Add($"Row {r + 1}: no valid correct answer, defaulted to Option 1.");
            }

            if (!string.IsNullOrWhiteSpace(Get(cells, map.Correct)))
            {
                foreach (int idx in map.Options)
                {
                    string t = Get(cells, idx);
                    if (t.Length == 1 && t[0] >= 'A' && t[0] <= 'Z')
                    {
                        result.Warnings.Add($"Row {r + 1}: option '{t}' looks like a leftover answer letter. " +
                            "Separate multiple answers in the Correct column with '&' (e.g. B&D), not commas.");
                        break;
                    }
                }
            }

            result.Questions.Add(new Question
            {
                Number = result.Questions.Count + 1,
                Text = question.Trim(),
                Options = options,
                CorrectIndices = correct,
                ImagePath = image.Length > 0 ? image.Trim() : null,
                Passage = passage,
                GroupId = groupId
            });
        }

        return result;
    }

    private static int FindHeaderRow(List<List<string>> rows)
    {
        int limit = Math.Min(rows.Count, 8);
        for (int i = 0; i < limit; i++)
            if (HasHeader(rows[i])) return i;
        return -1;
    }

    private static QuizInfo ParseInfoAboveHeader(List<List<string>> rows, int headerRow)
    {
        var info = new QuizInfo();
        for (int r = 0; r < headerRow; r++)
        {
            var cells = rows[r];
            if (cells.Count == 0 || string.IsNullOrWhiteSpace(cells[0])) continue;

            string key = cells[0].Trim();
            string value = "";
            int colon = key.IndexOf(':');
            if (colon > 0)
            {
                value = key[(colon + 1)..].Trim();
                key = key[..colon].Trim();
            }
            else if (cells.Count > 1)
            {
                value = cells[1].Trim();
            }
            if (value.Length == 0) continue;

            string k = key.ToLowerInvariant();
            if (k.Contains("subject")) info.Subject = value;
            else if (k.Contains("class")) info.Class = value;
            else if (k.Contains("exam") || k.Contains("term") || k.Contains("type")) info.ExamType = value;
        }
        return info;
    }

    internal static string CorrectAsText(List<int> indices)
    {
        var letters = indices.Select(i => ((char)('A' + i)).ToString());
        return string.Join("&", letters);
    }

    private sealed class ColumnMap
    {
        public int Question = -1;
        public int Image = -1;
        public int Correct = -1;
        public int Passage = -1;
        public List<int> Options = new();
    }

    private static ColumnMap BuildFixedMap()
    {
        var map = new ColumnMap
        {
            Question = 1,
            Image = 2,
            Correct = 3
        };
        for (int i = 4; i < 30; i++) map.Options.Add(i);
        return map;
    }

    private static ColumnMap BuildMapFromHeader(List<string> header)
    {
        var map = new ColumnMap();
        for (int i = 0; i < header.Count; i++)
        {
            string h = header[i].Trim().ToLowerInvariant();
            if (h.Contains("question") || (h.Contains("text") && !h.Contains("option")))
                map.Question = i;
            else if (h.Contains("image") || h.Contains("picture") || h.Contains("figure") || h.Contains("pic "))
                map.Image = i;
            else if (h.Contains("passage") || h.Contains("paragraph"))
                map.Passage = i;
            else if (h.Contains("correct") || h.Contains("answer") || h.Contains("key"))
                map.Correct = i;
            else if (h.Contains("opt") || h.Contains("choice"))
                map.Options.Add(i);
        }

        if (map.Question == -1) map.Question = 0;
        if (map.Options.Count == 0)
        {
            for (int i = 0; i < header.Count; i++)
                if (i != map.Question && i != map.Image && i != map.Correct)
                    map.Options.Add(i);
        }
        return map;
    }

    private static bool HasHeader(List<string> firstRow)
    {
        return firstRow.Any(c =>
        {
            string h = c.Trim().ToLowerInvariant();
            return h.Contains("question") || h.Contains("option") || h.Contains("choice")
                || h.Contains("correct") || h.Contains("answer") || h.Contains("image");
        });
    }

    private static List<int> ParseCorrect(string? raw, int optionCount)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        foreach (string token in raw.Split(new[] { ',', ';', ' ', '&', '|' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = token.Trim();
            if (t.Length == 0) continue;
            int idx = -1;
            char c = char.ToUpperInvariant(t[0]);
            if (c >= 'A' && c <= 'Z') idx = c - 'A';
            else if (c >= '1' && c <= '9') idx = c - '1';
            if (idx >= 0 && idx < optionCount && !result.Contains(idx))
                result.Add(idx);
        }
        result.Sort();
        return result;
    }

    private static string Get(List<string> cells, int idx)
        => idx >= 0 && idx < cells.Count ? (cells[idx] ?? "").Trim() : "";
}
