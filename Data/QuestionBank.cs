using System.Text;
using System.Text.RegularExpressions;

namespace QuizWeb.Data;

internal sealed class BankLoadResult
{
    public bool Found;
    public string? Error;
    public string Source = "";
    public List<Question> Questions = new();
    public List<string> Warnings = new();
    public QuizInfo Info = new();
}

internal static class QuestionBank
{
    public static BankLoadResult Load(string dir)
    {
        string xlsx = Path.Combine(dir, "questions.xlsx");
        if (File.Exists(xlsx))
        {
            try
            {
                var rows = XlsxReader.ReadGrid(xlsx);
                var parsed = QuestionGridParser.Parse(rows);
                return new BankLoadResult
                {
                    Found = true,
                    Source = "questions.xlsx",
                    Questions = parsed.Questions,
                    Warnings = parsed.Warnings,
                    Info = parsed.Info
                };
            }
            catch (Exception ex)
            {
                return new BankLoadResult { Found = false, Error = "Failed to read questions.xlsx:\n" + ex.Message };
            }
        }

        string txt = Path.Combine(dir, "questions.txt");
        if (File.Exists(txt))
        {
            string[] lines = File.ReadAllLines(txt);
            return new BankLoadResult
            {
                Found = true,
                Source = "questions.txt",
                Questions = ParseTxt(lines),
                Info = ParseTxtInfo(lines)
            };
        }

        return new BankLoadResult
        {
            Found = false,
            Error = "No questions file found. Add questions.xlsx (or questions.txt) to the application folder."
        };
    }

    public static QuizInfo ParseTxtInfo(string[] lines)
    {
        var info = new QuizInfo();
        for (int i = 0; i < lines.Length && i < 8; i++)
        {
            // string line = lines[i].Trim();
            string line = lines[i];
            if (line.StartsWith("#Q")) break;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string key = line[..colon].Trim().TrimStart('#').ToLowerInvariant();
            string value = line[(colon + 1)..].Trim();
            if (value.Length == 0) continue;

            if (key.Contains("subject")) info.Subject = value;
            else if (key.Contains("class")) info.Class = value;
            else if (key.Contains("exam") || key.Contains("term") || key.Contains("type")) info.ExamType = value;
        }
        return info;
    }

    public static List<Question> ParseTxt(string[] lines)
    {
        var questions = new List<Question>();

        var groupAtLine = new Dictionary<int, (string? Passage, string? GroupId)>();
        string? passage = null;
        string? groupId = null;
        int groupCounter = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            //var line = lines[i].Trim();
            //var line = lines[i].TrimEnd();
            var line = lines[i];
            if (Regex.IsMatch(line, @"^#EndPassage", RegexOptions.IgnoreCase))
            {
                passage = null;
                groupId = null;
                groupAtLine[i] = (null, null);
                continue;
            }
            var passageMatch = Regex.Match(line, @"^#Passage:\s*(.*)$", RegexOptions.IgnoreCase);
            if (passageMatch.Success)
            {
                groupCounter++;
                groupId = "P" + groupCounter;
                var sb = new StringBuilder(passageMatch.Groups[1].Value.Trim());
                for (int k = i + 1; k < lines.Length; k++)
                {
                    var inner = lines[k].Trim();
                    if (Regex.IsMatch(inner, @"^#Q\d+") || Regex.IsMatch(inner, @"^#Passage:", RegexOptions.IgnoreCase)
                        || Regex.IsMatch(inner, @"^#EndPassage", RegexOptions.IgnoreCase) || inner == "#Options#")
                        break;
                    if (inner.Length > 0)
                        sb.Append(Environment.NewLine).Append(inner);
                }
                passage = sb.ToString();
            }
            groupAtLine[i] = (passage, groupId);
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var match = Regex.Match(lines[i].Trim(), @"^#Q(\d+)\.\s*(.*)");
            if (!match.Success) continue;

            string? imagePath = null;
            var questionParts = new List<string>();

            var sameLine = match.Groups[2].Value.Trim();
            var sameLineImg = Regex.Match(sameLine, @"^#Image:\s*(.*)$");
            if (sameLineImg.Success)
            {
                imagePath = sameLineImg.Groups[1].Value.Trim();
                sameLine = "";
            }
            if (sameLine.Length > 0)
                questionParts.Add(sameLine);

            var options = new List<string>();
            var correctIndices = new List<int>();

            int optIdx = -1;
            for (int j = i + 1; j < lines.Length; j++)
            {
                string line = lines[j].Trim();
                if (line == "#Options#")
                {
                    optIdx = j;
                    break;
                }
                var imgMatch = Regex.Match(line, @"^#Image:\s*(.*)$");
                if (imgMatch.Success)
                {
                    imagePath = imgMatch.Groups[1].Value.Trim();
                    continue;
                }
                if (Regex.IsMatch(line, @"^#Q\d+"))
                    break;
                if (line.Length > 0)
                    questionParts.Add(line);
            }

            if (optIdx == -1) continue;

            var questionText = string.Join(Environment.NewLine, questionParts);

            int optCount = 0;
            for (int j = optIdx + 1; j < lines.Length; j++)
            {
                var line = lines[j].Trim();
                if (line.Length == 0) continue;
                if (Regex.IsMatch(line, @"^#Q\d+") || line == "#Options#"
                    || Regex.IsMatch(line, @"^#Passage:", RegexOptions.IgnoreCase)
                    || Regex.IsMatch(line, @"^#EndPassage", RegexOptions.IgnoreCase)) break;

                if (line.EndsWith("**"))
                {
                    options.Add(line[..^2].Trim());
                    correctIndices.Add(optCount);
                }
                else
                {
                    options.Add(line);
                }
                optCount++;
            }

            if (options.Count >= 2 && correctIndices.Count > 0)
            {
                var (qPassage, qGroupId) = groupAtLine.TryGetValue(i, out var grp) ? grp : (null, null);
                questions.Add(new Question
                {
                    Number = int.TryParse(match.Groups[1].Value, out int qno) ? qno : questions.Count + 1,
                    Text = questionText,
                    Options = options,
                    CorrectIndices = correctIndices,
                    ImagePath = imagePath,
                    Passage = qPassage,
                    GroupId = qGroupId
                });
            }
        }

        return questions;
    }
}
