namespace RepoScout.Api.Analysis;

public sealed class MockRepoAnalyzer : IRepoAnalyzer
{
    public Task<string> AnalyzeAsync(RepoRef repo, string question, CancellationToken ct)
    {
        var quotedQuestion = string.Join("\n> ", question.ReplaceLineEndings("\n").Split('\n'));

        var report = $"""
            # Report for {repo.Owner}/{repo.Name}

            *This is a mocked response. No repository was inspected.*

            > {quotedQuestion}

            ## Findings

            - The project appears to contain a `src/Program.cs` entry point
            - Dependencies are declared in a manifest file
            - Documentation lives in the README

            ## Example snippet

            ```csharp
            Console.WriteLine("Hello from the mock analyzer");
            ```
            """;

        return Task.FromResult(report);
    }
}
