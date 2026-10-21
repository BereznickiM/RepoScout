using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RepoScout.Api.Ai;
using RepoScout.Api.IntegrationTests.GitHub;

namespace RepoScout.Api.IntegrationTests.Ai;

public class GeminiChatClientTests
{
    private const string ApiKey = "SECRET-KEY-999";
    private const string TextResponse = """{"id":"int-1","model":"gemini-x","steps":[{"type":"model_output","content":[{"type":"text","text":"hello"}]}]}""";

    private sealed class Harness
    {
        public required GeminiChatClient Client { get; init; }
        public required StubHttpMessageHandler Handler { get; init; }
        public List<JsonElement> Bodies { get; } = [];
    }

    private static Harness Create(params Func<HttpResponseMessage>[] responses) =>
        CreateWith(new AiModelOptions { Model = "default-model", ApiKey = ApiKey }, responses);

    private static Harness CreateWithFallbacks(string[] fallbacks, params Func<HttpResponseMessage>[] responses) =>
        CreateWith(new AiModelOptions { Model = "A", ApiKey = ApiKey, FallbackModels = fallbacks }, responses);

    private static Harness CreateWith(AiModelOptions modelOptions, params Func<HttpResponseMessage>[] responses) =>
        CreateWith(modelOptions, TimeSpan.Zero, responses);

    private static Harness CreateWith(AiModelOptions modelOptions, TimeSpan retryBaseDelay, params Func<HttpResponseMessage>[] responses)
    {
        var queue = new Queue<Func<HttpResponseMessage>>(responses);
        Harness? h = null;
        var handler = new StubHttpMessageHandler(req =>
        {
            var json = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            h!.Bodies.Add(JsonDocument.Parse(json).RootElement.Clone());
            return queue.Dequeue()();
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://generativelanguage.googleapis.com/") };
        var options = Options.Create(modelOptions);
        h = new Harness { Client = new GeminiChatClient(http, options, NullLogger<GeminiChatClient>.Instance) { RetryBaseDelay = retryBaseDelay }, Handler = handler };
        return h;
    }

    private static Harness CreateOk(string body = TextResponse) => Create(() => Respond(body));

    private static HttpResponseMessage Respond(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static ChatMessage User(string text = "q") => new(ChatRole.User, text);

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static void AssertJsonEqual(string expected, JsonElement actual) =>
        Assert.True(JsonElement.DeepEquals(J(expected), actual), $"Expected {expected} but got {actual}");

    // ---- request shape ----

    [Fact]
    public async Task GetResponseAsync_UserAndSystemMessages_SendsExpectedRequest()
    {
        var h = CreateOk();

        await h.Client.GetResponseAsync([new ChatMessage(ChatRole.System, "sys1"), new ChatMessage(ChatRole.System, "sys2"), User("hi")]);

        var request = h.Handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/interactions", request.RequestUri!.AbsoluteUri);
        var body = h.Bodies.Single();
        Assert.Equal("default-model", body.GetProperty("model").GetString());
        Assert.False(body.GetProperty("store").GetBoolean());
        Assert.Equal("sys1\n\nsys2", body.GetProperty("system_instruction").GetString());
        AssertJsonEqual("""[{"type":"user_input","content":"hi"}]""", body.GetProperty("input"));
        Assert.False(body.TryGetProperty("tools", out _));
        AssertJsonEqual("""{"thinking_level":"low"}""", body.GetProperty("generation_config"));
    }

    [Fact]
    public async Task GetResponseAsync_NoSystemMessage_OmitsSystemInstruction()
    {
        var h = CreateOk();

        await h.Client.GetResponseAsync([User()]);

        Assert.False(h.Bodies.Single().TryGetProperty("system_instruction", out _));
    }

    [Fact]
    public async Task GetResponseAsync_ModelIdInOptions_OverridesConfiguredModel()
    {
        var h = CreateOk();

        await h.Client.GetResponseAsync([User()], new ChatOptions { ModelId = "other-model" });

        Assert.Equal("other-model", h.Bodies.Single().GetProperty("model").GetString());
    }

    [Fact]
    public async Task GetResponseAsync_AIFunctionTool_DeclaresToolWithSchemaAndKeepsPropertyNames()
    {
        var h = CreateOk();
        var tool = AIFunctionFactory.Create((string repoPath, int maxBytes) => "x", "read_file", "Reads a file");

        await h.Client.GetResponseAsync([User()], new ChatOptions { Tools = [tool] });

        var declared = h.Bodies.Single().GetProperty("tools").EnumerateArray().Single();
        Assert.Equal("function", declared.GetProperty("type").GetString());
        Assert.Equal("read_file", declared.GetProperty("name").GetString());
        Assert.Equal("Reads a file", declared.GetProperty("description").GetString());
        Assert.True(JsonElement.DeepEquals(tool.JsonSchema, declared.GetProperty("parameters")));
        var props = declared.GetProperty("parameters").GetProperty("properties");
        Assert.True(props.TryGetProperty("repoPath", out _), $"schema was {props}");
        Assert.True(props.TryGetProperty("maxBytes", out _), $"schema was {props}");
    }

    [Fact]
    public async Task GetResponseAsync_NonFunctionTool_ThrowsNotSupportedAndSendsNothing()
    {
        var h = CreateOk();
        var options = new ChatOptions { Tools = [new HostedWebSearchTool()] };

        await Assert.ThrowsAsync<NotSupportedException>(() => h.Client.GetResponseAsync([User()], options));

        Assert.Empty(h.Handler.Requests);
    }

    [Fact]
    public async Task GetResponseAsync_ToolModeAuto_SendsAutoToolChoice()
    {
        await AssertToolChoice(ChatToolMode.Auto, "auto");
    }

    [Fact]
    public async Task GetResponseAsync_ToolModeNone_SendsNoneToolChoice()
    {
        await AssertToolChoice(ChatToolMode.None, "none");
    }

    [Fact]
    public async Task GetResponseAsync_ToolModeRequireAny_SendsAnyToolChoice()
    {
        await AssertToolChoice(ChatToolMode.RequireAny, "any");
    }

    private static async Task AssertToolChoice(ChatToolMode mode, string expected)
    {
        var h = CreateOk();

        await h.Client.GetResponseAsync([User()], new ChatOptions { ToolMode = mode });

        var config = h.Bodies.Single().GetProperty("generation_config");
        Assert.Equal(expected, config.GetProperty("tool_choice").GetString());
        Assert.Equal("low", config.GetProperty("thinking_level").GetString());
    }

    [Fact]
    public async Task GetResponseAsync_ToolModeRequireSpecific_ThrowsNotSupportedAndSendsNothing()
    {
        var h = CreateOk();
        var options = new ChatOptions { ToolMode = ChatToolMode.RequireSpecific("read_file") };

        await Assert.ThrowsAsync<NotSupportedException>(() => h.Client.GetResponseAsync([User()], options));

        Assert.Empty(h.Handler.Requests);
    }

    [Fact]
    public async Task GetResponseAsync_ExplicitModelId_SendsLowThinkingLevel()
    {
        var h = CreateOk();

        await h.Client.GetResponseAsync([User()], new ChatOptions { ModelId = "other-model" });

        AssertJsonEqual("""{"thinking_level":"low"}""", h.Bodies.Single().GetProperty("generation_config"));
    }

    [Fact]
    public async Task GetResponseAsync_FallbackModel_SendsLowThinkingLevelOnEveryRequest()
    {
        var h = CreateWithFallbacks(["B"], Limited, () => Respond(TextResponse));

        await h.Client.GetResponseAsync([User()], new ChatOptions { ToolMode = ChatToolMode.Auto });

        Assert.Equal(["A", "B"], SentModels(h));
        Assert.All(h.Bodies, b => Assert.Equal("low", b.GetProperty("generation_config").GetProperty("thinking_level").GetString()));
    }

    // ---- unsupported input ----

    [Fact]
    public async Task GetResponseAsync_UserMessageWithImage_ThrowsNotSupportedAndSendsNothing()
    {
        var h = CreateOk();
        var message = new ChatMessage(ChatRole.User, [new TextContent("see"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);

        await Assert.ThrowsAsync<NotSupportedException>(() => h.Client.GetResponseAsync([message]));

        Assert.Empty(h.Handler.Requests);
    }

    [Fact]
    public async Task GetResponseAsync_AssistantMessageWithImage_ThrowsNotSupportedAndSendsNothing()
    {
        var h = CreateOk();
        var message = new ChatMessage(ChatRole.Assistant, [new DataContent(new byte[] { 1 }, "image/png")]);

        await Assert.ThrowsAsync<NotSupportedException>(() => h.Client.GetResponseAsync([User(), message]));

        Assert.Empty(h.Handler.Requests);
    }

    [Fact]
    public async Task GetResponseAsync_ToolMessageWithNonResultContent_ThrowsNotSupportedAndSendsNothing()
    {
        var h = CreateOk();
        var message = new ChatMessage(ChatRole.Tool, [new TextContent("not a result")]);

        await Assert.ThrowsAsync<NotSupportedException>(() => h.Client.GetResponseAsync([User(), message]));

        Assert.Empty(h.Handler.Requests);
    }

    [Fact]
    public async Task GetResponseAsync_UnknownRole_ThrowsNotSupportedAndSendsNothing()
    {
        var h = CreateOk();

        await Assert.ThrowsAsync<NotSupportedException>(
            () => h.Client.GetResponseAsync([new ChatMessage(new ChatRole("narrator"), "x")]));

        Assert.Empty(h.Handler.Requests);
    }

    // ---- response parsing ----

    [Fact]
    public async Task GetResponseAsync_TextResponse_ReturnsTextStopAndResponseId()
    {
        var h = CreateOk();

        var response = await h.Client.GetResponseAsync([User()]);

        Assert.Equal("hello", response.Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal("int-1", response.ResponseId);
        Assert.Equal("gemini-x", response.ModelId);
    }

    [Fact]
    public async Task GetResponseAsync_ResponseWithUsage_MapsTokenCounts()
    {
        var h = CreateOk("""{"id":"i","steps":[{"type":"model_output","content":[{"type":"text","text":"t"}]}],"usage":{"total_input_tokens":11,"total_output_tokens":4,"total_tokens":15}}""");

        var response = await h.Client.GetResponseAsync([User()]);

        Assert.NotNull(response.Usage);
        Assert.Equal(11, response.Usage.InputTokenCount);
        Assert.Equal(4, response.Usage.OutputTokenCount);
        Assert.Equal(15, response.Usage.TotalTokenCount);
    }

    [Fact]
    public async Task GetResponseAsync_ResponseWithoutUsage_UsageIsNull()
    {
        var h = CreateOk();

        var response = await h.Client.GetResponseAsync([User()]);

        Assert.Null(response.Usage);
    }

    [Fact]
    public async Task GetResponseAsync_FunctionCallResponse_ReturnsFunctionCallAndToolCallsFinish()
    {
        var h = CreateOk("""{"id":"i","steps":[{"type":"function_call","id":"c1","name":"get_x","arguments":{"a":1,"b":"s"}}]}""");

        var response = await h.Client.GetResponseAsync([User()]);

        var call = Assert.IsType<FunctionCallContent>(Assert.Single(response.Messages.Single().Contents));
        Assert.Equal("c1", call.CallId);
        Assert.Equal("get_x", call.Name);
        Assert.Equal(1, ((JsonElement)call.Arguments!["a"]!).GetInt32());
        Assert.Equal("s", ((JsonElement)call.Arguments["b"]!).GetString());
        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
    }

    [Fact]
    public async Task GetResponseAsync_UnknownStepTypesAlongsideText_AreIgnoredForContentButKeptInRaw()
    {
        var h = CreateOk("""{"steps":[{"type":"thought","signature":"S"},{"type":"model_output","content":[{"type":"text","text":"t"}]}]}""");

        var response = await h.Client.GetResponseAsync([User()]);

        var message = response.Messages.Single();
        Assert.Equal("t", Assert.IsType<TextContent>(Assert.Single(message.Contents)).Text);
        var raw = Assert.IsAssignableFrom<IReadOnlyList<JsonElement>>(message.RawRepresentation);
        Assert.Equal(2, raw.Count);
    }

    [Theory]
    [InlineData("""{"id":"i","steps":[]}""")]
    [InlineData("""{"id":"i"}""")]
    [InlineData("""{"id":"i","steps":[{"type":"thought","signature":"S"}]}""")]
    public async Task GetResponseAsync_NoTextOrFunctionCall_ThrowsGeminiApiException(string body)
    {
        var h = CreateOk(body);

        await Assert.ThrowsAsync<GeminiApiException>(() => h.Client.GetResponseAsync([User()]));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task GetResponseAsync_NonSuccessStatus_ThrowsGeminiApiExceptionWithoutBodyOrKey(HttpStatusCode status)
    {
        var h = Create(() => Respond($$"""{"error":"BODY-LEAK-123 {{ApiKey}}"}""", status));

        var ex = await Assert.ThrowsAsync<GeminiApiException>(() => h.Client.GetResponseAsync([User()]));

        Assert.Equal(status, ex.StatusCode);
        Assert.Contains(((int)status).ToString(), ex.Message);
        Assert.DoesNotContain("BODY-LEAK-123", ex.Message);
        Assert.DoesNotContain(ApiKey, ex.Message);
        Assert.Single(h.Handler.Requests);
    }

    // ---- model fallback on 429 ----

    private static HttpResponseMessage Limited() => Respond("""{"error":"BODY-LEAK-123"}""", HttpStatusCode.TooManyRequests);

    private static string[] SentModels(Harness h) => h.Bodies.Select(b => b.GetProperty("model").GetString()!).ToArray();

    [Fact]
    public async Task GetResponseAsync_PrimaryRateLimited_FallsBackToNextModelWithSameInput()
    {
        var h = CreateWithFallbacks(["B"], Limited, () => Respond(TextResponse));

        var response = await h.Client.GetResponseAsync([User("same question")]);

        Assert.Equal("hello", response.Text);
        Assert.Equal(["A", "B"], SentModels(h));
        Assert.True(JsonElement.DeepEquals(h.Bodies[0].GetProperty("input"), h.Bodies[1].GetProperty("input")));
    }

    [Fact]
    public async Task GetResponseAsync_AfterFallback_NextCallStartsOnFallbackModel()
    {
        var h = CreateWithFallbacks(["B"], Limited, () => Respond(TextResponse), () => Respond(TextResponse));

        await h.Client.GetResponseAsync([User()]);
        await h.Client.GetResponseAsync([User()]);

        Assert.Equal(["A", "B", "B"], SentModels(h));
    }

    [Fact]
    public async Task GetResponseAsync_ChainOfRateLimits_WalksThroughModelsInOrder()
    {
        var h = CreateWithFallbacks(["B", "C"], Limited, Limited, () => Respond(TextResponse));

        await h.Client.GetResponseAsync([User()]);

        Assert.Equal(["A", "B", "C"], SentModels(h));
    }

    [Fact]
    public async Task GetResponseAsync_AllModelsRateLimited_ThrowsAiRateLimitExceptionListingModelsWithoutLeaks()
    {
        var h = CreateWithFallbacks(["B"], Limited, Limited);

        var ex = await Assert.ThrowsAsync<AiRateLimitException>(() => h.Client.GetResponseAsync([User()]));

        Assert.Equal(["A", "B"], ex.ModelsTried);
        Assert.Equal(["A", "B"], SentModels(h));
        Assert.DoesNotContain("BODY-LEAK-123", ex.Message);
        Assert.DoesNotContain(ApiKey, ex.Message);
    }

    [Fact]
    public async Task GetResponseAsync_AfterAllModelsExhausted_ThrowsImmediatelyWithoutHttpRequest()
    {
        var h = CreateWithFallbacks(["B"], Limited, Limited);
        await Assert.ThrowsAsync<AiRateLimitException>(() => h.Client.GetResponseAsync([User()]));

        await Assert.ThrowsAsync<AiRateLimitException>(() => h.Client.GetResponseAsync([User()]));

        Assert.Equal(2, h.Handler.Requests.Count);
    }

    [Fact]
    public async Task GetResponseAsync_NoFallbackModels_SingleRateLimitThrowsAiRateLimitException()
    {
        var h = CreateWithFallbacks([], Limited);

        var ex = await Assert.ThrowsAsync<AiRateLimitException>(() => h.Client.GetResponseAsync([User()]));

        Assert.Equal(["A"], ex.ModelsTried);
        Assert.Single(h.Handler.Requests);
    }

    [Fact]
    public async Task GetResponseAsync_PrimaryRateLimitedThenFallbackServerError_ThrowsGeminiApiExceptionWithoutFurtherFallback()
    {
        var h = CreateWithFallbacks(["B", "C"], Limited,
            () => Respond("{}", HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<GeminiApiException>(() => h.Client.GetResponseAsync([User()]));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Equal(["A", "B"], SentModels(h));
    }

    [Fact]
    public async Task GetResponseAsync_ExplicitModelId_IgnoresFallbackListAndStickyIndex()
    {
        var h = CreateWithFallbacks(["B"], Limited, () => Respond(TextResponse), () => Respond(TextResponse));

        await h.Client.GetResponseAsync([User()]); // A limited -> B (sticky index now at B)
        await h.Client.GetResponseAsync([User()], new ChatOptions { ModelId = "X" });

        Assert.Equal(["A", "B", "X"], SentModels(h));
    }

    [Fact]
    public async Task GetResponseAsync_ExplicitModelIdRateLimited_ThrowsWithThatModelOnlyAndNoFallback()
    {
        var h = CreateWithFallbacks(["B"], Limited);

        var ex = await Assert.ThrowsAsync<AiRateLimitException>(
            () => h.Client.GetResponseAsync([User()], new ChatOptions { ModelId = "X" }));

        Assert.Equal(["X"], ex.ModelsTried);
        Assert.Equal(["X"], SentModels(h));
    }

    [Fact]
    public async Task GetResponseAsync_ExplicitModelIdRateLimited_DoesNotAdvanceStickyIndex()
    {
        var h = CreateWithFallbacks(["B"], Limited, () => Respond(TextResponse));
        await Assert.ThrowsAsync<AiRateLimitException>(
            () => h.Client.GetResponseAsync([User()], new ChatOptions { ModelId = "X" }));

        await h.Client.GetResponseAsync([User()]);

        Assert.Equal(["X", "A"], SentModels(h));
    }

    [Fact]
    public async Task GetResponseAsync_BlankAndDuplicateModels_AreNormalised()
    {
        var h = CreateWithFallbacks([" B ", "", "   ", "A", "B", " A "], Limited, Limited);

        var ex = await Assert.ThrowsAsync<AiRateLimitException>(() => h.Client.GetResponseAsync([User()]));

        Assert.Equal(["A", "B"], ex.ModelsTried);
        Assert.Equal(["A", "B"], SentModels(h));
    }

    [Fact]
    public async Task GetResponseAsync_FallbackSucceeds_ResponseModelIdFallsBackToUsedModelWhenNotReported()
    {
        var h = CreateWithFallbacks(["B"], Limited,
            () => Respond("""{"id":"i","steps":[{"type":"model_output","content":[{"type":"text","text":"t"}]}]}"""));

        var response = await h.Client.GetResponseAsync([User()]);

        Assert.Equal("B", response.ModelId);
    }

    // ---- 503 retry ----

    private static HttpResponseMessage Overloaded() => Respond("""{"error":"BODY-LEAK-123"}""", HttpStatusCode.ServiceUnavailable);

    [Fact]
    public async Task GetResponseAsync_503ThenSuccess_RetriesSameModelAndReturnsResponse()
    {
        var h = CreateWithFallbacks(["B"], Overloaded, () => Respond(TextResponse));

        var response = await h.Client.GetResponseAsync([User()]);

        Assert.Equal("hello", response.Text);
        Assert.Equal(["A", "A"], SentModels(h));
    }

    [Fact]
    public async Task GetResponseAsync_Two503ThenSuccess_SucceedsOnThirdAttempt()
    {
        var h = CreateWithFallbacks(["B"], Overloaded, Overloaded, () => Respond(TextResponse));

        var response = await h.Client.GetResponseAsync([User()]);

        Assert.Equal("hello", response.Text);
        Assert.Equal(["A", "A", "A"], SentModels(h));
    }

    [Fact]
    public async Task GetResponseAsync_Persistent503_ThrowsGeminiApiExceptionAfterThreeRequestsWithoutFallback()
    {
        var h = CreateWithFallbacks(["B"], Overloaded, Overloaded, Overloaded);

        var ex = await Assert.ThrowsAsync<GeminiApiException>(() => h.Client.GetResponseAsync([User()]));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Equal(["A", "A", "A"], SentModels(h));
        Assert.DoesNotContain("BODY-LEAK-123", ex.Message);
        Assert.DoesNotContain(ApiKey, ex.Message);
    }

    [Fact]
    public async Task GetResponseAsync_503ThenRateLimit_FallsBackToNextModel()
    {
        var h = CreateWithFallbacks(["B"], Overloaded, Limited, () => Respond(TextResponse));

        var response = await h.Client.GetResponseAsync([User()]);

        Assert.Equal("hello", response.Text);
        Assert.Equal(["A", "A", "B"], SentModels(h));
    }

    [Fact]
    public async Task GetResponseAsync_ServerError500_IsNotRetried()
    {
        var h = Create(() => Respond("{}", HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<GeminiApiException>(() => h.Client.GetResponseAsync([User()]));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Single(h.Handler.Requests);
    }

    [Fact]
    public async Task GetResponseAsync_503WithExplicitModelId_RetriesSameExplicitModel()
    {
        var h = CreateWithFallbacks(["B"], Overloaded, () => Respond(TextResponse));

        await h.Client.GetResponseAsync([User()], new ChatOptions { ModelId = "X" });

        Assert.Equal(["X", "X"], SentModels(h));
    }

    [Fact]
    public async Task GetResponseAsync_CancelledDuringBackoff_ThrowsOperationCanceledWithoutSecondRequest()
    {
        using var cts = new CancellationTokenSource();
        var h = CreateWith(
            new AiModelOptions { Model = "A", ApiKey = ApiKey },
            TimeSpan.FromHours(1),
            () =>
            {
                cts.Cancel();
                return Overloaded();
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Client.GetResponseAsync([User()], null, cts.Token));

        Assert.Single(h.Handler.Requests);
    }

    // ---- round trips ----

    [Fact]
    public async Task GetResponseAsync_ToolResultAfterRawAssistantMessage_ReplaysStepsVerbatimAndAddsFunctionResult()
    {
        const string thought = """{"type":"thought","signature":"SIG"}""";
        const string call = """{"type":"function_call","id":"c1","name":"get_x","arguments":{"a":1},"thought_signature":"TS"}""";
        var h = Create(() => Respond($$"""{"id":"i1","steps":[{{thought}},{{call}}]}"""), () => Respond(TextResponse));
        var user = User("question");
        var first = await h.Client.GetResponseAsync([user]);

        await h.Client.GetResponseAsync(
            [user, .. first.Messages, new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "res")])]);

        var input = h.Bodies[1].GetProperty("input").EnumerateArray().ToList();
        Assert.Equal(4, input.Count);
        AssertJsonEqual("""{"type":"user_input","content":"question"}""", input[0]);
        AssertJsonEqual(thought, input[1]);
        AssertJsonEqual(call, input[2]);
        AssertJsonEqual("""{"type":"function_result","name":"get_x","call_id":"c1","result":[{"type":"text","text":"res"}]}""", input[3]);
    }

    [Fact]
    public async Task GetResponseAsync_HandBuiltAssistantMessage_RebuildsSteps()
    {
        var h = CreateOk();
        var assistant = new ChatMessage(ChatRole.Assistant,
        [
            new TextContent("thinking"),
            new FunctionCallContent("c9", "get_y", new Dictionary<string, object?> { ["p"] = "v" })
        ]);
        var tool = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c9", "done")]);

        await h.Client.GetResponseAsync([User("q"), assistant, tool]);

        var input = h.Bodies.Single().GetProperty("input").EnumerateArray().ToList();
        Assert.Equal(4, input.Count);
        AssertJsonEqual("""{"type":"model_output","content":[{"type":"text","text":"thinking"}]}""", input[1]);
        AssertJsonEqual("""{"type":"function_call","id":"c9","name":"get_y","arguments":{"p":"v"}}""", input[2]);
        AssertJsonEqual("""{"type":"function_result","name":"get_y","call_id":"c9","result":[{"type":"text","text":"done"}]}""", input[3]);
    }

    [Fact]
    public async Task GetResponseAsync_ObjectToolResult_IsSerializedAsJsonText()
    {
        var h = CreateOk();
        var assistant = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "get_x")]);
        var tool = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", new { Value = 5, Name = "n" })]);

        await h.Client.GetResponseAsync([User(), assistant, tool]);

        var result = h.Bodies.Single().GetProperty("input").EnumerateArray().Last();
        var text = result.GetProperty("result")[0].GetProperty("text").GetString()!;
        var parsed = JsonDocument.Parse(text).RootElement;
        Assert.Equal(5, parsed.GetProperty("value").GetInt32());
        Assert.Equal("n", parsed.GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetResponseAsync_JsonElementStringToolResult_IsSentAsPlainTextNotQuotedJson()
    {
        var h = CreateOk();
        var assistant = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "get_x")]);
        var tool = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", J("\"line1\\nsaid \\\"hi\\\"\""))]);

        await h.Client.GetResponseAsync([User(), assistant, tool]);

        var result = h.Bodies.Single().GetProperty("input").EnumerateArray().Last();
        Assert.Equal("line1\nsaid \"hi\"", result.GetProperty("result")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task GetResponseAsync_ToolResultWithUnknownCallId_ThrowsInvalidOperationAndSendsNothing()
    {
        var h = CreateOk();
        var tool = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("nope", "res")]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Client.GetResponseAsync([User(), tool]));

        Assert.Empty(h.Handler.Requests);
    }

    // ---- misc ----

    [Fact]
    public void GetStreamingResponseAsync_ThrowsNotSupported()
    {
        var h = CreateOk();

        Assert.Throws<NotSupportedException>(() => h.Client.GetStreamingResponseAsync([User()]));
    }

    [Fact]
    public void GetService_OwnTypeOrInterfaceWithoutKey_ReturnsSelf()
    {
        var client = CreateOk().Client;

        Assert.Same(client, client.GetService(typeof(GeminiChatClient)));
        Assert.Same(client, client.GetService(typeof(IChatClient)));
    }

    [Fact]
    public void GetService_WithKeyOrOtherType_ReturnsNull()
    {
        var client = CreateOk().Client;

        Assert.Null(client.GetService(typeof(IChatClient), "key"));
        Assert.Null(client.GetService(typeof(string)));
    }

    // ---- DI registration ----

    private static ServiceCollection DiServices(Dictionary<string, string?> config)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        return services;
    }

    [Fact]
    public async Task AddGeminiChatClient_ResolvedClient_UsesBaseAddressAndHeadersFromOptions()
    {
        var handler = new StubHttpMessageHandler(_ => Respond(TextResponse));
        var services = DiServices(new() { ["AiModel:Model"] = "cfg-model", ["AiModel:ApiKey"] = "cfg-key" });
        services.AddGeminiChatClient().ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IChatClient>();
        await client.GetResponseAsync([User()]);

        Assert.IsType<GeminiChatClient>(client);
        var request = handler.Requests.Single();
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/interactions", request.RequestUri!.AbsoluteUri);
        Assert.Equal("2026-05-20", Assert.Single(request.Headers.GetValues("Api-Revision")));
        Assert.Equal("cfg-key", Assert.Single(request.Headers.GetValues("x-goog-api-key")));
    }

    [Fact]
    public void AddGeminiChatClient_MissingApiKey_ThrowsOptionsValidationExceptionOnResolve()
    {
        var services = DiServices(new() { ["AiModel:Model"] = "cfg-model" });
        services.AddGeminiChatClient();
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AiModelOptions>>().Value);
    }
}
