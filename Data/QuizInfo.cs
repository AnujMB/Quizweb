namespace QuizWeb.Data;

public class QuizInfo
{
    public string? Class { get; set; }
    public string? Subject { get; set; }
    public string? ExamType { get; set; }

    public bool HasAny =>
        !string.IsNullOrWhiteSpace(Class) ||
        !string.IsNullOrWhiteSpace(Subject) ||
        !string.IsNullOrWhiteSpace(ExamType);
}
