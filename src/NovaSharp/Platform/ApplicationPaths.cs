namespace NovaSharp.Platform;

/// <inheritdoc cref="IApplicationPaths"/>
public sealed class ApplicationPaths : IApplicationPaths
{
    /// <summary>The folder name NovaSharp's user state lives under, inside the platform's configuration directory.</summary>
    public const string FolderName = "NovaSharp";

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="Environment.SpecialFolder.ApplicationData"/> already resolves to the right place on every supported
    /// platform, so there is nothing here to branch on. <c>DoNotVerify</c> is deliberate: the directory is created when
    /// something is first written, and a missing one is not an error to report at startup.
    /// </remarks>
    public string ConfigurationDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        FolderName);
}
