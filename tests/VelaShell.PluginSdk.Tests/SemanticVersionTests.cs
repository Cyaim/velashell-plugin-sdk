using VelaShell.PluginSdk.Versioning;

namespace VelaShell.PluginSdk.Tests;

/// <summary>
/// 共享的版本比较。宿主(更新检查、装载闸)、<c>vela-plugin</c> 与插件商店以后都用它,
/// 所以这里既钉 SemVer 2.0 的优先级规则,也钉住宿主 <c>UpdateVersion</c> 原有的那几条宽松解析
/// —— 抬进 SDK 之后宿主换用它,行为不能悄悄变。
/// </summary>
[TestClass]
public sealed class SemanticVersionTests
{
    private static SemanticVersion V(string text) => SemanticVersion.Parse(text);

    // ---- 解析 ----

    [TestMethod]
    [DataRow("1.2.3", 1, 2, 3, 0, "", "")]
    [DataRow("v1.2.3", 1, 2, 3, 0, "", "")]
    [DataRow("V1.2.3", 1, 2, 3, 0, "", "")]
    [DataRow("  1.2.3\t", 1, 2, 3, 0, "", "")]
    [DataRow("0.1.0-beta", 0, 1, 0, 0, "beta", "")]
    [DataRow("1.2", 1, 2, 0, 0, "", "")]
    [DataRow("0.1.0.0", 0, 1, 0, 0, "", "")]
    [DataRow("1.2.3.4", 1, 2, 3, 4, "", "")]
    [DataRow("01.002.3", 1, 2, 3, 0, "", "")]
    [DataRow("1.2.3+build.5", 1, 2, 3, 0, "", "build.5")]
    [DataRow("1.0.0-beta+exp.sha.5114f85", 1, 0, 0, 0, "beta", "exp.sha.5114f85")]
    [DataRow("1.0.0-x-y-z.--", 1, 0, 0, 0, "x-y-z.--", "")]
    [DataRow("1.0.0-0.3.7", 1, 0, 0, 0, "0.3.7", "")]
    [DataRow("1.0.0+0001", 1, 0, 0, 0, "", "0001")]
    [DataRow("1.0.0-0a.01b", 1, 0, 0, 0, "0a.01b", "")]
    [DataRow("2147483647.0.0", int.MaxValue, 0, 0, 0, "", "")]
    [DataRow("2.1.0-preview.1", 2, 1, 0, 0, "preview.1", "")]
    public void TryParse_ValidInput(string text, int major, int minor, int patch, int revision, string pre, string build)
    {
        Assert.IsTrue(SemanticVersion.TryParse(text, out SemanticVersion v), text);
        Assert.AreEqual(major, v.Major);
        Assert.AreEqual(minor, v.Minor);
        Assert.AreEqual(patch, v.Patch);
        Assert.AreEqual(revision, v.Revision);
        Assert.AreEqual(pre, v.PreRelease);
        Assert.AreEqual(build, v.BuildMetadata);
        Assert.AreEqual(pre.Length > 0, v.IsPreRelease);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("v")]
    [DataRow("vv1.2.3")]
    [DataRow("abc")]
    [DataRow("1")]
    [DataRow("1.2.3.4.5")]
    [DataRow("1..3")]
    [DataRow("1.2.")]
    [DataRow(".1.2")]
    [DataRow("1.-2.3")]
    [DataRow("1.+2.3")]
    [DataRow("1. 2.3")]
    [DataRow("-1.2.3")]
    [DataRow("+1.2.3")]
    [DataRow("2147483648.0.0")]
    [DataRow("1.2.99999999999")]
    [DataRow("١.٢.٣")]
    [DataRow("1.2.3-")]
    [DataRow("1.2.3+")]
    [DataRow("1.2.3-+meta")]
    [DataRow("1.2.3-beta..1")]
    [DataRow("1.2.3-beta.")]
    [DataRow("1.2.3-.beta")]
    [DataRow("1.2.3-beta.01")]
    [DataRow("1.2.3-00")]
    [DataRow("1.2.3-be_ta")]
    [DataRow("1.2.3-bêta")]
    [DataRow("1.2.3-beta 1")]
    [DataRow("1.2.3+bu_ild")]
    [DataRow("1.2.3+a..b")]
    [DataRow("1.2.3+a+b")]
    public void TryParse_InvalidInput_Fails(string? text)
    {
        Assert.IsFalse(SemanticVersion.TryParse(text, out SemanticVersion v), text);
        Assert.AreEqual(default, v);
    }

    [TestMethod]
    public void Parse_ThrowsFormatException_NamingTheInput()
    {
        FormatException ex = Assert.ThrowsExactly<FormatException>(() => SemanticVersion.Parse("1.2.3-beta.01"));
        StringAssert.Contains(ex.Message, "1.2.3-beta.01");
    }

    // ---- SemVer 2.0 §11 的优先级 ----

    [TestMethod]
    public void Precedence_FollowsTheSemVerSpecExampleChain()
    {
        // SemVer 2.0 §11.4 给的那条链,外加数字段的递进。
        string[] ascending =
        [
            "0.9.9", "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2",
            "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0", "2.1.0", "2.1.1", "10.0.0"
        ];
        for (int i = 0; i < ascending.Length; i++)
        {
            for (int j = 0; j < ascending.Length; j++)
            {
                int expected = i.CompareTo(j);
                Assert.AreEqual(expected, Math.Sign(V(ascending[i]).CompareTo(V(ascending[j]))),
                    $"{ascending[i]} vs {ascending[j]}");
            }
        }
    }

    [TestMethod]
    [DataRow("1.0.0-beta.10", "1.0.0-beta.2")]       // 起因:按序数比字符串会把它判反
    [DataRow("1.0.0-beta.10", "1.0.0-beta.9")]
    [DataRow("1.0.0-rc.100", "1.0.0-rc.99")]
    [DataRow("1.0.0-alpha.a", "1.0.0-alpha.1")]       // 含字母的高于纯数字的
    [DataRow("1.0.0-alpha", "1.0.0-1")]
    [DataRow("1.0.0-alpha.0", "1.0.0-alpha")]         // 前面相同,标识符多的更高
    [DataRow("1.0.0-rc.1", "1.0.0-RC.1")]             // ASCII 序数,区分大小写
    [DataRow("1.0.0-alpha", "1.0.0-Beta")]
    [DataRow("1.0.0-a-b", "1.0.0-a")]
    [DataRow("1.0.0-alpha.123456789012345678901234567890", "1.0.0-alpha.99999999999999999999")]  // 超出 long 也照数值比
    [DataRow("1.0.0", "1.0.0-rc.1")]
    [DataRow("1.2.3.1", "1.2.3")]                      // 第四段参与比较
    [DataRow("1.2.4", "1.2.3.99")]
    [DataRow("1.2.3.1", "1.2.3.1-rc.1")]
    public void Precedence_LeftIsNewer(string newer, string older)
    {
        SemanticVersion a = V(newer);
        SemanticVersion b = V(older);
        Assert.IsGreaterThan(0, a.CompareTo(b), $"{newer} should be newer than {older}");
        Assert.IsLessThan(0, b.CompareTo(a));
        Assert.IsTrue(a > b);
        Assert.IsTrue(a >= b);
        Assert.IsTrue(b < a);
        Assert.IsTrue(b <= a);
        Assert.IsTrue(a != b);
        Assert.IsFalse(a == b);
    }

    [TestMethod]
    [DataRow("1.2.3", "v1.2.3")]
    [DataRow("1.2.3", "1.2.3.0")]
    [DataRow("1.2.0", "1.2")]
    [DataRow("1.2.3", "01.2.3")]
    [DataRow("1.2.3", "1.2.3+build.5")]                // 构建元数据不参与比较与相等
    [DataRow("1.0.0-beta+a", "1.0.0-beta+b")]
    [DataRow("1.0.0-beta.1", " V1.0.0.0-beta.1+x ")]
    public void EqualPrecedence_IsEquality_WithConsistentHash(string left, string right)
    {
        SemanticVersion a = V(left);
        SemanticVersion b = V(right);
        Assert.AreEqual(0, a.CompareTo(b));
        Assert.IsTrue(a == b);
        Assert.IsTrue(a.Equals(b));
        Assert.IsTrue(a.Equals((object)b));
        Assert.IsTrue(a >= b && a <= b);
        Assert.AreEqual(a.GetHashCode(), b.GetHashCode(), "相等的值必须同哈希,否则放进字典 / HashSet 就是两份");
    }

    [TestMethod]
    public void CompareCoreTo_IgnoresPreReleaseAndBuild_ForLoadGates()
    {
        // 装载闸的口径:预览版宿主要能装为它写的插件。
        Assert.AreEqual(0, V("1.1.0-beta").CompareCoreTo(V("1.1.0")));
        Assert.AreEqual(0, V("1.1.0").CompareCoreTo(V("1.1.0-beta.3+meta")));
        Assert.IsLessThan(0, V("1.1.0-beta").CompareTo(V("1.1.0")), "「哪个更新」仍然按完整优先级");
        Assert.IsLessThan(0, V("1.0.9-rc.1").CompareCoreTo(V("1.1.0")));
        Assert.IsGreaterThan(0, V("1.1.0.1-beta").CompareCoreTo(V("1.1.0")));
    }

    // ---- 宿主 UpdateVersion 的既有用例:换用共享类型后一条都不能变 ----

    [TestMethod]
    [DataRow("1.0.0", "0.9.9")]
    [DataRow("0.2.0", "0.1.9")]
    [DataRow("0.1.1", "0.1.0")]
    [DataRow("0.1.0", "0.1.0-beta")]
    [DataRow("0.1.0-beta.2", "0.1.0-beta.1")]
    [DataRow("0.1.0-beta.10", "0.1.0-beta.9")]
    [DataRow("0.1.0-beta", "0.1.0-alpha")]
    [DataRow("0.1.0-beta.1", "0.1.0-beta")]
    public void HostUpdateVersionCases_StillHold(string newer, string older)
        => Assert.IsTrue(V(newer) > V(older), $"{newer} should be newer than {older}");

    // ---- 默认值与文本 ----

    [TestMethod]
    public void Default_IsZero_WithEmptyNotNullSuffixes()
    {
        SemanticVersion zero = default;
        Assert.AreEqual(string.Empty, zero.PreRelease);
        Assert.AreEqual(string.Empty, zero.BuildMetadata);
        Assert.IsFalse(zero.IsPreRelease);
        Assert.AreEqual("0.0.0", zero.ToString());
        Assert.AreEqual(V("0.0.0"), zero);
        Assert.AreEqual(V("0.0.0").GetHashCode(), zero.GetHashCode());
    }

    [TestMethod]
    [DataRow("1.2.3-beta.1", "1.2.3-beta.1")]
    [DataRow("v1.2.3", "1.2.3")]
    [DataRow("1.2", "1.2.0")]
    [DataRow("01.2.3", "1.2.3")]
    [DataRow("1.2.3.0", "1.2.3")]
    [DataRow("1.2.3.4-rc.1+sha.abc", "1.2.3.4-rc.1+sha.abc")]
    [DataRow(" 1.0.0+build ", "1.0.0+build")]
    public void ToString_IsNormalized_AndReparsesToTheSameValue(string text, string expected)
    {
        SemanticVersion v = V(text);
        Assert.AreEqual(expected, v.ToString());
        SemanticVersion again = V(v.ToString());
        Assert.AreEqual(v, again);
        Assert.AreEqual(v.BuildMetadata, again.BuildMetadata);
    }

    // ---- 字符串比较器:「从一串版本里挑最新」 ----

    [TestMethod]
    public void TextComparer_SortsUnparseableAsOldest_AndPicksTheRealLatest()
    {
        List<string?> versions = ["1.0.0", "garbage", null, "1.0.0-beta.10", "1.0.0-beta.2", "v0.9.0", "1.0", "", "0.9.0"];
        versions.Sort(SemanticVersion.TextComparer);
        CollectionAssert.AreEqual(
            new[] { null, "", "garbage", "0.9.0", "v0.9.0", "1.0.0-beta.2", "1.0.0-beta.10", "1.0", "1.0.0" },
            versions,
            "解析不出的当最旧;同优先级的按原文序数,保证哪台机器上排出来都一样");
        Assert.AreEqual("1.0.0", versions.Max(SemanticVersion.TextComparer));
    }

    [TestMethod]
    public void TextComparer_IsAntisymmetric()
    {
        string?[] samples = [null, "", "x", "1.0.0", "v1.0.0", "1.0.0+b", "1.0.0-rc.1", "2.0", "1.0.0.1"];
        foreach (string? x in samples)
        {
            foreach (string? y in samples)
            {
                int forward = SemanticVersion.TextComparer.Compare(x, y);
                int backward = SemanticVersion.TextComparer.Compare(y, x);
                Assert.AreEqual(-Math.Sign(forward), Math.Sign(backward), $"{x ?? "null"} vs {y ?? "null"}");
                Assert.AreEqual(x == y, forward == 0, $"{x ?? "null"} vs {y ?? "null"}");
            }
        }
    }
}
