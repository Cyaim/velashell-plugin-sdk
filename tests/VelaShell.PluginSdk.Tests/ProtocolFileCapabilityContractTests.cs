using System.Text.Json;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.RemoteFs;
using VelaShell.PluginSdk.Rpc;

namespace VelaShell.PluginSdk.Tests;

/// <summary>
/// 插件协议的文件能力补口:条目上的链接字段、可选的「改修改时间」与「服务器端摘要」两面。
/// 守的是「只增不改」—— 老插件的实现、老宿主给的条目都不能因为这一批而失效或被误读。
/// </summary>
[TestClass]
public sealed class ProtocolFileCapabilityContractTests
{
    private static readonly DateTimeOffset Modified = new(2026, 10, 24, 8, 30, 15, TimeSpan.Zero);

    private static RemoteFileEntry Entry() =>
        new("app", "/srv/app", true, 0, Modified, "rwxrwxrwx", "root", "root");

    [TestMethod]
    public void RemoteFileEntry_KeepsPositionalSignature_AndDefaultsToNotALink()
    {
        Assert.IsNotNull(typeof(RemoteFileEntry).GetConstructor(
        [
            typeof(string), typeof(string), typeof(bool), typeof(long), typeof(DateTimeOffset),
            typeof(string), typeof(string), typeof(string)
        ]), "八参构造是老插件编译进去的签名,不能动");
        Assert.HasCount(8, typeof(RemoteFileEntry).GetMethod("Deconstruct")!.GetParameters());

        RemoteFileEntry entry = Entry();
        Assert.IsFalse(entry.IsSymbolicLink);
        Assert.IsNull(entry.LinkTarget);
    }

    [TestMethod]
    public void RemoteFileEntry_LinkFields_TakePartInEquality()
    {
        RemoteFileEntry plain = Entry();
        RemoteFileEntry link = plain with { IsSymbolicLink = true, LinkTarget = "../releases/current" };
        Assert.AreNotEqual(plain, link, "链接与同名普通目录不是同一个条目 —— 删除语义完全不同");
        Assert.AreEqual(link, link with { });
        Assert.IsTrue(link.IsDirectory, "指向目录的链接:IsDirectory 描述的是目标,可以直接进入");
    }

    [TestMethod]
    public void RemoteFileEntry_LinkFields_RoundTripJson_AndLegacyJsonReadsAsNotALink()
    {
        RemoteFileEntry link = Entry() with { IsSymbolicLink = true, LinkTarget = "/opt/app-2.1" };
        string json = JsonSerializer.Serialize(link);
        Assert.AreEqual(link, JsonSerializer.Deserialize<RemoteFileEntry>(json));

        // 老宿主 / 老 PluginHost 发出的条目没有这两个字段:读成「不是链接」,而不是失败。
        string legacy = JsonSerializer.Serialize(new
        {
            Name = "app", FullPath = "/srv/app", IsDirectory = true, Size = 0L, LastModified = Modified,
            Permissions = "rwxrwxrwx", Owner = "root", Group = "root"
        });
        RemoteFileEntry? old = JsonSerializer.Deserialize<RemoteFileEntry>(legacy);
        Assert.IsNotNull(old);
        Assert.IsFalse(old.IsSymbolicLink);
        Assert.IsNull(old.LinkTarget);
        Assert.AreEqual(Entry(), old);
    }

    [TestMethod]
    public async Task RemoteFileEntry_LinkFields_SurviveTheIsolatedFsListRpc()
    {
        await using RpcTestPipes pipes = await RpcTestPipes.ConnectAsync();
        await using var host = new RpcConnection(pipes.Server);
        await using var plugin = new RpcConnection(pipes.Client);
        RemoteFileEntry[] listing =
        [
            Entry() with { IsSymbolicLink = true, LinkTarget = "../releases/current" },
            new("dangling", "/srv/dangling", false, 12, Modified, "rwxrwxrwx", "root", "root")
            {
                IsSymbolicLink = true, LinkTarget = "/gone"
            },
            new("notes.txt", "/srv/notes.txt", false, 42, Modified, "rw-r--r--", "app", "app")
        ];
        host.SetRequestHandler((method, _, _) =>
            Task.FromResult<object?>(method == PluginRpc.FsList ? listing : throw new InvalidOperationException(method)));
        host.Start();
        plugin.Start();

        RemoteFileEntry[]? received = await plugin.RequestAsync<RemoteFileEntry[]>(PluginRpc.FsList,
            new FsPathRequest("s", "/srv"), TimeSpan.FromSeconds(5));

        Assert.IsNotNull(received);
        CollectionAssert.AreEqual(listing, received);
        Assert.IsFalse(received[1].IsDirectory, "断链保留链接自身的属性");
    }

    [TestMethod]
    public void OptionalCapabilities_AreSeparateInterfaces_SoExistingProtocolsStayValid()
    {
        // 与 IProtocolStreamUpload 同一种形态:实现类「兼实现」,宿主按 is 问。
        // 塞进 IProtocolFileSystem 本身就会让所有现成的协议插件编译不过。
        Type[] optional = [typeof(IProtocolLastWriteTime), typeof(IProtocolChecksum), typeof(IProtocolStreamUpload)];
        foreach (Type capability in optional)
        {
            Assert.IsTrue(capability.IsInterface);
            Assert.IsFalse(typeof(IProtocolFileSystem).IsAssignableFrom(capability), capability.Name);
            Assert.IsFalse(capability.IsAssignableFrom(typeof(IProtocolFileSystem)), capability.Name);
            Assert.IsEmpty(capability.GetInterfaces(), capability.Name);
        }
        string[] baseMembers = [.. typeof(IProtocolFileSystem).GetMethods().Select(m => m.Name)];
        CollectionAssert.DoesNotContain(baseMembers, nameof(IProtocolLastWriteTime.SetLastWriteTimeAsync));
        CollectionAssert.DoesNotContain(baseMembers, nameof(IProtocolChecksum.ComputeChecksumsAsync));
    }

    [TestMethod]
    public void SetLastWriteTime_SignatureIsPinned()
    {
        var method = typeof(IProtocolLastWriteTime).GetMethod(nameof(IProtocolLastWriteTime.SetLastWriteTimeAsync))!;
        Assert.AreEqual(typeof(Task), method.ReturnType);
        CollectionAssert.AreEqual(
            new[] { typeof(string), typeof(string), typeof(DateTimeOffset), typeof(CancellationToken) },
            method.GetParameters().Select(p => p.ParameterType).ToArray(),
            "绝对时刻用 DateTimeOffset,与 RemoteFileEntry.LastModified 同一口径");
        Assert.IsTrue(method.GetParameters()[3].HasDefaultValue);
        Assert.HasCount(1, typeof(IProtocolLastWriteTime).GetMembers());
    }

    [TestMethod]
    public void ComputeChecksums_SignatureIsPinned()
    {
        var method = typeof(IProtocolChecksum).GetMethod(nameof(IProtocolChecksum.ComputeChecksumsAsync))!;
        Assert.AreEqual(typeof(Task<IReadOnlyDictionary<string, string?>>), method.ReturnType);
        CollectionAssert.AreEqual(
            new[] { typeof(string), typeof(RemoteChecksumAlgorithm), typeof(IReadOnlyList<string>), typeof(CancellationToken) },
            method.GetParameters().Select(p => p.ParameterType).ToArray());
        Assert.IsTrue(method.GetParameters()[3].HasDefaultValue);
        Assert.HasCount(1, typeof(IProtocolChecksum).GetMembers());
    }

    [TestMethod]
    public void ChecksumAlgorithm_ValuesArePinned_AndMissingMeansUnknown()
    {
        // 数值进线上与比较缓存:只增不改。
        Assert.AreEqual(0, (int)RemoteChecksumAlgorithm.Unknown);
        Assert.AreEqual(1, (int)RemoteChecksumAlgorithm.Sha256);
        Assert.AreEqual(2, (int)RemoteChecksumAlgorithm.Sha512);
        Assert.AreEqual(3, (int)RemoteChecksumAlgorithm.Sha1);
        Assert.AreEqual(4, (int)RemoteChecksumAlgorithm.Md5);
        Assert.HasCount(5, Enum.GetValues<RemoteChecksumAlgorithm>());
        Assert.AreEqual(RemoteChecksumAlgorithm.Unknown, JsonSerializer.Deserialize<RemoteChecksumAlgorithm>("0"),
            "缺省值不能悄悄等于某个真实算法");
    }
}
