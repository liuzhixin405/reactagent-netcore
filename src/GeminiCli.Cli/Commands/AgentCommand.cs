using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using GeminiCli.Core.Agents;
using GeminiCli.Core.Agents.Builtin;
using GeminiCli.Core.Chat;
using GeminiCli.Core.Tools.Builtin;
using GeminiCli.Core.Types;
using GeminiCli.Core.Configuration;
using GeminiCli.Core.Logging;
using GeminiCli.Core.Tools;
using System.Diagnostics;
using System.Text.Json;

namespace GeminiCli.Cli.Commands;

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

        _console.MarkupLine("\n[dim]Use 'gemini agent run <agent> <prompt>' to execute an agent.[/]");
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
                    DisplayInfo("Run 'gemini agent list' to see available agents.");
                    return 1;
                }

                _console.MarkupLine($"[cyan]{agent.Name} Agent:[/]");

                return await WithSpinnerAsync("Agent thinking...", async () =>
                {
                    var result = await agent.ExecuteAsync(ContentMessage.UserMessage(prompt));
                    RenderAgentResult(result);
                    await RunPostTaskValidationAsync(result);
                    return 0;
                });
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
        _toolRegistry.RegisterDiscoveredTool(new EnterPlanModeTool());
        _toolRegistry.RegisterDiscoveredTool(new ExitPlanModeTool());
        _toolRegistry.RegisterDiscoveredTool(new MemoryTool(_config));

        var general = new GeneralPurposeAgent("general", _toolRegistry, contentGenerator);
        var explore = new ExploreAgent("explore", _toolRegistry, contentGenerator);
        var plan = new PlanAgent("plan", _toolRegistry, contentGenerator);
        var code = new CodeAgent("code", _toolRegistry, contentGenerator);

        _agentRegistry.Clear();
        _agentRegistry.RegisterAgent(general);
        _agentRegistry.RegisterAgent(explore);
        _agentRegistry.RegisterAgent(plan);
        _agentRegistry.RegisterAgent(code);
    }

    private void RenderAgentResult(AgentResult result)
    {
        foreach (var msg in result.Messages)
        {
            var text = msg.Parts.OfType<TextContentPart>().Select(p => p.Text).LastOrDefault();
            if (!string.IsNullOrWhiteSpace(text))
            {
                _console.MarkupLine("  " + Markup.Escape(text));
            }
        }

        if (result.ToolCalls != null && result.ToolCalls.Count > 0)
        {
            _console.MarkupLine($"\n[dim]Tool calls: {Markup.Escape(string.Join(", ", result.ToolCalls))}[/]");
        }
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
            DisplayInfo("Run 'gemini agent list' to see available agents.");
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
