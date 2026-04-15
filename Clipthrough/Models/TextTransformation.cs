namespace Clipthrough.Models;

public enum TextTransformation
{
    None,
    UpperCase,
    LowerCase,
    TitleCase,
    SentenceCase,
    UpperCamelCase,
    LowerCamelCase,
    FromCamelCase,
    TrimWhitespace,
    CollapseWhitespace,
    TabsToSpaces,
    SpacesToTabs,
    NormalizeEol,
    LinesToJsonArray,
    JoinWithDelimiter,
    SortLines,
    ReverseLines,
    RemoveEmptyLines,
    RemoveDuplicateLines,
}
