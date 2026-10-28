using Screeny.Logging;
using Screeny.Settings;
using Velopack;
using Velopack.Locators;

namespace Screeny;

/// <summary>
/// Точка входа вместо сгенерированного WPF Main (<c>StartupObject</c> в csproj): Velopack требует, чтобы
/// <see cref="VelopackApp"/> выполнился первым — при установке, обновлении и удалении Update.exe запускает Screeny.exe
/// с особыми ключами, и <c>Run()</c> обрабатывает их и завершает процесс до появления любого окна.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            // Хуки без UI и с таймаутом (удаление — 30 с): только быстрые действия.
            .OnBeforeUninstallFastCallback(_ => { RemoveStartupEntry(); RemoveModules(); })
            // Скачанное обновление применяется только явно (App.Updates.cs): кнопкой в окне или молча при выходе, если
            // проверка обновлений всё ещё включена. Иначе Velopack поставил бы его при следующем запуске сам.
            .SetAutoApplyOnStartup(false)
            .Run();

        // До любого окна (мастер, --settings): автозапуск записывает стабильный стаб корня, а не current\Screeny.exe.
        // Тестовый экземпляр Run-ключ не трогает вовсе, так что для него это значение ни на что не влияет.
        StartupRegistrar.UseLaunchPath(Updates.VelopackInfo.Detect().StubPath);

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    /// <summary>Удаление: запись автозапуска, указывающая в папку установки, иначе Windows запускала бы несуществующий exe.</summary>
    private static void RemoveStartupEntry()
    {
        try
        {
            if (VelopackLocator.IsCurrentSet && VelopackLocator.Current.RootAppDir is { } root)
                StartupRegistrar.RemoveIfInside(new RegistryRunKey(), root);
        }
        catch (Exception ex) { Log.Error("Uninstall hook: startup entry removal failed", ex); }
    }

    /// <summary>
    /// Удаление: скачанные модули (<c>%LocalAppData%\Screeny\Modules</c>, точное распознавание) — около 30 МБ, которые
    /// без Screeny не нужны. Только эта папка: история и настройки пользователя остаются. Папка общая с портативной
    /// копией: если она есть, она вернётся к распознаванию Windows и предложит скачать модуль снова. Если модуль сейчас
    /// загружен в работающую портативную копию, его папка помечается и стирается при её следующем запуске.
    /// </summary>
    private static void RemoveModules()
    {
        try { Modules.ModuleInstaller.DeleteRootOrMark(Modules.OcrModule.ModulesRoot(dev: false)); }
        catch (Exception ex) { Log.Error("Uninstall hook: modules removal failed", ex); }
    }
}
