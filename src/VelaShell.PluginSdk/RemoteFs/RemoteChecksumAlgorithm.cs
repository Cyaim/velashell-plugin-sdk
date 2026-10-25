namespace VelaShell.PluginSdk.RemoteFs;

/// <summary>
/// 服务器端文件摘要的算法。数值显式给出并且<b>只增不改</b>:它会出现在线上与持久化的比较缓存里,
/// 改动既有成员的值等于让新旧两端把同一个摘要当成两种算法。
/// <para>
/// 只收宿主能在本机用 BCL 算出同一种摘要的算法 —— 服务器端摘要的用处是与本地文件比对
/// (目录同步的「先比 SHA-256」、传输后校验),本地算不出的算法拿回来也没法比。
/// </para>
/// <para>可用版本:TBD(发版时替换)。</para>
/// </summary>
public enum RemoteChecksumAlgorithm
{
    /// <summary>未指定。不是合法的请求值:缺字段的线上数据读成它,实现应按不支持拒绝,绝不当成某个默认算法。</summary>
    Unknown = 0,

    /// <summary>SHA-256(宿主目录同步与传输校验使用的算法)。</summary>
    Sha256 = 1,

    /// <summary>SHA-512。</summary>
    Sha512 = 2,

    /// <summary>SHA-1。只适合判「内容是否相同」,不要用于任何安全判断。</summary>
    Sha1 = 3,

    /// <summary>MD5(对象存储未分片上传的 ETag 常是它)。只适合判「内容是否相同」,不要用于任何安全判断。</summary>
    Md5 = 4
}
