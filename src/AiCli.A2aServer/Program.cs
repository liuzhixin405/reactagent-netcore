using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiCli.Core.Configuration;
using AiCli.A2aServer.A2a;

// Ensure UTF-8 console I/O on all platforms.
Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

// ─── JSON options ──────────────────────────────────────────────────────────

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false,
};
jsonOptions.Converters.Add(new TaskStateJsonConverter());

// ─── Agent card definition ─────────────────────────────────────────────────

var agentCard = new AgentCard
{
    Name = "AiCli SDLC Agent",
    Description = "An agent that generates code based on natural language instructions.",
    Url = "http://localhost:41242/",
    Provider = new AgentProvider
    {
        Organization = "Google",
        Url = "https://google.com",
    },
    ProtocolVersion = "0.3.0",
    Version = "0.1.0",
    Capabilities = new AgentCapabilities
    {
        Streaming = true,
        PushNotifications = false,
        StateTransitionHistory = true,
    },
    DefaultInputModes = new[] { "text" },
    DefaultOutputModes = new[] { "text" },
    Skills = new[]
    {
        new AgentSkill
        {
            Id = "code_generation",
            Name = "Code Generation",
            Description = "Generates code snippets or complete files based on user requests.",
            Tags = new[] { "code", "development", "programming" },
            Examples = new[]
            {
                "Write a python function to calculate fibonacci numbers.",
                "Create a C# class representing a bank account.",
            },
        },
    },
};

// ─── Configuration ─────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<Config>(sp =>
{
    var cfg = new Config();
    cfg.InitializeAsync().GetAwaiter().GetResult();
    return cfg;
});
builder.Services.AddSingleton<A2aTaskStore>();
builder.Services.AddSingleton<A2aAgentExecutor>(sp =>
{
    var store = sp.GetRequiredService<A2aTaskStore>();
    var cfg = sp.GetRequiredService<Config>();
    return new A2aAgentExecutor(store, cfg);
});

var app = builder.Build();

// Update agent card URL based on actual listening port
app.Lifetime.ApplicationStarted.Register(() =>
{
    var addresses = app.Urls;
    foreach (var addr in addresses)
    {
        if (Uri.TryCreate(addr, UriKind.Absolute, out var uri))
        {
            agentCard.Url = $"{addr}/";
            app.Logger.LogInformation("[A2aServer] Agent Server started on {Url}", addr);
            app.Logger.LogInformation("[A2aServer] Agent Card: {Url}.well-known/agent.json", addr);
            break;
        }
    }
});

// ─── Routes ────────────────────────────────────────────────────────────────

// GET /.well-known/agent.json — agent card
app.MapGet("/.well-known/agent.json", () =>
    Results.Json(agentCard, jsonOptions));

// GET /.well-known/agent-card.json — alias
app.MapGet("/.well-known/agent-card.json", () =>
    Results.Json(agentCard, jsonOptions));

// GET /health
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// GET /tasks/metadata — list all task metadata
app.MapGet("/tasks/metadata", (A2aAgentExecutor executor) =>
{
    var tasks = executor.GetAllTasks();
    return Results.Json(tasks.Select(t => new
    {
        t.Id,
        t.ContextId,
        state = t.Status.State.ToString().ToLowerInvariant(),
        timestamp = t.Status.Timestamp,
    }), jsonOptions);
});

// GET /tasks/{taskId}/metadata — specific task
app.MapGet("/tasks/{taskId}/metadata", (string taskId, A2aAgentExecutor executor) =>
{
    var task = executor.GetTask(taskId);
    if (task == null) return Results.NotFound(new { error = "Task not found" });
    return Results.Json(task, jsonOptions);
});

// POST /tasks — create task
app.MapPost("/tasks", (HttpContext ctx, A2aAgentExecutor executor) =>
{
    var taskId = Guid.NewGuid().ToString();
    var contextId = ctx.Request.Headers.TryGetValue("X-Context-Id", out var cid)
        ? cid.ToString()
        : Guid.NewGuid().ToString();

    var task = executor.CreateTask(taskId, contextId);
    return Results.Created($"/tasks/{taskId}/metadata", taskId);
});

// POST / — A2A JSON-RPC 2.0 endpoint
app.MapPost("/", async (HttpContext ctx, A2aAgentExecutor executor) =>
{
    JsonRpcRequest? rpcReq;
    try
    {
        rpcReq = await JsonSerializer.DeserializeAsync<JsonRpcRequest>(
            ctx.Request.Body, jsonOptions);
    }
    catch
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new JsonRpcResponse
        {
            Id = null,
            Error = new JsonRpcError { Code = -32700, Message = "Parse error" },
        }, jsonOptions);
        return;
    }

    if (rpcReq == null || string.IsNullOrEmpty(rpcReq.Method))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new JsonRpcResponse
        {
            Id = rpcReq?.Id,
            Error = new JsonRpcError { Code = -32600, Message = "Invalid request" },
        }, jsonOptions);
        return;
    }

    switch (rpcReq.Method)
    {
        case "message/send":
            await HandleMessageSend(ctx, rpcReq, executor, jsonOptions);
            break;

        case "message/stream":
            await HandleMessageStream(ctx, rpcReq, executor, jsonOptions);
            break;

        case "tasks/get":
            await HandleTasksGet(ctx, rpcReq, executor, jsonOptions);
            break;

        case "tasks/cancel":
            await HandleTasksCancel(ctx, rpcReq, executor, jsonOptions);
            break;

        default:
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsJsonAsync(new JsonRpcResponse
            {
                Id = rpcReq.Id,
                Error = new JsonRpcError { Code = -32601, Message = $"Method not found: {rpcReq.Method}" },
            }, jsonOptions);
            break;
    }
});

// ─── Start server ──────────────────────────────────────────────────────────

var port = Environment.GetEnvironmentVariable("CODER_AGENT_PORT");
if (!string.IsNullOrEmpty(port) && int.TryParse(port, out var portNum))
{
    app.Urls.Add($"http://localhost:{portNum}");
}
else
{
    app.Urls.Add("http://localhost:0");
}

app.Run();

// ─── Handler helpers ───────────────────────────────────────────────────────

static async Task HandleMessageSend(
    HttpContext ctx,
    JsonRpcRequest rpcReq,
    A2aAgentExecutor executor,
    JsonSerializerOptions jsonOptions)
{
    if (rpcReq.Params == null)
    {
        await WriteRpcError(ctx, rpcReq.Id, -32602, "Missing params", jsonOptions);
        return;
    }

    MessageSendParams? sendParams;
    try
    {
        sendParams = rpcReq.Params.Value.Deserialize<MessageSendParams>(jsonOptions);
    }
    catch
    {
        await WriteRpcError(ctx, rpcReq.Id, -32602, "Invalid params", jsonOptions);
        return;
    }

    if (sendParams == null)
    {
        await WriteRpcError(ctx, rpcReq.Id, -32602, "Missing message in params", jsonOptions);
        return;
    }

    try
    {
        var task = await executor.SendMessageAsync(sendParams.Message, ctx.RequestAborted);
        ctx.Response.StatusCode = 200;
        await ctx.Response.WriteAsJsonAsync(new JsonRpcResponse
        {
            Id = rpcReq.Id,
            Result = task,
        }, jsonOptions);
    }
    catch (Exception ex)
    {
        await WriteRpcError(ctx, rpcReq.Id, -32000, $"Agent error: {ex.Message}", jsonOptions);
    }
}

static async Task HandleMessageStream(
    HttpContext ctx,
    JsonRpcRequest rpcReq,
    A2aAgentExecutor executor,
    JsonSerializerOptions jsonOptions)
{
    if (rpcReq.Params == null)
    {
        await WriteRpcError(ctx, rpcReq.Id, -32602, "Missing params", jsonOptions);
        return;
    }

    MessageSendParams? sendParams;
    try
    {
        sendParams = rpcReq.Params.Value.Deserialize<MessageSendParams>(jsonOptions);
    }
    catch
    {
        await WriteRpcError(ctx, rpcReq.Id, -32602, "Invalid params", jsonOptions);
        return;
    }

    if (sendParams == null)
    {
        await WriteRpcError(ctx, rpcReq.Id, -32602, "Missing message in params", jsonOptions);
        return;
    }

    ctx.Response.StatusCode = 200;
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    ctx.Response.Headers["Connection"] = "keep-alive";

    try
    {
        await foreach (var evt in executor.SendMessageStreamAsync(
            sendParams.Message, ctx.RequestAborted))
        {
            if (ctx.RequestAborted.IsCancellationRequested) break;

            var rpcEvt = new JsonRpcResponse
            {
                Id = rpcReq.Id,
                Result = evt,
            };

            var json = JsonSerializer.Serialize(rpcEvt, jsonOptions);
            var line = $"data: {json}\n\n";
            await ctx.Response.WriteAsync(line, Encoding.UTF8, ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }
    catch (OperationCanceledException)
    {
        // Client disconnected — normal
    }
}

static async Task HandleTasksGet(
    HttpContext ctx,
    JsonRpcRequest rpcReq,
    A2aAgentExecutor executor,
    JsonSerializerOptions jsonOptions)
{
    if (rpcReq.Params == null)
    {
        await WriteRpcError(ctx, rpcReq.Id, -32602, "Missing params", jsonOptions);
        return;
    }

    TasksGetParams? getParams;
    try
    {
        getParams = rpcReq.Params.Value.Deserialize<TasksGetParams>(jsonOptions);
    }
    catch
    {
        await WriteRpcError(ctx, rpcReq.Id, -32602, "Invalid params", jsonOptions);
        return;
    }

    if (getParams == null)
    {
        await WriteRpcError(ctx, rpcReq.Id, -32602, "Missing id in params", jsonOptions);
        return;
    }

    var task = executor.GetTask(getParams.Id);
    if (task == null)
    {
        await WriteRpcError(ctx, rpcReq.Id, -32001, $"Task not found: {getParams.Id}", jsonOptions);
        return;
    }

    ctx.Response.StatusCode = 200;
    await ctx.Response.WriteAsJsonAsync(new JsonRpcResponse
    {
        Id = rpcReq.Id,
        Result = task,
    }, jsonOptions);
}

static async Task HandleTasksCancel(
    HttpContext ctx,
    JsonRpcRequest rpcReq,
    A2aAgentExecutor executor,
    JsonSerializerOptions jsonOptions)
{
    if (rpcReq.Params == null)
    {
        await WriteRpcError(ctx, rpcReq.Id, -32602, "Missing params", jsonOptions);
        return;
    }

    TasksCancelParams? cancelParams;
    try
    {
        cancelParams = rpcReq.Params.Value.Deserialize<TasksCancelParams>(jsonOptions);
    }
    catch
    {
        await WriteRpcError(ctx, rpcReq.Id, -32602, "Invalid params", jsonOptions);
        return;
    }

    if (cancelParams == null)
    {
        await WriteRpcError(ctx, rpcReq.Id, -32602, "Missing id in params", jsonOptions);
        return;
    }

    var task = executor.CancelTask(cancelParams.Id);
    if (task == null)
    {
        await WriteRpcError(ctx, rpcReq.Id, -32001, $"Task not found: {cancelParams.Id}", jsonOptions);
        return;
    }

    ctx.Response.StatusCode = 200;
    await ctx.Response.WriteAsJsonAsync(new JsonRpcResponse
    {
        Id = rpcReq.Id,
        Result = task,
    }, jsonOptions);
}

static async Task WriteRpcError(
    HttpContext ctx,
    object? id,
    int code,
    string message,
    JsonSerializerOptions opts)
{
    ctx.Response.StatusCode = 200;
    await ctx.Response.WriteAsJsonAsync(new JsonRpcResponse
    {
        Id = id,
        Error = new JsonRpcError { Code = code, Message = message },
    }, opts);
}
