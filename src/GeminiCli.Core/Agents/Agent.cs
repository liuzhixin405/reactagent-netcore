using GeminiCli.Core.Chat;
using GeminiCli.Core.Logging;
using GeminiCli.Core.Tools;
using System.Diagnostics;
using System.Text.Json;

namespace GeminiCli.Core.Agents;

/// <summary>
/// Base agent class with common functionality.
/// </summary>
public abstract class Agent : IAgent
{
    protected readonly ILogger _logger;
    protected readonly CancellationTokenSource _cts = new();
    protected readonly List<ContentMessage> _messageHistory = new();
    protected readonly List<string> _toolCallHistory = new();
    protected readonly object _stateLock = new();

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public AgentKind Kind { get; }
    public List<string> Capabilities { get; }
    public ToolRegistry ToolRegistry { get; }
    public IContentGenerator Chat { get; }
    public AgentExecutionState State { get; protected set; } = AgentExecutionState.Idle;

    public event EventHandler<AgentEvent>? OnEvent;

    protected Agent(
        string id,
        string name,
        string description,
        AgentKind kind,
        List<string> capabilities,
        ToolRegistry toolRegistry,
        IContentGenerator chat)
    {
        Id = id;
        Name = name;
        Description = description;
        Kind = kind;
        Capabilities = capabilities;
        ToolRegistry = toolRegistry;
        Chat = chat;
        _logger = LoggerHelper.ForContext<Agent>();
    }

    /// <summary>
    /// Executes the agent with an initial message.
    /// </summary>
    public virtual async Task<AgentResult> ExecuteAsync(
        ContentMessage message,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var messages = new List<ContentMessage>();
        var toolCalls = new List<string>();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _cts.Token, cancellationToken);

        lock (_stateLock)
        {
            State = AgentExecutionState.Running;
        }

        EmitEvent(AgentEventType.Started, $"Starting agent: {Name}");

        try
        {
            var initialMessage = await TryEnrichInitialMessageAsync(message, linkedCts.Token);

            // Add initial message to history
            _messageHistory.Add(initialMessage);
            messages.Add(initialMessage);

            // Process message through generator with tool schemas enabled
            var response = await GenerateResponseWithToolsAsync(linkedCts.Token);
            _messageHistory.Add(response);
            messages.Add(response);

            // Handle any tool calls in the response
            var turnCount = 0;
            while (ContainsToolCalls(response) && turnCount < 100)
            {
                turnCount++;

                // Extract and execute tool calls
                var toolCallParts = response.Parts
                    .OfType<FunctionCallPart>()
                    .ToList();

                foreach (var toolCall in toolCallParts)
                {
                    // Debug log: raw function call arguments as JSON
                    var argsJson = JsonSerializer.Serialize(toolCall.Arguments);
                    _logger.Information("Preparing to call tool {Tool} with args: {ArgsJson}", toolCall.FunctionName, argsJson);
                    toolCalls.Add($"{toolCall.FunctionName}({argsJson})");
                    EmitEvent(AgentEventType.ToolCalled, $"Calling tool: {toolCall.FunctionName}");

                    var tool = ToolRegistry.GetTool(toolCall.FunctionName);
                    if (tool == null)
                    {
                        var errorMsg = $"Tool not found: {toolCall.FunctionName}";
                        _logger.Warning(errorMsg);
                        var errorResult = new ToolExecutionResult
                        {
                            IsError = true,
                            Output = errorMsg,
                            Content = new List<ContentPart>
                            {
                                new TextContentPart(errorMsg)
                            }
                        };
                        _messageHistory.Add(CreateToolResultMessage(
                            toolCall.FunctionName, errorResult));
                    }
                    else
                    {
                        // Log discovered tool builder details for debugging
                        try
                        {
                            _logger.Information("Discovered tool builder: Name={Name}, DisplayName={Display}, Kind={Kind}, Type={Type}",
                                tool.Name,
                                tool.DisplayName,
                                tool.Kind,
                                tool.GetType().FullName);

                            try
                            {
                                var schema = tool.GetSchema();
                                var schemaJson = JsonSerializer.Serialize(schema);
                                _logger.Information("Tool schema for {Tool}: {Schema}", tool.Name, schemaJson);
                            }
                            catch (Exception exSchema)
                            {
                                _logger.Warning(exSchema, "Failed to retrieve/serialize schema for tool: {Tool}", tool.Name);
                            }
                        }
                        catch (Exception exLog)
                        {
                            _logger.Warning(exLog, "Failed logging tool builder info for: {Tool}", toolCall.FunctionName);
                        }

                        try
                        {
                            var result = await ExecuteToolAsync(
                                tool,
                                toolCall.Arguments,
                                linkedCts.Token);

                            // Log tool execution result summary
                            _logger.Information("Tool {Tool} executed. Success: {Success}, OutputLen: {Len}",
                                toolCall.FunctionName,
                                result.Error is null,
                                result.Output?.Length ?? 0);

                            _messageHistory.Add(CreateToolResultMessage(
                                toolCall.FunctionName, result));

                            EmitEvent(AgentEventType.ToolCompleted,
                                $"Tool completed: {toolCall.FunctionName}");
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "Error executing tool: {Tool}", toolCall.FunctionName);
                            var errorResult = new ToolExecutionResult
                            {
                                IsError = true,
                                Output = ex.Message,
                                Content = new List<ContentPart>
                                {
                                    new TextContentPart(ex.Message)
                                }
                            };
                            _messageHistory.Add(CreateToolResultMessage(
                                toolCall.FunctionName, errorResult));
                        }
                    }
                }

                // Get next response from updated history (which now includes function responses)
                response = await GenerateResponseWithToolsAsync(linkedCts.Token);
                _messageHistory.Add(response);
                messages.Add(response);

                EmitEvent(AgentEventType.TurnCompleted, $"Turn {turnCount} completed");
            }

            stopwatch.Stop();

            lock (_stateLock)
            {
                State = AgentExecutionState.Completed;
            }

            EmitEvent(AgentEventType.Completed, $"Agent completed in {stopwatch.ElapsedMilliseconds}ms");

            return new AgentResult
            {
                State = AgentExecutionState.Completed,
                Messages = messages,
                ToolCalls = toolCalls,
                Duration = stopwatch.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();

            lock (_stateLock)
            {
                State = AgentExecutionState.Cancelled;
            }

            EmitEvent(AgentEventType.Cancelled, "Agent cancelled");

            return new AgentResult
            {
                State = AgentExecutionState.Cancelled,
                Messages = messages,
                ToolCalls = toolCalls,
                Duration = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.Error(ex, "Agent execution failed");

            lock (_stateLock)
            {
                State = AgentExecutionState.Failed;
            }

            EmitEvent(AgentEventType.Failed, $"Agent failed: {ex.Message}", error: ex);

            return new AgentResult
            {
                State = AgentExecutionState.Failed,
                Messages = messages,
                ToolCalls = toolCalls,
                Duration = stopwatch.Elapsed,
                Error = ex
            };
        }
    }

    protected virtual async Task<ContentMessage> TryEnrichInitialMessageAsync(
        ContentMessage message,
        CancellationToken cancellationToken)
    {
        if (message.Role != LlmRole.User)
        {
            return message;
        }

        var userText = message.GetText();
        if (string.IsNullOrWhiteSpace(userText))
        {
            return message;
        }

        var listTool = ToolRegistry.GetTool("list_directory");
        if (listTool == null)
        {
            return message;
        }

        try
        {
            var args = new Dictionary<string, object>
            {
                ["path"] = ".",
                ["directories_only"] = true,
                ["max_depth"] = 2
            };

            var result = await ExecuteToolAsync(listTool, args, cancellationToken);
            var contextText = result.Output
                ?? (result.LlmContent as TextContentPart)?.Text
                ?? string.Empty;

            if (result.IsError || string.IsNullOrWhiteSpace(contextText))
            {
                return message;
            }

            var lines = contextText
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Take(60);
            var snapshot = string.Join("\n", lines);

            if (string.IsNullOrWhiteSpace(snapshot))
            {
                return message;
            }

            _toolCallHistory.Add("preprocess:list_directory");

            return ContentMessage.UserMessage($"""
{userText}

[Preloaded workspace snapshot]
The following directory snapshot was collected before reasoning:
{snapshot}
""");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Preprocessing tool call failed. Continuing with original user prompt.");
            return message;
        }
    }

    /// <summary>
    /// Sends a message to the agent.
    /// </summary>
    public async Task<ContentMessage> SendMessageAsync(
        ContentMessage message,
        CancellationToken cancellationToken = default)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _cts.Token, cancellationToken);

        _messageHistory.Add(message);
        var response = await Chat.SendMessageAsync(message, linkedCts.Token);
        _messageHistory.Add(response);

        EmitEvent(AgentEventType.MessageReceived, "Message received");

        return response;
    }

    /// <summary>
    /// Pauses the agent execution.
    /// </summary>
    public virtual Task PauseAsync()
    {
        lock (_stateLock)
        {
            if (State != AgentExecutionState.Running)
            {
                throw new InvalidOperationException($"Cannot pause agent in state: {State}");
            }
            State = AgentExecutionState.Paused;
        }

        EmitEvent(AgentEventType.Paused, "Agent paused");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resumes the agent execution.
    /// </summary>
    public virtual Task ResumeAsync()
    {
        lock (_stateLock)
        {
            if (State != AgentExecutionState.Paused)
            {
                throw new InvalidOperationException($"Cannot resume agent in state: {State}");
            }
            State = AgentExecutionState.Running;
        }

        EmitEvent(AgentEventType.Resumed, "Agent resumed");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cancels the agent execution.
    /// </summary>
    public virtual Task CancelAsync()
    {
        _cts.Cancel();

        lock (_stateLock)
        {
            if (State == AgentExecutionState.Running)
            {
                State = AgentExecutionState.Cancelled;
            }
        }

        EmitEvent(AgentEventType.Cancelled, "Agent cancelled");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resets the agent state.
    /// </summary>
    public virtual void Reset()
    {
        lock (_stateLock)
        {
            State = AgentExecutionState.Idle;
        }

        _messageHistory.Clear();
        _toolCallHistory.Clear();

        EmitEvent(AgentEventType.Completed, "Agent reset");
    }

    /// <summary>
    /// Checks if a message contains tool calls.
    /// </summary>
    protected bool ContainsToolCalls(ContentMessage message)
    {
        return message.Parts.OfType<FunctionCallPart>().Any();
    }

    /// <summary>
    /// Generates a model response using full history and currently registered tools.
    /// </summary>
    protected virtual async Task<ContentMessage> GenerateResponseWithToolsAsync(CancellationToken cancellationToken)
    {
        var modelId = Chat.GetModelId();
        var request = new GenerateContentRequest
        {
            Model = modelId,
            Contents = new List<ContentMessage>(_messageHistory),
            Tools = ToolRegistry.GetGeminiTools(modelId),
            ToolConfig = new ToolConfig
            {
                FunctionCallingConfig = new FunctionCallingConfig
                {
                    Mode = FunctionCallingMode.Any,
                    AllowedFunctionNames = ToolRegistry.AllToolNames.ToList()
                }
            },
            CancellationToken = cancellationToken
        };

        var response = await Chat.GenerateContentAsync(request, cancellationToken);
        var candidate = response.Candidates.FirstOrDefault();

        return candidate is null
            ? ContentMessage.ModelMessage(string.Empty)
            : new ContentMessage
            {
                Role = LlmRole.Model,
                Parts = candidate.Content
            };
    }

    /// <summary>
    /// Executes a tool.
    /// </summary>
    protected abstract Task<ToolExecutionResult> ExecuteToolAsync(
        IToolBuilder tool,
        Dictionary<string, object>? arguments,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates a tool result message.
    /// </summary>
    protected ContentMessage CreateToolResultMessage(
        string functionName,
        ToolExecutionResult result)
    {
        return new ContentMessage
        {
            Role = LlmRole.Function,
            Parts = new List<ContentPart>
            {
                new FunctionResponsePart
                {
                    FunctionName = functionName,
                    Response = new Dictionary<string, object?>
                    {
                        ["result"] = result.Output,
                        ["is_error"] = result.IsError
                    }
                }
            }
        };
    }

    /// <summary>
    /// Creates a continuation message for the chat.
    /// </summary>
    protected ContentMessage CreateContinuationMessage()
    {
        return new ContentMessage
        {
            Role = LlmRole.User,
            Parts = new List<ContentPart>
            {
                new TextContentPart("Please continue.")
            }
        };
    }

    /// <summary>
    /// Emits an agent event.
    /// </summary>
    protected void EmitEvent(
        AgentEventType type,
        string? message = null,
        ContentMessage? contentMessage = null,
        Exception? error = null)
    {
        OnEvent?.Invoke(this, new AgentEvent
        {
            AgentId = Id,
            Type = type,
            Message = message,
            ContentMessage = contentMessage,
            Error = error
        });
    }
}
