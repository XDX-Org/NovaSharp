using System.Security.Cryptography;
using System.Text;

namespace NovaSharp;

internal sealed record SignedUpdateManifest(string Version, string Sha256, string Signature);

internal static class ReleaseUpdate
{
    internal static bool VerifyManifest(SignedUpdateManifest manifest, ECDsa publicKey)
    {
        if (!Version.TryParse(manifest.Version, out _) || manifest.Sha256.Length != 64
            || !manifest.Sha256.All(Uri.IsHexDigit)) return false;
        try
        {
            var payload = Encoding.UTF8.GetBytes($"{manifest.Version}\n{manifest.Sha256.ToLowerInvariant()}");
            return publicKey.VerifyData(payload, Convert.FromBase64String(manifest.Signature), HashAlgorithmName.SHA256);
        }
        catch (FormatException) { return false; }
    }

    internal static async Task<bool> VerifyArtifactAsync(Stream artifact, string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        if (!artifact.CanRead || expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit)) return false;
        var actual = await SHA256.HashDataAsync(artifact, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedSha256));
    }

    internal static void InstallStaged(string stagingDirectory, string installDirectory, string backupDirectory)
    {
        var staging = ValidateDirectory(stagingDirectory);
        var install = ValidateDirectory(installDirectory);
        var backup = ValidateDirectory(backupDirectory);
        if (!Directory.Exists(staging) || !Directory.Exists(install)) throw new DirectoryNotFoundException("Update staging or installation directory is missing.");
        if (Directory.Exists(backup) || PathsOverlap(staging, install) || PathsOverlap(staging, backup) || PathsOverlap(install, backup))
            throw new InvalidOperationException("Update directories must be distinct siblings and backup must not exist.");
        Directory.Move(install, backup);
        try { Directory.Move(staging, install); }
        catch
        {
            Directory.Move(backup, install);
            throw;
        }
    }

    internal static void Rollback(string installDirectory, string backupDirectory, string displacedDirectory)
    {
        var install = ValidateDirectory(installDirectory);
        var backup = ValidateDirectory(backupDirectory);
        var displaced = ValidateDirectory(displacedDirectory);
        if (!Directory.Exists(install) || !Directory.Exists(backup) || Directory.Exists(displaced)
            || PathsOverlap(install, backup) || PathsOverlap(install, displaced) || PathsOverlap(backup, displaced))
            throw new InvalidOperationException("Rollback directories are invalid.");
        Directory.Move(install, displaced);
        try { Directory.Move(backup, install); }
        catch
        {
            Directory.Move(displaced, install);
            throw;
        }
    }

    private static string ValidateDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) throw new ArgumentException("Update directory must be absolute.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (Path.GetPathRoot(full) == full) throw new ArgumentException("A filesystem root cannot be an update directory.");
        return full;
    }
    private static bool PathsOverlap(string left, string right) => left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || right.StartsWith(left + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
