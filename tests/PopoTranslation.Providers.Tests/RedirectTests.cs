using System.Net;
using System.Net.Sockets;
using System.Text;
using PopoTranslation.Core;
using PopoTranslation.Providers;

namespace PopoTranslation.Providers.Tests;

public sealed class RedirectTests
{
    [Theory]
    [InlineData(ApiProtocol.OpenAiChatCompletions)]
    [InlineData(ApiProtocol.OpenAiResponses)]
    [InlineData(ApiProtocol.AnthropicMessages)]
    [InlineData(ApiProtocol.GeminiGenerateContent)]
    public async Task DoesNotForwardCredentialsOrBodyOn307(ApiProtocol protocol)
    {
        using var source = new TcpListener(IPAddress.Loopback, 0);
        using var destination = new TcpListener(IPAddress.Loopback, 0);
        source.Start();
        destination.Start();
        int sourcePort = ((IPEndPoint)source.LocalEndpoint).Port;
        int destinationPort = ((IPEndPoint)destination.LocalEndpoint).Port;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var server = SendRedirectAsync(source, destinationPort, deadline.Token);
        var profile = new ProviderProfile("id", "test", protocol, $"http://127.0.0.1:{sourcePort}", "test-model", "ref", 10);
        var provider = TranslationProviderFactory.Create(profile, "dummy-key");
        var result = await provider.TranslateAsync(new TranslationRequest("dummy text", "Chinese", null, null), deadline.Token);
        await server;
        Assert.Equal(TranslationStatus.Failed, result.Status);
        Assert.Equal("http_307", result.ErrorCode);
        Assert.False(destination.Pending());
    }

    private static async Task SendRedirectAsync(TcpListener listener, int destinationPort, CancellationToken cancellationToken)
    {
        using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = connection.GetStream();
        var buffer = new byte[8192];
        int read = await stream.ReadAsync(buffer, cancellationToken);
        Assert.True(read > 0);
        var response = $"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{destinationPort}/capture\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
    }
}
