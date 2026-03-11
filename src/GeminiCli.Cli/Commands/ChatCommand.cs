using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using GeminiCli.Core.Chat;
using GeminiCli.Core.Configuration;
using GeminiCli.Core.Logging;
using GeminiCli.Core.Types;

namespace GeminiCli.Cli.Commands;

/// <summary>
/// Handler for the chat command.
/// </summary>
public class ChatCommand : CommandBase
{
    private readonly Option<string?> _modelOption;
    private readonly Option<bool> _continuousOption;
    private readonly Option<bool> _verboseOption;

    public override Command Command { get; }

    public ChatCommand(Config config) : base(config)
    {
        Command = new Command("chat")
        {
            Description = "Start an interactive chat session with the AI"
        };

        // Add options
        _modelOption = new Option<string?>(
            new[] { "-m", "--model" },
            () => null,
            "Use specific AI model");
        Command.AddOption(_modelOption);

        _continuousOption = new Option<bool>(
            new[] { "-c", "--continuous" },
            () => false,
            "Enable continuous mode");
        Command.AddOption(_continuousOption);

        _verboseOption = new Option<bool>(
            new[] { "-v", "--verbose" },
            () => false,
            "Enable verbose output");
        Command.AddOption(_verboseOption);

        // Set handler
        Command.SetHandler(async context => context.ExitCode = await HandleChatAsync(context));
    }

    private async Task<int> HandleChatAsync(InvocationContext context)
    {
        var model = context.ParseResult.GetValueForOption(_modelOption);
        var continuous = context.ParseResult.GetValueForOption(_continuousOption);
        var verbose = context.ParseResult.GetValueForOption(_verboseOption);

        if (verbose)
        {
            DisplayVerbose($"Model: {model ?? _config.GetModel()}");
            DisplayVerbose($"Continuous: {continuous}");
        }

        DisplayInfo("Starting chat session (press Ctrl+C to exit)");
        DisplayInfo("Type 'exit' or 'quit' to end the session");

        _console.WriteLine();

        try
        {
            if (_config.RequiresApiKey() && !_config.HasApiKey())
            {
                DisplayError("API key not configured. Run 'gemini config --api-key <key>' to set it.");
                return 1;
            }

            var selectedModel = model ?? _config.GetModel();
            var contentGenerator = ContentGeneratorFactory.Create(_config);
            await using var chat = new GeminiChat(contentGenerator, selectedModel);

            // Main chat loop
            while (true)
            {
                // Get user input. Use Spectre.Console prompt when interactive,
                // otherwise fall back to reading from standard input.
                string userInput;
                try
                {
                    if (AnsiConsole.Profile.Capabilities.Interactive)
                    {
                        var prompt = new TextPrompt<string>("[green]You:[/]")
                            .PromptStyle("green");

                        userInput = _console.Prompt<string>(prompt);
                    }
                    else
                    {
                        // Non-interactive: read a line from stdin. If stdin is closed,
                        // end the session gracefully.
                        var line = System.Console.ReadLine();
                        if (line is null)
                        {
                            DisplayInfo("Chat session ended (stdin closed).");
                            return 0;
                        }

                        userInput = line;
                    }
                }
                catch (OperationCanceledException)
                {
                    DisplayInfo("Chat session ended.");
                    return 0;
                }

                // Check for exit commands
                if (userInput.ToLower() is "exit" or "quit")
                {
                    DisplayInfo("Chat session ended.");
                    return 0;
                }

                if (userInput.StartsWith("/") && userInput.ToLower().StartsWith("/clear"))
                {
                    chat.ClearHistory();
                    DisplayInfo("Conversation history cleared.");
                    continue;
                }

                await WithSpinnerAsync<int>("Thinking...", async () =>
                {
                    var response = await chat.SendMessageAsync(
                        new List<ContentPart> { new TextContentPart(userInput) });

                    var text = response.Candidates
                        .SelectMany(c => c.Content)
                        .OfType<TextContentPart>()
                        .Select(p => p.Text)
                        .FirstOrDefault();

                    var output = string.IsNullOrWhiteSpace(text) ? "[No text returned by model]" : text;
                    _console.MarkupLine($"[cyan]AI:[/] {Markup.Escape(output)}");

                    if (continuous)
                    {
                        _console.MarkupLine("[dim]Press Enter to continue, or type your next message.[/]");
                    }

                    return 0;
                });

                _console.WriteLine();
            }
        }
        catch (Exception ex)
        {
            DisplayError($"Error in chat session: {ex.Message}");
            return 1;
        }
    }
}
