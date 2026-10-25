using System.Text.Json;
using VelaShell.PluginSdk.RemoteFs;
using VelaShell.PluginSdk.Rpc;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.PluginSdk.Tests;

/// <summary>
/// 条件撤回(<see cref="IRemoteFsApi.WithdrawPendingTransferAsync" />):只在批准还没生效时撤下传输。
/// 插件「停止」时要撤掉还在等确认的传输,而「先取快照、再取消」中间隔着一个往返 —— 宿主恰在这时批准,
/// 刚批准的就被撤了。这里钉住契约面:不支持就明确不支持、绝不退化成无条件取消;结论缺失或认不出时不算撤下;
/// 替身与线协都把代次、请求令牌和结论原样带过去。
/// </summary>
[TestClass]
public sealed class TransferWithdrawContractTests
{
    private static RemoteTransferSnapshot Snapshot(RemoteTransferPhase phase = RemoteTransferPhase.Approval,
        RemoteTransferState state = RemoteTransferState.Starting) => new()
    {
        TransferId = "transfer", OperationId = "start-operation", SessionId = "session",
        LocalPath = "local.bin", RemotePath = "/remote.bin", State = state, Phase = phase,
        Generation = 1, Sequence = 3, TotalBytes = 8L * 1024 * 1024 * 1024
    };

    private static RemoteTransferWithdrawResult Withdrawn() => new()
    {
        Outcome = RemoteTransferWithdrawOutcome.Withdrawn,
        Snapshot = Snapshot(state: RemoteTransferState.CancelRequested) with { CancellationRequested = true }
    };

    /// <summary>只实现必需成员的旧宿主;顺带数一数有没有人改走了无条件取消。</summary>
    private sealed class LegacyFs : IRemoteFsApi
    {
        public int CancelCalls { get; private set; }
        public bool SupportsTransferOperations => true;
        public bool SupportsDownloadOperations => true;
        public Task<RemoteTransferSnapshot> CancelTransferAsync(string transferId, long expectedGeneration, CancellationToken cancellationToken = default)
        {
            CancelCalls++;
            return Task.FromResult(Snapshot() with { CancellationRequested = true });
        }
        public Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string sessionId, string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RemoteFileEntry?> StatAsync(string sessionId, string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(string sessionId, string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> GetWorkingDirectoryAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DownloadFileAsync(string sessionId, string remotePath, string localPath, IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UploadFileAsync(string sessionId, string localPath, string remotePath, IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> ReadAllBytesAsync(string sessionId, string remotePath, int maxBytes = 16 * 1024 * 1024, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task WriteAllBytesAsync(string sessionId, string remotePath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EnsureDirectoryAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RenameAsync(string sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [TestMethod]
    public void LegacyHost_ExplicitlyUnsupported_NeverFallsBackToUnconditionalCancel()
    {
        var legacy = new LegacyFs();
        IRemoteFsApi fs = legacy;
        Assert.IsTrue(fs.SupportsTransferOperations, "实现了独立任务的宿主不一定实现条件撤回");
        Assert.IsFalse(fs.SupportsTransferWithdraw);
        Assert.ThrowsExactly<NotSupportedException>(() => fs.WithdrawPendingTransferAsync("transfer", 1));
        Assert.AreEqual(0, legacy.CancelCalls, "退化成无条件取消正是要合上的那个空当");
    }

    [TestMethod]
    public void OutcomeValuesArePinned()
    {
        Assert.AreEqual(0, (int)RemoteTransferWithdrawOutcome.Unknown);
        Assert.AreEqual(1, (int)RemoteTransferWithdrawOutcome.Withdrawn);
        Assert.AreEqual(2, (int)RemoteTransferWithdrawOutcome.PastApproval);
        Assert.AreEqual(3, (int)RemoteTransferWithdrawOutcome.AlreadyEnded);
        Assert.HasCount(4, Enum.GetValues<RemoteTransferWithdrawOutcome>());
    }

    [TestMethod]
    public void Result_OnlyWithdrawnCounts_MissingOrUnrecognizedOutcomeIsNot()
    {
        string snapshot = JsonSerializer.Serialize(Snapshot());
        RemoteTransferWithdrawResult missing = JsonSerializer.Deserialize<RemoteTransferWithdrawResult>(
            $$"""{"Snapshot":{{snapshot}}}""")!;
        Assert.AreEqual(RemoteTransferWithdrawOutcome.Unknown, missing.Outcome);
        Assert.IsFalse(missing.IsWithdrawn, "缺结论不能读成「撤下了」");
        Assert.AreEqual(Snapshot(), missing.Snapshot);

        RemoteTransferWithdrawResult newer = JsonSerializer.Deserialize<RemoteTransferWithdrawResult>(
            $$"""{"Outcome":9,"Snapshot":{{snapshot}}}""")!;
        Assert.IsFalse(newer.IsWithdrawn, "更新的宿主加的结论,本 SDK 认不出就不算撤下");

        Assert.IsTrue(Withdrawn().IsWithdrawn);
        Assert.IsFalse((Withdrawn() with { Outcome = RemoteTransferWithdrawOutcome.PastApproval }).IsWithdrawn);
        Assert.IsFalse((Withdrawn() with { Outcome = RemoteTransferWithdrawOutcome.AlreadyEnded }).IsWithdrawn,
            "什么都没传,但不是撤下的");
    }

    [TestMethod]
    public async Task Fake_RequiresItsOwnCapabilityAndHandler_AndNeverRoutesToCancel()
    {
        var fake = new FakeRemoteFs
        {
            CancelTransferHandler = (_, _, _) => throw new AssertFailedException("条件撤回不能改走无条件取消"),
            WithdrawTransferHandler = (_, _, _) => Task.FromResult(Withdrawn())
        };
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => fake.WithdrawPendingTransferAsync("transfer", 1));

        fake.SupportsTransferOperations = true;
        fake.SupportsDownloadOperations = true;
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => fake.WithdrawPendingTransferAsync("transfer", 1),
            "上传 / 下载能力不代表实现了条件撤回");

        fake.SupportsTransferWithdraw = true;
        fake.WithdrawTransferHandler = null;
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => fake.WithdrawPendingTransferAsync("transfer", 1));

        Assert.HasCount(3, fake.TransfersWithdrawn);
        Assert.IsEmpty(fake.TransfersCancelled);
    }

    [TestMethod]
    public async Task Fake_ForwardsIdGenerationAndRequestToken_AndReturnsTheScriptedResult()
    {
        using var cts = new CancellationTokenSource();
        RemoteTransferWithdrawResult expected = new()
        {
            Outcome = RemoteTransferWithdrawOutcome.PastApproval,
            Snapshot = Snapshot(RemoteTransferPhase.Transfer, RemoteTransferState.Running)
        };
        var fake = new FakeRemoteFs
        {
            SupportsTransferWithdraw = true,
            WithdrawTransferHandler = (id, generation, token) =>
            {
                Assert.AreEqual("transfer", id);
                Assert.AreEqual(2L, generation);
                Assert.AreEqual(cts.Token, token);
                return Task.FromResult(expected);
            }
        };

        RemoteTransferWithdrawResult result = await fake.WithdrawPendingTransferAsync("transfer", 2, cts.Token);

        Assert.AreSame(expected, result);
        Assert.IsFalse(result.IsWithdrawn);
        Assert.IsFalse(result.Snapshot.CancellationRequested, "批准已生效:任务照常进行,什么都没改");
        Assert.HasCount(1, fake.TransfersWithdrawn);
        Assert.AreEqual(("transfer", 2L), fake.TransfersWithdrawn[0]);
        Assert.IsEmpty(fake.TransfersCancelled);
    }

    [TestMethod]
    public async Task Fake_CancelledOrInvalidRequests_AreNotDispatched()
    {
        int dispatched = 0;
        var fake = new FakeRemoteFs
        {
            SupportsTransferWithdraw = true,
            WithdrawTransferHandler = (_, _, _) => { dispatched++; return Task.FromResult(Withdrawn()); }
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => fake.WithdrawPendingTransferAsync("transfer", 1, cts.Token));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fake.WithdrawPendingTransferAsync("transfer", 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fake.WithdrawPendingTransferAsync("transfer", -1));
        Assert.AreEqual(0, dispatched);
        Assert.IsEmpty(fake.TransfersWithdrawn);
    }

    [TestMethod]
    public async Task Fake_PropagatesScriptedHostRefusalsUnchanged()
    {
        var fake = new FakeRemoteFs { SupportsTransferWithdraw = true };
        fake.WithdrawTransferHandler = (_, generation, _) => generation == 1
            ? Task.FromException<RemoteTransferWithdrawResult>(new InvalidOperationException("Transfer generation does not match."))
            : Task.FromException<RemoteTransferWithdrawResult>(new FileNotFoundException("Transfer not found."));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fake.WithdrawPendingTransferAsync("transfer", 1),
            "恢复后的新代次要重新批准,旧代次的撤回碰不到它");
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => fake.WithdrawPendingTransferAsync("someone-else", 2));
    }

    [TestMethod]
    public void RpcDtos_RoundTrip_AndHandshakeStaysBinaryCompatible()
    {
        RoundTrip(new FsTransferWithdrawRequest("transfer", long.MaxValue));
        RoundTrip(Withdrawn());
        RoundTrip(new RemoteTransferWithdrawResult
        {
            Outcome = RemoteTransferWithdrawOutcome.AlreadyEnded,
            Snapshot = Snapshot(state: RemoteTransferState.Failed) with { ErrorCode = "permission-denied" }
        });

        HandshakeResponse legacy = JsonSerializer.Deserialize<HandshakeResponse>(
            """{"ApiLevel":2,"HostVersion":"1","Locale":"en","Theme":"dark","SupportsTransferOperations":true,"SupportsDownloadOperations":true}""")!;
        Assert.IsTrue(legacy.SupportsTransferOperations);
        Assert.IsFalse(legacy.SupportsTransferWithdraw, "旧宿主缺这个字段:不支持,不能按支持独立任务推断");
        RoundTrip(legacy with { SupportsTransferWithdraw = true });
        Assert.HasCount(6, typeof(HandshakeResponse).GetMethod("Deconstruct")!.GetParameters());

        string[] methods = [PluginRpc.FsTransferStartUpload, PluginRpc.FsTransferStartDownload, PluginRpc.FsTransferGet,
            PluginRpc.FsTransferWait, PluginRpc.FsTransferCancel, PluginRpc.FsTransferResume, PluginRpc.FsTransferProgress,
            PluginRpc.FsTransferWithdraw];
        Assert.HasCount(8, methods.Distinct(StringComparer.Ordinal));
        Assert.IsTrue(methods.All(m => m.StartsWith("fs/transfer/", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task IsolatedWithdraw_CarriesOutcomeAndSnapshot_AndRestoresAGenerationMismatch()
    {
        await using RpcTestPipes pipes = await RpcTestPipes.ConnectAsync();
        await using var host = new RpcConnection(pipes.Server);
        await using var plugin = new RpcConnection(pipes.Client);
        FsTransferWithdrawRequest? received = null;
        host.SetRequestHandler((method, payload, _) =>
        {
            Assert.AreEqual(PluginRpc.FsTransferWithdraw, method);
            received = payload!.Value.Deserialize<FsTransferWithdrawRequest>()!;
            return received.ExpectedGeneration == 1
                ? Task.FromResult<object?>(Withdrawn())
                : throw new InvalidOperationException("Transfer generation does not match.");
        });
        host.Start();
        plugin.Start();

        RemoteTransferWithdrawResult? result = await plugin.RequestAsync<RemoteTransferWithdrawResult>(
            PluginRpc.FsTransferWithdraw, new FsTransferWithdrawRequest("transfer", 1), TimeSpan.FromSeconds(5));

        Assert.AreEqual(new FsTransferWithdrawRequest("transfer", 1), received);
        Assert.IsNotNull(result);
        Assert.IsTrue(result.IsWithdrawn);
        Assert.AreEqual(Withdrawn(), result);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            plugin.RequestAsync<RemoteTransferWithdrawResult>(PluginRpc.FsTransferWithdraw,
                new FsTransferWithdrawRequest("transfer", 2), TimeSpan.FromSeconds(5)));
    }

    private static void RoundTrip<T>(T value) => Assert.AreEqual(value, JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value)));
}
