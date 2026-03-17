using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AiCli.Core.Chat;
using AiCli.Core.Configuration;
using AiCli.Core.Types;

namespace AiCli.A2aServer.A2a;

/// <summary>
/// Executes agent tasks by routing messages through AiChat.
/// Manages task lifecycle: created → working → input-required/completed/failed.
/// </summary>
public class A2aAgentExecutor : IAsyncDisposable
{
    private readonly A2aTaskStore _taskStore;
    private readonly Config _config;
    private readonly ConcurrentDictionary<string, TaskSession> _sessions = new();

    public A2aAgentExecutor(A2aTaskStore taskStore, Config config)
    {
        _taskStore = taskStore;
        _config = config;
    }

    /// <summary>
    /// Creates a new task session.
    /// </summary>
    public A2aTask CreateTask(string taskId, string contextId)
    {
        var contentGenerator = CreateContentGenerator();
        var model = _config.GetModel();
        var chat = new AiChat(contentGenerator, model);

        var task = new A2aTask
        {
            Id = taskId,
            ContextId = contextId,
            Status = new TaskStatus
            {
                State = TaskState.Submitted,
                Timestamp = DateTime.UtcNow.ToString("O"),
            },
        };

        var session = new TaskSession(task, chat);
        _sessions[taskId] = session;
        _taskStore.Save(task);
        return task;
    }

    /// <summary>
    /// Gets a task by ID.
    /// </summary>
    public A2aTask? GetTask(string taskId)
    {
        return _taskStore.Load(taskId);
    }

    /// <summary>
    /// Gets all tasks.
    /// </summary>
    public IReadOnlyList<A2aTask> GetAllTasks()
    {
        return _taskStore.GetAll();
    }

    /// <summary>
    /// Cancels a task.
    /// </summary>
    public A2aTask? CancelTask(string taskId)
    {
        var task = _taskStore.Load(taskId);
        if (task == null) return null;

        if (_sessions.TryGetValue(taskId, out var session))
        {
            session.CancellationSource.Cancel();
        }

        task.Status = new TaskStatus
        {
            State = TaskState.Canceled,
            Timestamp = DateTime.UtcNow.ToString("O"),
            Message = new A2aMessage
            {
                MessageId = Guid.NewGuid().ToString(),
                Role = "agent",
                Parts = new[] { new TextPart { Text = "Task canceled by user request." } },
                TaskId = taskId,
                ContextId = task.ContextId,
            },
        };

        _taskStore.Save(task);
        return task;
    }

    /// <summary>
    /// Sends a message and returns the completed task (non-streaming).
    /// </summary>
    public async Task<A2aTask> SendMessageAsync(
        A2aMessage userMessage,
        CancellationToken cancellationToken = default)
    {
        var taskId = userMessage.TaskId ?? Guid.NewGuid().ToString();
        var contextId = userMessage.ContextId ?? Guid.NewGuid().ToString();

        var task = EnsureTaskExists(taskId, contextId);
        task.History.Add(userMessage);

        var session = _sessions[taskId];
        var ct = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, session.CancellationSource.Token).Token;

        UpdateTaskState(task, TaskState.Working);

        try
        {
            var textParts = userMessage.Parts
                .OfType<TextPart>()
                .Select(p => (ContentPart)new TextContentPart(p.Text))
                .ToList();

            if (textParts.Count == 0)
            {
                UpdateTaskState(task, TaskState.InputRequired, "No text content in message.");
                return task;
            }

            var response = await session.Chat.SendMessageAsync(textParts, ct);

            var responseText = response.Candidates
                .SelectMany(c => c.Content)
                .OfType<TextContentPart>()
                .Select(p => p.Text)
                .FirstOrDefault() ?? string.Empty;

            var agentMessage = new A2aMessage
            {
                MessageId = Guid.NewGuid().ToString(),
                Role = "agent",
                Parts = new[] { new TextPart { Text = responseText } },
                TaskId = taskId,
                ContextId = contextId,
            };

            task.History.Add(agentMessage);
            UpdateTaskState(task, TaskState.InputRequired, agentMessage: agentMessage);
        }
        catch (OperationCanceledException)
        {
            UpdateTaskState(task, TaskState.Canceled, "Task canceled.");
        }
        catch (Exception ex)
        {
            UpdateTaskState(task, TaskState.Failed, $"Agent error: {ex.Message}");
        }

        return task;
    }

    /// <summary>
    /// Sends a message and streams back SSE events.
    /// </summary>
    public async IAsyncEnumerable<object> SendMessageStreamAsync(
        A2aMessage userMessage,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var taskId = userMessage.TaskId ?? Guid.NewGuid().ToString();
        var contextId = userMessage.ContextId ?? Guid.NewGuid().ToString();

        var task = EnsureTaskExists(taskId, contextId);
        task.History.Add(userMessage);

        var session = _sessions[taskId];
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, session.CancellationSource.Token);
        var ct = linkedCts.Token;

        // Emit task object first
        yield return task;

        // Transition to working
        UpdateTaskState(task, TaskState.Working);
        yield return new TaskStatusUpdateEvent
        {
            TaskId = taskId,
            ContextId = contextId,
            Status = task.Status,
            Final = false,
        };

        var textParts = userMessage.Parts
            .OfType<TextPart>()
            .Select(p => (ContentPart)new TextContentPart(p.Text))
            .ToList();

        if (textParts.Count == 0)
        {
            UpdateTaskState(task, TaskState.InputRequired, "No text content in message.");
            yield return new TaskStatusUpdateEvent
            {
                TaskId = taskId,
                ContextId = contextId,
                Status = task.Status,
                Final = true,
            };
            yield break;
        }

        var accumulatedText = new System.Text.StringBuilder();
        var artifactId = $"response-{Guid.NewGuid():N}";

        bool completedSuccessfully = false;
        Exception? streamError = null;

        // Stream response
        await foreach (var chunk in session.Chat.SendMessageStreamAsync(textParts, cancellationToken: ct).WithCancellation(ct))
        {
            if (ct.IsCancellationRequested) break;

            var chunkText = chunk.Candidates
                .SelectMany(c => c.Content)
                .OfType<TextContentPart>()
                .Select(p => p.Text)
                .FirstOrDefault();

            if (!string.IsNullOrEmpty(chunkText))
            {
                accumulatedText.Append(chunkText);

                var isLast = chunk.Candidates.FirstOrDefault()?.FinishReason == FinishReason.Stop;

                yield return new TaskArtifactUpdateEvent
                {
                    TaskId = taskId,
                    ContextId = contextId,
                    Artifact = new Artifact
                    {
                        ArtifactId = artifactId,
                        Parts = new[] { new TextPart { Text = chunkText } },
                    },
                    Append = accumulatedText.Length > chunkText.Length,
                    LastChunk = isLast,
                };

                if (isLast) completedSuccessfully = true;
            }
        }

        if (ct.IsCancellationRequested)
        {
            UpdateTaskState(task, TaskState.Canceled, "Task canceled.");
            yield return new TaskStatusUpdateEvent
            {
                TaskId = taskId,
                ContextId = contextId,
                Status = task.Status,
                Final = true,
            };
            yield break;
        }

        // Build agent response message
        var responseText = accumulatedText.ToString();
        var agentMessage = new A2aMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            Role = "agent",
            Parts = new[] { new TextPart { Text = responseText } },
            TaskId = taskId,
            ContextId = contextId,
        };

        task.History.Add(agentMessage);
        UpdateTaskState(task, TaskState.InputRequired, agentMessage: agentMessage);

        yield return new TaskStatusUpdateEvent
        {
            TaskId = taskId,
            ContextId = contextId,
            Status = task.Status,
            Final = true,
        };
    }

    private A2aTask EnsureTaskExists(string taskId, string contextId)
    {
        var existing = _taskStore.Load(taskId);
        if (existing != null && _sessions.ContainsKey(taskId))
            return existing;

        return CreateTask(taskId, contextId);
    }

    private void UpdateTaskState(
        A2aTask task,
        TaskState newState,
        string? messageText = null,
        A2aMessage? agentMessage = null)
    {
        var message = agentMessage;
        if (message == null && messageText != null)
        {
            message = new A2aMessage
            {
                MessageId = Guid.NewGuid().ToString(),
                Role = "agent",
                Parts = new[] { new TextPart { Text = messageText } },
                TaskId = task.Id,
                ContextId = task.ContextId,
            };
        }

        task.Status = new TaskStatus
        {
            State = newState,
            Timestamp = DateTime.UtcNow.ToString("O"),
            Message = message,
        };

        _taskStore.Save(task);
    }

    private IContentGenerator CreateContentGenerator()
        => ContentGeneratorFactory.Create(_config);

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            session.CancellationSource.Cancel();
            await session.Chat.DisposeAsync();
        }
        _sessions.Clear();
    }

    private sealed class TaskSession(A2aTask task, AiChat chat)
    {
        public A2aTask Task { get; } = task;
        public AiChat Chat { get; } = chat;
        public CancellationTokenSource CancellationSource { get; } = new();
    }
}
