using NovaSharp.Extensions;

namespace NovaSharp.HelloExtension;

public sealed class HelloExtension : INovaSharpExtension
{
    private IDisposable? _command;

    public ValueTask ActivateAsync(IExtensionContext context, CancellationToken cancellationToken)
    {
        _command = context.Commands.Register("novasharp.hello", _ =>
        {
            var greeting = context.Settings.Get<string>("novasharp.hello.greeting") ?? "Hello";
            context.Diagnostics.Publish("novasharp.hello", [new("HELLO001", greeting, DiagnosticSeverity.Information)]);
            return ValueTask.CompletedTask;
        });
        return ValueTask.CompletedTask;
    }

    public ValueTask DeactivateAsync(CancellationToken cancellationToken)
    {
        _command?.Dispose();
        _command = null;
        return ValueTask.CompletedTask;
    }
}
