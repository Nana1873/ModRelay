using System.Reflection;

namespace ModRelay.App;

internal static class AppVersion
{
    public static string Current { get; } = ReadCurrent();

    public static string Format(Version version) =>
        $"v{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    internal static string FromInformationalVersion(string? value, Version fallback)
    {
        var semantic = value?.Split('+', 2)[0].Trim();
        var coreParts = semantic?.Split('-', 2)[0].Split('.');
        return !string.IsNullOrWhiteSpace(semantic) &&
               coreParts is { Length: 3 } &&
               coreParts.All(part => int.TryParse(part, out var number) && number >= 0)
            ? $"v{semantic}"
            : Format(fallback);
    }

    private static string ReadCurrent()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return FromInformationalVersion(informational, assembly.GetName().Version ?? new Version(1, 0, 0));
    }
}
