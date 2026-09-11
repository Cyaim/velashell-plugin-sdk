namespace VelaShell.PluginSdk;

/// <summary>
/// 插件自报的一个图标。宿主拿它画标签页 —— 协议会话、工作台会话、面板,三处同一个类型。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是一个类型而不是几个散字段。</b>「一段路径 + 它的视框 + 描边还是填充」是**一件事**,
/// 拆成三个平行属性挂在三个宿主类型上,插件作者要记的就是九个名字而不是一个。
/// 这里收成一个入口:<c>ProtocolDescriptor.Icon</c> / <c>WorkspaceDescriptor.Icon</c> /
/// <c>PanelOptions.Icon</c> 填的都是它。
/// </para>
/// <para>
/// <b>为什么传路径而不是资源键。</b>隔离进程里没有宿主的 <c>Icon.*</c> 资源字典。
/// 而宿主也不该维护一张「插件 id → 图标」的对照表 —— 那种表第三方插件永远进不去。
/// </para>
/// <para>
/// ⚠️ 与 <c>PanelTitleAction.IconPathData</c> **不是一回事**:那个画的是窗口标题栏上的
/// 动作按钮,恒为 24×24 描边,没有填充与视框可言。这个画的是标签页。
/// </para>
/// <para>
/// ⚠️ <see cref="PathData" /> 是**一条会被画到屏幕上的字符串**。宿主解析失败时当作没给、
/// 画通用插件图标 —— 一段畸形路径不该把标签条顶掉。
/// </para>
/// <para>可用版本:2.0.4。</para>
/// </remarks>
/// <example>
/// lucide 那套描边字形直接抄路径即可(视框恒为 24):
/// <code>
/// Icon = PluginIcon.Stroked("M6 12h12 M6 8h12a4 4 0 0 1 0 8H6a4 4 0 0 1 0-8Z")   // usb-c-port
/// </code>
/// 品牌 logo 通常是实心的、视框也不是 24,两件都要说清楚:
/// <code>
/// Icon = PluginIcon.Filled(RedisLogoPath, viewBoxSize: 1030)
/// </code>
/// </example>
public sealed record PluginIcon
{
    /// <summary>图标的 SVG 路径数据(多段用空格连写即可)。</summary>
    public required string PathData { get; init; }

    /// <summary>
    /// <see cref="PathData" /> 的原生视框边长,默认 24(lucide 那套)。
    /// <para>
    /// 品牌 logo 导出的视框常是 1024 之类。**不报出来宿主会按 24 缩放,把它放大四十多倍** ——
    /// 「图标没显示」这类现象,原因通常就在这一条。
    /// </para>
    /// </summary>
    public double ViewBoxSize { get; init; } = 24d;

    /// <summary>
    /// 是否为实心填充图形。默认 <see langword="false" />,即按描边渲染。
    /// <para>
    /// 用描边去画一个实心图形,得到的是它的**轮廓线**,一团糊;反过来把描边字形填充起来
    /// 则是一堆色块。这一位选错了,图标不会消失,只会变得看不出是什么。
    /// </para>
    /// </summary>
    public bool IsFilled { get; init; }

    /// <summary>描边图标(lucide 那套):视框 24、描边渲染。</summary>
    /// <param name="pathData">SVG 路径数据。</param>
    /// <returns>描边图标。</returns>
    public static PluginIcon Stroked(string pathData) => new() { PathData = pathData };

    /// <summary>实心图标(品牌 logo 那类):填充渲染,视框自报。</summary>
    /// <param name="pathData">SVG 路径数据。</param>
    /// <param name="viewBoxSize">原生视框边长,默认 24。</param>
    /// <returns>实心图标。</returns>
    public static PluginIcon Filled(string pathData, double viewBoxSize = 24d) =>
        new() { PathData = pathData, ViewBoxSize = viewBoxSize, IsFilled = true };
}
