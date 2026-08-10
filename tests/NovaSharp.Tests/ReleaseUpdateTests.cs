using System.Security.Cryptography;
using System.Text;

namespace NovaSharp.Tests;

[TestClass]
public sealed class ReleaseUpdateTests
{
    [TestMethod]
    public async Task SignatureAndArtifactHashAreRequired()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bytes = Encoding.UTF8.GetBytes("release archive");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes($"0.2.0\n{hash}"), HashAlgorithmName.SHA256));
        var manifest = new SignedUpdateManifest("0.2.0", hash, signature);
        Assert.IsTrue(ReleaseUpdate.VerifyManifest(manifest, key));
        await using var artifact = new MemoryStream(bytes);
        Assert.IsTrue(await ReleaseUpdate.VerifyArtifactAsync(artifact, hash));
        Assert.IsFalse(ReleaseUpdate.VerifyManifest(manifest with { Version = "0.2.1" }, key));
    }

    [TestMethod]
    public void StagedInstallCanRollBackWithoutDeletingEitherVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), "novasharp-update-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "install");
        var staging = Path.Combine(root, "staging");
        var backup = Path.Combine(root, "backup");
        var displaced = Path.Combine(root, "displaced");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(install, "version"), "old");
        File.WriteAllText(Path.Combine(staging, "version"), "new");
        try
        {
            ReleaseUpdate.InstallStaged(staging, install, backup);
            Assert.AreEqual("new", File.ReadAllText(Path.Combine(install, "version")));
            ReleaseUpdate.Rollback(install, backup, displaced);
            Assert.AreEqual("old", File.ReadAllText(Path.Combine(install, "version")));
            Assert.AreEqual("new", File.ReadAllText(Path.Combine(displaced, "version")));
        }
        finally { Directory.Delete(root, true); }
    }
}
