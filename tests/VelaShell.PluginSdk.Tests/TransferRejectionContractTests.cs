using VelaShell.PluginSdk.RemoteFs;
using VelaShell.PluginSdk.Rpc;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.PluginSdk.Tests;

/// <summary>
/// 传输「启动前被拒」:宿主在登记之前就明确拒绝时,插件必须能把它与「结果未知」「用户拒绝」分开 ——
/// 进程内是同一个类型,隔离进程经错误码还原成同一个类型与原因。
/// </summary>
[TestClass]
public sealed class TransferRejectionContractTests
{
    private static readonly RemoteTransferRejectReason[] KnownReasons =
    [
        RemoteTransferRejectReason.SourceNotFound, RemoteTransferRejectReason.SourceInUse,
        RemoteTransferRejectReason.QuotaExceeded, RemoteTransferRejectReason.InsufficientSpace
    ];

    [TestMethod]
    public void Type_IsAnInvalidOperation_NotAnIoError()
    {
        var rejected = new PluginTransferRejectedException(RemoteTransferRejectReason.SourceInUse, "in use");
        Assert.IsInstanceOfType<InvalidOperationException>(rejected, "额度满此前抛的就是它,旧 catch 照样接得住");
        Assert.IsNotInstanceOfType<IOException>(rejected, "IOException 会被调用方当成「回执可能丢了」去重取");
        Assert.IsNotInstanceOfType<PluginPermissionDeniedException>(rejected);
        Assert.IsTrue(typeof(PluginTransferRejectedException).IsSealed);
        Assert.AreEqual(RemoteTransferRejectReason.SourceInUse, rejected.Reason);
        var inner = new IOException("sharing violation");
        Assert.AreSame(inner, new PluginTransferRejectedException(RemoteTransferRejectReason.SourceInUse, "x", inner).InnerException);
    }

    [TestMethod]
    public void ReasonValuesArePinned()
    {
        Assert.AreEqual(0, (int)RemoteTransferRejectReason.Unknown);
        Assert.AreEqual(1, (int)RemoteTransferRejectReason.SourceNotFound);
        Assert.AreEqual(2, (int)RemoteTransferRejectReason.SourceInUse);
        Assert.AreEqual(3, (int)RemoteTransferRejectReason.QuotaExceeded);
        Assert.AreEqual(4, (int)RemoteTransferRejectReason.InsufficientSpace);
        Assert.HasCount(5, Enum.GetValues<RemoteTransferRejectReason>());
    }

    [TestMethod]
    public void ErrorCodes_RoundTripEveryReason()
    {
        foreach (RemoteTransferRejectReason reason in KnownReasons)
        {
            string code = RpcErrorCodes.FromException(new PluginTransferRejectedException(reason, "no"));
            StringAssert.StartsWith(code, RpcErrorCodes.TransferRejected + ":", reason.ToString());
            Exception restored = RpcErrorCodes.ToException(code, "no");
            Assert.IsInstanceOfType<PluginTransferRejectedException>(restored, code);
            Assert.AreEqual(reason, ((PluginTransferRejectedException)restored).Reason, code);
            Assert.AreEqual("no", restored.Message);
        }
        CollectionAssert.AllItemsAreUnique(KnownReasons
            .Select(r => RpcErrorCodes.FromException(new PluginTransferRejectedException(r, ""))).ToArray());
    }

    [TestMethod]
    [DataRow("transfer-rejected")]
    [DataRow("transfer-rejected:")]
    [DataRow("transfer-rejected:disk-on-fire")]       // 更新的宿主加了原因
    [DataRow("transfer-rejected:SOURCE-IN-USE")]
    public void ErrorCodes_UnknownOrMissingReason_IsStillARejection(string code)
    {
        Exception restored = RpcErrorCodes.ToException(code, "no");
        Assert.IsInstanceOfType<PluginTransferRejectedException>(restored, "原因认不出,「什么都没开始」这一保证照样成立");
        Assert.AreEqual(RemoteTransferRejectReason.Unknown, ((PluginTransferRejectedException)restored).Reason);
        Assert.AreEqual(RpcErrorCodes.TransferRejected,
            RpcErrorCodes.FromException(new PluginTransferRejectedException(RemoteTransferRejectReason.Unknown, "")));
    }

    [TestMethod]
    public void ErrorCodes_DoNotCollapseIntoNeighbouringOutcomes()
    {
        string rejected = RpcErrorCodes.FromException(
            new PluginTransferRejectedException(RemoteTransferRejectReason.QuotaExceeded, "quota"));
        Assert.AreNotEqual(RpcErrorCodes.InvalidOperation, rejected, "子类必须排在 InvalidOperationException 之前");
        Assert.AreEqual(RpcErrorCodes.PermissionDenied, RpcErrorCodes.FromException(new PluginPermissionDeniedException("no")));
        Assert.IsNotInstanceOfType<PluginTransferRejectedException>(RpcErrorCodes.ToException(RpcErrorCodes.InvalidOperation, "x"));
        Assert.IsNotInstanceOfType<PluginTransferRejectedException>(RpcErrorCodes.ToException("transfer-rejectedx", "x"));
        Assert.IsNotInstanceOfType<PluginTransferRejectedException>(RpcErrorCodes.ToException(null, "x"));
    }

    [TestMethod]
    public async Task IsolatedStart_RestoresTheTypeAndReason_DistinctFromDenialAndUncertainty()
    {
        await using RpcTestPipes pipes = await RpcTestPipes.ConnectAsync();
        await using var host = new RpcConnection(pipes.Server);
        await using var plugin = new RpcConnection(pipes.Client);
        host.SetRequestHandler((method, payload, _) => method switch
        {
            PluginRpc.FsTransferStartUpload => throw new PluginTransferRejectedException(
                RemoteTransferRejectReason.SourceInUse, "The source is locked by another process."),
            PluginRpc.FsTransferStartDownload => throw new PluginPermissionDeniedException("The user denied this download."),
            _ => throw new InvalidOperationException(method)
        });
        host.Start();
        plugin.Start();

        PluginTransferRejectedException rejected = await Assert.ThrowsExactlyAsync<PluginTransferRejectedException>(() =>
            plugin.RequestAsync<RemoteTransferSnapshot>(PluginRpc.FsTransferStartUpload,
                new FsTransferStartUploadRequest("s", "C:/data/report.xlsx", "/srv/report.xlsx", new() { OperationId = "op" }),
                TimeSpan.FromSeconds(5)));
        Assert.AreEqual(RemoteTransferRejectReason.SourceInUse, rejected.Reason);

        await Assert.ThrowsExactlyAsync<PluginPermissionDeniedException>(() =>
            plugin.RequestAsync<RemoteTransferSnapshot>(PluginRpc.FsTransferStartDownload,
                new FsTransferStartDownloadRequest("s", "/srv/a", "C:/a", new() { OperationId = "op2" }),
                TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task Fake_PropagatesAScriptedRejectionUnchanged()
    {
        var fake = new FakeRemoteFs
        {
            SupportsTransferOperations = true,
            StartUploadHandler = (_, _, _, _, _) => Task.FromException<RemoteTransferSnapshot>(
                new PluginTransferRejectedException(RemoteTransferRejectReason.QuotaExceeded, "quota"))
        };
        PluginTransferRejectedException ex = await Assert.ThrowsExactlyAsync<PluginTransferRejectedException>(() =>
            fake.StartUploadAsync("s", "local", "/remote", new() { OperationId = "op" }));
        Assert.AreEqual(RemoteTransferRejectReason.QuotaExceeded, ex.Reason);
    }
}
