using System.Text.RegularExpressions;

namespace TSKHook.Workbench;

public static class ValidationRules
{
    public static List<ValidationIssue> Check(TranslationEntry row)
    {
        var issues = new List<ValidationIssue>();
        if (UiJsonTranslator.Sha256(row.Source) != row.SourceHash)
            issues.Add(new(row.Key, "sourceHash", "历史原文与哈希不一致，需重新捕获；保留旧匹配标识", false));
        if (string.IsNullOrWhiteSpace(row.Translation)) return issues;
        if (!Tokens(row.Source, "<[^>]+>").SequenceEqual(Tokens(row.Translation, "<[^>]+>")))
            issues.Add(new(row.Key, "tags", "富文本标签发生变化", true));
        const string placeholders = @"\{[^{}]+\}|%[0-9$.*+\-]*[diuoxXfFeEgGaAcspn](?![A-Za-z])";
        if (!Tokens(row.Source, placeholders).SequenceEqual(Tokens(row.Translation, placeholders)))
            issues.Add(new(row.Key, "placeholders", "占位符发生变化", true));
        const string numbers = @"\d+(?:[.,]\d+)?";
        if (!Tokens(row.Source, numbers).Select(NormalizeDigits).Order(StringComparer.Ordinal).SequenceEqual(Tokens(row.Translation, numbers).Select(NormalizeDigits).Order(StringComparer.Ordinal)))
            issues.Add(new(row.Key, "numbers", "数值不同，需人工核对", false));
        if (row.Source.Count(c => c == '\n') != row.Translation.Count(c => c == '\n'))
            issues.Add(new(row.Key, "newlines", "换行数不同，需人工核对", false));
        if (row.Protected && row.Source != row.Translation)
            issues.Add(new(row.Key, "protected", "此字段参与游戏逻辑，必须保持原文", true));
        return issues;
    }
    private static string NormalizeDigits(string value) => new(value.Select(c => c is >= '０' and <= '９' ? (char)(c - '０' + '0') : c).ToArray());

    private static IEnumerable<string> Tokens(string value, string pattern) =>
        Regex.Matches(value, pattern, RegexOptions.None, TimeSpan.FromSeconds(1)).Select(x => x.Value);
}
