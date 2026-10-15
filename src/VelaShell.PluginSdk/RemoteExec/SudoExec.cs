namespace VelaShell.PluginSdk.RemoteExec;

/// <summary>sudo 专用非敏感选项;不提供密码、stdin、环境或来源身份。</summary>
public sealed record SudoExecOptions
{
    /// <summary>执行死线;null 表示不限时。取消仍会关闭宿主认证框及执行通道。</summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>可公开的执行阶段;不包含认证协议内容。</summary>
public enum SudoExecState
{
    /// <summary>等待宿主可信命令批准。</summary>
    AwaitingApproval,
    /// <summary>等待宿主认证交互。</summary>
    AwaitingAuthentication,
    /// <summary>用户命令已启动,认证入口已永久关闭。</summary>
    Running
}

/// <summary>sudo 事件类型。</summary>
public enum SudoExecEventKind
{
    /// <summary>非敏感阶段变化。</summary>
    State,
    /// <summary>PTY 合并文本片段。</summary>
    Output
}

/// <summary>非敏感事件。State 事件只使用 State;Output 事件只使用 Text。</summary>
/// <param name="Kind">事件类型。</param>
/// <param name="State">执行阶段。</param>
/// <param name="Text">合并输出片段,可能包含 CR 进度或不完整行;不包含宿主认证标记。</param>
public sealed record SudoExecEvent(SudoExecEventKind Kind, SudoExecState? State = null, string? Text = null);

/// <summary>sudo 专用终态;非零进程退出不是认证失败。</summary>
public enum SudoExecOutcome
{
    /// <summary>可能已启动但无法确认远端结果;不可自动重跑。零值必须安全失败。</summary>
    Unknown = 0,
    /// <summary>已取得用户命令退出码。</summary>
    Exited,
    /// <summary>用户拒绝命令授权或取消认证,用户命令未启动。</summary>
    AuthenticationCancelled,
    /// <summary>认证失败,用户命令未启动。</summary>
    AuthenticationFailed,
    /// <summary>宿主 UI、PTY 或认证方式不受支持,用户命令未启动。</summary>
    NotSupported
}

/// <summary>sudo 执行结果。仅 Exited 可携带退出码,OutputChunks 只计合并输出事件。</summary>
/// <param name="Outcome">结构化终态。</param>
/// <param name="ExitCode">用户命令退出码,无确认结果时为 null。</param>
/// <param name="OutputChunks">已交付的合并输出片段数。</param>
public sealed record SudoExecResult(SudoExecOutcome Outcome, int? ExitCode = null, long OutputChunks = 0)
{
    /// <summary>只有明确退出且确认退出码为零才成功;缺省/缺失字段绝不表示成功。</summary>
    public bool IsSuccess => Outcome is SudoExecOutcome.Exited && ExitCode is 0;
}
