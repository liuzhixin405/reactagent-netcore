using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using AiCli.Core.Chat;
using AiCli.Core.Commands;
using AiCli.Core.Configuration;
using AiCli.Core.Logging;
using AiCli.Core.Types;

namespace AiCli.Cli.Commands;

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
                DisplayError("API key not configured. Run 'aicli config --api-key <key>' to set it.");
                return 1;
            }

            var selectedModel = model ?? _config.GetModel();
            var contentGenerator = ContentGeneratorFactory.Create(_config);
            await using var chat = new AiChat(contentGenerator, selectedModel);

            // Build slash command service
            var slashCommandService = new SlashCommandService();
            var builtins = BuiltinSlashCommands.Build(chat, _config, slashCommandService);
            slashCommandService.RegisterRange(builtins);

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

                // Check for plain exit commands (without slash)
                if (userInput.Trim().ToLowerInvariant() is "exit" or "quit")
                {
                    DisplayInfo("Chat session ended.");
                    return 0;
                }

                // Handle slash commands
                if (userInput.TrimStart().StartsWith('/'))
                {
                    var slashResult = await slashCommandService.ExecuteAsync(userInput);
                    if (slashResult.Handled)
                    {
                        if (!string.IsNullOrEmpty(slashResult.Output))
                            _console.MarkupLine(Markup.Escape(slashResult.Output));
                        if (slashResult.ShouldQuit)
                            return 0;
                        _console.WriteLine();
                        continue;
                    }
                    // Not a recognized slash command — treat as regular input
                }

                await StreamWithThinkingAsync(chat, userInput, continuous, contentGenerator);

                _console.WriteLine();
            }
        }
        catch (Exception ex)
        {
            DisplayError($"Error in chat session: {ex.Message}");
            return 1;
        }
    }

    // ── Streaming thinking display ────────────────────────────────────────────

    private async Task StreamWithThinkingAsync(
        AiChat chat,
        string userInput,
        bool continuous,
        IContentGenerator? orchestrator = null)
    {
        // AutoSelect：短指令+代码关键字 → 快速模型；其余 → 思考模型（null = 默认）
        IContentGenerator? selectedGenerator = null;
        if (orchestrator is MultiModelOrchestrator mmo)
        {
            var msgPart = new ContentMessage
            {
                Role = LlmRole.User,
                Parts = new List<ContentPart> { new TextContentPart(userInput) }
            };
            selectedGenerator = mmo.AutoSelect(msgPart);
        }

        var thinkingBuffer = new System.Text.StringBuilder();
        var responseBuffer = new System.Text.StringBuilder();
        int thinkingLinesShown = 0;
        bool thinkingDone = false;
        var startTime = DateTime.UtcNow;

        await foreach (var chunk in chat.SendMessageStreamAsync(
            new List<ContentPart> { new TextContentPart(userInput) },
            generatorOverride: selectedGenerator))
        {
            foreach (var part in chunk.Candidates.SelectMany(c => c.Content))
            {
                if (part is ThinkingContentPart tp && !string.IsNullOrEmpty(tp.Text))
                {
                    thinkingBuffer.Append(tp.Text);
                    thinkingLinesShown = AgentThinkingRenderer.Render(thinkingBuffer.ToString(), thinkingLinesShown);
                }
                else if (part is TextContentPart tx && !string.IsNullOrEmpty(tx.Text))
                {
                    if (!thinkingDone && thinkingBuffer.Length > 0)
                    {
                        // Clear thinking lines and print summary
                        AgentThinkingRenderer.Clear(thinkingLinesShown);
                        var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                        _console.MarkupLine($"[dim]◆ 思考完成 ({elapsed:F1}s)[/]");
                        thinkingDone = true;
                        thinkingLinesShown = 0;
                    }
                    responseBuffer.Append(tx.Text);
                }
            }
        }

        // Print final response
        var output = responseBuffer.Length > 0 ? responseBuffer.ToString() : "[No text returned by model]";
        _console.MarkupLine($"[cyan]AI:[/] {Markup.Escape(output)}");

        if (continuous)
            _console.MarkupLine("[dim]Press Enter to continue, or type your next message.[/]");
    }

}
