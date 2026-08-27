using NovaSharp.Async;
using NovaSharp.Platform;

namespace NovaSharp.Solutions;

public sealed class SolutionDiscovery
{
    private readonly IWorkspacePaths _paths;
    private readonly BoundedWorkQueue _background;

    public SolutionDiscovery(IWorkspacePaths paths, BoundedWorkQueue background)
    {
        _paths = paths;
        _background = background;
    }

    public Task<string?> DiscoverAsync(string root, CancellationToken cancellationToken = default)
    {
        return _background.EnqueueAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            var candidates = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                .Where(IsSupported)
                .Select(_paths.Canonicalize)
                .OrderBy(static path => Rank(Path.GetExtension(path)))
                .ThenBy(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var bestRank = candidates.Length == 0 ? int.MaxValue : Rank(Path.GetExtension(candidates[0]));
            var best = candidates.Where(path => Rank(Path.GetExtension(path)) == bestRank).Take(2).ToArray();
            return Task.FromResult(best.Length == 1 ? best[0] : null);
        }, cancellationToken);
    }

    public static bool IsSupported(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase);
    }

    private static int Rank(string extension)
    {
        return extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ? 0
        : extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ? 1
        : 2;
    }
}
