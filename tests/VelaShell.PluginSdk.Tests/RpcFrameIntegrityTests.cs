using System.Text;
using System.Text.Json;
using VelaShell.PluginSdk.Rpc;

namespace VelaShell.PluginSdk.Tests;

/// <summary>
/// <see cref="RpcConnection" /> 的帧原子性:一帧要么整帧写完、要么一个字节都不写。
/// 长度前缀的帧流没法重新同步,写出半帧之后对端会把后续字节当成下一帧的长度去读。
/// </summary>
[TestClass]
public sealed class RpcFrameIntegrityTests
{
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 包一层可控的写:记录流写入收到的每个令牌、能让下一次正文写入卡在一半(模拟对端不读、管道写满)、
    /// 能让下一次正文写入写一半就失败(模拟管道断开)。释放时把卡住的写入以 ObjectDisposedException 放出来,
    /// 与真实管道「释放本端流会结束挂起的 I/O」一致。
    /// </summary>
    private sealed class ControlledWriteStream(Stream inner) : Stream
    {
        private readonly Lock _gate = new();
        private readonly List<CancellationToken> _tokens = [];
        private readonly List<byte[]> _bodies = [];
        private TaskCompletionSource? _armedStall;
        private TaskCompletionSource? _activeStall;
        private bool _failNextBody;

        public TaskCompletionSource Stalled { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Failed { get; private set; }

        public int WritesAfterFailure { get; private set; }

        public CancellationToken[] Tokens { get { lock (_gate) return [.. _tokens]; } }

        /// <summary>写出过的帧正文(含写到一半的那一个),按 UTF-8 解成文本便于断言。</summary>
        public string[] Bodies { get { lock (_gate) return [.. _bodies.Select(b => Encoding.UTF8.GetString(b))]; } }

        /// <summary>下一次正文写入写一半后卡住,直到返回的 TCS 被放行。</summary>
        public TaskCompletionSource StallNextBody()
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _armedStall = release;
            return release;
        }

        public void FailNextBodyMidway() => _failNextBody = true;

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (_gate) _tokens.Add(cancellationToken);
            if (Failed)
            {
                WritesAfterFailure++;
            }
            bool isBody = buffer.Length > 4;   // 帧头恒为 4 字节
            if (isBody && Interlocked.Exchange(ref _armedStall, null) is { } stall)
            {
                _activeStall = stall;
                int half = buffer.Length / 2;
                await inner.WriteAsync(buffer[..half], CancellationToken.None);
                await inner.FlushAsync(CancellationToken.None);
                Stalled.TrySetResult();
                await stall.Task;
                await inner.WriteAsync(buffer[half..], CancellationToken.None);
                lock (_gate) _bodies.Add(buffer.ToArray());
                return;
            }
            if (isBody && _failNextBody)
            {
                _failNextBody = false;
                Failed = true;
                await inner.WriteAsync(buffer[..(buffer.Length / 2)], CancellationToken.None);
                await inner.FlushAsync(CancellationToken.None);
                lock (_gate) _bodies.Add(buffer[..(buffer.Length / 2)].ToArray());
                throw new IOException("Injected mid-frame write failure.");
            }
            await inner.WriteAsync(buffer, CancellationToken.None);
            if (isBody)
            {
                lock (_gate) _bodies.Add(buffer.ToArray());
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            lock (_gate) _tokens.Add(cancellationToken);
            return inner.FlushAsync(CancellationToken.None);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);

        /// <summary>
        /// 释放时是否把卡住的写入放出来(默认是,与真实管道一致)。置 false 模拟「释放了流,那次写入仍然回不来」——
        /// 只有这样才看得出排在后面的写入是靠连接寿命、而不是靠前一帧碰巧结束被放出来的。
        /// </summary>
        public bool ReleaseStallOnDispose { get; init; } = true;

        public override async ValueTask DisposeAsync()
        {
            if (ReleaseStallOnDispose)
            {
                _activeStall?.TrySetException(new ObjectDisposedException("stream"));
                _armedStall?.TrySetException(new ObjectDisposedException("stream"));
            }
            await inner.DisposeAsync();
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>宿主一侧:<c>echo</c> 原样返回字符串载荷,并记下收到过哪些。</summary>
    private static (RpcConnection Host, List<string> Received, Func<string, Task> WaitFor) StartEchoHost(Stream stream)
    {
        var host = new RpcConnection(stream);
        var received = new List<string>();
        var arrivals = new Dictionary<string, TaskCompletionSource>(StringComparer.Ordinal);
        TaskCompletionSource Arrival(string value)
        {
            lock (arrivals)
            {
                return arrivals.TryGetValue(value, out TaskCompletionSource? tcs)
                    ? tcs
                    : arrivals[value] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        host.SetRequestHandler((method, payload, _) =>
        {
            string value = payload!.Value.GetString()!;
            lock (received) received.Add(value);
            Arrival(value).TrySetResult();
            return Task.FromResult<object?>(method == "echo" ? value : throw new InvalidOperationException(method));
        });
        host.SetNotificationHandler((_, payload) =>
        {
            string value = payload!.Value.GetString()!;
            lock (received) received.Add(value);
            Arrival(value).TrySetResult();
        });
        host.Start();
        return (host, received, value => Arrival(value).Task.WaitAsync(Generous));
    }

    [TestMethod]
    public async Task CancelAfterTheFrameStarted_ReturnsPromptly_ButTheWholeFrameIsStillWritten()
    {
        await using RpcTestPipes pipes = await RpcTestPipes.ConnectAsync();
        var wire = new ControlledWriteStream(pipes.Client);
        (RpcConnection host, List<string> received, Func<string, Task> waitFor) = StartEchoHost(pipes.Server);
        await using RpcConnection hostScope = host;
        await using var plugin = new RpcConnection(wire);
        plugin.Start();

        TaskCompletionSource release = wire.StallNextBody();
        using var cts = new CancellationTokenSource();
        Task<string?> first = plugin.RequestAsync<string>("echo", "first", Generous, cts.Token);
        await wire.Stalled.Task.WaitAsync(Generous);          // 帧头已写、正文写了一半
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => first.WaitAsync(Prompt),
            "调用方的取消要立即生效,不陪着一次卡住的写入等");
        Assert.IsFalse(release.Task.IsCompleted, "写入还卡着,调用方就已经拿到了取消");

        release.SetResult();
        await waitFor("first");                                 // 帧被写完整,对端照样收到、照样执行
        Assert.AreEqual("second", await plugin.RequestAsync<string>("echo", "second", Generous),
            "连接没有失步:取消之后同一条连接照常收发");
        CollectionAssert.AreEqual(new[] { "first", "second" }, received.ToArray());
        Assert.IsTrue(wire.Tokens.All(token => !token.CanBeCanceled),
            "调用方的令牌一个都不该传进流写入 —— 传进去就可能在帧中途被撕断");
    }

    [TestMethod]
    public async Task CancelWhileQueuedForTheWriteGate_SendsNoBytesOfThatFrame()
    {
        await using RpcTestPipes pipes = await RpcTestPipes.ConnectAsync();
        var wire = new ControlledWriteStream(pipes.Client);
        (RpcConnection host, List<string> received, Func<string, Task> waitFor) = StartEchoHost(pipes.Server);
        await using RpcConnection hostScope = host;
        await using var plugin = new RpcConnection(wire);
        plugin.Start();

        TaskCompletionSource release = wire.StallNextBody();
        Task holding = plugin.NotifyAsync("hold", "holding");   // 占着写闸
        await wire.Stalled.Task.WaitAsync(Generous);
        using var cts = new CancellationTokenSource();
        Task<string?> queued = plugin.RequestAsync<string>("echo", "never-sent", Generous, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => queued.WaitAsync(Prompt));

        release.SetResult();
        await holding.WaitAsync(Generous);
        await waitFor("holding");
        Assert.AreEqual("after", await plugin.RequestAsync<string>("echo", "after", Generous));
        Assert.IsFalse(wire.Bodies.Any(body => body.Contains("never-sent", StringComparison.Ordinal)),
            "排队时取消的那一帧一个字节都不该上线");
        CollectionAssert.DoesNotContain(received.ToArray(), "never-sent");
    }

    [TestMethod]
    public async Task WriteFailingMidFrame_AbandonsTheConnection_InsteadOfSendingMoreFrames()
    {
        await using RpcTestPipes pipes = await RpcTestPipes.ConnectAsync();
        var wire = new ControlledWriteStream(pipes.Client);
        (RpcConnection host, List<string> received, _) = StartEchoHost(pipes.Server);
        await using RpcConnection hostScope = host;
        var hostDisconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Disconnected += () => hostDisconnected.TrySetResult();
        await using var plugin = new RpcConnection(wire);
        var pluginDisconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        plugin.Disconnected += () => pluginDisconnected.TrySetResult();
        plugin.Start();

        wire.FailNextBodyMidway();
        await plugin.NotifyAsync("note", "half-written");       // 尽力而为:不抛,但连接必须作废

        await pluginDisconnected.Task.WaitAsync(Generous);
        await Assert.ThrowsExactlyAsync<RpcDisconnectedException>(() =>
            plugin.RequestAsync<string>("echo", "after-break", Generous).WaitAsync(Prompt));
        await plugin.NotifyAsync("note", "also-after-break");
        Assert.AreEqual(0, wire.WritesAfterFailure, "半帧之后一个字节都不能再写");

        await hostDisconnected.Task.WaitAsync(Generous);       // 对端看到的是断开,不是一条被错读的消息
        Assert.IsEmpty(received);
    }

    [TestMethod]
    public async Task WritersQueuedBehindAStuckFrame_AreReleasedWhenTheConnectionCloses()
    {
        await using RpcTestPipes pipes = await RpcTestPipes.ConnectAsync();
        var wire = new ControlledWriteStream(pipes.Client) { ReleaseStallOnDispose = false };
        (RpcConnection host, _, _) = StartEchoHost(pipes.Server);
        await using RpcConnection hostScope = host;
        var plugin = new RpcConnection(wire);
        plugin.Start();

        TaskCompletionSource release = wire.StallNextBody();      // 释放连接也回不来:模拟对端彻底不读
        Task holding = plugin.NotifyAsync("hold", "stuck");
        await wire.Stalled.Task.WaitAsync(Generous);
        // 不带令牌排在后面的请求(执行操作就是这样发的):只能靠连接寿命放出来。
        Task<string?> queued = plugin.RequestAsync<string>("echo", "queued", Generous);
        await Task.Delay(50);
        Assert.IsFalse(queued.IsCompleted);

        await plugin.DisposeAsync().AsTask().WaitAsync(Generous);
        await Assert.ThrowsExactlyAsync<RpcDisconnectedException>(() => queued.WaitAsync(Prompt),
            "连接关了,排队的调用必须退出,不能等那次回不来的写入");
        Assert.IsFalse(holding.IsCompleted, "占着写闸的那次写入确实还没回来");

        release.SetResult();                                       // 收尾:那次写入终于回来,落到已释放的流上
        await holding.WaitAsync(Prompt);                           // 通知照旧静默,不把异常漏出来
    }

    [TestMethod]
    public async Task ExistingExecutionHalfFrameGuard_StillDisconnects()
    {
        // 执行通知的严格发送路径:半帧之后整条连接失败,不能在坏帧后补一个「成功」终态。
        await using RpcTestPipes pipes = await RpcTestPipes.ConnectAsync();
        var wire = new ControlledWriteStream(pipes.Server);
        await using var host = new RpcConnection(wire);
        await using var plugin = new RpcConnection(pipes.Client);
        host.SetRequestHandler(async (_, _, _) =>
        {
            wire.FailNextBodyMidway();
            await host.NotifyExecutionAsync(PluginRpc.ExecOutput,
                new ExecOutputNotification("token", false, "must not vanish") { Sequence = 1 });
            return "ok";
        });
        host.Start();
        plugin.Start();
        await Assert.ThrowsExactlyAsync<RpcDisconnectedException>(() =>
            plugin.RequestAsync<string>(PluginRpc.ExecStream, JsonSerializer.SerializeToElement("x"), Generous));
        Assert.AreEqual(0, wire.WritesAfterFailure);
    }
}
