using QuizWeb.Data;

namespace QuizWeb.Services;

/// <summary>
/// Command-line tooling kept from the WinForms app: --import, --export-xlsx,
/// --check. Run these from the folder that holds the data files.
/// </summary>
public static class Cli
{
    public static int RunImport(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: QuizWeb --import <file.csv> [--out <file.txt>]");
            return 2;
        }
        string inPath = args[1];
        string outPath = args.Length >= 4 && args[2] == "--out"
            ? args[3]
            : Path.Combine(Directory.GetCurrentDirectory(), "questions.txt");
        try
        {
            var result = CsvImporter.Import(inPath, outPath);
            Console.WriteLine($"Imported {result.QuestionsWritten} question(s) to {outPath}");
            foreach (string w in result.Warnings)
                Console.WriteLine("WARNING: " + w);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Import failed: " + ex.Message);
            return 1;
        }
    }

    public static int RunCheck(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: QuizWeb --check <file.xlsx>");
            return 2;
        }
        try
        {
            var rows = XlsxReader.ReadGrid(args[1]);
            var parsed = QuestionGridParser.Parse(rows);
            Console.WriteLine($"Valid questions: {parsed.Questions.Count}");
            var info = parsed.Info;
            if (info.HasAny)
                Console.WriteLine($"Quiz info: Class={info.Class} Subject={info.Subject} ExamType={info.ExamType}");
            var groups = parsed.Questions
                .Where(q => q.GroupId != null)
                .GroupBy(q => q.GroupId!);
            foreach (var g in groups)
                Console.WriteLine($"Passage group {g.Key}: {g.Count()} question(s)");
            if (parsed.RowsSkipped > 0)
                Console.WriteLine($"Rows skipped: {parsed.RowsSkipped}");
            foreach (string w in parsed.Warnings)
                Console.WriteLine("WARNING: " + w);
            if (parsed.Questions.Count == 0)
            {
                Console.Error.WriteLine("No valid questions found.");
                return 1;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Check failed: " + ex.Message);
            return 1;
        }
    }

    public static int RunExport(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: QuizWeb --export-xlsx <questions.txt> [--out <questions.xlsx>]");
            return 2;
        }
        string inPath = args[1];
        string outPath = args.Length >= 4 && args[2] == "--out"
            ? args[3]
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(inPath)) ?? ".", "questions.xlsx");
        try
        {
            string[] lines = File.ReadAllLines(inPath);
            var questions = QuestionBank.ParseTxt(lines);
            if (questions.Count == 0)
            {
                Console.Error.WriteLine("No questions found in " + inPath);
                return 1;
            }
            XlsxReader.WriteBank(outPath, questions, QuestionBank.ParseTxtInfo(lines));
            Console.WriteLine($"Exported {questions.Count} question(s) to {outPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Export failed: " + ex.Message);
            return 1;
        }
    }
}
