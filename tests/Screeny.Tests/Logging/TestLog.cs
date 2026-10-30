using System.Runtime.CompilerServices;
using Screeny.Logging;

namespace Screeny.Tests.Logging;

/// <summary>
/// Журнал тестов уходит во временную папку до первого обращения к <see cref="Log"/>: иначе ожидаемые ошибки из тестов
/// попадали бы в настоящий screeny.log, а его хвост прикладывается к сообщениям об ошибках.
/// </summary>
internal static class TestLog
{
    public static readonly string FilePath = Path.Combine(Path.GetTempPath(), "ScreenyTests", "screeny.test.log");

    [ModuleInitializer]
    internal static void Redirect() => Environment.SetEnvironmentVariable(Log.PathVariable, FilePath);
}

public class TestLogTests
{
    [Fact]
    public void Log_WritesToTheTestFile() => Assert.Equal(TestLog.FilePath, Log.FilePath);
}
