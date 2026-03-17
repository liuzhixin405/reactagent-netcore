using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using AiCli.Core.Chat;
using AiCli.Core.Configuration;
using AiCli.Core.Types;

namespace AiCli.Cli.Commands;

/// <summary>
/// Handler for the prompt command - single prompt execution.
/// </summary>
public class PromptCommand : CommandBase
{
    private readonly Argument<string[]> _promptArgument;
    private readonly Option<string?> _modelOption;
    private readonly Option<string?> _outputOption;
    private readonly Option<bool> _verboseOption;
    private readonly Option<bool> _noMarkdownOption;

    public override Command Command { get; }

    public PromptCommand(Config config) : base(config)
    {
        Command = new Command("prompt")
        {
            Description = "Execute a single prompt with the AI"
        };

        // Add arguments
        _promptArgument = new Argument<string[]>("prompt")
        {
            Description = "The prompt to send to the AI",
            Arity = ArgumentArity.OneOrMore
        };
        Command.AddArgument(_promptArgument);

        // Add options
        _modelOption = new Option<string?>(
            new[] { "-m", "--model" },
            () => null,
            "Use specific AI model");
        Command.AddOption(_modelOption);

        _outputOption = new Option<string?>(
            new[] { "-o", "--output" },
            () => null,
            "Output file for the response");
        Command.AddOption(_outputOption);

        _verboseOption = new Option<bool>(
            new[] { "-v", "--verbose" },
            () => false,
            "Enable verbose output");
        Command.AddOption(_verboseOption);

        _noMarkdownOption = new Option<bool>(
            new[] { "--no-markdown" },
            () => false,
            "Disable markdown formatting");
        Command.AddOption(_noMarkdownOption);

        // Set handler
        Command.SetHandler(async context => context.ExitCode = await HandlePromptAsync(context));
    }

    private async Task<int> HandlePromptAsync(InvocationContext context)
    {
        var promptArgs = context.ParseResult.GetValueForArgument(_promptArgument);
        var model = context.ParseResult.GetValueForOption(_modelOption);
        var outputFile = context.ParseResult.GetValueForOption(_outputOption);
        var verbose = context.ParseResult.GetValueForOption(_verboseOption);
        var noMarkdown = context.ParseResult.GetValueForOption(_noMarkdownOption);

        var prompt = string.Join(" ", promptArgs ?? Array.Empty<string>());
        IContentGenerator? contentGenerator = null;

        if (verbose)
        {
            DisplayVerbose($"Prompt: {prompt}");
            DisplayVerbose($"Model: {model ?? _config.GetModel()}");
            if (outputFile != null)
            {
                DisplayVerbose($"Output: {outputFile}");
            }
        }

        try
        {
            if (_config.RequiresApiKey() && !_config.HasApiKey())
            {
                DisplayError("API key not configured. Run 'aicli config --api-key <key>' to set it.");
                return 1;
            }

            var selectedModel = model ?? _config.GetModel();
            contentGenerator = ContentGeneratorFactory.Create(_config);

            var response = await WithSpinnerAsync("Generating response...", async () =>
            {
                var request = new GenerateContentRequest
                {
                    Model = selectedModel,
                    Contents = new List<ContentMessage> { ContentMessage.UserMessage(prompt) },
                    Config = new GenerationConfig
                    {
                        Temperature = _config.GetTemperature(),
                        TopP = _config.GetTopP(),
                        TopK = _config.GetTopK(),
                        MaxOutputTokens = _config.GetMaxTokens()
                    }
                };

                var apiResponse = await contentGenerator.GenerateContentAsync(request);
                var firstCandidate = apiResponse.Candidates.FirstOrDefault();

                var text = firstCandidate?.Content
                    .OfType<TextContentPart>()
                    .Select(p => p.Text)
                    .FirstOrDefault();

                return string.IsNullOrWhiteSpace(text)
                    ? "[No text returned by model]"
                    : text;
            });

            // Display response
            if (noMarkdown)
            {
                _console.WriteLine(response);
            }
            else
            {
                _console.Markup($"[bold cyan]Response:[/]\n{Markup.Escape(response)}");
            }

            _console.WriteLine();

            // Save to file if requested
            if (!string.IsNullOrEmpty(outputFile))
            {
                try
                {
                    await File.WriteAllTextAsync(outputFile!, response);
                    DisplaySuccess($"Response saved to: {outputFile}");
                }
                catch (Exception ex)
                {
                    DisplayError($"Failed to save output: {ex.Message}");
                    return 1;
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            DisplayError($"Error processing prompt: {ex.Message}");
            return 1;
        }
        finally
        {
            if (contentGenerator is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
        }
    }
}
