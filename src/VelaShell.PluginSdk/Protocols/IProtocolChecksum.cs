using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.PluginSdk.Protocols;

/// <summary>
/// 由 <see cref="IProtocolFileSystem" /> <b>可选</b>兼实现的接口:在服务器端算文件摘要,不经网络传内容。
/// <para>
/// 存在的理由:宿主的目录同步先比摘要、不支持或出错再回退到大小与修改时间;传输之后也拿它核对。
/// 没有这一面,插件协议上的比较只能看大小与时间 —— 时间设不准(见 <see cref="IProtocolLastWriteTime" />)
/// 或被别的工具改过时就判错。
/// </para>
/// <para>
/// 不实现它完全合法:宿主问不到这一面,就把这个协议当作「算不了服务器端摘要」,整体回退,其余功能不受影响。
/// </para>
/// <para>
/// 纪律:
/// <list type="bullet">
///   <item><b>只能用服务器给出的摘要</b>(FTP 的 <c>HASH</c> / <c>XSHA256</c>、对象存储保存的校验和、服务端 API)。
///   不许为了凑出一个值把文件整份读回来自己算 —— 那等于一次完整下载,宿主选它正是为了省掉这一次。</item>
///   <item>这条会话(服务器)整体算不了所请求的算法(包括 <see cref="RemoteChecksumAlgorithm.Unknown" />)
///   时抛 <see cref="ProtocolUnsupportedException" />:宿主据此整体回退,不逐个文件重试。</item>
///   <item>单个文件算不出(不存在、是目录、没权限、服务器没存这种校验和)时,那一项的值为 <see langword="null" />,
///   其余照常返回 —— 一个文件出错不该让整批回退。</item>
///   <item>返回值的键是<b>请求里的路径原样</b>(序数比较);请求里没有的键不要返回,缺了的键宿主按 <see langword="null" /> 处理。</item>
///   <item>摘要是<b>小写十六进制</b>、不带前缀与分隔符(SHA-256 恰为 64 个字符)。服务器给的是 base64 或大写时由实现换算。</item>
///   <item>路径是符号链接时算的是<b>链接指向的文件</b>(与 <c>sha256sum</c> 一致)。</item>
///   <item>与 <see cref="IProtocolFileSystem" /> 的其余方法一样可能被并发调用,且在后台线程上调用;
///   一次请求可能带上百个路径,能批量就批量。</item>
/// </list>
/// </para>
/// <para>可用版本:2.1.3。只增不改,<c>apiLevel</c> 不变。</para>
/// </summary>
public interface IProtocolChecksum
{
    /// <summary>在服务器端批量算 <paramref name="paths" /> 的摘要。</summary>
    /// <param name="sessionId">会话标识(即 <see cref="IProtocolFileSystem.ConnectAsync" /> 收到的那个)。</param>
    /// <param name="algorithm">摘要算法;宿主目前只请求 <see cref="RemoteChecksumAlgorithm.Sha256" />。</param>
    /// <param name="paths">远端文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>请求的路径 → 小写十六进制摘要;单个文件算不出时值为 <see langword="null" />。</returns>
    /// <exception cref="PluginSessionNotFoundException">未知的 <paramref name="sessionId" />。</exception>
    /// <exception cref="ProtocolUnsupportedException">这条会话(服务器)算不了这种摘要。</exception>
    Task<IReadOnlyDictionary<string, string?>> ComputeChecksumsAsync(string sessionId, RemoteChecksumAlgorithm algorithm,
        IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
}
