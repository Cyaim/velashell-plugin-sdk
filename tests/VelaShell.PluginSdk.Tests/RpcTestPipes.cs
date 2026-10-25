using System.IO.Pipes;

namespace VelaShell.PluginSdk.Tests;

/// <summary>
/// 一对已连接的本机命名管道(.NET 在 macOS / Linux 底层是 UDS),与宿主拉隔离进程同一种传输。
/// 契约测试拿它把 RPC 两端真正接起来,而不是在内存里伪造帧。
/// </summary>
internal sealed class RpcTestPipes : IAsyncDisposable
{
    private RpcTestPipes(NamedPipeServerStream server, NamedPipeClientStream client)
    {
        Server = server;
        Client = client;
    }

    /// <summary>宿主一侧。</summary>
    public NamedPipeServerStream Server { get; }

    /// <summary>插件一侧。</summary>
    public NamedPipeClientStream Client { get; }

    public static async Task<RpcTestPipes> ConnectAsync()
    {
        string name = "vela-sdk-contract-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        Task accepted = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await accepted;
        return new(server, client);
    }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        await Client.DisposeAsync();
    }
}
