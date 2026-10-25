namespace VelaShell.PluginSdk.Ui;

/// <summary>面板的呈现方式,由插件在打开时选择。</summary>
public enum PanelDisplayMode
{
    /// <summary>
    /// 停靠文档:出现在主窗口标签区,用户可拖拽到任意分栏位置。
    /// 仅进程内插件可用;隔离进程插件请求该模式时自动回退为 <see cref="Window" />
    /// (原生控件无法跨进程嵌入,回退会记一条警告日志)。
    /// </summary>
    Document,

    /// <summary>独立窗口:进程内为宿主同款自绘卡片窗口;隔离进程为插件进程自己的窗口。</summary>
    Window
}

/// <summary>
/// 停靠文档的初始落位。等价于"打开后立刻拖到某一侧" —— 用的就是拖放停靠那条路径,
/// 因此落位后的一切(拖回去、拆分、关闭)与用户手动拖出来的分栏完全一致。
/// 仅 <see cref="PanelDisplayMode.Document" /> 有意义;窗口模式忽略。
/// </summary>
public enum PanelPlacement
{
    /// <summary>并入当前标签组,成为一个新标签(默认)。</summary>
    Tabs,

    /// <summary>在标签区最右侧拆出一栏(VSCode 里 Copilot 聊天面板的位置)。</summary>
    Right,

    /// <summary>在标签区最左侧拆出一栏。</summary>
    Left,

    /// <summary>在标签区底部拆出一栏。</summary>
    Bottom,

    /// <summary>在标签区顶部拆出一栏。</summary>
    Top
}

/// <summary>打开面板的选项。</summary>
public sealed record PanelOptions
{
    /// <summary>面板标题(标签页文字 / 窗口标题)。</summary>
    public required string Title { get; init; }

    /// <summary>
    /// 标签页上的图标;留空则宿主画一个通用的插件图标。
    /// 形态与取舍见 <see cref="PluginIcon" />(标签页图标三处共用同一个类型)。
    /// <para>
    /// 与协议 / 工作台那两处的区别只在**填在哪**:那两个是注册期一个协议一份,
    /// 这个是每次开面板一份 —— 同一个插件开的聊天页与设置页可以各画各的。
    /// </para>
    /// <para>
    /// <b>窗口模式同样吃它</b>:画在自绘标题栏最左边(进程内与隔离进程都是)。
    /// 几个插件的设置窗口并排开着时,标题栏顶着各自的标才分得清哪扇是谁的。
    /// </para>
    /// <para>
    /// 可用版本:2.0.4。**窗口标题栏那一路是宿主行为,不是契约面** ——
    /// 契约没变,老宿主上照旧只影响标签页。
    /// </para>
    /// </summary>
    public PluginIcon? Icon { get; init; }

    /// <summary>呈现方式,默认停靠文档。</summary>
    public PanelDisplayMode DisplayMode { get; init; } = PanelDisplayMode.Document;

    /// <summary>停靠文档的初始落位,默认并入当前标签组;窗口模式忽略。</summary>
    public PanelPlacement Placement { get; init; } = PanelPlacement.Tabs;

    /// <summary>
    /// 侧边落位时这一栏初始占标签区的比例(0.15–0.85,超界自动夹取),默认 0.3。
    /// <see cref="PanelPlacement.Tabs" /> 与窗口模式忽略。
    /// </summary>
    /// <remarks>
    /// 只是"打开时多宽";之后用户拖分割条随时可改,宿主也不会把这个值写回去。
    /// 插件若想记住用户偏好,自己存一份再在下次打开时传进来。
    /// </remarks>
    public double PlacementRatio { get; init; } = 0.3;

    /// <summary>窗口模式的初始宽度(逻辑像素),文档模式忽略。</summary>
    public double WindowWidth { get; init; } = 520;

    /// <summary>窗口模式的初始高度(逻辑像素),文档模式忽略。</summary>
    public double WindowHeight { get; init; } = 420;

    /// <summary>
    /// 窗口模式下摆在标题栏、紧挨最小化键左侧的动作按钮(按给出的顺序从左到右),文档模式忽略。
    /// 与主窗体标题栏那排工具按钮同一套版式;用于"这个窗口的附属设置"之类不值得占内容区的入口。
    /// </summary>
    public IReadOnlyList<PanelTitleAction> TitleActions { get; init; } = [];
}

/// <summary>
/// 窗口标题栏上的一个动作按钮。
/// </summary>
/// <param name="IconPathData">
/// 图标的 SVG 路径数据(24×24 视框、描边风格,即 lucide 那套),宿主按标题栏字号缩放描边。
/// 传路径而不是资源键:隔离进程里没有宿主的 <c>Icon.*</c> 资源字典。
/// </param>
/// <param name="ToolTip">悬停提示。</param>
/// <param name="OnClick">点击回调(UI 线程)。</param>
public sealed record PanelTitleAction(string IconPathData, string ToolTip, Action OnClick);

/// <summary>
/// 一个已打开的插件面板句柄:纯生命周期。内容是插件自己的 Avalonia 控件,
/// 事件与状态更新都由插件直接操作控件完成,不经过宿主。
/// </summary>
public interface IPluginPanel : IAsyncDisposable
{
    /// <summary>面板的不透明 id。</summary>
    string PanelId { get; }

    /// <summary>面板是否仍打开(用户关闭或 <see cref="CloseAsync" /> 后为 false)。</summary>
    bool IsOpen { get; }

    /// <summary>面板已关闭(用户关闭、插件关闭或插件停用),只触发一次。</summary>
    event Action? Closed;

    /// <summary>
    /// 用户拖分割条改完这一栏的宽度后触发,参数是新的比例(占所在分栏的 0–1)。
    /// </summary>
    /// <remarks>
    /// <b>拖动结束才触发一次</b>,不是拖动过程中连续触发 —— 调用方可以直接落盘,不必自己防抖。
    /// 仅 <see cref="PanelDisplayMode.Document" /> 有意义;窗口形态永不触发。
    /// 插件想"记住用户拖出来的宽度"就订阅它,下次打开把值经
    /// <see cref="PanelOptions.PlacementRatio" /> 传回来。
    /// </remarks>
    event Action<double>? PlacementRatioChanged;

    /// <summary>
    /// 把面板带到眼前:窗口形态激活并置前,停靠文档形态选中那个标签。
    /// 面板已关闭时什么也不做。任意线程可调。
    /// </summary>
    /// <remarks>
    /// 用于"这个面板已经开着了"的场景 —— 再开一个重复的不对,什么都不做又像是按钮坏了。
    /// </remarks>
    Task ActivateAsync();

    /// <summary>程序性关闭面板(幂等,任意线程可调)。</summary>
    Task CloseAsync();
}

/// <summary>
/// 界面能力:插件用**完整的 Avalonia** 自行设计界面(AXAML/代码任选,可自带样式、
/// 国际化与第三方组件包)。约束只有一条:Avalonia 相关包必须
/// <c>ExcludeAssets="runtime"</c> 且版本与宿主一致 —— 运行时由装载方共享同一套
/// Avalonia 程序集(进程内 = 宿主的;隔离进程 = PluginHost 自带的),保证类型同一。
/// </summary>
public interface IUiApi
{
    /// <summary>
    /// 打开一个面板。<paramref name="contentFactory" /> 在 **UI 线程**被调用
    /// (进程内 = 宿主 UI 线程;隔离进程 = 插件进程自己的 Avalonia UI 线程),
    /// 必须返回一个 <c>Avalonia.Controls.Control</c>;同一控件实例只能挂进一个面板。
    /// 之后对控件的操作请经 Avalonia 的 <c>Dispatcher.UIThread</c> 封送。
    /// 插件停用时其全部面板由宿主自动关闭。
    /// </summary>
    Task<IPluginPanel> ShowPanelAsync(PanelOptions options, Func<object> contentFactory, CancellationToken cancellationToken = default);

    /// <summary>
    /// 宿主是否实现了 <see cref="PostNotificationAsync" />;旧宿主默认 <see langword="false" />。
    /// 为 false 时不要退回别的打扰渠道(弹窗、开面板)去补 —— 发不了就只写日志。
    /// <para>可用版本:2.1.3。</para>
    /// </summary>
    bool SupportsNotifications => false;

    /// <summary>
    /// 往宿主的消息中心(侧边栏铃铛)发一条<b>要留存、可回看</b>的消息。边界与写法见 <see cref="PluginNotification" />。
    /// <para>
    /// 约定(宿主实现须遵守,插件可以依赖):
    /// <list type="bullet">
    ///   <item><b>身份由宿主绑定</b>:消息记在调用方插件名下,列表里标出插件名;消息里没有、也不接受「以谁的名义」。
    ///   宿主用 <see cref="PluginNotification.Validate" /> 校验,不合格抛 <see cref="ArgumentException" />。</item>
    ///   <item><b>先去重、后限频</b>:同 <see cref="PluginNotification.Id" /> 已在列表里时返回
    ///   <see cref="PluginNotificationOutcome.AlreadyPosted" />,不计入频率。</item>
    ///   <item><b>频率限制</b>:每插件每分钟最多 6 条<b>新</b>消息(插件系统蓝图 07 §7 的应用内通知频控),
    ///   超出返回 <see cref="PluginNotificationOutcome.RateLimited" />、不抛异常。宿主可以收得更紧,
    ///   插件不要贴着上限发:正常的插件一天也发不了几条。同类事件请合并成一条摘要。</item>
    ///   <item>用户可以在插件管理页关掉某个插件的通知,此时返回 <see cref="PluginNotificationOutcome.Muted" />。</item>
    ///   <item>消息跨重启留存,与其它消息一样受消息中心的总量上限约束(旧的会被淘汰);插件卸载时宿主删掉它发的全部消息。
    ///   插件不能改写或撤回已发的消息 —— 发之前想清楚值不值得留。</item>
    ///   <item>标题、正文按纯文本显示,超出 <see cref="PluginNotification.MaxTitleLength" /> /
    ///   <see cref="PluginNotification.MaxBodyLength" /> 截断。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 权限:应用内消息随普通权限 <c>ui.contributions</c>(安装即授予),不需要另行申请;
    /// 这不是操作系统通知(那是另一项权限 <c>notifications.system</c>,本方法不涉及)。
    /// 任意线程可调;令牌只取消这一次请求,不撤回已经入列的消息。
    /// </para>
    /// <para>可用版本:2.1.3。隔离进程经 <c>ui/notify</c> 转发,握手未宣告支持时在插件进程内直接抛不支持。</para>
    /// </summary>
    /// <param name="notification">要发的消息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>这条消息的去向。</returns>
    /// <exception cref="NotSupportedException">宿主没有实现插件通知(见 <see cref="SupportsNotifications" />)。</exception>
    /// <exception cref="ArgumentException">消息不合格(见 <see cref="PluginNotification.Validate" />)。</exception>
    Task<PluginNotificationOutcome> PostNotificationAsync(PluginNotification notification,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This host does not implement plugin notifications.");
}
