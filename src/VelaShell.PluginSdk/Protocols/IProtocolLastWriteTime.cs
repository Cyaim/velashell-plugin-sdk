namespace VelaShell.PluginSdk.Protocols;

/// <summary>
/// 由 <see cref="IProtocolFileSystem" /> <b>可选</b>兼实现的接口:改远端文件的修改时间。
/// <para>
/// 存在的理由:宿主的目录同步在上传之后把远端修改时间对齐到本地源文件,
/// 否则下一次比较会把刚传上去的文件判成「远端较新」、来回反复。没有这一面,
/// 插件协议上的双向同步只能靠摘要或大小去猜。
/// </para>
/// <para>
/// 不实现它完全合法:宿主问不到这一面,就把这个协议当作「设不了修改时间」并如实报不支持,
/// 同步改用大小 / 摘要比较,其余功能不受影响。实现了但<b>这条会话</b>做不到
/// (服务器不支持 <c>MFMT</c> 一类的命令、对象存储的时间戳只读)时,抛
/// <see cref="ProtocolUnsupportedException" /> —— 宿主把它翻成「不支持」,不当作失败重试。
/// 不要静默成功:那会让同步以为对齐了,下一轮照样判错。
/// </para>
/// <para>
/// 纪律:
/// <list type="bullet">
///   <item>时间是绝对时刻(<see cref="DateTimeOffset" />),实现按协议需要自行换算(如 FTP 的 <c>MFMT</c> 要 UTC);
///   不要按本机时区解读。</item>
///   <item>后端精度更粗(秒、两秒)时向下截断到它能表示的精度即可;之后 <see cref="IProtocolFileSystem.StatAsync" />
///   报出的值应与写入值在该精度内一致 —— 宿主比较时按两边较粗的精度、留 1 秒容差。</item>
///   <item>只改修改时间,不改内容、权限与其它时间戳;路径是目录时可以按不支持拒绝。</item>
///   <item>路径是符号链接时改的是<b>链接指向的文件</b>(与 <c>touch</c> 一致);做不到就按不支持拒绝。</item>
///   <item>与 <see cref="IProtocolFileSystem" /> 的其余方法一样可能被并发调用,且在后台线程上调用。</item>
/// </list>
/// </para>
/// <para>可用版本:2.1.3。只增不改,<c>apiLevel</c> 不变。</para>
/// </summary>
public interface IProtocolLastWriteTime
{
    /// <summary>把远端文件 <paramref name="path" /> 的修改时间设为 <paramref name="lastWriteTime" />。</summary>
    /// <param name="sessionId">会话标识(即 <see cref="IProtocolFileSystem.ConnectAsync" /> 收到的那个)。</param>
    /// <param name="path">远端文件路径。</param>
    /// <param name="lastWriteTime">要写入的修改时间(绝对时刻)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="PluginSessionNotFoundException">未知的 <paramref name="sessionId" />。</exception>
    /// <exception cref="ProtocolUnsupportedException">这条会话(服务器)设不了修改时间。</exception>
    /// <exception cref="FileNotFoundException">路径不存在。</exception>
    Task SetLastWriteTimeAsync(string sessionId, string path, DateTimeOffset lastWriteTime,
        CancellationToken cancellationToken = default);
}
