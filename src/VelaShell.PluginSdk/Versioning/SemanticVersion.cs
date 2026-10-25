namespace VelaShell.PluginSdk.Versioning;

/// <summary>
/// 版本号与它的比较规则 —— 宿主、<c>vela-plugin</c> 与插件商店<b>共用的那一份</b>。
/// <para>
/// 存在的理由:四处各写一套时,<c>1.0.0-beta.10</c> 与 <c>1.0.0-beta.2</c> 谁新能得到不同答案
/// (按序数比字符串,<c>"10" &lt; "2"</c>),于是会出现「CLI 说该升、宿主说没变」这种没法解释的结果。
/// 比较口径收在这里,谁都别再自己写。
/// </para>
/// <para>
/// <b>比较(<see cref="CompareTo" />)严格按 SemVer 2.0 §11 的优先级</b>:
/// <list type="number">
///   <item>主、次、修订(以及下面说的第四段)逐段按数值比;</item>
///   <item>数字段相同时,带预发布后缀的<b>低于</b>正式版(<c>1.0.0-rc.1 &lt; 1.0.0</c>);</item>
///   <item>两个预发布后缀按点分标识符逐个比:纯数字的按数值比(<c>beta.2 &lt; beta.10</c>),
///   含字母或连字符的按 ASCII 序数比(区分大小写,<c>RC &lt; rc</c>),纯数字的<b>低于</b>含字母的;</item>
///   <item>前面都相同时,标识符多的那个更高(<c>alpha &lt; alpha.1</c>);</item>
///   <item>构建元数据(<c>+…</c>)不参与比较,也不参与相等 —— 只保留下来供 <see cref="ToString" /> 原样带出。</item>
/// </list>
/// 相等(<see cref="Equals(SemanticVersion)" />、<c>==</c>)与优先级一致:优先级相同即相等。
/// </para>
/// <para>
/// <b>解析(<see cref="TryParse" />)在数字段上比 SemVer 宽,在后缀上与 SemVer 一样严</b>,
/// 宽的那几条都是宿主与清单里真实出现的写法:
/// <list type="bullet">
///   <item>前后空白与一个前缀 <c>v</c> / <c>V</c>(Git 标签 <c>v1.7.2</c>);</item>
///   <item>两段(<c>1.2</c>,修订补 0)与四段(<c>1.2.3.4</c>,程序集 / 文件版本)—— 与 <c>plugin.json</c> 的版本规则同口径。
///   第四段记为 <see cref="Revision" />,参与比较(<c>1.2.3.0 == 1.2.3</c>、<c>1.2.3.1 &gt; 1.2.3</c>);</item>
///   <item>数字段的前导零按数值读(<c>01.2.3 == 1.2.3</c>,数值不会因此含糊)。</item>
/// </list>
/// 后缀则照 SemVer 2.0 的语法:标识符非空、只含 <c>[0-9A-Za-z-]</c>,预发布里的纯数字标识符不许有前导零
/// (<c>beta.01</c> 无法与 <c>beta.1</c> 区分轻重,判为不合法)。单段(<c>1</c>)与五段以上不合法。
/// 数字段须落在 <see cref="int" /> 范围内。
/// </para>
/// <para>
/// <b>装载闸(<c>minSdkVersion</c> / <c>minHostVersion</c>)用 <see cref="CompareCoreTo" /></b>:只比数字段、忽略预发布 ——
/// 把 <c>1.1.0-beta</c> 判成「不够 1.1.0」会让预览版宿主装不上正是为它写的插件。
/// 「哪个更新」(升级提示、从商店挑最新版)用 <see cref="CompareTo" />。
/// </para>
/// <para>不是线协 DTO:跨进程、进清单、进商店一律传字符串,两端各自解析。可用版本:TBD(发版时替换)。</para>
/// </summary>
public readonly struct SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private readonly string? _preRelease;
    private readonly string? _buildMetadata;

    private SemanticVersion(int major, int minor, int patch, int revision, string preRelease, string buildMetadata)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Revision = revision;
        _preRelease = preRelease;
        _buildMetadata = buildMetadata;
    }

    /// <summary>
    /// 按优先级比较<b>版本字符串</b>的比较器,给「一串版本号里挑最新」这类场景用(商店的版本列表、CLI 的升级判断)。
    /// <para>
    /// 解析不出的字符串排在所有合法版本<b>之前</b>(当作最旧,于是永远不会被挑成「最新」),
    /// 彼此按序数比;<see langword="null" /> 排在最前。优先级相同的两个合法版本(只差构建元数据、
    /// 或 <c>v1.2.3</c> 与 <c>1.2.3</c>)再按原文序数比,保证排序结果在哪台机器上都一样。
    /// </para>
    /// </summary>
    public static IComparer<string?> TextComparer { get; } = new TextPrecedenceComparer();

    /// <summary>主版本号。</summary>
    public int Major { get; }

    /// <summary>次版本号。</summary>
    public int Minor { get; }

    /// <summary>修订号;两段写法时为 0。</summary>
    public int Patch { get; }

    /// <summary>第四段(程序集 / 文件版本那种四段写法);三段及以下为 0。参与比较,<see cref="ToString" /> 只在非 0 时写出。</summary>
    public int Revision { get; }

    /// <summary>预发布后缀(不含引导的 <c>-</c>);正式版为空字符串,永不为 <see langword="null" />。</summary>
    public string PreRelease => _preRelease ?? string.Empty;

    /// <summary>构建元数据(不含引导的 <c>+</c>);没有时为空字符串。不参与比较与相等。</summary>
    public string BuildMetadata => _buildMetadata ?? string.Empty;

    /// <summary>是否为预发布版本。</summary>
    public bool IsPreRelease => PreRelease.Length > 0;

    /// <summary>解析版本字符串;规则见类型说明。不合法时返回 <see langword="false" />,不抛异常。</summary>
    /// <param name="text">版本字符串。</param>
    /// <param name="version">解析结果;失败时为 <see langword="default" />(<c>0.0.0</c>)。</param>
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (text is null)
        {
            return false;
        }
        ReadOnlySpan<char> s = text.AsSpan().Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V'))
        {
            s = s[1..];
        }
        string build = string.Empty;
        int plus = s.IndexOf('+');
        if (plus >= 0)
        {
            if (!AreValidIdentifiers(s[(plus + 1)..], forbidLeadingZeros: false))
            {
                return false;
            }
            build = s[(plus + 1)..].ToString();
            s = s[..plus];
        }
        string pre = string.Empty;
        int dash = s.IndexOf('-');
        if (dash >= 0)
        {
            if (!AreValidIdentifiers(s[(dash + 1)..], forbidLeadingZeros: true))
            {
                return false;
            }
            pre = s[(dash + 1)..].ToString();
            s = s[..dash];
        }
        Span<int> numbers = stackalloc int[4];
        int count = 0;
        foreach (Range range in s.Split('.'))
        {
            if (count == numbers.Length || !TryParseNumber(s[range], out numbers[count]))
            {
                return false;
            }
            count++;
        }
        if (count < 2)
        {
            return false;
        }
        version = new(numbers[0], numbers[1], numbers[2], numbers[3], pre, build);
        return true;
    }

    /// <summary>解析版本字符串;规则见类型说明。</summary>
    /// <exception cref="FormatException">不合法。</exception>
    public static SemanticVersion Parse(string text) =>
        TryParse(text, out SemanticVersion version)
            ? version
            : throw new FormatException($"'{text}' is not a valid version (expected e.g. 1.2.3 or 1.2.3-beta.1).");

    /// <summary>按 SemVer 2.0 优先级比较(含预发布,忽略构建元数据)。</summary>
    public int CompareTo(SemanticVersion other)
    {
        int c = CompareCoreTo(other);
        return c != 0 ? c : ComparePreRelease(PreRelease, other.PreRelease);
    }

    /// <summary>
    /// 只比数字段(主、次、修订、第四段),忽略预发布与构建元数据。
    /// 装载闸(<c>minSdkVersion</c> / <c>minHostVersion</c>)用它,理由见类型说明。
    /// </summary>
    public int CompareCoreTo(SemanticVersion other)
    {
        int c = Major.CompareTo(other.Major);
        if (c == 0)
        {
            c = Minor.CompareTo(other.Minor);
        }
        if (c == 0)
        {
            c = Patch.CompareTo(other.Patch);
        }
        return c != 0 ? c : Revision.CompareTo(other.Revision);
    }

    /// <inheritdoc />
    public bool Equals(SemanticVersion other) => CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    /// <inheritdoc />
    /// <remarks>与 <see cref="Equals(SemanticVersion)" /> 一致:不含构建元数据。预发布后缀语法上已排除了前导零,
    /// 优先级相同的两个后缀逐字相同,所以按序数取哈希即可。</remarks>
    public override int GetHashCode() =>
        HashCode.Combine(Major, Minor, Patch, Revision, string.GetHashCode(PreRelease, StringComparison.Ordinal));

    /// <summary>规范写法:不带 <c>v</c>,三段(第四段非 0 时四段),再接 <c>-预发布</c> 与 <c>+构建元数据</c>。</summary>
    public override string ToString()
    {
        string core = Revision == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}.{Revision}";
        string text = IsPreRelease ? $"{core}-{PreRelease}" : core;
        return BuildMetadata.Length > 0 ? $"{text}+{BuildMetadata}" : text;
    }

    /// <summary>相等(优先级相同)。</summary>
    public static bool operator ==(SemanticVersion left, SemanticVersion right) => left.Equals(right);

    /// <summary>不等。</summary>
    public static bool operator !=(SemanticVersion left, SemanticVersion right) => !left.Equals(right);

    /// <summary>更新。</summary>
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    /// <summary>更旧。</summary>
    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    /// <summary>不旧于。</summary>
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    /// <summary>不新于。</summary>
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    /// <summary>数字段:只认 ASCII 数字(不认正负号、空白与别的书写系统的数字),须在 int 范围内。</summary>
    private static bool TryParseNumber(ReadOnlySpan<char> part, out int value)
    {
        value = 0;
        if (part.IsEmpty)
        {
            return false;
        }
        foreach (char ch in part)
        {
            if (!char.IsAsciiDigit(ch))
            {
                return false;
            }
            int digit = ch - '0';
            if (value > (int.MaxValue - digit) / 10)
            {
                return false;
            }
            value = value * 10 + digit;
        }
        return true;
    }

    /// <summary>点分标识符:非空、只含 <c>[0-9A-Za-z-]</c>;预发布里的纯数字标识符不许有前导零。</summary>
    private static bool AreValidIdentifiers(ReadOnlySpan<char> identifiers, bool forbidLeadingZeros)
    {
        if (identifiers.IsEmpty)
        {
            return false;
        }
        foreach (Range range in identifiers.Split('.'))
        {
            ReadOnlySpan<char> identifier = identifiers[range];
            if (identifier.IsEmpty)
            {
                return false;
            }
            foreach (char ch in identifier)
            {
                if (!char.IsAsciiLetterOrDigit(ch) && ch != '-')
                {
                    return false;
                }
            }
            if (forbidLeadingZeros && identifier.Length > 1 && identifier[0] == '0' && IsNumeric(identifier))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsNumeric(ReadOnlySpan<char> identifier)
    {
        foreach (char ch in identifier)
        {
            if (!char.IsAsciiDigit(ch))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>SemVer 2.0 §11.4:正式版最高;逐个标识符比;前面都相同时多的更高。</summary>
    private static int ComparePreRelease(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            // 正式版(空)高于任何预发布;两边都空时相等。
            return Math.Sign(right.Length.CompareTo(left.Length));
        }
        ReadOnlySpan<char> a = left;
        ReadOnlySpan<char> b = right;
        var leftParts = a.Split('.');
        var rightParts = b.Split('.');
        while (true)
        {
            bool hasLeft = leftParts.MoveNext();
            bool hasRight = rightParts.MoveNext();
            if (!hasLeft || !hasRight)
            {
                return hasLeft == hasRight ? 0 : hasLeft ? 1 : -1;
            }
            int c = CompareIdentifier(a[leftParts.Current], b[rightParts.Current]);
            if (c != 0)
            {
                return c;
            }
        }
    }

    /// <summary>
    /// 单个标识符:纯数字按数值(没有前导零,所以先比长度、再按序数比,任意长度都不会溢出),
    /// 纯数字低于含字母的,含字母的按 ASCII 序数比。
    /// </summary>
    private static int CompareIdentifier(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        bool leftNumeric = IsNumeric(left);
        bool rightNumeric = IsNumeric(right);
        if (leftNumeric && rightNumeric)
        {
            int byLength = left.Length.CompareTo(right.Length);
            return byLength != 0 ? byLength : Math.Sign(left.SequenceCompareTo(right));
        }
        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }
        return Math.Sign(left.SequenceCompareTo(right));
    }

    private sealed class TextPrecedenceComparer : IComparer<string?>
    {
        public int Compare(string? x, string? y)
        {
            if (x is null || y is null)
            {
                return x is null ? (y is null ? 0 : -1) : 1;
            }
            bool leftValid = TryParse(x, out SemanticVersion left);
            bool rightValid = TryParse(y, out SemanticVersion right);
            if (leftValid != rightValid)
            {
                return leftValid ? 1 : -1;
            }
            int c = leftValid ? left.CompareTo(right) : 0;
            return c != 0 ? c : Math.Sign(string.CompareOrdinal(x, y));
        }
    }
}
