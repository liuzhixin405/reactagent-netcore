using Spectre.Console;
using System.CommandLine;
using GeminiCli.Cli.Commands;
using GeminiCli.Core.Configuration;
using GeminiCli.Core.Logging;

namespace GeminiCli.Cli;

/// <summary>
/// Main entry point for Gemini CLI application.
/// </summary>
public class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Display banner
        DisplayBanner();

        try
        {
            // Initialize logger
            LoggerHelper.SetGlobalLogger(LoggerHelper.CreateLogger(new LoggerConfig()));
            var logger = LoggerHelper.ForContext<Program>();

            // Load configuration
            await using var config = new Config();
            await config.InitializeAsync();
            logger.Information("Gemini CLI started");

            // Build root command
            var rootCommand = new RootCommand("gemini")
            {
                Description = "Gemini CLI - AI-powered command line assistant"
            };

            // Chat command
            var chatCommand = new ChatCommand(config);
            rootCommand.AddCommand(chatCommand.Command);

            // Prompt command
            var promptCommand = new PromptCommand(config);
            rootCommand.AddCommand(promptCommand.Command);

            // Agent command
            var agentCommand = new AgentCommand(config);
            rootCommand.AddCommand(agentCommand.Command);

            // Config command
            var configCommand = new ConfigCommand(config);
            rootCommand.AddCommand(configCommand.Command);

            // Plan command
            var planCommand = new PlanCommand(config);
            rootCommand.AddCommand(planCommand.Command);

            // Parse and invoke
            var invokeArgs = args;
            if (args.Length == 0)
            {
                invokeArgs = BuildInteractiveStartupArgs();
                if (invokeArgs.Length == 0)
                {
                    return 0;
                }
            }

            return await rootCommand.InvokeAsync(invokeArgs);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Fatal error:[/] {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Displays the application banner.
    /// </summary>
    private static void DisplayBanner()
    {
        Console.WriteLine();
        AnsiConsole.MarkupLine("  ____  _____ _     _       _____ ");
        AnsiConsole.MarkupLine(" |  _ \\|_   | |   __|__   |__  /\\ \\  ");
        AnsiConsole.MarkupLine(" | | | ||   | |  |____ |     \\_/ | |\\ |");
        AnsiConsole.MarkupLine(" | |_| ||___| |__|      /\\___/ |__| |__|");
        AnsiConsole.MarkupLine("                  ");
        AnsiConsole.MarkupLine("[bold yellow]Agent CLI[/] - C# Edition");
        AnsiConsole.MarkupLine("[dim]v0.1.0 - Porting in progress[/]");
        Console.WriteLine();
    }

    private static string[] BuildInteractiveStartupArgs()
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            AnsiConsole.MarkupLine("[yellow]终端不支持交互式提示，使用默认非交互模式：聊天（本地生成器）[/]");
            Environment.SetEnvironmentVariable("GEMINI_USE_LOCAL_GENERATOR", "true");
            return new[] { "chat" };
        }

        var mode = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]请选择启动模式[/]")
                .AddChoices(new[]
                {
                    "聊天模式（本地生成器）",
                    "聊天模式（Google API）",
                    "Agent 模式（general）",
                    "单次 Prompt",
                    "退出"
                }));

        return mode switch
        {
            "聊天模式（本地生成器）" => BuildLocalChatArgs(),
            "聊天模式（Google API）" => new[] { "chat" },
            "Agent 模式（general）" => BuildAgentArgs(),
            "单次 Prompt" => BuildPromptArgs(),
            //_ => Array.Empty<string>()
            _ => BuildAgentArgs()
        };
    }

    private static string[] BuildLocalChatArgs()
    {
        Environment.SetEnvironmentVariable("GEMINI_USE_LOCAL_GENERATOR", "true");
        return new[] { "chat" };
    }

    private static string[] BuildAgentArgs()
    {
        return new[] { "agent", "interactive", "general" };
    }

    private static string[] BuildPromptArgs()
    {
        var input = AnsiConsole.Ask<string>("[green]请输入 prompt[/]");
        return new[] { "prompt", input };
    }
}
