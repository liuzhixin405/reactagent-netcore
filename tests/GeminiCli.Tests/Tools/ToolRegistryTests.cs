using FluentAssertions;
using GeminiCli.Core.Tools;
using GeminiCli.Core.Types;
using Moq;
using Xunit;

namespace GeminiCli.Tests.Tools;

/// <summary>
/// Tests for ToolRegistry.
/// </summary>
public class ToolRegistryTests
{
    private readonly ToolRegistry _registry;
    private readonly Mock<ITool> _mockTool;

    public ToolRegistryTests()
    {
        _registry = new ToolRegistry();
        _mockTool = new Mock<ITool>();

        _mockTool.Setup(t => t.Name).Returns("test_tool");
        _mockTool.Setup(t => t.DisplayName).Returns("Test Tool");
        _mockTool.Setup(t => t.Description).Returns("A test tool");
        _mockTool.Setup(t => t.Kind).Returns(ToolKind.Read);
    }

    [Fact]
    public void RegisterTool_ShouldAddTool()
    {
        // Act
        _registry.Register("test_tool", _mockTool.Object);

        // Assert
        var tool = _registry.GetTool("test_tool");
        tool.Should().NotBeNull();
        tool.Should().Be(_mockTool.Object);
    }

    [Fact]
    public void RegisterTool_ShouldThrowOnDuplicate()
    {
        // Arrange
        _registry.Register("test_tool", _mockTool.Object);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
        {
            _registry.Register("test_tool", _mockTool.Object);
        });
    }

    [Fact]
    public void GetTool_ShouldReturnTool()
    {
        // Arrange
        _registry.Register("test_tool", _mockTool.Object);

        // Act
        var tool = _registry.GetTool("test_tool");

        // Assert
        tool.Should().NotBeNull();
        tool.Should().Be(_mockTool.Object);
    }

    [Fact]
    public void GetTool_ShouldReturnNullForNotFound()
    {
        // Act
        var tool = _registry.GetTool("non_existent_tool");

        // Assert
        tool.Should().BeNull();
    }

    [Fact]
    public void GetAll_ShouldReturnAllTools()
    {
        // Arrange
        _registry.Register("tool1", _mockTool.Object);
        var mockTool2 = new Mock<ITool>();
        mockTool2.Setup(t => t.Name).Returns("tool2");
        _registry.Register("tool2", mockTool2.Object);

        // Act
        var tools = _registry.GetAll();

        // Assert
        tools.Should().HaveCount(2);
    }

    [Fact]
    public void Unregister_ShouldRemoveTool()
    {
        // Arrange
        _registry.Register("test_tool", _mockTool.Object);

        // Act
        var removed = _registry.Unregister("test_tool");

        // Assert
        removed.Should().BeTrue();
        _registry.GetTool("test_tool").Should().BeNull();
    }

    [Fact]
    public void Unregister_ShouldReturnFalseForNotFound()
    {
        // Act
        var removed = _registry.Unregister("non_existent_tool");

        // Assert
        removed.Should().BeFalse();
    }

    [Fact]
    public void HasTool_ShouldReturnTrue()
    {
        // Arrange
        _registry.Register("test_tool", _mockTool.Object);

        // Act
        var hasTool = _registry.HasTool("test_tool");

        // Assert
        hasTool.Should().BeTrue();
    }

    [Fact]
    public void HasTool_ShouldReturnFalseForNotFound()
    {
        // Act
        var hasTool = _registry.HasTool("non_existent_tool");

        // Assert
        hasTool.Should().BeFalse();
    }

    [Fact]
    public void FindByKind_ShouldReturnMatchingTools()
    {
        // Arrange
        var mockTool2 = new Mock<ITool>();
        mockTool2.Setup(t => t.Name).Returns("tool2");
        mockTool2.Setup(t => t.Kind).Returns(ToolKind.Write);

        _registry.Register("tool1", _mockTool.Object);
        _registry.Register("tool2", mockTool2.Object);

        // Act
        var readTools = _registry.FindByKind(ToolKind.Read);

        // Assert
        readTools.Should().HaveCount(1);
        readTools.First().Name.Should().Be("test_tool");
    }
}
