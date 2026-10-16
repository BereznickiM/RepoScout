using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace RepoScout.Api.Ai;

public sealed class GeminiChatClient(HttpClient http, IOptions<AiModelOptions> options) : IChatClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? chatOptions = null, CancellationToken cancellationToken = default)
    {
        var model = chatOptions?.ModelId ?? options.Value.Model;
        var request = BuildRequest(model, messages, chatOptions);

        using var response = await http.PostAsJsonAsync("v1beta/interactions", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new GeminiApiException(response.StatusCode, $"Gemini returned {(int)response.StatusCode}");
        }

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
            FinishReason = contents.Any(c => c is FunctionCallContent) ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop
        };
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
        var system = new List<string>();
        var input = new List<object>();
        var callNames = new Dictionary<string, string>();

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                system.Add(message.Text);
            }
            else if (message.Role == ChatRole.User)
            {
                var unsupported = message.Contents.FirstOrDefault(c => c is not TextContent);
                if (unsupported is not null)
                {
                    throw new NotSupportedException($"Unsupported content {unsupported.GetType().Name} in user message.");
                }

                input.Add(new UserInputStep("user_input", message.Text));
            }
            else if (message.Role == ChatRole.Assistant)
            {
                AddAssistant(message, input, callNames);
            }
            else if (message.Role == ChatRole.Tool)
            {
                foreach (var content in message.Contents)
                {
                    if (content is not FunctionResultContent result)
                    {
                        throw new NotSupportedException($"Unsupported content {content.GetType().Name} in tool message.");
                    }

                    if (!callNames.TryGetValue(result.CallId, out var name))
                    {
                        throw new InvalidOperationException($"No function_call for call_id {result.CallId}");
                    }

                    var text = result.Result as string ?? JsonSerializer.Serialize(result.Result, JsonOptions);
                    input.Add(new FunctionResultStep("function_result", name, result.CallId, [new TextPart("text", text)]));
                }
            }
            else
            {
                throw new NotSupportedException($"Unsupported chat role {message.Role}.");
            }
        }

        List<ToolDeclaration>? tools = null;
        if (chatOptions?.Tools is { Count: > 0 })
        {
            tools = [];
            foreach (var tool in chatOptions.Tools)
            {
                if (tool is not AIFunction f)
                {
                    throw new NotSupportedException($"Unsupported tool type {tool.GetType().Name}.");
                }

                tools.Add(new ToolDeclaration("function", f.Name, f.Description, f.JsonSchema));
            }
        }

        GenerationConfig? config = chatOptions?.ToolMode switch
        {
            null => null,
            AutoChatToolMode => new GenerationConfig("auto"),
            NoneChatToolMode => new GenerationConfig("none"),
            RequiredChatToolMode { RequiredFunctionName: null } => new GenerationConfig("any"),
            _ => throw new NotSupportedException("Requiring a specific tool is not supported.")
        };

        return new InteractionRequest(
            model,
            input,
            false,
            system.Count > 0 ? string.Join("\n\n", system) : null,
            tools,
            config);
    }

    private static void AddAssistant(ChatMessage message, List<object> input, Dictionary<string, string> callNames)
    {
        // Calls are registered in both branches so later tool results can resolve the function name.
        if (message.RawRepresentation is IReadOnlyList<JsonElement> steps)
        {
            foreach (var step in steps)
            {
                input.Add(step);
                if (StepType(step) == "function_call"
                    && step.TryGetProperty("id", out var id) && id.GetString() is { } callId
                    && step.TryGetProperty("name", out var name) && name.GetString() is { } functionName)
                {
                    callNames[callId] = functionName;
                }
            }

            return;
        }

        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    input.Add(new ModelOutputStep("model_output", [new TextPart("text", text.Text)]));
                    break;
                case FunctionCallContent call:
                    callNames[call.CallId] = call.Name;
                    input.Add(new FunctionCallStep(
                        "function_call", call.CallId, call.Name, call.Arguments ?? new Dictionary<string, object?>()));
                    break;
                default:
                    throw new NotSupportedException($"Unsupported content {content.GetType().Name} in assistant message.");
            }
        }
    }

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

    private sealed record UserInputStep(string Type, string Content);

    private sealed record ModelOutputStep(string Type, List<TextPart> Content);

    private sealed record FunctionCallStep(string Type, string Id, string Name, IDictionary<string, object?> Arguments);

    private sealed record FunctionResultStep(string Type, string Name, string CallId, List<TextPart> Result);

    private sealed record TextPart(string Type, string Text);

    private sealed record ToolDeclaration(string Type, string Name, string Description, JsonElement Parameters);

    private sealed record GenerationConfig(string ToolChoice);

    private sealed record InteractionResponse(string? Id, string? Model, List<JsonElement>? Steps);
}
