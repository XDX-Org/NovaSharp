using PhotinoEx.Blazor;

namespace NovaSharp;

internal static class Program
{
    internal static PhotinoExBlazorApp App { get; private set; } = null!;

    [STAThread]
    private static void Main(string[] args)
    {
        var builder = PhotinoExBlazorAppBuilder.CreateDefault(args);
        builder.RootComponents.Add<App>("app");

        App = builder.Build();
        App.MainWindow
            .SetLogVerbosity(0)
            .SetTitle("NovaSharp")
            .SetUseOsDefaultSize(false)
            .SetWidth(1200)
            .SetHeight(800)
            .SetMinWidth(640)
            .SetMinHeight(480);

        App.Run();
    }
}
