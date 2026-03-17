using Spectre.Console;
using System.CommandLine;
using AiCli.Core.Configuration;

namespace AiCli.Cli.Commands;

/// <summary>
/// Base class for CLI commands.
/// </summary>
public abstract class CommandBase
{
    protected readonly Config _config;
    protected readonly IAnsiConsole _console;

    protected CommandBase(Config config)
    {
        _config = config;
        _console = AnsiConsole.Console;
    }

    /// <summary>
    /// Gets the command for this handler.
    /// </summary>
    public abstract Command Command { get; }

    /// <summary>
    /// Displays an error message.
    /// </summary>
    protected void DisplayError(string message)
    {
        _console.MarkupLine($"[red]Error:[/] {Markup.Escape(message)}");
    }

    /// <summary>
    /// Displays a warning message.
    /// </summary>
    protected void DisplayWarning(string message)
    {
        _console.MarkupLine($"[yellow]Warning:[/] {Markup.Escape(message)}");
    }

    /// <summary>
    /// Displays a success message.
    /// </summary>
    protected void DisplaySuccess(string message)
    {
        _console.MarkupLine($"[green]Success:[/] {Markup.Escape(message)}");
    }

    /// <summary>
    /// Displays an info message.
    /// </summary>
    protected void DisplayInfo(string message)
    {
        _console.MarkupLine($"[blue]Info:[/] {Markup.Escape(message)}");
    }

    /// <summary>
    /// Displays a verbose message.
    /// </summary>
    protected void DisplayVerbose(string message)
    {
        _console.MarkupLine($"[dim]Verbose:[/] {Markup.Escape(message)}");
    }

    /// <summary>
    /// Displays a spinner with a task.
    /// </summary>
    protected async Task<T> WithSpinnerAsync<T>(
        string message,
        Func<Task<T>> action)
    {
        return await _console.Status()
            .Spinner(Spinner.Known.Default)
            .Start(message, async ctx =>
            {
                var result = await action();
                ctx.Status(message);
                return result;
            });
    }

    /// <summary>
    /// Displays a table.
    /// </summary>
    protected void DisplayTable<T>(string title, List<T> items, Func<T, string>[] columns)
    {
        var table = new Table();
        table.Title(title);
        table.Border(TableBorder.Rounded);

        // Add columns
        for (var i = 0; i < columns.Length; i++)
        {
            table.AddColumn(new TableColumn($"[bold]{i + 1}[/]"));
        }

        // Add rows
        foreach (var item in items)
        {
            var values = columns.Select(col => col(item)).ToArray();
            table.AddRow(values);
        }

        _console.Write(table);
    }
}
