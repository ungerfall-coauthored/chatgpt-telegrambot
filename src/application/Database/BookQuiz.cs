using System;
using System.Linq;

namespace Ungerfall.ChatGpt.TelegramBot.Database;

public sealed class BookQuiz
{
    public const string Series = "linux-programming-interface";
    public const string BookTitle = "The Linux Programming Interface";

    public string Id { get; set; } = "";
    public long ChatId { get; set; }
    public string Type { get; set; } = "book-quiz";
    public string SeriesId { get; set; } = Series;
    public int Sequence { get; set; }
    public int Chapter { get; set; }
    public string Section { get; set; } = "";
    public int PrintedPage { get; set; }
    public int PdfPage { get; set; }
    public string Question { get; set; } = "";
    public string[] Options { get; set; } = [];
    public int CorrectOptionId { get; set; }
    public string Explanation { get; set; } = "";
    public string? ImageReference { get; set; }
    public string? ImageDescription { get; set; }
    public int Ttl { get; set; } = -1;

    public static string QuizId(int sequence) => $"book-quiz:{Series}:{sequence:D6}";

    public string AnswerExplanation() => $"{Explanation}\n{BookTitle}, гл. {Chapter}, с. {PrintedPage}.";

    public void Validate()
    {
        if (Type != "book-quiz" || SeriesId != Series || Ttl != -1 || Sequence <= 0 || Chapter is < 1 or > 64 || PrintedPage <= 0 || PdfPage <= 0
            || SectionParts() is not { Length: >= 2 and <= 3 } parts || parts[0] != Chapter)
            throw new ArgumentException("Invalid series, sequence, chapter, section, or page.");
        if (string.IsNullOrWhiteSpace(Question) || Question.Length > 300)
            throw new ArgumentException("Question must have 1–300 characters.");
        if (Options == null || Options.Length is < 4 or > 8
            || Options.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 100)
            || Options.Distinct(StringComparer.Ordinal).Count() != Options.Length
            || CorrectOptionId < 0 || CorrectOptionId >= Options.Length)
            throw new ArgumentException("Quiz must have 4–8 distinct options and a valid correct option.");
        if (string.IsNullOrWhiteSpace(Explanation) || AnswerExplanation().Length > 200
            || AnswerExplanation().Count(c => c == '\n') > 2)
            throw new ArgumentException("Explanation and citation must fit Telegram's poll explanation.");
        if ((ImageReference == null) != (ImageDescription == null)
            || (ImageReference != null && (string.IsNullOrWhiteSpace(ImageReference) || string.IsNullOrWhiteSpace(ImageDescription)))
            || ImageDescription?.Length > 900)
            throw new ArgumentException("Images require both a Telegram file ID or URL and a short description.");
    }

    public static void ValidateBatch(BookQuiz[] quizzes)
    {
        if (quizzes.Length == 0) throw new ArgumentException("The book quiz batch is empty.");
        for (var i = 0; i < quizzes.Length; i++)
        {
            quizzes[i].Validate();
            if (i > 0 && (quizzes[i].Sequence != quizzes[i - 1].Sequence + 1
                || quizzes[i].PdfPage < quizzes[i - 1].PdfPage
                || CompareSections(quizzes[i - 1].SectionParts()!, quizzes[i].SectionParts()!) > 0))
                throw new ArgumentException("Book quizzes must be contiguous and in book order.");
        }
    }

    private int[]? SectionParts()
    {
        if (string.IsNullOrWhiteSpace(Section)) return null;
        var segments = Section.Split('.');
        var parts = new int[segments.Length];
        for (var i = 0; i < segments.Length; i++)
            if (!int.TryParse(segments[i], out parts[i]) || parts[i] <= 0) return null;
        return parts;
    }

    private static int CompareSections(int[] left, int[] right)
    {
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var comparison = left[i].CompareTo(right[i]);
            if (comparison != 0) return comparison;
        }
        return left.Length.CompareTo(right.Length);
    }
}