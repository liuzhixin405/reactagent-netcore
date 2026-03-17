using FluentAssertions;
using AiCli.Core.Scheduler;
using Xunit;

namespace AiCli.Tests.Scheduler;

/// <summary>
/// Tests for MessageBus.
/// </summary>
public class MessageBusTests : IDisposable
{
    private readonly MessageBus _messageBus;

    public MessageBusTests()
    {
        _messageBus = new MessageBus();
    }

    [Fact]
    public void Publish_ShouldRaiseEvent()
    {
        // Arrange
        MessageBusMessage? receivedMessage = null;
        _messageBus.MessageReceived += (s, e) =>
        {
            receivedMessage = e.Message;
        };

        // Act
        _messageBus.Publish("test_type", new { data = "test" });

        // Assert
        receivedMessage.Should().NotBeNull();
        receivedMessage.Type.Should().Be("test_type");
    }

    [Fact]
    public void Subscribe_ShouldReceiveMessages()
    {
        // Arrange
        List<MessageBusMessage> messages = new();
        _messageBus.Subscribe("test_type", (e) =>
        {
            messages.Add(e.Message);
        });

        // Act
        _messageBus.Publish("test_type", new { data = "test1" });
        _messageBus.Publish("test_type", new { data = "test2" });
        _messageBus.Publish("other_type", new { data = "ignored" });

        // Assert
        messages.Should().HaveCount(2);
    }

    [Fact]
    public void Unsubscribe_ShouldStopReceiving()
    {
        // Arrange
        List<MessageBusMessage> messages = new();
        Action<AiCli.Core.Scheduler.MessageBusEventArgs> handler = (e) =>
        {
            messages.Add(e.Message);
        };

        _messageBus.Subscribe("test_type", handler);
        _messageBus.Publish("test_type", new { data = "test1" });

        // Act
        _messageBus.Unsubscribe("test_type", handler);
        _messageBus.Publish("test_type", new { data = "test2" });

        // Assert
        messages.Should().HaveCount(1);
    }

    [Fact]
    public async Task Respond_ShouldSendResponseToPublisher()
    {
        // Arrange
        MessageBusMessage? originalMessage = null;
        _messageBus.MessageReceived += (s, e) =>
        {
            if (e.Message.Type == "request")
            {
                originalMessage = e.Message;
                _messageBus.Respond(e.Message.Id, new { response = "test_response" });
            }
        };

        var responseReceived = false;
        var responseTcs = new TaskCompletionSource<bool>();
        _messageBus.MessageReceived += (s, e) =>
        {
            if (e.Message.Type == "response" && e.Message.InResponseTo == originalMessage?.Id)
            {
                responseReceived = true;
                responseTcs.SetResult(true);
            }
        };

        // Act
        var response = await _messageBus.PublishAndWaitAsync("request", new { data = "test" }, TimeSpan.FromSeconds(1));

        // Assert
        responseReceived.Should().BeTrue();
    }

    [Fact]
    public void Dispose_ShouldCleanup()
    {
        // Arrange
        _messageBus.Subscribe("test_type", _ => { });

        // Act
        _messageBus.Dispose();

        // Assert - no exceptions
        // (In a full test, we would verify cleanup)
    }

    public void Dispose()
    {
        _messageBus.Dispose();
    }
}
