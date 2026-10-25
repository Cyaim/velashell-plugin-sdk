using System.Text.Json;
using VelaShell.PluginSdk.Rpc;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.PluginSdk.Tests;

/// <summary>
/// 插件往消息中心发通知:旧宿主如实不支持、身份不由消息自报、动作只能是自己的命令、
/// 同 id 去重先于限频。消息中心只收「要留存、可回看」的东西,这几条是守住它的机械保障。
/// </summary>
[TestClass]
public sealed class PluginNotificationContractTests
{
    private const string PluginId = "acme.backup";

    private static PluginNotification Notice(string id = "backup-failed:2026-10-24") => new()
    {
        Id = id,
        Title = "夜间备份失败",
        Body = "prod-db 的备份在 03:12 失败:磁盘已满。",
        Level = PluginNotificationLevel.Warning,
        ActionCommandId = PluginId + ".showBackupLog",
        ActionLabel = "查看日志"
    };

    private sealed class LegacyUi : IUiApi
    {
        public Task<IPluginPanel> ShowPanelAsync(PanelOptions options, Func<object> contentFactory,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [TestMethod]
    public void LegacyHost_ReportsNoSupport_AndThrowsInsteadOfPretending()
    {
        IUiApi legacy = new LegacyUi();
        Assert.IsFalse(legacy.SupportsNotifications);
        Assert.ThrowsExactly<NotSupportedException>(() => legacy.PostNotificationAsync(Notice()));
    }

    [TestMethod]
    public void Validate_AcceptsAWellFormedNotification_AndBareMinimum()
    {
        Notice().Validate(PluginId);
        new PluginNotification { Id = "x", Title = "t" }.Validate(PluginId);
        (Notice() with { ActionLabel = null }).Validate(PluginId);
        (Notice() with { Id = new string('a', PluginNotification.MaxIdLength) }).Validate(PluginId);
        // 标题 / 正文超长由宿主截断,不是错误。
        (Notice() with { Title = new string('t', 5000), Body = new string('b', 50000) }).Validate(PluginId);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("line\nbreak")]
    [DataRow("tab\tinside")]
    public void Validate_RejectsUnusableIds(string id)
        => Assert.ThrowsExactly<ArgumentException>(() => (Notice() with { Id = id }).Validate(PluginId));

    [TestMethod]
    public void Validate_RejectsOverlongIdInsteadOfTruncating()
        => Assert.ThrowsExactly<ArgumentException>(() =>
            (Notice() with { Id = new string('a', PluginNotification.MaxIdLength + 1) }).Validate(PluginId));

    [TestMethod]
    public void Validate_RejectsEmptyTitle_AndUnknownLevel()
    {
        Assert.ThrowsExactly<ArgumentException>(() => (Notice() with { Title = " " }).Validate(PluginId));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            (Notice() with { Level = (PluginNotificationLevel)42 }).Validate(PluginId));
    }

    [TestMethod]
    [DataRow("settings.open")]                  // 宿主命令
    [DataRow("acme.other.run")]                 // 别的插件
    [DataRow("acme.backup2.showBackupLog")]     // 前缀长得像
    [DataRow("acme.backup.")]                   // 只有前缀
    [DataRow("ACME.BACKUP.showBackupLog")]      // 序数比较,不认大小写变体
    [DataRow(" ")]
    public void Validate_RejectsActionsThatAreNotThisPluginsCommands(string command)
        => Assert.ThrowsExactly<ArgumentException>(() =>
            (Notice() with { ActionCommandId = command }).Validate(PluginId));

    [TestMethod]
    public void Validate_RejectsLabelWithoutAction_AndMissingSender()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            (Notice() with { ActionCommandId = null }).Validate(PluginId));
        Assert.ThrowsExactly<ArgumentException>(() => Notice().Validate(""));
    }

    [TestMethod]
    public void Notification_CarriesNoSenderIdentity_OrExternalLink()
    {
        CollectionAssert.AreEquivalent(
            new[] { "Id", "Title", "Body", "Level", "ActionCommandId", "ActionLabel" },
            typeof(PluginNotification).GetProperties().Select(p => p.Name).ToArray(),
            "发送方由宿主绑定;刻意没有网址字段 —— 新增字段前先想清楚它会不会打开这两扇门");
        CollectionAssert.AreEquivalent(new[] { "Notification" },
            typeof(UiNotifyRequest).GetProperties().Select(p => p.Name).ToArray());
    }

    [TestMethod]
    public void EnumValuesArePinned_AndMissingFieldsAreConservative()
    {
        Assert.AreEqual(0, (int)PluginNotificationLevel.Info);
        Assert.AreEqual(1, (int)PluginNotificationLevel.Warning);
        Assert.AreEqual(2, (int)PluginNotificationLevel.Critical);
        Assert.HasCount(3, Enum.GetValues<PluginNotificationLevel>());
        Assert.AreEqual(0, (int)PluginNotificationOutcome.Unknown);
        Assert.AreEqual(1, (int)PluginNotificationOutcome.Posted);
        Assert.AreEqual(2, (int)PluginNotificationOutcome.AlreadyPosted);
        Assert.AreEqual(3, (int)PluginNotificationOutcome.Muted);
        Assert.AreEqual(4, (int)PluginNotificationOutcome.RateLimited);
        Assert.HasCount(5, Enum.GetValues<PluginNotificationOutcome>());

        PluginNotification minimal = JsonSerializer.Deserialize<PluginNotification>("""{"Id":"x","Title":"t"}""")!;
        Assert.AreEqual(PluginNotificationLevel.Info, minimal.Level);
        Assert.IsNull(minimal.ActionCommandId);
        UiNotifyResponse silent = JsonSerializer.Deserialize<UiNotifyResponse>("{}")!;
        Assert.AreEqual(PluginNotificationOutcome.Unknown, silent.Outcome, "缺结果不能读成「已发出」");
    }

    [TestMethod]
    public void RpcDtos_RoundTrip_AndHandshakeStaysBinaryCompatible()
    {
        var request = new UiNotifyRequest(Notice());
        Assert.AreEqual(request, JsonSerializer.Deserialize<UiNotifyRequest>(JsonSerializer.Serialize(request)));
        var response = new UiNotifyResponse(PluginNotificationOutcome.RateLimited);
        Assert.AreEqual(response, JsonSerializer.Deserialize<UiNotifyResponse>(JsonSerializer.Serialize(response)));

        HandshakeResponse legacy = JsonSerializer.Deserialize<HandshakeResponse>(
            """{"ApiLevel":2,"HostVersion":"1","Locale":"en","Theme":"dark"}""")!;
        Assert.IsFalse(legacy.SupportsNotifications, "旧宿主不宣告就是不支持");
        HandshakeResponse modern = legacy with { SupportsNotifications = true };
        Assert.AreEqual(modern, JsonSerializer.Deserialize<HandshakeResponse>(JsonSerializer.Serialize(modern)));
        Assert.HasCount(6, typeof(HandshakeResponse).GetMethod("Deconstruct")!.GetParameters());
        string[] methods = [.. typeof(PluginRpc).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!)];
        Assert.HasCount(methods.Length, methods.Distinct(StringComparer.Ordinal), "方法名撞了,隔离进程会把请求派错");
        CollectionAssert.Contains(methods, "ui/notify");
    }

    [TestMethod]
    public async Task IsolatedRequest_CarriesTheNotificationAndOutcome()
    {
        await using RpcTestPipes pipes = await RpcTestPipes.ConnectAsync();
        await using var host = new RpcConnection(pipes.Server);
        await using var plugin = new RpcConnection(pipes.Client);
        PluginNotification? received = null;
        host.SetRequestHandler((method, payload, _) =>
        {
            Assert.AreEqual(PluginRpc.UiNotify, method);
            received = payload!.Value.Deserialize<UiNotifyRequest>()!.Notification;
            received.Validate(PluginId);   // 宿主用连接绑定的身份校验,不看消息自报
            return Task.FromResult<object?>(new UiNotifyResponse(PluginNotificationOutcome.Posted));
        });
        host.Start();
        plugin.Start();

        UiNotifyResponse? response = await plugin.RequestAsync<UiNotifyResponse>(PluginRpc.UiNotify,
            new UiNotifyRequest(Notice()), TimeSpan.FromSeconds(5));

        Assert.AreEqual(PluginNotificationOutcome.Posted, response?.Outcome);
        Assert.AreEqual(Notice(), received);
    }

    [TestMethod]
    public async Task Fake_DeduplicatesById_BeforeApplyingTheRateLimitScript()
    {
        using var context = new TestPluginContext { PluginId = PluginId };
        FakeUi ui = context.FakeUi;
        int scripted = 0;
        ui.NotificationOutcome = _ => ++scripted > 1 ? PluginNotificationOutcome.RateLimited : null;

        Assert.IsTrue(context.Ui.SupportsNotifications);
        Assert.AreEqual(PluginNotificationOutcome.Posted, await context.Ui.PostNotificationAsync(Notice("a")));
        Assert.AreEqual(PluginNotificationOutcome.AlreadyPosted, await context.Ui.PostNotificationAsync(Notice("a")),
            "同 id 重投不计入频率,也不会被判成限流");
        Assert.AreEqual(PluginNotificationOutcome.RateLimited, await context.Ui.PostNotificationAsync(Notice("b")));
        Assert.AreEqual(PluginNotificationOutcome.RateLimited, await context.Ui.PostNotificationAsync(Notice("b")),
            "被限流的那条没进列表,再发仍要过限频");

        Assert.HasCount(4, ui.NotificationRequests);
        Assert.HasCount(1, ui.PostedNotifications);
        Assert.AreEqual("a", ui.PostedNotifications[0].Id);
        Assert.AreEqual(3, scripted, "四次投递只问了三次脚本:去重命中的那次不该问");
    }

    [TestMethod]
    public async Task Fake_ValidatesWithTheContextsPluginId_AndRecordsNothingWhenRejected()
    {
        using var context = new TestPluginContext { PluginId = PluginId };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => context.Ui.PostNotificationAsync(
            Notice() with { ActionCommandId = "test.plugin.run" }));
        Assert.IsEmpty(context.FakeUi.NotificationRequests);

        context.FakeUi.SupportsNotifications = false;
        Assert.ThrowsExactly<NotSupportedException>(() => context.Ui.PostNotificationAsync(Notice()));
        Assert.IsEmpty(context.FakeUi.NotificationRequests);

        context.FakeUi.SupportsNotifications = true;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => context.Ui.PostNotificationAsync(Notice(), cancelled.Token));
        Assert.IsEmpty(context.FakeUi.PostedNotifications);
    }

    [TestMethod]
    public async Task Fake_Muted_DoesNotEnterTheList()
    {
        var ui = new FakeUi { PluginId = PluginId, NotificationOutcome = _ => PluginNotificationOutcome.Muted };
        Assert.AreEqual(PluginNotificationOutcome.Muted, await ui.PostNotificationAsync(Notice()));
        Assert.IsEmpty(ui.PostedNotifications);
        ui.NotificationOutcome = null;
        Assert.AreEqual(PluginNotificationOutcome.Posted, await ui.PostNotificationAsync(Notice()),
            "静音时没有入列,解除后同 id 是一条新消息");
    }
}
