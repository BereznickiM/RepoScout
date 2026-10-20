using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace RepoScout.Api.Ai;

public sealed class GeminiChatClient(HttpClient http, IOptions<AiModelOptions> options, ILogger<GeminiChatClient> logger) : IChatClient
{
    // Primary model first, then fallbacks. The index is sticky: once a model is rate-limited it is not retried.
    private readonly List<string> models = new[] { options.Value.Model }.Concat(options.Value.FallbackModels)
        .Select(m => m?.Trim() ?? "").Where(m => m.Length > 0).Distinct().ToList();

    private int currentModel;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? chatOptions = null, CancellationToken cancellationToken = default)
    {
        var history = messages.ToList();
        var (model, response) = await SendAsync(history, chatOptions, cancellationToken);
        using var _ = response;

        var dto = await response.Content.ReadFromJsonAsync<InteractionResponse>(JsonOptions, cancellationToken)
            ?? throw new GeminiApiException(response.StatusCode, "Gemini returned an empty response");

        var steps = dto.Steps ?? [];
        var contents = new List<AIContent>();
        foreach (var step in steps)
        {
            ReadStep(step, contents);
        }

        if (contents.Count == 0)
        {
            throw new GeminiApiException(response.StatusCode, "Gemini returned no text or function call");
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents) { RawRepresentation = (IReadOnlyList<JsonElement>)steps })
        {
            ResponseId = dto.Id,
            ModelId = dto.Model ?? model,
            Usage = dto.Usage is { } u
                ? new UsageDetails
                {
                    InputTokenCount = u.TotalInputTokens,
                    OutputTokenCount = u.TotalOutputTokens,
                    TotalTokenCount = u.TotalTokens
                }
                : null,
            FinishReason = contents.Any(c => c is FunctionCallContent) ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop
        };
    }

    // Sends the request to the current model; on 429 moves on to the next configured model with the same history.
    // An explicit ChatOptions.ModelId bypasses the fallback list.
    private async Task<(string Model, HttpResponseMessage Response)> SendAsync(
        List<ChatMessage> history, ChatOptions? chatOptions, CancellationToken cancellationToken)
    {
        if (chatOptions?.ModelId is { } explicitModel)
        {
            var explicitResponse = await TryPostAsync(explicitModel, history, chatOptions, cancellationToken);
            return explicitResponse is null
                ? throw new AiRateLimitException([explicitModel])
                : (explicitModel, explicitResponse);
        }

        while (currentModel < models.Count)
        {
            var model = models[currentModel];
            var response = await TryPostAsync(model, history, chatOptions, cancellationToken);
            if (response is not null)
            {
                return (model, response);
            }

            currentModel++;
        }

        throw new AiRateLimitException(models);
    }

    // Posts to one model. Returns null when it is rate-limited (429); throws on any other failure.
    private async Task<HttpResponseMessage?> TryPostAsync(
        string model, List<ChatMessage> history, ChatOptions? chatOptions, CancellationToken cancellationToken)
    {
        var request = BuildRequest(model, history, chatOptions);
        var response = await http.PostAsJsonAsync("v1beta/interactions", request, JsonOptions, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var status = response.StatusCode;
        response.Dispose();
        if (status != HttpStatusCode.TooManyRequests)
        {
            throw new GeminiApiException(status, $"Gemini returned {(int)status}");
        }

        logger.LogWarning("Model {Model} is rate-limited (429).", model);
        return null;
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? chatOptions = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Streaming is not supported.");

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private static InteractionRequest BuildRequest(string model, IEnumerable<ChatMessage> messages, ChatOptions? chatOptions)
    {
        var list = messages.ToList();
        var system = string.Join("\n\n", list.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
        var callNames = CollectCallNames(list);

        // ToList() maps (and validates) the whole history before any HTTP call is made.
        return new InteractionRequest(
            model,
            Input: list.Where(m => m.Role != ChatRole.System).SelectMany(m => ToSteps(m, callNames)).ToList(),
            Store: false,
            SystemInstruction: system.Length > 0 ? system : null,
            Tools: ToToolDeclarations(chatOptions?.Tools),
            GenerationConfig: ToGenerationConfig(chatOptions?.ToolMode));
    }

    private static IEnumerable<object> ToSteps(ChatMessage message, IReadOnlyDictionary<string, string> callNames)
    {
        if (message.Role == ChatRole.User) return [ToUserStep(message)];
        if (message.Role == ChatRole.Assistant) return ToAssistantSteps(message);
        if (message.Role == ChatRole.Tool) return message.Contents.Select(c => ToFunctionResultStep(c, callNames));
        throw new NotSupportedException($"Unsupported chat role {message.Role}.");
    }

    private static UserInputStep ToUserStep(ChatMessage message)
    {
        if (message.Contents.FirstOrDefault(c => c is not TextContent) is { } unsupported)
        {
            throw Unsupported(unsupported, "user");
        }

        return new UserInputStep(message.Text);
    }

    private static IEnumerable<object> ToAssistantSteps(ChatMessage message)
    {
        // Gemini requires its own steps (thoughts, signatures) to be replayed unchanged.
        if (message.RawRepresentation is IReadOnlyList<JsonElement> steps)
        {
            return steps.Cast<object>();
        }

        return message.Contents.Select<AIContent, object>(content => content switch
        {
            TextContent text => new ModelOutputStep([new TextPart(text.Text)]),
            FunctionCallContent call => new FunctionCallStep(call.CallId, call.Name, call.Arguments ?? new Dictionary<string, object?>()),
            _ => throw Unsupported(content, "assistant")
        });
    }

    private static FunctionResultStep ToFunctionResultStep(AIContent content, IReadOnlyDictionary<string, string> callNames)
    {
        if (content is not FunctionResultContent result)
        {
            throw Unsupported(content, "tool");
        }

        if (!callNames.TryGetValue(result.CallId, out var name))
        {
            throw new InvalidOperationException($"No function_call for call_id {result.CallId}");
        }

        var text = result.Result switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString()!,
            _ => JsonSerializer.Serialize(result.Result, JsonOptions)
        };
        return new FunctionResultStep(name, result.CallId, [new TextPart(text)]);
    }

    // function_result requires the function name, which FunctionResultContent lacks; resolve it from the calls in history.
    private static Dictionary<string, string> CollectCallNames(IEnumerable<ChatMessage> messages)
    {
        var names = new Dictionary<string, string>();
        foreach (var message in messages.Where(m => m.Role == ChatRole.Assistant))
        {
            if (message.RawRepresentation is IReadOnlyList<JsonElement> steps)
            {
                foreach (var step in steps.Where(s => StepType(s) == "function_call"))
                {
                    if (step.TryGetProperty("id", out var id) && id.GetString() is { } callId
                        && step.TryGetProperty("name", out var name) && name.GetString() is { } functionName)
                    {
                        names[callId] = functionName;
                    }
                }
            }
            else
            {
                foreach (var call in message.Contents.OfType<FunctionCallContent>())
                {
                    names[call.CallId] = call.Name;
                }
            }
        }

        return names;
    }

    private static List<ToolDeclaration>? ToToolDeclarations(IList<AITool>? tools)
        => tools is { Count: > 0 }
            ? tools.Select(tool => tool is AIFunction f
                ? new ToolDeclaration(f.Name, f.Description, f.JsonSchema)
                : throw new NotSupportedException($"Unsupported tool type {tool.GetType().Name}.")).ToList()
            : null;

    private static GenerationConfig? ToGenerationConfig(ChatToolMode? toolMode) => toolMode switch
    {
        null => null,
        AutoChatToolMode => new GenerationConfig("auto"),
        NoneChatToolMode => new GenerationConfig("none"),
        RequiredChatToolMode { RequiredFunctionName: null } => new GenerationConfig("any"),
        _ => throw new NotSupportedException("Requiring a specific tool is not supported.")
    };

    private static NotSupportedException Unsupported(AIContent content, string role)
        => new($"Unsupported content {content.GetType().Name} in {role} message.");

    private static string? StepType(JsonElement step)
        => step.ValueKind == JsonValueKind.Object && step.TryGetProperty("type", out var t) ? t.GetString() : null;

    private static void ReadStep(JsonElement step, List<AIContent> contents)
    {
        switch (StepType(step))
        {
            case "model_output":
                if (step.TryGetProperty("content", out var parts) && parts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (StepType(part) == "text" && part.TryGetProperty("text", out var text) && text.GetString() is { } value)
                        {
                            contents.Add(new TextContent(value));
                        }
                    }
                }

                break;
            case "function_call":
                var args = new Dictionary<string, object?>();
                if (step.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in arguments.EnumerateObject())
                    {
                        args[p.Name] = p.Value.Clone();
                    }
                }

                contents.Add(new FunctionCallContent(
                    step.GetProperty("id").GetString() ?? "",
                    step.GetProperty("name").GetString() ?? "",
                    args));
                break;
        }
    }

    private sealed record InteractionRequest(
        string Model,
        List<object> Input,
        bool Store,
        string? SystemInstruction,
        List<ToolDeclaration>? Tools,
        GenerationConfig? GenerationConfig);

    // Each step/part record carries its fixed Gemini "type" discriminator.
    private sealed record UserInputStep(string Content)
    {
        public string Type => "user_input";
    }

    private sealed record ModelOutputStep(List<TextPart> Content)
    {
        public string Type => "model_output";
    }

    private sealed record FunctionCallStep(string Id, string Name, IDictionary<string, object?> Arguments)
    {
        public string Type => "function_call";
    }

    private sealed record FunctionResultStep(string Name, string CallId, List<TextPart> Result)
    {
        public string Type => "function_result";
    }

    private sealed record TextPart(string Text)
    {
        public string Type => "text";
    }

    private sealed record ToolDeclaration(string Name, string Description, JsonElement Parameters)
    {
        public string Type => "function";
    }

    private sealed record GenerationConfig(string ToolChoice);

    private sealed record InteractionResponse(string? Id, string? Model, List<JsonElement>? Steps, InteractionUsage? Usage);

    private sealed record InteractionUsage(long? TotalInputTokens, long? TotalOutputTokens, long? TotalTokens);
}
