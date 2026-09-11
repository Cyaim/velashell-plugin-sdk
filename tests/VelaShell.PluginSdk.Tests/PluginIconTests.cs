namespace VelaShell.PluginSdk.Tests;

/// <summary>
/// 标签页图标那一个入口的两个便捷构造。
/// </summary>
/// <remarks>
/// 值得钉住的是**默认值**而不是构造本身:两位选错了图标不会消失,只会变得看不出是什么
/// (用描边画实心图形得到轮廓线;视框报错会把图标放大几十倍),
/// 而那种错在编译期与运行期都不会有任何提示。
/// </remarks>
[TestClass]
[TestCategory("Plugins")]
public sealed class PluginIconTests
{
    private const string Path = "M6 12h12";

    [TestMethod]
    public void StrokedIsLucidesDefault()
    {
        PluginIcon icon = PluginIcon.Stroked(Path);

        Assert.AreEqual(Path, icon.PathData);
        Assert.AreEqual(24d, icon.ViewBoxSize, "lucide 那套恒为 24。");
        Assert.IsFalse(icon.IsFilled, "描边才是宿主图标集的语言,填充要显式要求。");
    }

    [TestMethod]
    public void FilledCarriesItsOwnViewBox()
    {
        // 品牌 logo 的典型形状:实心,而且视框不是 24。
        PluginIcon icon = PluginIcon.Filled(Path, viewBoxSize: 1030);

        Assert.IsTrue(icon.IsFilled);
        Assert.AreEqual(1030d, icon.ViewBoxSize);
    }

    [TestMethod]
    public void FilledStillDefaultsToTwentyFourWhenTheViewBoxIsOmitted() =>
        // 实心不等于大视框 —— 一个实心的 24×24 字形是完全正常的东西。
        Assert.AreEqual(24d, PluginIcon.Filled(Path).ViewBoxSize);

    [TestMethod]
    public void TheRecordInitialiserAgreesWithTheFactories() =>
        // 两条路造出来的必须是同一个东西,否则文档里给的例子会和实际行为分家。
        Assert.AreEqual(
            PluginIcon.Filled(Path, 1030),
            new PluginIcon { PathData = Path, ViewBoxSize = 1030, IsFilled = true });
}
