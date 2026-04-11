using Clipthrough.Localization;
namespace Clipthrough.Models;
public sealed class ContentTypeOption
{
    public ContentTypeOption(ContentType? value)
    {
        Value = value;
    }
    public ContentType? Value { get; }
    public string Label => AppText.GetFilterContentTypeLabel(Value);
    public override string ToString() => Label;
}
