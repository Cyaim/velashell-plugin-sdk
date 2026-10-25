namespace VelaShell.PluginSdk.Ui;

/// <summary>
/// 消息的轻重,决定消息中心里的强调程度。与宿主消息中心的三档一一对应。
/// 数值显式给出并且<b>只增不改</b>(线上按数值传)。
/// </summary>
public enum PluginNotificationLevel
{
    /// <summary>一般信息(缺省)。</summary>
    Info = 0,

    /// <summary>需要留意。</summary>
    Warning = 1,

    /// <summary>需要尽快处理(列表里换警示色)。不要拿它表示「出错了」的每一次 —— 那是日志的事。</summary>
    Critical = 2
}

/// <summary><see cref="IUiApi.PostNotificationAsync" /> 的结果。数值显式给出并且<b>只增不改</b>。</summary>
public enum PluginNotificationOutcome
{
    /// <summary>宿主没有给出结果(不应出现)。按「没有进列表」处理,不要据此重发。</summary>
    Unknown = 0,

    /// <summary>已作为一条新消息进入消息中心。</summary>
    Posted = 1,

    /// <summary>
    /// 消息中心里已经有同 <see cref="PluginNotification.Id" /> 的消息:<b>不新增、不改内容、不把已读变回未读</b>。
    /// 每次启动都重新投递同一条时,靠的就是这一条。想换内容就换一个 id。
    /// </summary>
    AlreadyPosted = 2,

    /// <summary>
    /// 用户关掉了这个插件的通知(或宿主策略不收):没有进列表。<b>不要换个 id 重发</b>,
    /// 也不要改走别的打扰渠道去补 —— 用户已经说了不想看。
    /// </summary>
    Muted = 3,

    /// <summary>超出频率限制,没有进列表。不要立即重试;合并成一条摘要、晚些再发。</summary>
    RateLimited = 4
}

/// <summary>
/// 插件发往宿主消息中心(侧边栏铃铛)的一条消息。
/// <para>
/// ⚠️ <b>消息中心只收「要留存、可回看」的东西。</b>判断标准:用户明天打开 VelaShell,这条还值得读吗?
/// 值得的才发 —— 夜间备份失败了、证书 7 天后过期、后台任务跑完需要你确认结果。
/// <b>不要</b>用它发:进度、运行时状态(连上了 / 断开了)、每次操作的回执(「已复制」「已保存」)、调试信息、
/// 重复的同类告警。那些要么属于插件自己的面板与状态,要么属于日志(<see cref="Logging.IPluginLogger" />);
/// 塞进消息中心只会把真正要读的东西淹掉,宿主也会限频(见 <see cref="IUiApi.PostNotificationAsync" />)。
/// </para>
/// <para>
/// 刻意<b>没有</b>网址字段:插件不能把用户从消息中心直接导到外部链接。要打开网页、跳到某个界面,
/// 注册一条自己的命令(<see cref="ActionCommandId" />)在命令里做。
/// </para>
/// <para>可用版本:TBD(发版时替换)。</para>
/// </summary>
public sealed record PluginNotification
{
    /// <summary><see cref="Id" /> 的长度上限。超长拒绝而不截断:截断会让两条不同的消息撞成同一个键。</summary>
    public const int MaxIdLength = 128;

    /// <summary><see cref="Title" /> 的显示上限(字符);超出由宿主截断,不报错。</summary>
    public const int MaxTitleLength = 200;

    /// <summary><see cref="Body" /> 的显示上限(字符);超出由宿主截断,不报错。</summary>
    public const int MaxBodyLength = 1000;

    /// <summary>
    /// 本插件内稳定的去重键(如 <c>backup-failed:2026-10-24</c>、<c>cert-expiry:prod-web</c>)。
    /// 宿主自己加上插件 id 前缀再入库,所以只需在本插件内唯一;同 id 重投得到
    /// <see cref="PluginNotificationOutcome.AlreadyPosted" />。用确定性字符串,不要每次随机生成 ——
    /// 随机 id 等于放弃去重,每次启动都会多出一条。
    /// </summary>
    public required string Id { get; init; }

    /// <summary>标题:一行纯文本,列表里加粗显示。</summary>
    public required string Title { get; init; }

    /// <summary>正文:可选的纯文本,列表里最多显示两行。不解析 Markdown / HTML / 链接。</summary>
    public string? Body { get; init; }

    /// <summary>轻重,缺省 <see cref="PluginNotificationLevel.Info" />。</summary>
    public PluginNotificationLevel Level { get; init; } = PluginNotificationLevel.Info;

    /// <summary>
    /// 可选动作:用户点这条消息的动作按钮时执行的<b>本插件</b>命令 id(必须以 <c>&lt;pluginId&gt;.</c> 为前缀,
    /// 与 <see cref="Commands.PluginCommandDescriptor.Id" /> 同一条规则)。宿主拒绝别人的命令与宿主命令 ——
    /// 否则一条插件消息就能让用户「点一下」去执行任何东西。
    /// <para>
    /// 消息会跨重启留存,点击时插件可能还没激活:把这条命令写进清单的 <c>contributes.commands</c>,
    /// 宿主才能按需惰性激活后执行;只在运行期注册的命令,插件不在时点击会提示不可用。
    /// </para>
    /// </summary>
    public string? ActionCommandId { get; init; }

    /// <summary>动作按钮文案(如「查看详情」);没给时宿主用通用文案兜底。没有 <see cref="ActionCommandId" /> 时不能给。</summary>
    public string? ActionLabel { get; init; }

    /// <summary>
    /// 宿主、PluginHost 与测试替身共用的校验。<paramref name="pluginId" /> 是<b>宿主确认的</b>发送方插件 id
    /// (不来自消息本身,插件冒充不了别人)。
    /// </summary>
    /// <param name="pluginId">发送方插件 id;用来核对 <see cref="ActionCommandId" /> 的前缀。</param>
    /// <exception cref="ArgumentException">id / 标题为空,id 超长或含控制字符,动作命令不属于本插件,或只有动作文案没有动作。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="Level" /> 不是已定义的值。</exception>
    public void Validate(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > MaxIdLength || Id.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"Notification id must be 1–{MaxIdLength} characters without control characters.", nameof(Id));
        }
        if (string.IsNullOrWhiteSpace(Title))
        {
            throw new ArgumentException("Notification title must not be empty.", nameof(Title));
        }
        if (!Enum.IsDefined(Level))
        {
            throw new ArgumentOutOfRangeException(nameof(Level), Level, "Unknown notification level.");
        }
        if (ActionCommandId is { } command)
        {
            string prefix = pluginId + ".";
            if (command.Length <= prefix.Length || !command.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Notification action '{command}' must be one of this plugin's commands ('{prefix}…').", nameof(ActionCommandId));
            }
        }
        else if (ActionLabel is not null)
        {
            throw new ArgumentException("A notification action label needs an action command.", nameof(ActionLabel));
        }
    }
}
