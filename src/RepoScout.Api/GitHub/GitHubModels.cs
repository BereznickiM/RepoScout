namespace RepoScout.Api.GitHub;

public sealed record RepoInfo(string FullName, string? Description, string DefaultBranch, string? Language);

public sealed record RepoTree(IReadOnlyList<RepoTreeEntry> Entries, bool Truncated);

public sealed record RepoTreeEntry(string Path, RepoTreeEntryKind Kind, long? Size);

public enum RepoTreeEntryKind
{
    File,
    Directory,
    Submodule
}

public sealed record RepoFile(string Path, string Content, bool Truncated);
