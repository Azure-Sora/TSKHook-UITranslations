using System.Text.RegularExpressions;

namespace TSKHook.Workbench;

// Deliberately limited to observed tower floor names. Never generalize skill numbers or IDs.
public static class NumberedNames
{
    public const string Token = "{floor}";
    private static readonly Regex Tower = new(@"\A(?<label>[^0-9{}<>\r\n]{1,60}塔)(?<number>[0-9]{1,4})F\z", RegexOptions.CultureInvariant);
    private static readonly Regex Numbers = new(@"[0-9]+", RegexOptions.CultureInvariant);
    public static string? Template(TranslationEntry row)
    {
        if (row.Field != "stage_name" || row.Protected || UiJsonTranslator.Sha256(row.Source) != row.SourceHash) return null;
        var match = Tower.Match(row.Source);
        return match.Success ? match.Groups["label"].Value + Token + "F" : null;
    }
    public static string? TranslationTemplate(TranslationEntry row)
    {
        if (row.Translation.Length == 0) return "";
        var matches = Numbers.Matches(row.Translation);
        var number = Tower.Match(row.Source).Groups["number"].Value;
        if (matches.Count != 1 || matches[0].Value != number || row.Translation.Contains('{') || row.Translation.Contains('}')) return null;
        var match = matches[0];
        return row.Translation[..match.Index] + Token + row.Translation[(match.Index + match.Length)..];
    }
    public static string Expand(string template, TranslationEntry row)
    {
        if (Template(row) == null || template.Split(Token).Length != 2 || Numbers.IsMatch(template) ||
            template.Replace(Token, "").IndexOfAny(['{', '}']) >= 0)
            throw new InvalidDataException("楼层模板必须保留且仅包含一个 {floor}，不能填写固定数字或其他占位符。");
        return template.Replace(Token, Tower.Match(row.Source).Groups["number"].Value, StringComparison.Ordinal);
    }
    public static string FamilyKey(TranslationEntry row) => JsonData.Write(new[] { row.Category, row.Field, Template(row) });
}
