using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GeminiCli.Core.Configuration;
using GeminiCli.Core.Logging;
using GeminiCli.Core.Types;

namespace GeminiCli.Core.Chat;

/// <summary>
/// Ollama API backed content generator.
/// Uses /api/chat endpoint and supports basic tool-calling payloads.
/// </summary>
public sealed class OllamaContentGenerator : IContentGenerator, IAsyncDisposable
{
    private readonly Config _config;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public OllamaContentGenerator(Config config, HttpClient? httpClient = null)
    {
        _config = config;
        _logger = LoggerHelper.ForContext<OllamaContentGenerator>();
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public string GetModelId() => _config.GetModel();

    public async Task<ContentMessage> SendMessageAsync(
        ContentMessage message,
        CancellationToken cancellationToken = default)
    {
        var request = new GenerateContentRequest
        {
            Model = _config.GetModel(),
            Contents = new List<ContentMessage> { message }
        };

        var response = await GenerateContentAsync(request, cancellationToken);
        var first = response.Candidates.FirstOrDefault();

        return first is null
            ? ContentMessage.ModelMessage(string.Empty)
            : new ContentMessage
            {
                Role = LlmRole.Model,
                Parts = first.Content
            };
    }

    public async IAsyncEnumerable<ContentMessage> SendMessageStreamAsync(
        ContentMessage message,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await SendMessageAsync(message, cancellationToken);
        yield return response;
    }

    public async Task<GenerateContentResponse> GenerateContentAsync(
        GenerateContentRequest request,
        CancellationToken cancellationToken = default)
    {
        var endpoint = BuildChatEndpoint();
        var payload = BuildChatPayload(request);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Ollama chat failed ({(int)response.StatusCode} {response.StatusCode}): {content}");
        }

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        var parts = new List<ContentPart>();
        if (root.TryGetProperty("message", out var messageElement))
        {
            if (messageElement.TryGetProperty("content", out var textElement))
            {
                var text = textElement.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    parts.Add(new TextContentPart(text));
                }
            }

            if (messageElement.TryGetProperty("tool_calls", out var toolCallsElement) &&
                toolCallsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var toolCall in toolCallsElement.EnumerateArray())
                {
                    if (!toolCall.TryGetProperty("function", out var fnElement))
                    {
                        continue;
                    }

                    var functionName = fnElement.TryGetProperty("name", out var fnName)
                        ? fnName.GetString() ?? string.Empty
                        : string.Empty;

                    if (string.IsNullOrWhiteSpace(functionName))
                    {
                        continue;
                    }

                    Dictionary<string, object?> args;
                    if (fnElement.TryGetProperty("arguments", out var argsElement))
                    {
                        if (argsElement.ValueKind == JsonValueKind.String)
                        {
                            var json = argsElement.GetString() ?? "{}";
                            args = JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? new Dictionary<string, object?>();
                        }
                        else
                        {
                            args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsElement.GetRawText()) ?? new Dictionary<string, object?>();
                        }
                    }
                    else
                    {
                        args = new Dictionary<string, object?>();
                    }

                    parts.Add(new FunctionCallPart
                    {
                        FunctionName = functionName,
                        Arguments = args,
                        Id = Guid.NewGuid().ToString()
                    });
                }
            }
        }

        if (parts.Count == 0)
        {
            parts.Add(new TextContentPart(string.Empty));
        }

        return new GenerateContentResponse
        {
            Candidates = new List<Candidate>
            {
                new()
                {
                    Content = parts,
                    Index = 0,
                    FinishReason = FinishReason.Stop
                }
            },
            ModelVersion = request.Model
        };
    }

    public async IAsyncEnumerable<GenerateContentResponse> GenerateContentStreamAsync(
        GenerateContentRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return await GenerateContentAsync(request, cancellationToken);
    }

    public Task<CountTokensResponse> CountTokensAsync(
        CountTokensRequest request,
        CancellationToken cancellationToken = default)
    {
        var totalText = request.Contents
            .SelectMany(c => c.Parts)
            .OfType<TextContentPart>()
            .Sum(p => p.Text.Length);

        return Task.FromResult(new CountTokensResponse
        {
            TotalTokens = Math.Max(1, totalText / 4)
        });
    }

    public Task<EmbedContentResponse> EmbedContentAsync(
        EmbedContentRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.Warning("EmbedContent is not implemented for OllamaContentGenerator, returning zero vector.");
        return Task.FromResult(new EmbedContentResponse
        {
            Embedding = new List<double> { 0.0, 0.0, 0.0 }
        });
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private string BuildChatEndpoint()
    {
        var baseUrl = _config.GetBaseUrl().TrimEnd('/');
        return $"{baseUrl}/api/chat";
    }

    private JsonObject BuildChatPayload(GenerateContentRequest request)
    {
        var payload = new JsonObject
        {
            ["model"] = request.Model,
            ["stream"] = false,
            ["messages"] = ToOllamaMessages(request.Contents)
        };

        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = ToOllamaTools(request.Tools);
        }

        if (request.Config is not null)
        {
            var options = new JsonObject();
            if (request.Config.Temperature is not null) options["temperature"] = request.Config.Temperature.Value;
            if (request.Config.TopP is not null) options["top_p"] = request.Config.TopP.Value;
            if (request.Config.TopK is not null) options["top_k"] = request.Config.TopK.Value;
            if (options.Count > 0) payload["options"] = options;
        }

        return payload;
    }

    private static JsonArray ToOllamaMessages(IEnumerable<ContentMessage> contents)
    {
        var messages = new JsonArray();

        foreach (var msg in contents)
        {
            var textParts = msg.Parts.OfType<TextContentPart>().Select(p => p.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            var functionCalls = msg.Parts.OfType<FunctionCallPart>().ToList();
            var functionResponses = msg.Parts.OfType<FunctionResponsePart>().ToList();

            if (functionResponses.Count > 0)
            {
                foreach (var fr in functionResponses)
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["name"] = fr.FunctionName,
                        ["content"] = JsonSerializer.Serialize(fr.Response)
                    });
                }

                continue;
            }

            var role = msg.Role switch
            {
                LlmRole.Model => "assistant",
                LlmRole.System => "system",
                _ => "user"
            };

            var messageObj = new JsonObject
            {
                ["role"] = role,
                ["content"] = string.Join("\n", textParts)
            };

            if (functionCalls.Count > 0)
            {
                var calls = new JsonArray();
                foreach (var fc in functionCalls)
                {
                    calls.Add(new JsonObject
                    {
                        ["function"] = new JsonObject
                        {
                            ["name"] = fc.FunctionName,
                            ["arguments"] = JsonSerializer.SerializeToNode(fc.Arguments)
                        }
                    });
                }

                messageObj["tool_calls"] = calls;
            }

            messages.Add(messageObj);
        }

        return messages;
    }

    private static JsonArray ToOllamaTools(IEnumerable<Tool> tools)
    {
        var toolArray = new JsonArray();

        foreach (var tool in tools)
        {
            foreach (var fn in tool.FunctionDeclarations ?? Array.Empty<FunctionDeclaration>())
            {
                toolArray.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = fn.Name,
                        ["description"] = fn.Description,
                        ["parameters"] = fn.Parameters is null
                            ? null
                            : JsonSerializer.SerializeToNode(fn.Parameters)
                    }
                });
            }
        }

        return toolArray;
    }
}
