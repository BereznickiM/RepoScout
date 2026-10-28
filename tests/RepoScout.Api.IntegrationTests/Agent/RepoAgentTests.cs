using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RepoScout.Api.Agent;
using RepoScout.Api.Analysis;
using RepoScout.Api.GitHub;

namespace RepoScout.Api.IntegrationTests.Agent;

public class RepoAgentTests
{
    private const string BudgetNote = "> Note: analysis stopped after 20 tool calls; parts of the repository were not inspected.";
    private static readonly RepoRef Repo = new("o", "r");

    private sealed record ChatCall(List<ChatMessage> Messages, ChatOptions? Options);

    // Replies with respond(callIndex); records a snapshot of the history of every call.
    private sealed class ScriptedChatClient(Func<int, ChatResponse> respond) : IChatClient
    {
        public List<ChatCall> Calls { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var index = Calls.Count;
            Calls.Add(new ChatCall(messages.ToList(), options));
            return Task.FromResult(respond(index));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FakeGitHubClient(RepoInfo info, RepoTree tree) : IGitHubClient
    {
        public Exception? RepoFailure { get; init; }
        public Func<string, RepoFile>? File { get; init; }
        public int RepoCalls { get; private set; }
        public int TreeCalls { get; private set; }
        public List<(string Path, string Ref, int MaxBytes)> FileCalls { get; } = [];
        public string? TreeRef { get; private set; }

        public Task<RepoInfo> GetRepoAsync(RepoRef repo, CancellationToken ct)
        {
            RepoCalls++;
            return RepoFailure is null ? Task.FromResult(info) : Task.FromException<RepoInfo>(RepoFailure);
        }

        public Task<RepoTree> GetTreeAsync(RepoRef repo, string gitRef, CancellationToken ct)
        {
            TreeCalls++;
            TreeRef = gitRef;
            return Task.FromResult(tree);
        }

        public Task<RepoFile> GetFileAsync(RepoRef repo, string path, string gitRef, int maxBytes, CancellationToken ct)
        {
            FileCalls.Add((path, gitRef, maxBytes));
            try
            {
                return Task.FromResult(File!(path));
            }
            catch (Exception ex)
            {
                return Task.FromException<RepoFile>(ex);
            }
        }
    }

    private static RepoInfo Info(string? description = "A demo repo", string? language = "C#") =>
        new("o/r", description, "develop", language);

    private static RepoTreeEntry Dir(string path) => new(path, RepoTreeEntryKind.Directory, null);

    private static RepoTreeEntry FileEntry(string path, long size = 10) => new(path, RepoTreeEntryKind.File, size);

    private static FakeGitHubClient Github(
        IReadOnlyList<RepoTreeEntry>? entries = null, bool truncated = false, Func<string, RepoFile>? file = null,
        RepoInfo? info = null) =>
        new(info ?? Info(), new RepoTree(entries ?? [FileEntry("README.md")], truncated)) { File = file };

    private static RepoAgent Agent(IChatClient chat, IGitHubClient github) => new(chat, github, NullLogger<RepoAgent>.Instance);

    private static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    private static FunctionCallContent Call(string id, string name, string? path = null) =>
        new(id, name, path is null ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["path"] = path });

    private static ChatResponse Calls(params FunctionCallContent[] calls) =>
        new(new ChatMessage(ChatRole.Assistant, calls.Cast<AIContent>().ToList()));

    private static IEnumerable<FunctionResultContent> ToolResults(ChatCall call) =>
        call.Messages.Where(m => m.Role == ChatRole.Tool).SelectMany(m => m.Contents).OfType<FunctionResultContent>();

    // Runs one tool call and returns the result text the model receives.
    private static async Task<string> RunTool(FakeGitHubClient github, string name, string? path)
    {
        var chat = new ScriptedChatClient(i => i == 0 ? Calls(Call("c1", name, path)) : Text("done"));
        await Agent(chat, github).AnalyzeAsync(Repo, "q", CancellationToken.None);
        return (string)ToolResults(chat.Calls[1]).Single().Result!;
    }

    private static string UserPrompt(ChatCall call) => call.Messages.First(m => m.Role == ChatRole.User).Text;

    // ---- no tool calls ----

    [Fact]
    public async Task AnalyzeAsync_ModelAnswersWithoutTools_ReturnsTextAndNeverFetchesTree()
    {
        var chat = new ScriptedChatClient(_ => Text("# Answer"));
        var github = Github();

        var report = (await Agent(chat, github).AnalyzeAsync(Repo, "q", CancellationToken.None)).Report;

        Assert.Equal("# Answer", report);
        Assert.Equal(1, github.RepoCalls);
        Assert.Equal(0, github.TreeCalls);
        Assert.Single(chat.Calls);
    }

    [Fact]
    public async Task AnalyzeAsync_Always_OffersBothToolsWithAutoToolMode()
    {
        var chat = new ScriptedChatClient(_ => Text("r"));

        await Agent(chat, Github()).AnalyzeAsync(Repo, "q", CancellationToken.None);

        var options = chat.Calls[0].Options!;
        Assert.Equal(["list_files", "read_file"], options.Tools!.OfType<AIFunction>().Select(t => t.Name).Order());
        Assert.Equal(ChatToolMode.Auto, options.ToolMode);
    }

    // ---- repo info failures ----

    [Fact]
    public async Task AnalyzeAsync_RepoNotFound_PropagatesAndModelNotCalled()
    {
        var chat = new ScriptedChatClient(_ => Text("r"));
        var github = new FakeGitHubClient(Info(), new RepoTree([], false)) { RepoFailure = new GitHubNotFoundException("gone") };

        await Assert.ThrowsAsync<GitHubNotFoundException>(() => Agent(chat, github).AnalyzeAsync(Repo, "q", CancellationToken.None));

        Assert.Empty(chat.Calls);
    }

    // ---- prompts ----

    [Fact]
    public async Task AnalyzeAsync_Always_UserMessageHasQuestionAndRepoDataBlockWithMetadata()
    {
        var chat = new ScriptedChatClient(_ => Text("r"));

        await Agent(chat, Github()).AnalyzeAsync(Repo, "What stack?", CancellationToken.None);

        var prompt = UserPrompt(chat.Calls[0]);
        Assert.StartsWith("What stack?", prompt);
        Assert.Contains("<repo_data>", prompt);
        Assert.EndsWith("</repo_data>", prompt);
        Assert.Contains("o/r", prompt);
        Assert.Contains("A demo repo", prompt);
        Assert.Contains("C#", prompt);
        Assert.Contains("develop", prompt);
    }

    [Fact]
    public async Task AnalyzeAsync_MissingDescriptionAndLanguage_UsesPlaceholders()
    {
        var chat = new ScriptedChatClient(_ => Text("r"));

        await Agent(chat, Github(info: Info(description: null, language: null))).AnalyzeAsync(Repo, "q", CancellationToken.None);

        var prompt = UserPrompt(chat.Calls[0]);
        Assert.Contains("Description: (none)", prompt);
        Assert.Contains("Language: (unknown)", prompt);
    }

    [Fact]
    public async Task AnalyzeAsync_InvisibleCharsInMetadata_AreStripped()
    {
        var chat = new ScriptedChatClient(_ => Text("r"));

        await Agent(chat, Github(info: Info(description: "ab​cd\U000E0041"))).AnalyzeAsync(Repo, "q", CancellationToken.None);

        Assert.Contains("Description: abcd", UserPrompt(chat.Calls[0]));
    }

    // ---- loop ----

    [Fact]
    public async Task AnalyzeAsync_ListAndReadInOneTurn_SendsOneToolMessageWithMatchingCallIds()
    {
        var github = Github([FileEntry("README.md")], file: p => new RepoFile(p, "hello", false));
        var assistant = Calls(Call("a", "list_files", ""), Call("b", "read_file", "README.md"));
        var chat = new ScriptedChatClient(i => i == 0 ? assistant : Text("final"));

        var report = (await Agent(chat, github).AnalyzeAsync(Repo, "q", CancellationToken.None)).Report;

        Assert.Equal("final", report);
        Assert.Equal(2, chat.Calls.Count);
        var toolMessage = Assert.Single(chat.Calls[1].Messages, m => m.Role == ChatRole.Tool);
        Assert.Equal(["a", "b"], toolMessage.Contents.OfType<FunctionResultContent>().Select(r => r.CallId));
        Assert.Contains(chat.Calls[1].Messages, m => ReferenceEquals(m, assistant.Messages[0]));
    }

    [Fact]
    public async Task AnalyzeAsync_ToolCallsRequested_FetchesTreeOfDefaultBranchAndReadsFileFromIt()
    {
        var github = Github([FileEntry("README.md")], file: p => new RepoFile(p, "hello", false));

        await RunTool(github, "read_file", "README.md");

        Assert.Equal("develop", github.TreeRef);
        Assert.Equal([("README.md", "develop", 12_000)], github.FileCalls);
    }

    [Fact]
    public async Task AnalyzeAsync_ReadFileWithMissingPathArgument_ReturnsInvalidArgumentsError()
    {
        var result = await RunTool(Github(), "read_file", null);

        Assert.StartsWith("Error: invalid arguments:", result);
    }

    [Fact]
    public async Task AnalyzeAsync_UnknownToolRequested_ReturnsUnknownToolError()
    {
        var result = await RunTool(Github(), "delete_repo", "x");

        Assert.Equal("Error: unknown tool", result);
    }

    // ---- budget ----

    private static ChatResponse ListTurn(int i) => Calls(Call($"c{i}", "list_files", ""));

    [Fact]
    public async Task AnalyzeAsync_ModelNeverStopsCallingTools_ForcesFinalCallWithoutToolsAndAppendsNote()
    {
        var chat = new ScriptedChatClient(i => i < 20 ? ListTurn(i) : Text("partial report"));

        var report = (await Agent(chat, Github()).AnalyzeAsync(Repo, "q", CancellationToken.None)).Report;

        Assert.Equal(21, chat.Calls.Count);
        var last = chat.Calls[^1];
        Assert.Equal(ChatToolMode.None, last.Options!.ToolMode);
        Assert.Equal(ChatRole.User, last.Messages[^1].Role);
        Assert.Contains("budget exhausted", last.Messages[^1].Text, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("partial report", report);
        Assert.EndsWith(BudgetNote, report);
    }

    [Fact]
    public async Task AnalyzeAsync_19ToolCallsThenAnswer_ReturnsReportWithoutNote()
    {
        var chat = new ScriptedChatClient(i => i < 19 ? ListTurn(i) : Text("report"));

        var report = (await Agent(chat, Github()).AnalyzeAsync(Repo, "q", CancellationToken.None)).Report;

        Assert.Equal("report", report);
        Assert.Equal(20, chat.Calls.Count);
    }

    [Fact]
    public async Task AnalyzeAsync_25CallsInOneTurn_ExecutesFirst20AndRejectsRestWithBudgetError()
    {
        var calls = Enumerable.Range(0, 25).Select(i => Call($"c{i}", "list_files", "")).ToArray();
        var chat = new ScriptedChatClient(i => i == 0 ? Calls(calls) : Text("report"));
        var github = Github();

        var response = await Agent(chat, github).AnalyzeAsync(Repo, "q", CancellationToken.None);

        var results = ToolResults(chat.Calls[1]).ToList();
        Assert.Equal(25, results.Count);
        Assert.Equal(20, results.Count(r => ((string)r.Result!).StartsWith("<repo_data")));
        Assert.Equal(5, results.Count(r => (string)r.Result! == "Error: tool budget exhausted"));
        Assert.Equal(2, chat.Calls.Count);
        Assert.Equal(20, response.Usage.ToolCalls);
        Assert.Equal(2, response.Usage.Rounds);
        Assert.Equal(20, response.Usage.ToolCallsByName["list_files"]);
    }

    [Fact]
    public async Task AnalyzeAsync_20UnknownToolTurns_ConsumeBudgetAndForceFinalCallWithNote()
    {
        var chat = new ScriptedChatClient(i => i < 20 ? Calls(Call($"c{i}", "nope", "")) : Text("partial"));

        var report = (await Agent(chat, Github()).AnalyzeAsync(Repo, "q", CancellationToken.None)).Report;

        Assert.Equal(21, chat.Calls.Count);
        Assert.Equal(ChatToolMode.None, chat.Calls[^1].Options!.ToolMode);
        Assert.EndsWith(BudgetNote, report);
    }

    [Fact]
    public async Task AnalyzeAsync_MixedKnownAndUnknownBatchCrossing20_CountsUnknownAndRejectsRest()
    {
        var calls = Enumerable.Range(0, 22)
            .Select(i => Call($"c{i}", i % 2 == 0 ? "list_files" : "nope", ""))
            .ToArray();
        var chat = new ScriptedChatClient(i => i == 0 ? Calls(calls) : Text("report"));

        var report = (await Agent(chat, Github()).AnalyzeAsync(Repo, "q", CancellationToken.None)).Report;

        var results = ToolResults(chat.Calls[1]).ToList();
        Assert.Equal(22, results.Count);
        Assert.Equal(10, results.Count(r => ((string)r.Result!).StartsWith("<repo_data")));
        Assert.Equal(10, results.Count(r => (string)r.Result! == "Error: unknown tool"));
        Assert.Equal(2, results.Count(r => (string)r.Result! == "Error: tool budget exhausted"));
        Assert.Equal(2, chat.Calls.Count);
        Assert.EndsWith(BudgetNote, report);
    }

    // ---- usage stats ----

    private static ChatResponse WithUsage(ChatResponse response, long? input, long? output, long? total)
    {
        response.Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = total };
        return response;
    }

    private static async Task<AnalysisUsage> RunUsage(ScriptedChatClient chat) =>
        (await Agent(chat, Github()).AnalyzeAsync(Repo, "q", CancellationToken.None)).Usage;

    [Fact]
    public async Task AnalyzeAsync_NoTools_UsageHasOneRoundAndNoToolCalls()
    {
        var usage = await RunUsage(new ScriptedChatClient(_ => Text("r")));

        Assert.Equal(1, usage.Rounds);
        Assert.Equal(0, usage.ToolCalls);
        Assert.Empty(usage.ToolCallsByName);
        Assert.False(usage.BudgetExhausted);
        Assert.True(usage.DurationMs >= 0);
    }

    [Fact]
    public async Task AnalyzeAsync_UsageInTwoRounds_SumsEachField()
    {
        var chat = new ScriptedChatClient(i => i == 0
            ? WithUsage(ListTurn(0), 10, 2, 12)
            : WithUsage(Text("r"), 10, 2, 12));

        var usage = await RunUsage(chat);

        Assert.Equal(2, usage.Rounds);
        Assert.Equal(20, usage.InputTokens);
        Assert.Equal(4, usage.OutputTokens);
        Assert.Equal(24, usage.TotalTokens);
    }

    [Fact]
    public async Task AnalyzeAsync_NoUsageReported_TokenFieldsAreNull()
    {
        var usage = await RunUsage(new ScriptedChatClient(i => i == 0 ? ListTurn(0) : Text("r")));

        Assert.Null(usage.InputTokens);
        Assert.Null(usage.OutputTokens);
        Assert.Null(usage.TotalTokens);
    }

    [Fact]
    public async Task AnalyzeAsync_UsageInOnlyOneRound_UsesThatRoundsValues()
    {
        var chat = new ScriptedChatClient(i => i == 0 ? ListTurn(0) : WithUsage(Text("r"), 7, 3, 10));

        var usage = await RunUsage(chat);

        Assert.Equal(7, usage.InputTokens);
        Assert.Equal(3, usage.OutputTokens);
        Assert.Equal(10, usage.TotalTokens);
    }

    [Fact]
    public async Task AnalyzeAsync_TotalMissingButInputAndOutputPresent_TotalIsDerivedFromThem()
    {
        var usage = await RunUsage(new ScriptedChatClient(_ => WithUsage(Text("r"), 7, 3, null)));

        Assert.Equal(10, usage.TotalTokens);
    }

    [Fact]
    public async Task AnalyzeAsync_UsageWithOnlyOutputTokens_InputAndTotalStayNull()
    {
        var usage = await RunUsage(new ScriptedChatClient(_ => WithUsage(Text("r"), null, 5, null)));

        Assert.Null(usage.InputTokens);
        Assert.Equal(5, usage.OutputTokens);
        Assert.Null(usage.TotalTokens);
    }

    [Fact]
    public async Task AnalyzeAsync_BudgetExhausted_UsageCountsFinalRoundAndCapsToolCalls()
    {
        var chat = new ScriptedChatClient(i => i < 20 ? ListTurn(i) : Text("partial"));

        var usage = await RunUsage(chat);

        Assert.Equal(21, usage.Rounds);
        Assert.Equal(20, usage.ToolCalls);
        Assert.Equal(new Dictionary<string, int> { ["list_files"] = 20 }, usage.ToolCallsByName);
        Assert.True(usage.BudgetExhausted);
    }

    [Fact]
    public async Task AnalyzeAsync_KnownAndUnknownTools_GroupsUnknownUnderPlaceholderNotModelName()
    {
        var calls = new[]
        {
            Call("a", "list_files", ""), Call("b", "read_file", "README.md"), Call("c", "list_files", ""),
            Call("d", "delete_repo", "x"), Call("e", "nope", "")
        };
        var github = Github(file: p => new RepoFile(p, "x", false));
        var chat = new ScriptedChatClient(i => i == 0 ? Calls(calls) : Text("report"));

        var usage = (await Agent(chat, github).AnalyzeAsync(Repo, "q", CancellationToken.None)).Usage;

        Assert.Equal(
            new Dictionary<string, int> { ["list_files"] = 2, ["read_file"] = 1, ["(unknown)"] = 2 },
            usage.ToolCallsByName);
        Assert.Equal(5, usage.ToolCalls);
        Assert.False(usage.ToolCallsByName.ContainsKey("delete_repo"));
    }

    [Fact]
    public async Task AnalyzeAsync_InvalidArgumentCall_CountsAsToolCallByName()
    {
        var chat = new ScriptedChatClient(i => i == 0 ? Calls(Call("a", "read_file")) : Text("report"));

        var usage = await RunUsage(chat);

        Assert.Equal(1, usage.ToolCalls);
        Assert.Equal(1, usage.ToolCallsByName["read_file"]);
    }

    // ---- list_files ----

    private static List<RepoTreeEntry> SampleTree() =>
    [
        FileEntry("README.md"),
        FileEntry("package-lock.json", 999),
        Dir("node_modules"), FileEntry("node_modules/x.js"),
        Dir("src"), Dir("src/app"), Dir("src/app/deep"), FileEntry("src/app/deep/f.cs"), FileEntry("src/main.cs", 42),
        new("lib", RepoTreeEntryKind.Submodule, null)
    ];

    [Fact]
    public async Task ListFiles_Root_ShowsTwoLevelsWithMarkersAndHidesNoise()
    {
        var result = await RunTool(Github(SampleTree()), "list_files", "");

        var lines = result.Split('\n');
        Assert.Equal("<repo_data source=\"list_files\" path=\"\">", lines[0]);
        Assert.Contains("README.md (10 bytes)", lines);
        Assert.Contains("src/", lines);
        Assert.Contains("src/app/", lines);
        Assert.Contains("src/main.cs (42 bytes)", lines);
        Assert.Contains("lib", lines);
        Assert.DoesNotContain("src/app/deep/", lines);
        Assert.DoesNotContain("node_modules", result);
        Assert.DoesNotContain("package-lock", result);
    }

    [Fact]
    public async Task ListFiles_Subdirectory_ListsItsChildrenTwoLevelsDeep()
    {
        var result = await RunTool(Github(SampleTree()), "list_files", "src");

        var lines = result.Split('\n');
        Assert.Contains("src/app/", lines);
        Assert.Contains("src/app/deep/", lines);
        Assert.Contains("src/main.cs (42 bytes)", lines);
        Assert.DoesNotContain("src/app/deep/f.cs (10 bytes)", lines);
        Assert.DoesNotContain("README.md (10 bytes)", lines);
    }

    [Theory]
    [InlineData("src/")]
    [InlineData("/src")]
    [InlineData("  src  ")]
    public async Task ListFiles_PathWithSlashesOrSpaces_IsNormalized(string path)
    {
        var result = await RunTool(Github(SampleTree()), "list_files", path);

        Assert.Contains("path=\"src\"", result);
        Assert.Contains("src/main.cs (42 bytes)", result);
    }

    [Fact]
    public async Task ListFiles_UnknownDirectory_ReturnsError()
    {
        var result = await RunTool(Github(SampleTree()), "list_files", "nope");

        Assert.Equal("Error: no such directory: nope", result);
    }

    [Fact]
    public async Task ListFiles_MoreThan300Entries_CapsListAndNotesIncomplete()
    {
        var entries = Enumerable.Range(0, 301).Select(i => FileEntry($"f{i:D4}.txt")).ToList();

        var result = await RunTool(Github(entries), "list_files", "");

        Assert.Equal(300, result.Split('\n').Count(l => l.StartsWith("f0")));
        Assert.Contains("(listing incomplete", result);
    }

    [Fact]
    public async Task ListFiles_Exactly300Entries_ListsAllWithoutNote()
    {
        var entries = Enumerable.Range(0, 300).Select(i => FileEntry($"f{i:D4}.txt")).ToList();

        var result = await RunTool(Github(entries), "list_files", "");

        Assert.Equal(300, result.Split('\n').Count(l => l.StartsWith("f0")));
        Assert.DoesNotContain("incomplete", result);
    }

    [Fact]
    public async Task ListFiles_TruncatedTree_NotesIncomplete()
    {
        var result = await RunTool(Github(truncated: true), "list_files", "");

        Assert.Contains("(listing incomplete", result);
    }

    [Fact]
    public async Task ListFiles_CalledTwice_FetchesTreeOnce()
    {
        var github = Github(SampleTree());
        var chat = new ScriptedChatClient(i => i == 0
            ? Calls(Call("a", "list_files", ""), Call("b", "list_files", "src"))
            : Text("done"));

        await Agent(chat, github).AnalyzeAsync(Repo, "q", CancellationToken.None);

        Assert.Equal(1, github.TreeCalls);
    }

    // ---- read_file ----

    private static FakeGitHubClient WithFile(string content, bool truncated = false, string path = "README.md") =>
        Github([FileEntry(path)], file: p => new RepoFile(p, content, truncated));

    [Fact]
    public async Task ReadFile_ExistingFile_ReturnsContentWrappedAsRepoData()
    {
        var result = await RunTool(WithFile("hello"), "read_file", "README.md");

        Assert.Equal("<repo_data source=\"read_file\" path=\"README.md\">\nhello\n</repo_data>", result);
    }

    [Theory]
    [InlineData("/README.md")]
    [InlineData("README.md/")]
    [InlineData("  README.md ")]
    public async Task ReadFile_PathWithSlashesOrSpaces_IsNormalized(string path)
    {
        var github = WithFile("hello");

        var result = await RunTool(github, "read_file", path);

        Assert.Contains("hello", result);
        Assert.Equal("README.md", github.FileCalls.Single().Path);
    }

    [Fact]
    public async Task ReadFile_NoisePath_ReturnsSkippedWithoutAnyGitHubCall()
    {
        var github = Github();

        var result = await RunTool(github, "read_file", "node_modules/a/index.js");

        Assert.StartsWith("Error: skipped", result);
        Assert.Equal(0, github.TreeCalls);
        Assert.Empty(github.FileCalls);
    }

    [Fact]
    public async Task ReadFile_EmptyPath_ReturnsPathRequiredError()
    {
        var result = await RunTool(Github(), "read_file", "");

        Assert.Equal("Error: path is required", result);
    }

    [Fact]
    public async Task ReadFile_PathNotInCompleteTree_ReturnsNotFoundWithoutFileFetch()
    {
        var github = Github([FileEntry("README.md")], file: p => new RepoFile(p, "x", false));

        var result = await RunTool(github, "read_file", "missing.md");

        Assert.Equal("Error: file not found", result);
        Assert.Empty(github.FileCalls);
    }

    [Fact]
    public async Task ReadFile_PathNotInTruncatedTree_StillTriesToFetchFile()
    {
        var github = Github([FileEntry("README.md")], truncated: true, file: p => new RepoFile(p, "deep content", false));

        var result = await RunTool(github, "read_file", "very/deep/file.md");

        Assert.Contains("deep content", result);
        Assert.Equal("very/deep/file.md", github.FileCalls.Single().Path);
    }

    [Fact]
    public async Task ReadFile_Directory_ReturnsDirectoryError()
    {
        var github = Github([Dir("src")], file: p => new RepoFile(p, "x", false));

        var result = await RunTool(github, "read_file", "src");

        Assert.Contains("is a directory", result);
        Assert.Empty(github.FileCalls);
    }

    [Fact]
    public async Task ReadFile_ContentWithNullByte_ReturnsBinaryError()
    {
        var result = await RunTool(WithFile("MZ\0\0binary"), "read_file", "README.md");

        Assert.Equal("Error: binary file", result);
    }

    [Fact]
    public async Task ReadFile_TruncatedFile_AppendsTruncationMarker()
    {
        var result = await RunTool(WithFile("partial", truncated: true), "read_file", "README.md");

        Assert.Contains("partial\n[truncated at 12000 bytes]", result);
    }

    [Fact]
    public async Task ReadFile_ContentWithInvisibleChars_StripsThem()
    {
        var result = await RunTool(WithFile("a​b﻿c⁠d\U000E0041e"), "read_file", "README.md");

        Assert.Contains("\nabcde\n", result);
    }

    [Fact]
    public async Task ReadFile_GitHubReportsNotFound_ReturnsErrorStringToModel()
    {
        var github = Github([FileEntry("README.md")], file: _ => throw new GitHubNotFoundException("gone upstream"));

        var result = await RunTool(github, "read_file", "README.md");

        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public async Task ReadFile_GitHubRateLimited_ExceptionPropagates()
    {
        var github = Github([FileEntry("README.md")],
            file: _ => throw new GitHubRateLimitException(HttpStatusCode.Forbidden, "limit", null));
        var chat = new ScriptedChatClient(i => i == 0 ? Calls(Call("c1", "read_file", "README.md")) : Text("done"));

        await Assert.ThrowsAsync<GitHubRateLimitException>(() => Agent(chat, github).AnalyzeAsync(Repo, "q", CancellationToken.None));
    }
}
