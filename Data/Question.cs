namespace QuizWeb.Data;

public class Question
{
    public required string Text { get; set; }
    public required List<string> Options { get; set; }
    public List<int> CorrectIndices { get; set; } = [];
    public int Number { get; set; }
    public bool IsMultiCorrect => CorrectIndices.Count > 1;
    public int CorrectIndex => CorrectIndices.Count > 0 ? CorrectIndices[0] : -1;
    public string? ImagePath { get; set; }
    public string? Passage { get; set; }
    public string? GroupId { get; set; }
}
