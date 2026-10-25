using System.Reflection;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.PluginSdk.Tests;

/// <summary>
/// <see cref="RecordingProtocols" /> / <see cref="RecordingWorkspaces" /> 的注册句柄语义。
/// </summary>
/// <remarks>
/// 宿主的 <c>PluginProtocolRegistry</c> 按**注册条目**判定一个句柄还是不是自己那一份:
/// 同 id 被后来者替换过,旧句柄 Dispose 就是空操作 —— 哪怕后来者用的是**同一个实现实例**。
/// 后一种正是插件换语言时的写法(同一个实例带着新文案的描述重注册,再释放旧句柄),
/// Telnet / 串口 / S3 都这么写。替身若按实现实例判定,单测里新注册会被撤掉,而真机上它还在。
/// </remarks>
[TestClass]
[TestCategory("Plugins")]
public sealed class RecordingRegistrationTests
{
    private const string Id = "test.plugin";

    private static ProtocolDescriptor Protocol(string displayName = "Echo") =>
        new() { Id = Id, DisplayName = displayName, DefaultPort = 7 };

    private static WorkspaceDescriptor Workspace(string displayName = "Echo") =>
        new() { Id = Id, DisplayName = displayName, DefaultPort = 7 };

    [TestMethod]
    public void Protocols_ReRegisteringTheSameFileSystem_OldHandleDoesNotRemoveTheNewOne()
    {
        var protocols = new RecordingProtocols();
        IProtocolFileSystem fileSystem = Unused.Of<IProtocolFileSystem>();

        IDisposable first = protocols.Register(Protocol("Echo"), fileSystem);
        IDisposable second = protocols.Register(Protocol("Écho"), fileSystem);
        first.Dispose();

        Assert.AreSame(fileSystem, protocols.GetFileSystem(Id), "旧句柄不该撤掉同一个实例的新注册。");
        Assert.AreEqual("Écho", protocols.GetDescriptor(Id)?.DisplayName, "留下的是后来那份描述。");

        second.Dispose();
        Assert.IsNull(protocols.GetDescriptor(Id), "新句柄照常撤销。");
    }

    [TestMethod]
    public void Protocols_ReRegisteringTheSameTerminal_OldHandleDoesNotRemoveTheNewOne()
    {
        var protocols = new RecordingProtocols();
        var terminal = new NullTerminal();

        IDisposable first = protocols.Register(Protocol("Echo"), terminal);
        IDisposable second = protocols.Register(Protocol("Écho"), terminal);
        first.Dispose();

        Assert.AreSame(terminal, protocols.GetTerminal(Id));
        Assert.HasCount(1, protocols.Registered);

        second.Dispose();
        Assert.IsEmpty(protocols.Registered);
    }

    /// <summary>同一个实例、连描述都一字不差地重注册:两条注册值相等,但仍是两条。</summary>
    [TestMethod]
    public void Protocols_IdenticalReRegistration_IsStillASeparateRegistration()
    {
        var protocols = new RecordingProtocols();
        var terminal = new NullTerminal();
        ProtocolDescriptor descriptor = Protocol();

        IDisposable first = protocols.Register(descriptor, terminal);
        IDisposable second = protocols.Register(descriptor, terminal);
        first.Dispose();

        Assert.AreSame(terminal, protocols.GetTerminal(Id));

        second.Dispose();
        Assert.IsNull(protocols.GetTerminal(Id));
    }

    [TestMethod]
    public void Protocols_ReplacedByAnotherImplementation_OldHandleIsANoOp()
    {
        var protocols = new RecordingProtocols();
        var before = new NullTerminal();
        var after = new NullTerminal();

        IDisposable first = protocols.Register(Protocol(), before);
        protocols.Register(Protocol(), after);
        first.Dispose();

        Assert.AreSame(after, protocols.GetTerminal(Id));
    }

    [TestMethod]
    public void Workspaces_ReRegisteringTheSameProvider_OldHandleDoesNotRemoveTheNewOne()
    {
        var workspaces = new RecordingWorkspaces();
        var provider = new NullProvider();

        IDisposable first = workspaces.Register(Workspace("Echo"), provider);
        IDisposable second = workspaces.Register(Workspace("Écho"), provider);
        first.Dispose();

        Assert.AreSame(provider, workspaces.GetProvider(Id), "旧句柄不该撤掉同一个实例的新注册。");
        Assert.AreEqual("Écho", workspaces.GetDescriptor(Id)?.DisplayName);

        second.Dispose();
        Assert.IsNull(workspaces.GetProvider(Id), "新句柄照常撤销。");
    }

    /// <summary>值元组 / record 是值相等:同一个提供方带同一份描述重注册,也得是两条注册。</summary>
    [TestMethod]
    public void Workspaces_IdenticalReRegistration_IsStillASeparateRegistration()
    {
        var workspaces = new RecordingWorkspaces();
        var provider = new NullProvider();
        WorkspaceDescriptor descriptor = Workspace();

        IDisposable first = workspaces.Register(descriptor, provider);
        IDisposable second = workspaces.Register(descriptor, provider);
        first.Dispose();

        Assert.HasCount(1, workspaces.Registered);

        second.Dispose();
        Assert.IsEmpty(workspaces.Registered);
    }

    [TestMethod]
    public void Workspaces_ReplacedByAnotherProvider_OldHandleIsANoOp()
    {
        var workspaces = new RecordingWorkspaces();
        var before = new NullProvider();
        var after = new NullProvider();

        IDisposable first = workspaces.Register(Workspace(), before);
        workspaces.Register(Workspace(), after);
        first.Dispose();

        Assert.AreSame(after, workspaces.GetProvider(Id));
    }

    private sealed class NullTerminal : IProtocolTerminal
    {
        public Task<IProtocolTerminalSession> ConnectAsync(
            ProtocolConnectRequest request,
            ProtocolTerminalOptions options,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("注册语义只看实例身份,不连接。");
    }

    private sealed class NullProvider : IWorkspaceProvider
    {
        public Task<IWorkspaceDocument> OpenAsync(WorkspaceConnectRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("注册语义只看实例身份,不打开。");
    }

    /// <summary>成员很多、而这里只需要一个实例身份的接口,用一个什么都不做的代理顶上。</summary>
    internal class Unused : DispatchProxy
    {
        public static T Of<T>() where T : class => DispatchProxy.Create<T, Unused>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException($"{targetMethod?.Name} 不在这组用例的范围内。");
    }
}
