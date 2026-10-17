namespace RepoScout.Api.Agent;

public static class NoiseFilter
{
    private static readonly HashSet<string> NoiseDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "dist", "build", "bin", "obj", ".git", "vendor", "coverage"
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".svg", ".tiff",
        ".woff", ".woff2", ".ttf", ".otf", ".eot",
        ".zip", ".tar", ".gz", ".tgz", ".7z", ".rar", ".bz2",
        ".dll", ".exe", ".so", ".dylib", ".pdb", ".class", ".jar",
        ".pdf", ".mp3", ".mp4", ".mov", ".avi", ".wav", ".webm"
    };

    public static bool IsNoise(string path)
    {
        var segments = path.Split('/');
        if (segments.Any(NoiseDirectories.Contains))
        {
            return true;
        }

        var name = segments[^1];
        return name.Equals("package-lock.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("yarn.lock", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pnpm-lock.yaml", StringComparison.OrdinalIgnoreCase)
            || name.Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".min.css", StringComparison.OrdinalIgnoreCase)
            || BinaryExtensions.Contains(Path.GetExtension(name));
    }
}
