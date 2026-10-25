using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.PluginSdk;

/// <summary>
/// 宿主在<b>登记任务之前</b>就明确拒绝了这次传输启动(<see cref="IRemoteFsApi.StartUploadAsync" /> /
/// <see cref="IRemoteFsApi.StartDownloadAsync" />):文件被占用、已删除、额度满了、本机空间不够。
/// <para>
/// 抛出它即是宿主的<b>保证</b>:没有登记任务、没有生成 <c>transferId</c>、没有读写任何文件内容,
/// 这次的 <c>OperationId</c> 也没有记进回执账本。所以它与另外两种结局处置完全不同,不该混成一个类型:
/// <list type="bullet">
///   <item><b>结果未知</b>(请求超时、回执丢了、连接断了):任务可能已经登记,只能用<b>同一个</b>
///   <c>OperationId</c> 重取回执,绝不另起一次;</item>
///   <item><b>用户拒绝</b>(<see cref="PluginPermissionDeniedException" />,或快照里审批被拒):用户说了不,
///   重试没有意义;</item>
///   <item><b>启动前被拒</b>(本类型):什么都没发生,也不用对账。条件变了之后(文件关掉了、已有任务结束了、
///   腾出了空间)可以再试,同一个 <c>OperationId</c> 也不会撞上「参数不符」。但<b>不要紧接着自动重试</b> ——
///   条件没变,结果只会一样;把 <see cref="Reason" /> 告诉用户或模型。</item>
/// </list>
/// </para>
/// <para>
/// 刻意派生自 <see cref="InvalidOperationException" />(与额度满此前抛的类型一致,旧的 catch 照样接得住),
/// 而<b>不是</b> <see cref="IOException" />:调用方常把 IOException 当作「回执可能丢了」去重取,
/// 那正是要和它区分开的「结果未知」。
/// </para>
/// <para>
/// 别的启动前拒绝仍是原来的类型,本类型不收:参数不合法(<see cref="ArgumentException" />)、
/// 宿主或平台不支持(<see cref="NotSupportedException" />)、会话不在(<see cref="PluginSessionNotFoundException" />)、
/// 同一 <c>OperationId</c> 参数不符(<see cref="InvalidOperationException" />)、没有授权(<see cref="PluginPermissionDeniedException" />)。
/// </para>
/// <para>隔离进程经错误码 <c>transfer-rejected:&lt;原因&gt;</c> 还原成本类型与原因。可用版本:2.1.3。</para>
/// </summary>
public sealed class PluginTransferRejectedException : InvalidOperationException
{
    /// <summary>用原因与说明构造。</summary>
    public PluginTransferRejectedException(RemoteTransferRejectReason reason, string message)
        : base(message)
        => Reason = reason;

    /// <summary>用原因、说明与内层异常构造(内层异常不跨进程)。</summary>
    public PluginTransferRejectedException(RemoteTransferRejectReason reason, string message, Exception innerException)
        : base(message, innerException)
        => Reason = reason;

    /// <summary>拒绝的原因;更新的宿主给出本 SDK 不认识的原因时为 <see cref="RemoteTransferRejectReason.Unknown" />。</summary>
    public RemoteTransferRejectReason Reason { get; }
}
