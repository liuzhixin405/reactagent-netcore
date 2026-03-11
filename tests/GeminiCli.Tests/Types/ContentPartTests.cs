using FluentAssertions;
using GeminiCli.Core.Types;
using Xunit;

namespace GeminiCli.Tests.Types;

/// <summary>
/// Tests for ContentPart types.
/// </summary>
public class ContentPartTests
{
    [Fact]
    public void TextContentPart_ShouldCreateTextPart()
    {
        // Arrange
        var text = "Hello, world!";

        // Act
        var part = new TextContentPart(text);

        // Assert
        part.Text.Should().Be(text);
    }

    [Fact]
    public void FunctionCallPart_ShouldCreateFunctionCall()
    {
        // Arrange
        var functionName = "read_file";
        var arguments = new Dictionary<string, object?>
        {
            ["file_path"] = "test.txt"
        };

        // Act
        var part = new FunctionCallPart
        {
            FunctionName = functionName,
            Arguments = arguments
        };

        // Assert
        part.FunctionName.Should().Be(functionName);
        part.Arguments.Should().HaveCount(1);
        part.Arguments["file_path"].Should().Be("test.txt");
    }

    [Fact]
    public void FunctionResponsePart_ShouldCreateFunctionResponse()
    {
        // Arrange
        var functionName = "read_file";
        var response = new Dictionary<string, object?>
        {
            ["content"] = "File content",
            ["success"] = true
        };

        // Act
        var part = new FunctionResponsePart
        {
            FunctionName = functionName,
            Response = response
        };

        // Assert
        part.FunctionName.Should().Be(functionName);
        part.Response["content"].Should().Be("File content");
    }

    [Fact]
    public void ContentMessage_ShouldCreateMessageWithRole()
    {
        // Arrange
        var role = LlmRole.User;
        var parts = new List<ContentPart>
        {
            new TextContentPart("Test message")
        };

        // Act
        var message = new ContentMessage
        {
            Role = role,
            Parts = parts
        };

        // Assert
        message.Role.Should().Be(role);
        message.Parts.Should().HaveCount(1);
    }

    [Fact]
    public void ToolExecutionResult_ShouldCreateSuccessResult()
    {
        // Arrange
        var output = "Operation completed successfully";

        // Act
        var result = new ToolExecutionResult
        {
            IsError = false,
            Output = output
        };

        // Assert
        result.IsError.Should().BeFalse();
        result.Output.Should().Be(output);
    }

    [Fact]
    public void ToolExecutionResult_ShouldCreateErrorResult()
    {
        // Arrange
        var errorMessage = "File not found";

        // Act
        var result = ToolExecutionResult.Failure(
            errorMessage,
            ToolErrorType.NotFound);

        // Assert
        result.IsError.Should().BeTrue();
        result.Output.Should().Be(errorMessage);
        result.ErrorType.Should().Be(ToolErrorType.NotFound);
    }
}
