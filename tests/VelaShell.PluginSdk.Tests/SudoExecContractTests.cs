using System.IO.Pipes;
using System.Text.Json;
using VelaShell.PluginSdk.RemoteExec;
using VelaShell.PluginSdk.Rpc;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.PluginSdk.Tests;

[TestClass]
public class SudoExecContractTests
{
    private sealed class Sink<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class LegacyExec : IRemoteExecApi
    {
        public int Calls { get; private set; }
        public Task<ExecResult> RunAsync(string sessionId, string command, ExecOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ExecResult("ordinary"));
        }
    }

    [TestMethod]
    public void LegacyHost_DoesNotSilentlyDowngradeSudo()
    {
        var legacy = new LegacyExec();
        Assert.ThrowsExactly<NotSupportedException>(() => ((IRemoteExecApi)legacy).StreamSudoAsync(
            "session", "id", null, new Sink<SudoExecEvent>(_ => { })));
        Assert.AreEqual(0, legacy.Calls);
    }

    [TestMethod]
    public async Task FakeSudo_RequiresDedicatedHandler()
    {
        var fake = new FakeRemoteExec { Handler = (_, _) => throw new AssertFailedException("ordinary executed") };
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => fake.StreamSudoAsync(
            "s", "id", null, new Sink<SudoExecEvent>(_ => { })));
        Assert.AreEqual(0, fake.Executed.Count);
        Assert.AreEqual(1, fake.SudoStreamed.Count);
    }

    [TestMethod]
    public async Task FakeSudo_PreservesOrderedMergedFragmentsAndNonzeroExit()
    {
        List<SudoExecEvent> received = [];
        using var cts = new CancellationTokenSource();
        var options = new SudoExecOptions { Timeout = TimeSpan.FromMinutes(1) };
        var fake = new FakeRemoteExec
        {
            SudoHandler = (session, command, actualOptions, sink, token) =>
            {
                Assert.AreEqual("s", session);
                Assert.AreEqual("id", command);
                Assert.AreSame(options, actualOptions);
                Assert.AreEqual(cts.Token, token);
                sink.Report(new(SudoExecEventKind.State, SudoExecState.AwaitingApproval));
                sink.Report(new(SudoExecEventKind.State, SudoExecState.Running));
                sink.Report(new(SudoExecEventKind.Output, Text: "进度\r"));
                sink.Report(new(SudoExecEventKind.Output, Text: "failure without newline"));
                return Task.FromResult(new SudoExecResult(SudoExecOutcome.Exited, 7, 2));
            }
        };
        SudoExecResult result = await fake.StreamSudoAsync("s", "id", options,
            new Sink<SudoExecEvent>(received.Add), cts.Token);
        Assert.AreEqual(SudoExecOutcome.Exited, result.Outcome);
        Assert.AreEqual(7, result.ExitCode);
        Assert.AreEqual(2L, result.OutputChunks);
        Assert.AreEqual(4, received.Count);
        Assert.AreEqual("进度\r", received[2].Text);
    }

    [TestMethod]
    public async Task FakeSudo_PropagatesCancellationWithoutExecution()
    {
        var fake = new FakeRemoteExec();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => fake.StreamSudoAsync(
            "s", "id", null, new Sink<SudoExecEvent>(_ => { }), cts.Token));
        Assert.AreEqual(0, fake.SudoStreamed.Count);
    }

    [TestMethod]
    public void MissingOutcomeOrExitCode_CannotBecomeSuccess()
    {
        SudoExecResult empty = JsonSerializer.Deserialize<SudoExecResult>("{}")!;
        Assert.AreEqual(SudoExecOutcome.Unknown, empty.Outcome);
        Assert.IsFalse(empty.IsSuccess);
        Assert.IsFalse(new SudoExecResult(SudoExecOutcome.Exited).IsSuccess);
        Assert.IsFalse(new SudoExecResult(SudoExecOutcome.Unknown, 0).IsSuccess);
        Assert.IsTrue(new SudoExecResult(SudoExecOutcome.Exited, 0).IsSuccess);
        Assert.AreEqual(RpcErrorCodes.Cancelled, RpcErrorCodes.FromException(new OperationCanceledException()));
        Assert.IsInstanceOfType<OperationCanceledException>(RpcErrorCodes.ToException(RpcErrorCodes.Cancelled, "cancelled"));
        Assert.IsInstanceOfType<NotSupportedException>(RpcErrorCodes.ToException(RpcErrorCodes.NotSupported, "unsupported"));
    }

    [TestMethod]
    public void RpcDtos_ContainNoInputOrCallerIdentity()
    {
        string[] expected = ["SessionId", "Command", "OperationId", "TimeoutSeconds"];
        CollectionAssert.AreEquivalent(expected, typeof(ExecSudoStreamRequest).GetProperties().Select(p => p.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "Timeout" }, typeof(SudoExecOptions).GetProperties().Select(p => p.Name).ToArray());
        var request = new ExecSudoStreamRequest("s", "id", "operation", 0);
        Assert.AreEqual(request, JsonSerializer.Deserialize<ExecSudoStreamRequest>(JsonSerializer.Serialize(request)));
        var old = JsonSerializer.Deserialize<ExecStreamRequest>("{\"SessionId\":\"s\",\"Command\":\"c\",\"OutputToken\":\"t\",\"TimeoutSeconds\":0,\"IncludeStandardError\":true}");
        Assert.IsNotNull(old);
        Assert.IsNull(old.OperationId);
        HandshakeResponse legacy = JsonSerializer.Deserialize<HandshakeResponse>(
            "{\"ApiLevel\":2,\"HostVersion\":\"1\",\"Locale\":\"en\",\"Theme\":\"dark\"}")!;
        Assert.IsFalse(legacy.SupportsExecutionOperations);
    }

    [TestMethod]
    public void ExistingRpcConstructorsAndDeconstructors_RemainBinaryCompatible()
    {
        Assert.IsNotNull(typeof(ExecStreamRequest).GetConstructor(
            [typeof(string), typeof(string), typeof(string), typeof(double), typeof(bool)]));
        Assert.IsNotNull(typeof(ExecOutputNotification).GetConstructor(
            [typeof(string), typeof(bool), typeof(string)]));
        Assert.AreEqual(5, typeof(ExecStreamRequest).GetMethod("Deconstruct")!.GetParameters().Length);
        Assert.AreEqual(3, typeof(ExecOutputNotification).GetMethod("Deconstruct")!.GetParameters().Length);
    }

    private sealed class FailNextWriteStream(Stream inner) : Stream
    {
        internal bool FailNextWrite { get; set; }
        internal bool FailMidBody { get; set; }
        private bool _headerWritten;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new IOException("Injected execution notification write failure.");
            }
            if (FailMidBody)
            {
                if (_headerWritten)
                {
                    FailMidBody = false;
                    await inner.WriteAsync(buffer[..(buffer.Length / 2)], cancellationToken);
                    await inner.FlushAsync(cancellationToken);
                    throw new IOException("Injected partial execution frame write failure.");
                }
                _headerWritten = true;
            }
            await inner.WriteAsync(buffer, cancellationToken);
        }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExecutionNotificationWriteFailure_CannotBecomeSuccessfulTerminalResult(bool partialFrame)
    {
        string name = "vela-sdk-failure-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        Task accepted = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await accepted;
        var transport = new FailNextWriteStream(server);
        await using var host = new RpcConnection(transport);
        await using var plugin = new RpcConnection(client);
        int received = 0;
        plugin.SetOrderedNotificationHandler(PluginRpc.ExecSudoEvent, _ => received++);
        host.SetRequestHandler(async (_, _, _) =>
        {
            transport.FailNextWrite = !partialFrame;
            transport.FailMidBody = partialFrame;
            await host.NotifyExecutionAsync(PluginRpc.ExecSudoEvent,
                new ExecSudoEventNotification("op", 1, new(SudoExecEventKind.Output, Text: "must not disappear")));
            return new SudoExecResult(SudoExecOutcome.Exited, 0, 1);
        });
        host.Start();
        plugin.Start();
        await Assert.ThrowsExactlyAsync<RpcDisconnectedException>(() => plugin.RequestAsync<SudoExecResult>(
            PluginRpc.ExecSudoStream, new ExecSudoStreamRequest("s", "id", "op", 0), TimeSpan.FromSeconds(5)));
        Assert.AreEqual(0, received);
        await Assert.ThrowsExactlyAsync<RpcDisconnectedException>(() => plugin.RequestAsync<string>(
            "probe-after-partial-frame", null, TimeSpan.FromSeconds(5)));
        // 通用通知仍然是原来的尽力语义,不把整个RPC系统改成严格发送。
        await host.NotifyAsync("ordinary/best-effort", new { });
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => host.NotifyExecutionAsync("ordinary/best-effort", new { }));
    }

    [TestMethod]
    public async Task OrderedExecutionNotifications_AreDeliveredBeforeTerminalResponse()
    {
        string name = "vela-sdk-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        Task accepted = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await accepted;
        await using var host = new RpcConnection(server);
        await using var plugin = new RpcConnection(client);
        List<int> received = [];
        plugin.SetOrderedNotificationHandler(PluginRpc.ExecSudoEvent, payload =>
        {
            var notification = payload!.Value.Deserialize<ExecSudoEventNotification>()!;
            received.Add((int)notification.Sequence);
        });
        host.SetRequestHandler(async (_, _, _) =>
        {
            for (int i = 1; i <= 100; i++)
            {
                await host.NotifyExecutionAsync(PluginRpc.ExecSudoEvent,
                    new ExecSudoEventNotification("op", i, new(SudoExecEventKind.Output, Text: i.ToString())));
            }
            return new SudoExecResult(SudoExecOutcome.Exited, 0, 100);
        });
        host.Start();
        plugin.Start();
        SudoExecResult? result = await plugin.RequestAsync<SudoExecResult>(PluginRpc.ExecSudoStream,
            new ExecSudoStreamRequest("s", "c", "op", 0), TimeSpan.FromSeconds(5));
        Assert.IsNotNull(result);
        CollectionAssert.AreEqual(Enumerable.Range(1, 100).ToArray(), received.ToArray());
    }
}
