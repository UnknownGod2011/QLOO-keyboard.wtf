namespace KeyboardWtf.Helpers;

using System.Globalization;
using KeyboardWtf.Models;

public static class CaseTransformer
{
    public static string Apply(string text, CaseTransform transform) => transform switch
    {
        CaseTransform.Upper => text?.ToUpper(CultureInfo.CurrentCulture) ?? string.Empty,
        CaseTransform.Lower => text?.ToLower(CultureInfo.CurrentCulture) ?? string.Empty,
        CaseTransform.Title => string.IsNullOrEmpty(text)
            ? string.Empty
            : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.ToLower(CultureInfo.CurrentCulture)),
        _ => text ?? string.Empty
    };
}
