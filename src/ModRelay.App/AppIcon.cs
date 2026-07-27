using System.Reflection;

namespace ModRelay.App;

internal static class AppIcon
{
    public static Icon Current { get; } = Load();

    private static Icon Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ModRelay.AppIcon.ico")
            ?? throw new InvalidOperationException("The embedded ModRelay icon is missing.");
        return new Icon(stream);
    }
}
