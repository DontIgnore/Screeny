namespace Screeny.Feedback;

public enum FeedbackFieldError { TooShort, TooLong }

/// <summary>Длины полей из контракта сервера. Считаются по тексту без пробелов по краям.</summary>
public static class FeedbackRules
{
    public const int TitleMin = 3, TitleMax = 120;
    public const int DescriptionMin = 10, DescriptionMax = 5000;
    public const int ContactMax = 200;

    public static FeedbackFieldError? CheckTitle(string? text) => Check(text, TitleMin, TitleMax);

    public static FeedbackFieldError? CheckDescription(string? text) => Check(text, DescriptionMin, DescriptionMax);

    /// <summary>Контакт необязателен: пустой — без ошибки.</summary>
    public static FeedbackFieldError? CheckContact(string? text) => Check(text, 0, ContactMax);

    public static bool IsValid(string? title, string? description, string? contact)
        => CheckTitle(title) is null && CheckDescription(description) is null && CheckContact(contact) is null;

    private static FeedbackFieldError? Check(string? text, int min, int max)
    {
        int length = text?.Trim().Length ?? 0;
        return length < min ? FeedbackFieldError.TooShort : length > max ? FeedbackFieldError.TooLong : null;
    }
}
