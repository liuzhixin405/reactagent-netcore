using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using AiCli.Core.Agents;
using AiCli.Core.Agents.Builtin;
using AiCli.Core.Chat;
using AiCli.Core.Tools.Builtin;
using AiCli.Core.Types;
using AiCli.Core.Configuration;
using AiCli.Core.Logging;
using AiCli.Core.Tools;
using System.Diagnostics;
using System.Text.Json;

namespace AiCli.Cli.Commands;

/// <summary>
/// Handler for the agent command.
/// </summary>
public class AgentCommand : CommandBase
{
    private readonly AgentRegistry _agentRegistry;
    private readonly ToolRegistry _toolRegistry;
    private readonly Argument<string> _runAgentArgument;
    private readonly Argument<string> _runPromptArgument;
    private readonly Argument<string> _infoAgentArgument;
    private readonly Argument<string> _interactiveAgentArgument;

    public override Command Command { get; }

    public AgentCommand(Config config) : base(config)
    {
        _toolRegistry = new ToolRegistry();
        _agentRegistry = new AgentRegistry(_toolRegistry);

        Command = new Command("agent")
        {
            Description = "Manage and execute AI agents"
        };

        // Add subcommands
        var listCommand = new Command("list")
        {
            Description = "List all available agents"
        };
        listCommand.SetHandler(async context => context.ExitCode = await HandleListAsync(context));

        var runCommand = new Command("run")
        {
            Description = "Execute an agent"
        };
        _runAgentArgument = new Argument<string>("agent")
        {
            Description = "The agent to execute"
        };
        runCommand.AddArgument(_runAgentArgument);
        _runPromptArgument = new Argument<string>("prompt")
        {
            Description = "The prompt/task for the agent"
        };
        runCommand.AddArgument(_runPromptArgument);
        runCommand.SetHandler(async context => context.ExitCode = await HandleRunAsync(context));

        var infoCommand = new Command("info")
        {
            Description = "Show information about an agent"
        };
        _infoAgentArgument = new Argument<string>("agent")
        {
            Description = "The agent to show info for"
        };
        infoCommand.AddArgument(_infoAgentArgument);
        infoCommand.SetHandler(async context => context.ExitCode = await HandleInfoAsync(context));

        var interactiveCommand = new Command("interactive")
        {
            Description = "Start interactive agent session"
        };
        _interactiveAgentArgument = new Argument<string>("agent", () => "general")
        {
            Description = "The agent to execute (default: general)"
        };
        interactiveCommand.AddArgument(_interactiveAgentArgument);
        interactiveCommand.SetHandler(async context => context.ExitCode = await HandleInteractiveAsync(context));

        // Add subcommands to parent
        Command.AddCommand(listCommand);
        Command.AddCommand(runCommand);
        Command.AddCommand(infoCommand);
        Command.AddCommand(interactiveCommand);
    }

    /// <summary>
    /// Handles the agent list subcommand.
    /// </summary>
    private Task<int> HandleListAsync(InvocationContext context)
    {
        DisplayInfo("Available Agents:");

        // Display built-in agents
        var agents = new[]
        {
            new { Name = "General Purpose", Kind = "General", Description = "Handles a wide range of tasks" },
            new { Name = "Explore", Kind = "Explore", Description = "Fast codebase exploration" },
            new { Name = "Plan", Kind = "Plan", Description = "Creates implementation plans" },
            new { Name = "Code", Kind = "Code", Description = "Coding and refactoring" }
        };

        DisplayTable("Agents", agents.ToList(), new Func<dynamic, string>[]
        {
            a => a.Name,
            a => a.Kind,
            a => a.Description
        });

        _console.MarkupLine("\n[dim]Use 'aicli agent run <agent> <prompt>' to execute an agent.[/]");
        return Task.FromResult(0);
    }

    /// <summary>
    /// Handles the agent run subcommand.
    /// </summary>
    private async Task<int> HandleRunAsync(InvocationContext context)
    {
        var agentName = context.ParseResult.GetValueForArgument(_runAgentArgument);
        var prompt = context.ParseResult.GetValueForArgument(_runPromptArgument);

        DisplayInfo($"Running agent: {agentName}");
        DisplayInfo($"Task: {prompt}");
        _console.WriteLine();

        return await ExecuteSingleTaskAsync(agentName ?? string.Empty, prompt ?? string.Empty);
    }

    private async Task<int> HandleInteractiveAsync(InvocationContext context)
    {
        var agentName = context.ParseResult.GetValueForArgument(_interactiveAgentArgument) ?? "general";

        DisplayInfo($"Starting interactive agent session: {agentName}");
        DisplayInfo("Type 'exit' or 'quit' to end the session. Use '/clear' to reset agent state.");
        _console.WriteLine();

        while (true)
        {
            string task;
            try
            {
                task = _console.Prompt(new TextPrompt<string>("[green]Task:[/]").PromptStyle("green"));
            }
            catch (OperationCanceledException)
            {
                DisplayInfo("Interactive agent session ended.");
                return 0;
            }

            var lowered = task.Trim().ToLowerInvariant();
            if (lowered is "exit" or "quit")
            {
                DisplayInfo("Interactive agent session ended.");
                return 0;
            }

            if (lowered == "/clear")
            {
                _agentRegistry.Clear();
                DisplayInfo("Agent runtime state cleared.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(task))
            {
                continue;
            }

            var code = await ExecuteSingleTaskAsync(agentName, task);
            if (code != 0)
            {
                return code;
            }

            _console.WriteLine();
        }
    }

    private async Task<int> ExecuteSingleTaskAsync(string agentName, string prompt)
    {
        try
        {
            IContentGenerator? contentGenerator = null;
            try
            {
                contentGenerator = ContentGeneratorFactory.Create(_config);
                RegisterToolsAndAgents(contentGenerator);

                var agent = _agentRegistry.GetAgent(agentName)
                            ?? _agentRegistry.GetAgentByName(agentName);
                if (agent == null)
                {
                    DisplayError($"Unknown agent: {agentName}");
                    DisplayInfo("Run 'aicli agent list' to see available agents.");
                    return 1;
                }

                _console.MarkupLine($"[cyan]{agent.Name} Agent:[/] [dim]{Markup.Escape(prompt)}[/]");
                _console.WriteLine();

                // 实时任务清单：思考→工具调用→完成，逐条追加并打勾
                var renderer = new LiveTaskListRenderer();
                var thinkingBuf = new System.Text.StringBuilder();
                var thinkingStart = DateTime.UtcNow;
                bool thinkingActive = false;

                agent.OnEvent += (_, e) =>
                {
                    switch (e.Type)
                    {
                        case AgentEventType.Thinking when !string.IsNullOrEmpty(e.Message):
                            thinkingBuf.Append(e.Message);
                            var preview = TailText(thinkingBuf.ToString(), 55);
                            if (!thinkingActive)
                            {
                                renderer.Add($"◆ 思考中  {preview}");
                                thinkingActive = true;
                            }
                            else
                            {
                                renderer.UpdateLast($"◆ 思考中  {preview}");
                            }
                            break;

                        case AgentEventType.ToolCalled when !string.IsNullOrEmpty(e.Message):
                            if (thinkingActive)
                            {
                                var secs = (DateTime.UtcNow - thinkingStart).TotalSeconds;
                                renderer.CompleteLastWith($"◆ 思考完成 ({secs:F1}s)");
                                thinkingActive = false;
                                thinkingBuf.Clear();
                                thinkingStart = DateTime.UtcNow;
                            }
                            renderer.Add(e.Message);  // 直接使用描述性标签（已含参数摘要）
                            break;

                        case AgentEventType.ToolCompleted:
                            renderer.CompleteLast();
                            break;
                    }
                };

                var result = await agent.ExecuteAsync(ContentMessage.UserMessage(prompt));

                // 收尾：将未关闭的思考条目打勾
                if (thinkingActive)
                {
                    var secs = (DateTime.UtcNow - thinkingStart).TotalSeconds;
                    renderer.CompleteLastWith($"◆ 思考完成 ({secs:F1}s)");
                }

                // 将动态清单冻结为永久行
                renderer.PrintCompleted();

                RenderAgentResult(result);
                await RunPostTaskValidationAsync(result);
                return 0;
            }
            finally
            {
                if (contentGenerator is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                }
            }
        }
        catch (Exception ex)
        {
            DisplayError($"Error running agent: {ex.Message}");
            return 1;
        }
    }

    private void RegisterToolsAndAgents(IContentGenerator contentGenerator)
    {
        var targetDir = Directory.GetCurrentDirectory();

        _toolRegistry.Clear();
        _toolRegistry.RegisterDiscoveredTool(new ReadFileTool(targetDir));
        _toolRegistry.RegisterDiscoveredTool(new WriteFileTool(targetDir));
        _toolRegistry.RegisterDiscoveredTool(new ShellTool(targetDir));
        _toolRegistry.RegisterDiscoveredTool(new GrepTool(targetDir));
        _toolRegistry.RegisterDiscoveredTool(new GlobTool(targetDir));
        _toolRegistry.RegisterDiscoveredTool(new LsTool(targetDir));
        _toolRegistry.RegisterDiscoveredTool(new EditTool(targetDir));
        _toolRegistry.RegisterDiscoveredTool(new WebFetchTool());
        _toolRegistry.RegisterDiscoveredTool(new WebSearchTool());
        _toolRegistry.RegisterDiscoveredTool(new MemoryTool(_config));
        // enter_plan_mode / exit_plan_mode 不注册：会导致模型卡在计划模式而不执行

        // Agent 工具调用路由：
        // qwen2.5-coder:7b 有原生 function calling 支持，所有需要调用工具的 agent 都用它。
        // gpt-oss:20b (think:true) 在带工具时只推理不输出，留给 chat 纯推理场景使用。
        IContentGenerator agentGen = contentGenerator;
        if (contentGenerator is MultiModelOrchestrator mmo)
        {
            agentGen = mmo.GetGenerator(ModelRole.Fast); // qwen2.5-coder:7b
        }

        var general = new GeneralPurposeAgent("general", _toolRegistry, agentGen);
        var explore = new ExploreAgent("explore", _toolRegistry, agentGen);
        var plan    = new PlanAgent("plan",    _toolRegistry, agentGen);
        var code    = new CodeAgent("code",    _toolRegistry, agentGen);

        _agentRegistry.Clear();
        _agentRegistry.RegisterAgent(general);
        _agentRegistry.RegisterAgent(explore);
        _agentRegistry.RegisterAgent(plan);
        _agentRegistry.RegisterAgent(code);
    }

    private void RenderAgentResult(AgentResult result)
    {
        // 分隔线
        _console.MarkupLine($"\n[dim]{"─".PadRight(60, '─')}[/]");

        if (result.State == AgentExecutionState.Failed)
        {
            DisplayError($"任务失败: {result.Error?.Message ?? "未知错误"}");
            return;
        }

        // 统计摘要行
        var toolCount = result.ToolCalls?.Count ?? 0;
        var duration = result.Duration.TotalSeconds;
        var summary = toolCount > 0
            ? $"[dim]共执行 {toolCount} 个工具调用，耗时 {duration:F1}s[/]"
            : $"[dim]耗时 {duration:F1}s[/]";
        _console.MarkupLine(summary);
        _console.WriteLine();

        // 最后一条模型文字响应
        var lastText = result.Messages
            .Where(m => m.Role == LlmRole.Model)
            .SelectMany(m => m.Parts.OfType<TextContentPart>())
            .Where(p => !string.IsNullOrWhiteSpace(p.Text))
            .Select(p => p.Text)
            .LastOrDefault();

        if (!string.IsNullOrWhiteSpace(lastText))
        {
            _console.MarkupLine($"[cyan]AI:[/] {Markup.Escape(lastText)}");
        }
        else if (toolCount > 0)
        {
            _console.MarkupLine("[green]✓ 任务已完成[/]");
        }
        else
        {
            _console.MarkupLine("[dim]（无返回内容）[/]");
        }
    }

    /// <summary>截取文本末尾 maxLen 个字符，用于思考预览。</summary>
    private static string TailText(string text, int maxLen)
    {
        var flat = text.Replace('\n', ' ').Replace('\r', ' ').TrimEnd();
        return flat.Length <= maxLen ? flat : "…" + flat[^maxLen..];
    }

    private async Task RunPostTaskValidationAsync(AgentResult result)
    {
        var buildTargets = ExtractBuildTargetsFromToolCalls(result.ToolCalls);
        if (buildTargets.Count == 0)
        {
            return;
        }

        foreach (var target in buildTargets)
        {
            var (ok, summary) = await TryBuildTargetAsync(target);
            if (ok)
            {
                DisplaySuccess($"自动编译验证通过: {target}");
            }
            else
            {
                DisplayWarning($"自动编译验证失败: {target}\n{summary}");
            }
        }
    }

    private static List<string> ExtractBuildTargetsFromToolCalls(IReadOnlyList<string>? toolCalls)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (toolCalls == null || toolCalls.Count == 0)
        {
            return targets.ToList();
        }

        foreach (var call in toolCalls)
        {
            if (!call.StartsWith("write_file(", StringComparison.OrdinalIgnoreCase) || !call.EndsWith(")", StringComparison.Ordinal))
            {
                continue;
            }

            var json = call.Substring("write_file(".Length, call.Length - "write_file(".Length - 1);
            try
            {
                var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
                if (args == null || !args.TryGetValue("file_path", out var filePathObj))
                {
                    continue;
                }

                var filePath = filePathObj?.ToString();
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    continue;
                }

                if (filePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                    filePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    targets.Add(filePath);
                }
            }
            catch
            {
                // Ignore malformed call payloads.
            }
        }

        return targets.ToList();
    }

    private static async Task<(bool Success, string Summary)> TryBuildTargetAsync(string target)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{target}\" -c Debug -nologo",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode == 0)
        {
            return (true, string.Empty);
        }

        var summary = string.Join("\n", (stdout + "\n" + stderr)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .TakeLast(12));

        return (false, summary);
    }

    /// <summary>
    /// Handles the agent info subcommand.
    /// </summary>
    private Task<int> HandleInfoAsync(InvocationContext context)
    {
        var agentName = context.ParseResult.GetValueForArgument(_infoAgentArgument) ?? string.Empty;

        // Display agent info
        var agentInfo = agentName.ToLower() switch
        {
            "general" or "general-purpose" => new
            {
                Name = "General Purpose",
                Kind = "General",
                Description = "Handles a wide range of tasks and can delegate to specialists",
                Capabilities = new[] { "general", "delegation", "coordination" }
            },
            "explore" => new
            {
                Name = "Explore",
                Kind = "Explore",
                Description = "Fast codebase exploration using grep and glob patterns",
                Capabilities = new[] { "search", "file_discovery", "code_exploration" }
            },
            "plan" => new
            {
                Name = "Plan",
                Kind = "Plan",
                Description = "Creates detailed implementation plans with step-by-step breakdown",
                Capabilities = new[] { "planning", "task_breakdown", "strategy" }
            },
            "code" => new
            {
                Name = "Code",
                Kind = "Code",
                Description = "Specialized in coding, refactoring, debugging, and code review",
                Capabilities = new[] { "coding", "refactoring", "debugging", "code_review" }
            },
            _ => null
        };

        if (agentInfo == null)
        {
            DisplayError($"Unknown agent: {agentName}");
            DisplayInfo("Run 'aicli agent list' to see available agents.");
            return Task.FromResult(1);
        }

        // Display agent details
        _console.MarkupLine($"[bold yellow]{agentInfo.Name} Agent[/]");
        _console.MarkupLine($"[bold]Kind:[/] {agentInfo.Kind}");
        _console.MarkupLine($"[bold]Description:[/] {agentInfo.Description}");
        _console.MarkupLine($"[bold]Capabilities:[/] {string.Join(", ", agentInfo.Capabilities)}");

        return Task.FromResult(0);
    }

    private record AgentInfo
    {
        public required string Name { get; init; }
        public required string Kind { get; init; }
        public required string Description { get; init; }
        public required List<string> Capabilities { get; init; }
    }
}
