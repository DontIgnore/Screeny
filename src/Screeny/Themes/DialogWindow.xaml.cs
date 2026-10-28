using System.Windows;

namespace Screeny.Themes;

/// <summary>Модальный диалог в стиле приложения вместо системного MessageBox.</summary>
public partial class DialogWindow : ChromeWindow
{
    /// <param name="confirmLabel">Текст основной кнопки.</param>
    /// <param name="cancelLabel">Текст кнопки отмены; null — диалог с одной кнопкой (информация).</param>
    /// <param name="danger">Основная кнопка красная: необратимое действие.</param>
    public DialogWindow(string title, string message, string confirmLabel, string? cancelLabel, bool danger)
    {
        InitializeComponent();
        HeadingText.Text = title;
        MessageText.Text = message;
        MessageText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        ConfirmButton.Content = confirmLabel;
        if (danger) ConfirmButton.SetResourceReference(StyleProperty, "DangerButton");
        if (cancelLabel is null)
        {
            CancelButton.Visibility = Visibility.Collapsed;
            ConfirmButton.Margin = new Thickness(0);
            ConfirmButton.IsCancel = true; // Esc закрывает и информационный диалог
        }
        else CancelButton.Content = cancelLabel;
        // Для опасного действия фокус на «Отмене»: случайный Enter ничего не удаляет.
        Loaded += (_, _) => (danger && cancelLabel is not null ? CancelButton : ConfirmButton).Focus();
        if (danger && cancelLabel is not null) { ConfirmButton.IsDefault = false; CancelButton.IsDefault = true; }
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}

/// <summary>Короткие обёртки над <see cref="DialogWindow"/>.</summary>
public static class Dialogs
{
    /// <summary>Подтверждение; true — пользователь выбрал основное действие. Без <paramref name="cancelLabel"/> — «Отмена».</summary>
    public static bool Confirm(Window? owner, string title, string text, string confirmLabel, bool danger = false, string? cancelLabel = null)
        => Show(owner, new DialogWindow(title, text, confirmLabel, cancelLabel ?? Localization.Loc.T("Common.Cancel"), danger));

    public static void Info(Window? owner, string title, string text)
        => Show(owner, new DialogWindow(title, text, Localization.Loc.T("Common.OK"), cancelLabel: null, danger: false));

    private static bool Show(Window? owner, DialogWindow dialog)
    {
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true;
    }
}
