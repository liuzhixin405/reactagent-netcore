using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using GeminiCli.Core.Configuration;

namespace GeminiCli.Cli.Commands;

/// <summary>
/// Handler for the plan command.
/// </summary>
public class PlanCommand : CommandBase
{
    private readonly Argument<string> _taskArgument;

    public override Command Command { get; }

    public PlanCommand(Config config) : base(config)
    {
        Command = new Command("plan")
        {
            Description = "Enter plan mode for creating implementation plans"
        };

        // Add subcommands
        var enterCommand = new Command("enter")
        {
            Description = "Enter plan mode"
        };
        _taskArgument = new Argument<string>("task")
        {
            Description = "The task to create a plan for",
            Arity = ArgumentArity.ZeroOrOne
        };
        enterCommand.AddArgument(_taskArgument);
        enterCommand.SetHandler(async context => context.ExitCode = await HandleEnterAsync(context));

        var exitCommand = new Command("exit")
        {
            Description = "Exit plan mode"
        };
        exitCommand.SetHandler(async context => context.ExitCode = await HandleExitAsync(context));

        // Add subcommands to parent
        Command.AddCommand(enterCommand);
        Command.AddCommand(exitCommand);
    }

    /// <summary>
    /// Handles the plan enter subcommand.
    /// </summary>
    private async Task<int> HandleEnterAsync(InvocationContext context)
    {
        var task = context.ParseResult.GetValueForArgument(_taskArgument);

        DisplayInfo("Entering plan mode...");
        DisplayInfo("In plan mode, the AI will:");
        DisplayInfo("  1. Understand your task requirements");
        DisplayInfo("  2. Explore the codebase if needed");
        DisplayInfo("  3. Create a detailed implementation plan");
        DisplayInfo("  4. Present the plan for your approval");
        _console.WriteLine();

        if (string.IsNullOrEmpty(task))
        {
            _console.MarkupLine("[yellow]No task specified. Enter your task:[/]");
            var prompt = new TextPrompt<string>("[green]Task:[/]")
                .PromptStyle("green")
                .AllowEmpty();

            try
            {
                task = _console.Prompt(prompt);
            }
            catch (OperationCanceledException)
            {
                DisplayInfo("Plan mode cancelled.");
                return 0;
            }
        }

        _console.MarkupLine($"\n[bold]Task:[/] {task}");
        _console.MarkupLine("[dim]Creating implementation plan...[/]");

        return await WithSpinnerAsync("Analyzing requirements and creating plan...", async () =>
        {
            await Task.Delay(2000); // Simulate processing

            // Display placeholder plan
            DisplayPlan();

            return 0;
        });
    }

    /// <summary>
    /// Handles the plan exit subcommand.
    /// </summary>
    private Task<int> HandleExitAsync(InvocationContext context)
    {
        DisplayInfo("Exiting plan mode.");

        // Placeholder - in real implementation, this would save the plan and return
        _console.MarkupLine("\n[dim]Plan mode functionality is being implemented.[/]");

        return Task.FromResult(0);
    }

    /// <summary>
    /// Displays a sample plan.
    /// </summary>
    private void DisplayPlan()
    {
        var planSteps = new[]
        {
            "1. Analyze the task requirements and constraints",
            "2. Explore the codebase to understand existing implementation",
            "3. Identify files that need to be modified or created",
            "4. Create detailed step-by-step implementation plan",
            "5. Define success criteria for each step",
            "6. Present the plan for approval"
        };

        _console.MarkupLine("[bold yellow]Implementation Plan[/]");
        foreach (var step in planSteps)
        {
            _console.MarkupLine($"[green]✓[/] {step}");
        }

        _console.MarkupLine("\n[dim]Type 'gemini plan exit' to accept this plan.[/]");
        _console.MarkupLine("[dim]Type 'gemini plan enter <modified_task>' to revise the plan.[/]");
    }
}
