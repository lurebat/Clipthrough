using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using HtmlAgilityPack;

namespace AvRichTextBox;

internal static class HtmlConversions
{
	private static readonly Regex Rgba = new Regex("^\\s*rgba?\\(\\s*(?<r>\\d{1,3})\\s*,\\s*(?<g>\\d{1,3})\\s*,\\s*(?<b>\\d{1,3})\\s*(?:,\\s*(?<a>[-+]?\\d*\\.?\\d+)\\s*)?\\)\\s*;?\\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static ColorConverter colConverter = new ColorConverter();

	internal static HtmlDocument GetHtmlFromFlowDocument(FlowDocument fdoc)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Expected O, but got Unknown
		//IL_0048: Unknown result type (might be due to invalid IL or missing references)
		//IL_004f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0055: Unknown result type (might be due to invalid IL or missing references)
		//IL_0062: Unknown result type (might be due to invalid IL or missing references)
		//IL_0067: Unknown result type (might be due to invalid IL or missing references)
		HtmlDocument val = new HtmlDocument();
		HtmlNode val2 = val.CreateElement("html");
		HtmlNode val3 = val.CreateElement("head");
		HtmlNode val4 = val.CreateElement("body");
		val2.AppendChild(val3);
		val2.AppendChild(val4);
		val.DocumentNode.AppendChild(val2);
		if (fdoc.PagePadding != default(Thickness))
		{
			Thickness pagePadding = fdoc.PagePadding;
			string text = $"padding:{((Thickness)(ref pagePadding)).Top}px {((Thickness)(ref pagePadding)).Right}px {((Thickness)(ref pagePadding)).Bottom}px {((Thickness)(ref pagePadding)).Left}px;";
			val4.SetAttributeValue("style", text);
		}
		foreach (Block block in fdoc.Blocks)
		{
			if (!(block is Paragraph p))
			{
				if (block is Table t)
				{
					HtmlNode tableNode = GetTableNode(t, val);
					val4.AppendChild(tableNode);
				}
			}
			else
			{
				HtmlNode paragraphNode = GetParagraphNode(p, val);
				val4.AppendChild(paragraphNode);
			}
		}
		return val;
	}

	private static HtmlNode GetTableNode(Table t, HtmlDocument hdoc)
	{
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_002c: Unknown result type (might be due to invalid IL or missing references)
		//IL_002f: Invalid comparison between Unknown and I4
		//IL_0031: Unknown result type (might be due to invalid IL or missing references)
		//IL_0034: Invalid comparison between Unknown and I4
		//IL_0047: Unknown result type (might be due to invalid IL or missing references)
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_005c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0142: Unknown result type (might be due to invalid IL or missing references)
		//IL_0147: Unknown result type (might be due to invalid IL or missing references)
		//IL_0257: Unknown result type (might be due to invalid IL or missing references)
		//IL_025c: Unknown result type (might be due to invalid IL or missing references)
		//IL_025e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0261: Unknown result type (might be due to invalid IL or missing references)
		//IL_0273: Expected I4, but got Unknown
		//IL_02b6: Unknown result type (might be due to invalid IL or missing references)
		//IL_02bb: Unknown result type (might be due to invalid IL or missing references)
		//IL_02d9: Unknown result type (might be due to invalid IL or missing references)
		//IL_02de: Unknown result type (might be due to invalid IL or missing references)
		//IL_02fc: Unknown result type (might be due to invalid IL or missing references)
		//IL_0301: Unknown result type (might be due to invalid IL or missing references)
		//IL_031f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0324: Unknown result type (might be due to invalid IL or missing references)
		//IL_03db: Unknown result type (might be due to invalid IL or missing references)
		//IL_03e0: Unknown result type (might be due to invalid IL or missing references)
		//IL_03fe: Unknown result type (might be due to invalid IL or missing references)
		//IL_0403: Unknown result type (might be due to invalid IL or missing references)
		//IL_0421: Unknown result type (might be due to invalid IL or missing references)
		//IL_0426: Unknown result type (might be due to invalid IL or missing references)
		//IL_0444: Unknown result type (might be due to invalid IL or missing references)
		//IL_0449: Unknown result type (might be due to invalid IL or missing references)
		int count = ((AvaloniaList<ColumnDefinition>)(object)t.ColDefs).Count;
		double value = Math.Round(100.0 / (double)count);
		string text = "margin: 0;";
		HorizontalAlignment tableAlignment = t.TableAlignment;
		if ((int)tableAlignment != 2)
		{
			if ((int)tableAlignment == 3)
			{
				text = "margin-left: auto; margin-right: 0;";
			}
		}
		else
		{
			text = "margin: 0 auto;";
		}
		Thickness val = t.BorderThickness;
		double left = ((Thickness)(ref val)).Left;
		string value2 = ColorToCss(t.BorderBrush.Color);
		HtmlNode val2 = hdoc.CreateElement("table");
		string text2 = "border-spacing: 0;border-collapse: collapse;" + $"border: {left}px solid {value2};" + $"width: {t.Width}px;" + "box-sizing:border-box;" + text + "table-layout: fixed;";
		val2.SetAttributeValue("style", text2);
		HtmlNode val3 = hdoc.CreateElement("colgroup");
		Enumerator<ColumnDefinition> enumerator = ((AvaloniaList<ColumnDefinition>)(object)t.ColDefs).GetEnumerator();
		try
		{
			while (enumerator.MoveNext())
			{
				_ = enumerator.Current;
				HtmlNode val4 = hdoc.CreateElement("col");
				string text3 = $"width: {value}%;";
				val4.SetAttributeValue("style", text3);
				val3.ChildNodes.Add(val4);
			}
		}
		finally
		{
			((IDisposable)enumerator/*cast due to .constrained prefix*/).Dispose();
		}
		val2.ChildNodes.Add(val3);
		for (int rowno = 0; rowno < ((AvaloniaList<RowDefinition>)(object)t.RowDefs).Count; rowno++)
		{
			HtmlNode val5 = hdoc.CreateElement("tr");
			val5.SetAttributeValue("style", "height: 40px;");
			int colno;
			for (colno = 0; colno < ((AvaloniaList<ColumnDefinition>)(object)t.ColDefs).Count; colno++)
			{
				Cell cell = t.Cells.FirstOrDefault((Cell c) => c.RowNo == rowno && c.ColNo == colno);
				if (cell != null)
				{
					HtmlNode val6 = hdoc.CreateElement("td");
					VerticalAlignment cellVerticalAlignment = cell.CellVerticalAlignment;
					string value3 = (cellVerticalAlignment - 1) switch
					{
						0 => "top", 
						1 => "center", 
						2 => "bottom", 
						_ => "center", 
					};
					DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(118, 11);
					defaultInterpolatedStringHandler.AppendLiteral("border-width: ");
					val = cell.BorderThickness;
					defaultInterpolatedStringHandler.AppendFormatted(((Thickness)(ref val)).Top);
					defaultInterpolatedStringHandler.AppendLiteral("px ");
					val = cell.BorderThickness;
					defaultInterpolatedStringHandler.AppendFormatted(((Thickness)(ref val)).Right);
					defaultInterpolatedStringHandler.AppendLiteral("px ");
					val = cell.BorderThickness;
					defaultInterpolatedStringHandler.AppendFormatted(((Thickness)(ref val)).Bottom);
					defaultInterpolatedStringHandler.AppendLiteral("px ");
					val = cell.BorderThickness;
					defaultInterpolatedStringHandler.AppendFormatted(((Thickness)(ref val)).Left);
					defaultInterpolatedStringHandler.AppendLiteral("px;");
					defaultInterpolatedStringHandler.AppendLiteral("border-style: solid;");
					defaultInterpolatedStringHandler.AppendLiteral("border-color: ");
					defaultInterpolatedStringHandler.AppendFormatted(ToCssColor((IBrush?)(object)cell.BorderBrush, (IBrush?)(object)Brushes.Black));
					defaultInterpolatedStringHandler.AppendLiteral(";");
					defaultInterpolatedStringHandler.AppendLiteral("vertical-align: ");
					defaultInterpolatedStringHandler.AppendFormatted(value3);
					defaultInterpolatedStringHandler.AppendLiteral(";");
					defaultInterpolatedStringHandler.AppendLiteral("background-color: ");
					defaultInterpolatedStringHandler.AppendFormatted(ToCssColor((IBrush?)(object)cell.CellBackground, (IBrush?)(object)Brushes.Transparent));
					defaultInterpolatedStringHandler.AppendLiteral(";");
					defaultInterpolatedStringHandler.AppendLiteral("padding: ");
					val = cell.Padding;
					defaultInterpolatedStringHandler.AppendFormatted(((Thickness)(ref val)).Top);
					defaultInterpolatedStringHandler.AppendLiteral("px ");
					val = cell.Padding;
					defaultInterpolatedStringHandler.AppendFormatted(((Thickness)(ref val)).Right);
					defaultInterpolatedStringHandler.AppendLiteral("px ");
					val = cell.Padding;
					defaultInterpolatedStringHandler.AppendFormatted(((Thickness)(ref val)).Bottom);
					defaultInterpolatedStringHandler.AppendLiteral("px ");
					val = cell.Padding;
					defaultInterpolatedStringHandler.AppendFormatted(((Thickness)(ref val)).Left);
					defaultInterpolatedStringHandler.AppendLiteral("px;");
					string text4 = defaultInterpolatedStringHandler.ToStringAndClear();
					val6.SetAttributeValue("style", text4);
					HtmlAttribute val7 = hdoc.CreateAttribute("colspan", cell.ColSpan.ToString());
					HtmlAttribute val8 = hdoc.CreateAttribute("rowspan", cell.RowSpan.ToString());
					val6.Attributes.Add(val7);
					val6.Attributes.Add(val8);
					if (cell.CellContent is Paragraph p)
					{
						val6.ChildNodes.Add(GetParagraphNode(p, hdoc));
					}
					val5.ChildNodes.Add(val6);
				}
			}
			val2.ChildNodes.Add(val5);
		}
		return val2;
	}

	private static HtmlNode GetParagraphNode(Paragraph p, HtmlDocument hdoc)
	{
		HtmlNode val = hdoc.CreateElement("p");
		foreach (IEditable inline in p.Inlines)
		{
			HtmlNode val2 = hdoc.CreateElement("span");
			if (!(inline is EditableRun editableRun))
			{
				if (!(inline is EditableLineBreak))
				{
					if (inline is EditableInlineUIContainer editableInlineUIContainer)
					{
						Control child = ((InlineUIContainer)editableInlineUIContainer).Child;
						Image val3 = (Image)(object)((child is Image) ? child : null);
						if (val3 != null)
						{
							IImage source = val3.Source;
							Bitmap val4 = (Bitmap)(object)((source is Bitmap) ? source : null);
							if (val4 != null)
							{
								using MemoryStream memoryStream = new MemoryStream();
								val4.Save((Stream)memoryStream, (int?)null);
								string text = Convert.ToBase64String(memoryStream.ToArray());
								HtmlNode val5 = hdoc.CreateElement("img");
								val5.SetAttributeValue("src", "data:image/png;base64," + text);
								val5.SetAttributeValue("width", ((Layoutable)val3).Width.ToString());
								val5.SetAttributeValue("height", ((Layoutable)val3).Height.ToString());
								val.AppendChild(val5);
							}
						}
					}
				}
				else
				{
					val2 = hdoc.CreateElement("br");
				}
			}
			else
			{
				val2.InnerHtml = WebUtility.HtmlEncode(((Run)editableRun).Text ?? "");
				val2.SetAttributeValue("style", GetInlineStyle(editableRun));
			}
			val.AppendChild(val2);
		}
		val.SetAttributeValue("style", GetParStyle(p));
		return val;
	}

	private static string GetParStyle(Paragraph p)
	{
		//IL_0053: Unknown result type (might be due to invalid IL or missing references)
		//IL_0058: Unknown result type (might be due to invalid IL or missing references)
		//IL_005a: Unknown result type (might be due to invalid IL or missing references)
		//IL_007d: Expected I4, but got Unknown
		//IL_00b6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ec: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f1: Unknown result type (might be due to invalid IL or missing references)
		//IL_010e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0113: Unknown result type (might be due to invalid IL or missing references)
		//IL_0130: Unknown result type (might be due to invalid IL or missing references)
		//IL_0135: Unknown result type (might be due to invalid IL or missing references)
		//IL_0152: Unknown result type (might be due to invalid IL or missing references)
		//IL_0157: Unknown result type (might be due to invalid IL or missing references)
		//IL_0222: Unknown result type (might be due to invalid IL or missing references)
		//IL_0229: Unknown result type (might be due to invalid IL or missing references)
		//IL_022f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0258: Unknown result type (might be due to invalid IL or missing references)
		//IL_025d: Unknown result type (might be due to invalid IL or missing references)
		//IL_027a: Unknown result type (might be due to invalid IL or missing references)
		//IL_027f: Unknown result type (might be due to invalid IL or missing references)
		//IL_029c: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_02be: Unknown result type (might be due to invalid IL or missing references)
		//IL_02c3: Unknown result type (might be due to invalid IL or missing references)
		StringBuilder stringBuilder = new StringBuilder();
		if (p.LineSpacing > 0.0)
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder3 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(15, 1, stringBuilder2);
			handler.AppendLiteral("line-height:");
			handler.AppendFormatted(p.LineHeight);
			handler.AppendLiteral("px;");
			stringBuilder3.Append(ref handler);
		}
		TextAlignment textAlignment = p.TextAlignment;
		switch ((int)textAlignment)
		{
		case 1:
			stringBuilder.Append("text-align:center;");
			break;
		case 2:
			stringBuilder.Append("text-align:right;");
			break;
		case 0:
			stringBuilder.Append("text-align:left;");
			break;
		case 6:
			stringBuilder.Append("text-align:justify;");
			break;
		}
		Thickness margin = p.Margin;
		Thickness val = default(Thickness);
		if (margin != val)
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder4 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(19, 4, stringBuilder2);
			handler.AppendLiteral("margin:");
			val = p.Margin;
			handler.AppendFormatted(((Thickness)(ref val)).Top);
			handler.AppendLiteral("px ");
			val = p.Margin;
			handler.AppendFormatted(((Thickness)(ref val)).Right);
			handler.AppendLiteral("px ");
			val = p.Margin;
			handler.AppendFormatted(((Thickness)(ref val)).Bottom);
			handler.AppendLiteral("px ");
			val = p.Margin;
			handler.AppendFormatted(((Thickness)(ref val)).Left);
			handler.AppendLiteral("px;");
			stringBuilder4.Append(ref handler);
		}
		string text = ToCssColor((IBrush?)(object)p.Background, (IBrush?)(object)Brushes.Transparent);
		if (text != "")
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder5 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(18, 1, stringBuilder2);
			handler.AppendLiteral("background-color:");
			handler.AppendFormatted(text);
			handler.AppendLiteral(";");
			stringBuilder5.Append(ref handler);
		}
		string text2 = ToCssColor((IBrush?)(object)p.BorderBrush, (IBrush?)(object)Brushes.Black);
		if (text2 != "")
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder6 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(14, 1, stringBuilder2);
			handler.AppendLiteral("border-color:");
			handler.AppendFormatted(text2);
			handler.AppendLiteral(";");
			stringBuilder6.Append(ref handler);
		}
		Thickness borderThickness = p.BorderThickness;
		val = default(Thickness);
		if (borderThickness != val)
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder7 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(44, 4, stringBuilder2);
			handler.AppendLiteral("border-style:solid;border-width:");
			val = p.BorderThickness;
			handler.AppendFormatted(((Thickness)(ref val)).Top);
			handler.AppendLiteral("px ");
			val = p.BorderThickness;
			handler.AppendFormatted(((Thickness)(ref val)).Right);
			handler.AppendLiteral("px ");
			val = p.BorderThickness;
			handler.AppendFormatted(((Thickness)(ref val)).Bottom);
			handler.AppendLiteral("px ");
			val = p.BorderThickness;
			handler.AppendFormatted(((Thickness)(ref val)).Left);
			handler.AppendLiteral("px;");
			stringBuilder7.Append(ref handler);
		}
		return stringBuilder.ToString();
	}

	private static string GetInlineStyle(EditableRun run)
	{
		//IL_0054: Unknown result type (might be due to invalid IL or missing references)
		//IL_005e: Invalid comparison between Unknown and I4
		//IL_006d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0073: Invalid comparison between Unknown and I4
		//IL_01c4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ca: Invalid comparison between Unknown and I4
		//IL_016e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0173: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d9: Unknown result type (might be due to invalid IL or missing references)
		//IL_01df: Invalid comparison between Unknown and I4
		//IL_017e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0183: Unknown result type (might be due to invalid IL or missing references)
		//IL_0185: Unknown result type (might be due to invalid IL or missing references)
		//IL_0189: Unknown result type (might be due to invalid IL or missing references)
		//IL_018c: Invalid comparison between Unknown and I4
		StringBuilder stringBuilder = new StringBuilder();
		if (!string.IsNullOrEmpty(((object)((TextElement)run).FontFamily).ToString()))
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder3 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(13, 1, stringBuilder2);
			handler.AppendLiteral("font-family:");
			handler.AppendFormatted<FontFamily>(((TextElement)run).FontFamily);
			handler.AppendLiteral(";");
			stringBuilder3.Append(ref handler);
		}
		if ((int)((TextElement)run).FontWeight == 700)
		{
			stringBuilder.Append("font-weight:bold;");
		}
		if ((int)((TextElement)run).FontStyle == 1)
		{
			stringBuilder.Append("font-style:italic;");
		}
		if (((TextElement)run).FontSize > 0.0)
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder4 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(13, 1, stringBuilder2);
			handler.AppendLiteral("font-size:");
			handler.AppendFormatted(((TextElement)run).FontSize);
			handler.AppendLiteral("px;");
			stringBuilder4.Append(ref handler);
		}
		string text = ToCssColor(((TextElement)run).Foreground, (IBrush?)(object)Brushes.Black);
		if (text != null)
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder5 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder2);
			handler.AppendLiteral("color:");
			handler.AppendFormatted(text);
			handler.AppendLiteral(";");
			stringBuilder5.Append(ref handler);
		}
		string text2 = ToCssColor(((TextElement)run).Background, (IBrush?)(object)Brushes.Transparent);
		if (text2 != null)
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder6 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(18, 1, stringBuilder2);
			handler.AppendLiteral("background-color:");
			handler.AppendFormatted(text2);
			handler.AppendLiteral(";");
			stringBuilder6.Append(ref handler);
		}
		if (((Inline)run).TextDecorations != null)
		{
			Enumerator<TextDecoration> enumerator = ((AvaloniaList<TextDecoration>)(object)((Inline)run).TextDecorations).GetEnumerator();
			try
			{
				while (enumerator.MoveNext())
				{
					TextDecorationLocation location = enumerator.Current.Location;
					if ((int)location != 0)
					{
						if ((int)location == 2)
						{
							stringBuilder.Append("text-decoration:line-through;");
						}
					}
					else
					{
						stringBuilder.Append("text-decoration:underline;");
					}
				}
			}
			finally
			{
				((IDisposable)enumerator/*cast due to .constrained prefix*/).Dispose();
			}
		}
		if ((int)((Inline)run).BaselineAlignment == 7)
		{
			stringBuilder.Append("vertical-align: super;");
		}
		if ((int)((Inline)run).BaselineAlignment == 6)
		{
			stringBuilder.Append("vertical-align: sub;");
		}
		return stringBuilder.ToString();
	}

	private static string ToCssColor(IBrush? brush, IBrush? defaultBrush)
	{
		//IL_000b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_004d: Unknown result type (might be due to invalid IL or missing references)
		SolidColorBrush val = (SolidColorBrush)(object)((brush is SolidColorBrush) ? brush : null);
		if (val != null)
		{
			return ColorToCss(val.Color);
		}
		ImmutableSolidColorBrush val2 = (ImmutableSolidColorBrush)(object)((brush is ImmutableSolidColorBrush) ? brush : null);
		if (val2 != null)
		{
			return ColorToCss(val2.Color);
		}
		SolidColorBrush val3 = (SolidColorBrush)(object)((defaultBrush is SolidColorBrush) ? defaultBrush : null);
		if (val3 != null)
		{
			return ColorToCss(val3.Color);
		}
		ImmutableSolidColorBrush val4 = (ImmutableSolidColorBrush)(object)((defaultBrush is ImmutableSolidColorBrush) ? defaultBrush : null);
		if (val4 != null)
		{
			return ColorToCss(val4.Color);
		}
		return "transparent";
	}

	private static string ColorToCss(Color c)
	{
		return $"rgba({((Color)(ref c)).R},{((Color)(ref c)).G},{((Color)(ref c)).B},{(double)(int)((Color)(ref c)).A / 255.0})";
	}

	internal static void GetFlowDocumentFromHtml(HtmlDocument hdoc, FlowDocument fdoc)
	{
		//IL_00b7: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			HtmlNode val = hdoc.DocumentNode.SelectSingleNode("//body");
			if (val == null)
			{
				return;
			}
			foreach (KeyValuePair<string, string> item in ParseStyleAttribute(val.GetAttributeValue("style", "")))
			{
				if (item.Key == "padding")
				{
					List<int> list = (from text in item.Value.Split(' ')
						select int.TryParse(text.Replace("px", ""), out var result) ? result : 0).ToList();
					if (list.Count == 4)
					{
						fdoc.PagePadding = new Thickness((double)list[3], (double)list[0], (double)list[1], (double)list[2]);
					}
				}
			}
			foreach (HtmlNode item2 in (IEnumerable<HtmlNode>)val.ChildNodes)
			{
				string name = item2.Name;
				if (!(name == "p"))
				{
					if (name == "table")
					{
						Table tableFromNode = GetTableFromNode(item2, fdoc);
						fdoc.Blocks.Add(tableFromNode);
					}
				}
				else
				{
					Paragraph paragraphFromNode = GetParagraphFromNode(item2, fdoc);
					fdoc.Blocks.Add(paragraphFromNode);
				}
			}
		}
		catch (Exception)
		{
		}
	}

	private static Table GetTableFromNode(HtmlNode tableNode, FlowDocument fdoc)
	{
		//IL_0071: Unknown result type (might be due to invalid IL or missing references)
		//IL_009f: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d5: Expected O, but got Unknown
		//IL_010d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0117: Expected O, but got Unknown
		//IL_0242: Unknown result type (might be due to invalid IL or missing references)
		//IL_029b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0372: Unknown result type (might be due to invalid IL or missing references)
		//IL_0349: Unknown result type (might be due to invalid IL or missing references)
		Table table = new Table(fdoc);
		int num = 0;
		HtmlNodeCollection val = tableNode.SelectNodes("./colgroup/col");
		if (val != null && val.Count > 0)
		{
			num = val.Count;
		}
		double num2 = 100.0;
		Dictionary<string, string> dictionary = ParseStyleAttribute(tableNode.GetAttributeValue("style", ""));
		ISolidColorBrush cellBorderBrush = (ISolidColorBrush)(object)Brushes.Black;
		Thickness cellBorderThickness = default(Thickness);
		((Thickness)(ref cellBorderThickness))..ctor(1.0);
		GetBordersFromCssStyle(dictionary, ref cellBorderBrush, ref cellBorderThickness);
		table.BorderBrush = cellBorderBrush;
		table.BorderThickness = cellBorderThickness;
		if (dictionary.TryGetValue("width", out string value))
		{
			num2 = double.Parse(value.Replace("px", ""));
		}
		HorizontalAlignment tableHorizAlignment = (HorizontalAlignment)1;
		GetAlignmentFromCssStyle(dictionary, ref tableHorizAlignment);
		table.TableAlignment = tableHorizAlignment;
		table.Width = num2;
		double num3 = num2 / (double)num;
		for (int i = 0; i < num; i++)
		{
			((AvaloniaList<ColumnDefinition>)(object)table.ColDefs).Add(new ColumnDefinition(num3, (GridUnitType)1));
		}
		List<HtmlNode> list = ((IEnumerable<HtmlNode>)tableNode.SelectNodes("./tr|./tbody/tr|./thead/tr|./tfoot/tr"))?.ToList() ?? new List<HtmlNode>();
		for (int j = 0; j < list.Count; j++)
		{
			((AvaloniaList<RowDefinition>)(object)table.RowDefs).Add(new RowDefinition());
		}
		int[] array = new int[num];
		Thickness cellBorderThickness2 = default(Thickness);
		for (int k = 0; k < list.Count; k++)
		{
			List<HtmlNode> obj = ((IEnumerable<HtmlNode>)list[k].SelectNodes("./td|./th"))?.ToList() ?? new List<HtmlNode>();
			int l = 0;
			int num4 = 1;
			int num5 = 1;
			foreach (HtmlNode item in obj)
			{
				for (; l < num && k < array[l]; l++)
				{
				}
				foreach (HtmlAttribute item2 in (IEnumerable<HtmlAttribute>)item.Attributes)
				{
					if (item2.Name == "colspan")
					{
						num4 = Math.Max(1, int.Parse(item2.Value));
					}
					if (item2.Name == "rowspan")
					{
						num5 = Math.Max(1, int.Parse(item2.Value));
					}
				}
				Cell cell = new Cell(table)
				{
					RowNo = k,
					ColNo = l,
					ColSpan = num4,
					RowSpan = num5,
					BorderThickness = new Thickness(1.0),
					BorderBrush = (ISolidColorBrush)(object)Brushes.Black
				};
				Dictionary<string, string> dictionary2 = ParseStyleAttribute(item.GetAttributeValue("style", ""));
				ISolidColorBrush cellBorderBrush2 = (ISolidColorBrush)(object)Brushes.Black;
				((Thickness)(ref cellBorderThickness2))..ctor(1.0);
				GetBordersFromCssStyle(dictionary2, ref cellBorderBrush2, ref cellBorderThickness2);
				cell.BorderBrush = cellBorderBrush2;
				cell.BorderThickness = cellBorderThickness2;
				if (dictionary2.TryGetValue("background-color", out string value2))
				{
					ISolidColorBrush val2 = (ISolidColorBrush)(object)ParseCssColor(value2);
					if (val2 != null)
					{
						cell.CellBackground = val2;
					}
				}
				if (dictionary2.TryGetValue("padding", out string value3))
				{
					if (value3.Contains(' '))
					{
						List<int> list2 = (from text in value3.Split(' ')
							select int.TryParse(text.Replace("px", ""), out var result) ? result : 0).ToList();
						if (list2.Count == 4)
						{
							cell.Padding = new Thickness((double)list2[3], (double)list2[0], (double)list2[1], (double)list2[2]);
						}
					}
					else
					{
						int num6 = int.Parse(value3.Replace("px", ""));
						cell.Padding = new Thickness((double)num6);
					}
				}
				if (dictionary2.TryGetValue("vertical-align", out string value4))
				{
					switch (value4)
					{
					case "top":
						cell.CellVerticalAlignment = (VerticalAlignment)1;
						break;
					case "center":
						cell.CellVerticalAlignment = (VerticalAlignment)2;
						break;
					case "bottom":
						cell.CellVerticalAlignment = (VerticalAlignment)3;
						break;
					}
				}
				HtmlNode val3 = ((IEnumerable<HtmlNode>)item.ChildNodes).FirstOrDefault((HtmlNode n) => (int)n.NodeType == 1);
				if (val3 != null)
				{
					Paragraph paragraphFromNode = GetParagraphFromNode(val3, fdoc);
					cell.CellContent = paragraphFromNode;
				}
				else
				{
					cell.CellContent = new Paragraph(fdoc);
				}
				table.Cells.Add(cell);
				for (int num7 = l; num7 < l + num4; num7++)
				{
					array[num7] += num5;
				}
				l += num4;
			}
		}
		return table;
	}

	private static void GetBordersFromCssStyle(Dictionary<string, string> parsedStyles, ref ISolidColorBrush cellBorderBrush, ref Thickness cellBorderThickness)
	{
		//IL_00a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ac: Unknown result type (might be due to invalid IL or missing references)
		//IL_008b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0090: Unknown result type (might be due to invalid IL or missing references)
		foreach (KeyValuePair<string, string> parsedStyle in parsedStyles)
		{
			switch (parsedStyle.Key.Trim().ToLowerInvariant())
			{
			case "border":
			{
				(Thickness?, ISolidColorBrush)? tuple = ParseBorderShorthand(parsedStyle.Value);
				if (tuple.HasValue)
				{
					var (val2, val3) = tuple.Value;
					if (val2.HasValue)
					{
						cellBorderThickness = val2.Value;
					}
					if (val3 != null)
					{
						cellBorderBrush = val3;
					}
				}
				break;
			}
			case "border-width":
				cellBorderThickness = GetBorderThickness(parsedStyle.Value);
				break;
			case "border-color":
			{
				SolidColorBrush val = ParseCssColor(parsedStyle.Value);
				if (val != null)
				{
					cellBorderBrush = (ISolidColorBrush)(object)val;
				}
				break;
			}
			}
		}
	}

	private static void GetAlignmentFromCssStyle(Dictionary<string, string> styles, ref HorizontalAlignment tableHorizAlignment)
	{
		styles.TryGetValue("margin-left", out string value);
		styles.TryGetValue("margin-right", out string value2);
		if (styles.TryGetValue("margin", out string value3))
		{
			string[] array = (from p in value3.Split(' ', StringSplitOptions.RemoveEmptyEntries)
				select p.Trim().ToLowerInvariant()).ToArray();
			if (array.Length == 1)
			{
				if (value == null)
				{
					value = array[0];
				}
				if (value2 == null)
				{
					value2 = array[0];
				}
			}
			else if (array.Length == 2)
			{
				if (value == null)
				{
					value = array[1];
				}
				if (value2 == null)
				{
					value2 = array[1];
				}
			}
			else if (array.Length == 3)
			{
				if (value == null)
				{
					value = array[1];
				}
				if (value2 == null)
				{
					value2 = array[1];
				}
			}
			else if (array.Length >= 4)
			{
				if (value2 == null)
				{
					value2 = array[1];
				}
				if (value == null)
				{
					value = array[3];
				}
			}
		}
		value = value?.Trim().ToLowerInvariant();
		value2 = value2?.Trim().ToLowerInvariant();
		bool flag = string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase);
		bool flag2 = string.Equals(value2, "auto", StringComparison.OrdinalIgnoreCase);
		if (flag && flag2)
		{
			tableHorizAlignment = (HorizontalAlignment)2;
		}
		else if (flag && !flag2)
		{
			tableHorizAlignment = (HorizontalAlignment)3;
		}
		else
		{
			tableHorizAlignment = (HorizontalAlignment)1;
		}
	}

	private static Paragraph GetParagraphFromNode(HtmlNode childNode, FlowDocument fdoc)
	{
		//IL_0401: Unknown result type (might be due to invalid IL or missing references)
		//IL_0408: Expected O, but got Unknown
		//IL_0408: Unknown result type (might be due to invalid IL or missing references)
		//IL_040d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0415: Unknown result type (might be due to invalid IL or missing references)
		//IL_041e: Expected O, but got Unknown
		//IL_0602: Unknown result type (might be due to invalid IL or missing references)
		//IL_061a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0607: Unknown result type (might be due to invalid IL or missing references)
		//IL_06c9: Unknown result type (might be due to invalid IL or missing references)
		//IL_060c: Unknown result type (might be due to invalid IL or missing references)
		//IL_02ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_02b5: Expected O, but got Unknown
		//IL_01f3: Unknown result type (might be due to invalid IL or missing references)
		//IL_0611: Unknown result type (might be due to invalid IL or missing references)
		//IL_06a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_0715: Unknown result type (might be due to invalid IL or missing references)
		//IL_0209: Unknown result type (might be due to invalid IL or missing references)
		//IL_01fc: Unknown result type (might be due to invalid IL or missing references)
		//IL_0616: Unknown result type (might be due to invalid IL or missing references)
		//IL_0205: Unknown result type (might be due to invalid IL or missing references)
		Paragraph paragraph = new Paragraph(fdoc);
		foreach (HtmlNode item in ((IEnumerable<HtmlNode>)childNode.ChildNodes).Where(delegate(HtmlNode cn)
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			//IL_0006: Unknown result type (might be due to invalid IL or missing references)
			//IL_0007: Unknown result type (might be due to invalid IL or missing references)
			//IL_0009: Invalid comparison between Unknown and I4
			//IL_000b: Unknown result type (might be due to invalid IL or missing references)
			//IL_000d: Invalid comparison between Unknown and I4
			HtmlNodeType nodeType = cn.NodeType;
			return ((int)nodeType == 1 || (int)nodeType == 3) ? true : false;
		}))
		{
			switch (item.Name)
			{
			case "span":
			{
				EditableRun editableRun = new EditableRun();
				((Run)editableRun).Text = item.InnerText;
				EditableRun editableRun2 = editableRun;
				foreach (KeyValuePair<string, string> item2 in ParseStyleAttribute(item.GetAttributeValue("style", "")))
				{
					switch (item2.Key)
					{
					case "font-weight":
					{
						EditableRun editableRun3 = editableRun2;
						string value = item2.Value;
						FontWeight fontWeight = ((value == "bold") ? ((FontWeight)700) : ((!(value == "normal")) ? ((FontWeight)400) : ((FontWeight)400)));
						((TextElement)editableRun3).FontWeight = fontWeight;
						break;
					}
					case "font-style":
						((TextElement)editableRun2).FontStyle = (FontStyle)(item2.Value == "italic");
						break;
					case "font-family":
					{
						string text2 = item2.Value;
						if (text2.StartsWith("compositefont:", StringComparison.OrdinalIgnoreCase))
						{
							string value = text2;
							int num2 = "compositefont:".Length;
							text2 = value.Substring(num2, value.Length - num2);
						}
						int num3 = text2.IndexOf('#');
						if (num3 >= 0)
						{
							string value = text2;
							int num2 = num3 + 1;
							text2 = value.Substring(num2, value.Length - num2);
						}
						((TextElement)editableRun2).FontFamily = new FontFamily(text2.Trim());
						break;
					}
					case "font-size":
					{
						if (double.TryParse(item2.Value.Replace("px", ""), out var result3))
						{
							((TextElement)editableRun2).FontSize = result3;
						}
						break;
					}
					case "color":
					{
						SolidColorBrush val3 = ParseCssColor(item2.Value);
						if (val3 != null)
						{
							((TextElement)editableRun2).Foreground = (IBrush)(object)val3;
						}
						break;
					}
					case "background-color":
					{
						SolidColorBrush val2 = ParseCssColor(item2.Value);
						if (val2 != null)
						{
							((TextElement)editableRun2).Background = (IBrush)(object)val2;
						}
						break;
					}
					case "vertical-align":
					{
						string value = item2.Value;
						if (!(value == "super"))
						{
							if (value == "sub")
							{
								((Inline)editableRun2).BaselineAlignment = (BaselineAlignment)6;
							}
						}
						else
						{
							((Inline)editableRun2).BaselineAlignment = (BaselineAlignment)7;
						}
						break;
					}
					}
				}
				paragraph.Inlines.Add(editableRun2);
				break;
			}
			case "br":
				paragraph.Inlines.Add(new EditableLineBreak());
				break;
			case "img":
			{
				string attributeValue = item.GetAttributeValue("src", (string)null);
				if (string.IsNullOrEmpty(attributeValue) || !attributeValue.StartsWith("data:image"))
				{
					break;
				}
				int num = attributeValue.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
				if (num < 0)
				{
					break;
				}
				string text = attributeValue;
				int num2 = num + 7;
				using (MemoryStream memoryStream = new MemoryStream(Convert.FromBase64String(text.Substring(num2, text.Length - num2))))
				{
					Bitmap source = new Bitmap((Stream)memoryStream);
					Image val = new Image
					{
						Source = (IImage)(object)source,
						IsVisible = true
					};
					string attributeValue2 = item.GetAttributeValue("width", (string)null);
					if (attributeValue2 != null && double.TryParse(attributeValue2, out var result))
					{
						((Layoutable)val).Width = result;
					}
					string attributeValue3 = item.GetAttributeValue("height", (string)null);
					if (attributeValue3 != null && double.TryParse(attributeValue3, out var result2))
					{
						((Layoutable)val).Height = result2;
					}
					paragraph.Inlines.Add(new EditableInlineUIContainer((Control)(object)val));
				}
				break;
			}
			}
		}
		foreach (KeyValuePair<string, string> item3 in ParseStyleAttribute(childNode.GetAttributeValue("style", "")))
		{
			switch (item3.Key)
			{
			case "line-height":
				if (paragraph.Inlines.Count > 0)
				{
					double result4;
					double lineHeight = (double.TryParse(item3.Value.Replace("px", ""), out result4) ? result4 : 0.0);
					double maxFontSize = paragraph.Inlines.Max((IEditable ilh) => ilh.InlineHeight);
					paragraph.LineSpacing = LineHeightToLineSpacing(lineHeight, maxFontSize);
				}
				break;
			case "text-align":
			{
				Paragraph paragraph2 = paragraph;
				paragraph2.TextAlignment = (TextAlignment)(item3.Value switch
				{
					"center" => 1, 
					"right" => 2, 
					"left" => 0, 
					"justify" => 6, 
					_ => 0, 
				});
				break;
			}
			case "margin":
			{
				string value2 = item3.Value;
				if (value2.Contains(' '))
				{
					List<int> list = (from text3 in value2.Split(' ')
						select int.TryParse(text3.Replace("px", ""), out var result5) ? result5 : 0).ToList();
					if (list.Count == 4)
					{
						paragraph.Margin = new Thickness((double)list[3], (double)list[0], (double)list[1], (double)list[2]);
					}
				}
				else
				{
					int num4 = int.Parse(value2.Replace("px", ""));
					paragraph.Margin = new Thickness((double)num4);
				}
				break;
			}
			case "background-color":
			{
				SolidColorBrush val5 = ParseCssColor(item3.Value);
				if (val5 != null)
				{
					paragraph.Background = (ISolidColorBrush)(object)val5;
				}
				break;
			}
			case "border-color":
			{
				SolidColorBrush val4 = ParseCssColor(item3.Value);
				if (val4 != null)
				{
					paragraph.BorderBrush = (ISolidColorBrush)(object)val4;
				}
				break;
			}
			case "border-width":
				paragraph.BorderThickness = GetBorderThickness(item3.Value);
				break;
			}
		}
		return paragraph;
	}

	private static Thickness GetBorderThickness(string val)
	{
		//IL_0073: Unknown result type (might be due to invalid IL or missing references)
		Thickness result = default(Thickness);
		((Thickness)(ref result))..ctor(1.0);
		List<int> list = (from text in val.Split(' ')
			select int.TryParse(text.Replace("px", ""), out var result2) ? result2 : 0).ToList();
		if (list.Count == 4)
		{
			((Thickness)(ref result))..ctor((double)list[3], (double)list[0], (double)list[1], (double)list[2]);
		}
		return result;
	}

	private static Dictionary<string, string> ParseStyleAttribute(string? style)
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>();
		if (string.IsNullOrWhiteSpace(style))
		{
			return dictionary;
		}
		string[] array = style.Split(';', StringSplitOptions.RemoveEmptyEntries);
		for (int i = 0; i < array.Length; i++)
		{
			string[] array2 = array[i].Split(':', 2);
			if (array2.Length == 2)
			{
				dictionary[array2[0].Trim().ToLower()] = array2[1].Trim();
			}
		}
		return dictionary;
	}

	private static SolidColorBrush? ParseCssColor(string cssColor)
	{
		//IL_0139: Unknown result type (might be due to invalid IL or missing references)
		//IL_013e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0140: Unknown result type (might be due to invalid IL or missing references)
		//IL_014b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0152: Expected O, but got Unknown
		//IL_0105: Unknown result type (might be due to invalid IL or missing references)
		//IL_0113: Unknown result type (might be due to invalid IL or missing references)
		//IL_0119: Expected O, but got Unknown
		if (string.IsNullOrWhiteSpace(cssColor))
		{
			return null;
		}
		cssColor = cssColor.Trim();
		Match match = Rgba.Match(cssColor);
		if (match.Success)
		{
			if (!byte.TryParse(match.Groups["r"].Value, out var result))
			{
				return null;
			}
			if (!byte.TryParse(match.Groups["g"].Value, out var result2))
			{
				return null;
			}
			if (!byte.TryParse(match.Groups["b"].Value, out var result3))
			{
				return null;
			}
			double result4 = 1.0;
			if (match.Groups["a"].Success && !double.TryParse(match.Groups["a"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out result4))
			{
				return null;
			}
			result4 = Math.Clamp(result4, 0.0, 1.0);
			return new SolidColorBrush(Color.FromArgb((byte)Math.Round(result4 * 255.0), result, result2, result3), 1.0);
		}
		try
		{
			if (colConverter.ConvertFromString(cssColor.TrimEnd(';')) is Color val)
			{
				return new SolidColorBrush(val, 1.0);
			}
		}
		catch
		{
		}
		return null;
	}

	private static double LineHeightToLineSpacing(double lineHeight, double maxFontSize)
	{
		return lineHeight - maxFontSize * 1.25;
	}

	private static (Thickness? thickness, ISolidColorBrush? brush)? ParseBorderShorthand(string value)
	{
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ec: Unknown result type (might be due to invalid IL or missing references)
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}
		string text = value.Trim();
		if (string.Equals(text, "none", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "hidden", StringComparison.OrdinalIgnoreCase))
		{
			return ((Thickness?)new Thickness(0.0), null);
		}
		List<string> list = SplitCssTokens(text).ToList();
		if (list.Count == 0)
		{
			return null;
		}
		if (list.Any((string t) => t.Equals("none", StringComparison.OrdinalIgnoreCase) || t.Equals("hidden", StringComparison.OrdinalIgnoreCase)))
		{
			return ((Thickness?)new Thickness(0.0), null);
		}
		Thickness? item = null;
		ISolidColorBrush val = null;
		foreach (string item2 in list)
		{
			double? num = TryParseCssLengthPx(item2);
			if (num.HasValue)
			{
				item = new Thickness(num.Value);
				break;
			}
		}
		foreach (string item3 in list)
		{
			SolidColorBrush val2 = ParseCssColor(item3);
			if (val2 != null)
			{
				val = (ISolidColorBrush)(object)val2;
				break;
			}
		}
		if (!item.HasValue && val == null)
		{
			return null;
		}
		return (item, val);
	}

	private static IEnumerable<string> SplitCssTokens(string s)
	{
		int i = 0;
		while (i < s.Length)
		{
			for (; i < s.Length && char.IsWhiteSpace(s[i]); i++)
			{
			}
			if (i >= s.Length)
			{
				break;
			}
			int num = i;
			int num2 = 0;
			for (; i < s.Length; i++)
			{
				char c = s[i];
				switch (c)
				{
				case '(':
					num2++;
					continue;
				case ')':
					num2 = Math.Max(0, num2 - 1);
					continue;
				default:
					if (num2 != 0 || !char.IsWhiteSpace(c))
					{
						continue;
					}
					break;
				}
				break;
			}
			string text = s.Substring(num, i - num).Trim();
			if (text.Length > 0)
			{
				yield return text;
			}
		}
	}

	private static double? TryParseCssLengthPx(string token)
	{
		if (string.IsNullOrWhiteSpace(token))
		{
			return null;
		}
		token = token.Trim();
		Match match = Regex.Match(token, "^(?<n>\\d+(\\.\\d+)?)\\s*(px)?$", RegexOptions.IgnoreCase);
		if (!match.Success)
		{
			return null;
		}
		if (double.TryParse(match.Groups["n"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
		{
			return result;
		}
		return null;
	}
}
