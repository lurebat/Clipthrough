using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.RegularExpressions.Generated;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DynamicData;
using RtfDomParserAv;

namespace AvRichTextBox;

internal static class RtfConversions
{
	private enum BorderType
	{
		Left,
		Top,
		Right,
		Bottom
	}

	internal static string DefaultEastAsiaFont = "";

	internal static string DefaultAsciiFont = "";

	internal static string GetRtfFromFlowDocument(FlowDocument fdoc)
	{
		StringBuilder stringBuilder = new StringBuilder();
		Dictionary<string, int> fontMap = new Dictionary<string, int>();
		Dictionary<Color, int> colorMap = new Dictionary<Color, int>();
		stringBuilder.Append(GetFontAndColorTables(fdoc.Blocks, ref fontMap, ref colorMap));
		string value = Math.Round(HelperMethods.PixToTwip(fdoc.PagePadding.Left)).ToString();
		string value2 = Math.Round(HelperMethods.PixToTwip(fdoc.PagePadding.Right)).ToString();
		string value3 = Math.Round(HelperMethods.PixToTwip(fdoc.PagePadding.Top)).ToString();
		string value4 = Math.Round(HelperMethods.PixToTwip(fdoc.PagePadding.Bottom)).ToString();
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(24, 4, stringBuilder2);
		handler.AppendLiteral("\\margl");
		handler.AppendFormatted(value);
		handler.AppendLiteral("\\margr");
		handler.AppendFormatted(value2);
		handler.AppendLiteral("\\margt");
		handler.AppendFormatted(value3);
		handler.AppendLiteral("\\margb");
		handler.AppendFormatted(value4);
		stringBuilder2.Append(ref handler);
		foreach (Block block in fdoc.Blocks)
		{
			if (!(block is Paragraph par))
			{
				if (block is Table table)
				{
					string tableRtf = GetTableRtf(table, fontMap, colorMap);
					stringBuilder.Append(tableRtf);
				}
			}
			else
			{
				string paragraphRtf = GetParagraphRtf(par, fontMap, colorMap);
				stringBuilder.Append(paragraphRtf);
			}
		}
		stringBuilder.Remove(stringBuilder.Length - 5, 5);
		stringBuilder.Append('}');
		return stringBuilder.ToString();
	}

	internal static string GetTableRtf(Table table, Dictionary<string, int> fontMap, Dictionary<Color, int> colorMap)
	{
		int[] array = new int[table.ColDefs.Count];
		int num = 0;
		for (int i = 0; i < table.ColDefs.Count; i++)
		{
			num = (array[i] = num + (int)HelperMethods.PixToTwip(table.ColDefs[i].Width.Value));
		}
		(int, int, int, int, Thickness)[] array2 = new(int, int, int, int, Thickness)[table.ColDefs.Count];
		StringBuilder stringBuilder = new StringBuilder();
		for (int rowno = 0; rowno < table.RowDefs.Count; rowno++)
		{
			stringBuilder.Append("\\trowd ");
			switch (table.TableAlignment)
			{
			default:
				stringBuilder.Append("\\trql");
				break;
			case HorizontalAlignment.Center:
				stringBuilder.Append("\\trqc");
				break;
			case HorizontalAlignment.Right:
				stringBuilder.Append("\\trqr");
				break;
			}
			int colno;
			for (colno = 0; colno < table.ColDefs.Count; colno++)
			{
				if (rowno < array2[colno].Item1)
				{
					stringBuilder.Append("\\clvmrg");
				}
				string value = "";
				int num2 = 0;
				Cell cell = table.Cells.FirstOrDefault((Cell c) => c.RowNo == rowno && c.ColNo == colno);
				if (cell != null)
				{
					switch (cell.CellVerticalAlignment)
					{
					case VerticalAlignment.Top:
						stringBuilder.Append("\\clvertalt");
						break;
					case VerticalAlignment.Center:
						stringBuilder.Append("\\clvertalc");
						break;
					case VerticalAlignment.Bottom:
						stringBuilder.Append("\\clvertalb");
						break;
					}
					if (cell.RowSpan > 1)
					{
						stringBuilder.Append("\\clvmgf");
					}
					num2 = cell.ColSpan - 1;
					if (cell.CellContent is Paragraph par)
					{
						value = GetParagraphRtf(par, fontMap, colorMap, isTablePar: true);
					}
					array2[colno].Item1 = rowno + cell.RowSpan;
					array2[colno].Item2 = cell.ColSpan;
					array2[colno].Item5 = cell.Padding;
					ISolidColorBrush borderBrush = cell.BorderBrush;
					if (borderBrush != null && colorMap.TryGetValue(borderBrush.Color, out var value2))
					{
						array2[colno].Item3 = value2;
					}
					ISolidColorBrush cellBackground = cell.CellBackground;
					if (cellBackground != null && colorMap.TryGetValue(cellBackground.Color, out var value3))
					{
						array2[colno].Item4 = value3;
					}
				}
				else
				{
					num2 = array2[colno].Item2 - 1;
				}
				int item = array2[colno].Item3;
				StringBuilder stringBuilder2;
				StringBuilder.AppendInterpolatedStringHandler handler;
				if (item != 0)
				{
					stringBuilder2 = stringBuilder;
					StringBuilder stringBuilder3 = stringBuilder2;
					handler = new StringBuilder.AppendInterpolatedStringHandler(116, 4, stringBuilder2);
					handler.AppendLiteral("\\clbrdrt\\brdrs\\brdrw20\\brdrcf");
					handler.AppendFormatted(item);
					handler.AppendLiteral("\\clbrdrl\\brdrs\\brdrw20\\brdrcf");
					handler.AppendFormatted(item);
					handler.AppendLiteral("\\clbrdrb\\brdrs\\brdrw20\\brdrcf");
					handler.AppendFormatted(item);
					handler.AppendLiteral("\\clbrdrr\\brdrs\\brdrw20\\brdrcf");
					handler.AppendFormatted(item);
					stringBuilder3.Append(ref handler);
				}
				int item2 = array2[colno].Item4;
				if (item2 != 0)
				{
					stringBuilder2 = stringBuilder;
					StringBuilder stringBuilder4 = stringBuilder2;
					handler = new StringBuilder.AppendInterpolatedStringHandler(8, 1, stringBuilder2);
					handler.AppendLiteral("\\clcbpat");
					handler.AppendFormatted(item2);
					stringBuilder4.Append(ref handler);
				}
				Thickness item3 = array2[colno].Item5;
				int value4 = (int)HelperMethods.PixToTwip(item3.Left);
				int value5 = (int)HelperMethods.PixToTwip(item3.Top);
				int value6 = (int)HelperMethods.PixToTwip(item3.Right);
				int value7 = (int)HelperMethods.PixToTwip(item3.Bottom);
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder5 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(64, 4, stringBuilder2);
				handler.AppendLiteral("\\clpadl");
				handler.AppendFormatted(value4);
				handler.AppendLiteral("\\clpadfl3\\clpadt");
				handler.AppendFormatted(value5);
				handler.AppendLiteral("\\clpadft3\\clpadr");
				handler.AppendFormatted(value6);
				handler.AppendLiteral("\\clpadfr3\\clpadb");
				handler.AppendFormatted(value7);
				handler.AppendLiteral("\\clpadfb3");
				stringBuilder5.Append(ref handler);
				colno += num2;
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder6 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder2);
				handler.AppendLiteral("\\cellx");
				handler.AppendFormatted(array[colno]);
				handler.AppendLiteral(" ");
				stringBuilder6.Append(ref handler);
				stringBuilder.Append(value);
				stringBuilder.Append("\\cell ");
			}
			stringBuilder.Append("\\row");
		}
		return stringBuilder.ToString();
	}

	internal static string GetParagraphRtf(Paragraph par, Dictionary<string, int> fontMap, Dictionary<Color, int> colorMap, bool isTablePar = false)
	{
		bool BoldOn = false;
		bool ItalicOn = false;
		bool UnderlineOn = false;
		bool SuperscriptOn = false;
		bool SubscriptOn = false;
		int currentLang = 1033;
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.Append("\\pard" + (isTablePar ? "\\intbl" : ""));
		StringBuilder stringBuilder2 = stringBuilder;
		stringBuilder2.Append(par.TextAlignment switch
		{
			TextAlignment.Center => "\\qc", 
			TextAlignment.Left => "\\ql", 
			TextAlignment.Right => "\\qr", 
			TextAlignment.Justify => "\\qj", 
			_ => "\\ql", 
		});
		double num = par.Inlines.Max((IEditable il) => (!il.IsRun) ? par.LineHeight : ((EditableRun)il).FontSize);
		double value = ((num != 0.0) ? ((int)(par.LineHeight / num * 2.0 * 240.0)) : 0);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder3 = stringBuilder2;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(11, 1, stringBuilder2);
		handler.AppendLiteral("\\sl");
		handler.AppendFormatted(value);
		handler.AppendLiteral("\\slmult0");
		stringBuilder3.Append(ref handler);
		ISolidColorBrush borderBrush = par.BorderBrush;
		if (borderBrush != null && borderBrush.Color != Colors.Transparent)
		{
			int value2 = 0;
			if (colorMap.TryGetValue(borderBrush.Color, out var value3))
			{
				value2 = value3;
			}
			string value4 = HelperMethods.PixToTwip(par.BorderThickness.Left).ToString();
			string value5 = HelperMethods.PixToTwip(par.BorderThickness.Right).ToString();
			string value6 = HelperMethods.PixToTwip(par.BorderThickness.Top).ToString();
			string value7 = HelperMethods.PixToTwip(par.BorderThickness.Bottom).ToString();
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder4 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(25, 2, stringBuilder2);
			handler.AppendLiteral("\\brdrt\\brdrs\\brdrw");
			handler.AppendFormatted(value6);
			handler.AppendLiteral("\\brdrcf");
			handler.AppendFormatted(value2);
			stringBuilder4.Append(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder5 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(25, 2, stringBuilder2);
			handler.AppendLiteral("\\brdrl\\brdrs\\brdrw");
			handler.AppendFormatted(value4);
			handler.AppendLiteral("\\brdrcf");
			handler.AppendFormatted(value2);
			stringBuilder5.Append(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder6 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(25, 2, stringBuilder2);
			handler.AppendLiteral("\\brdrb\\brdrs\\brdrw");
			handler.AppendFormatted(value7);
			handler.AppendLiteral("\\brdrcf");
			handler.AppendFormatted(value2);
			stringBuilder6.Append(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder7 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(25, 2, stringBuilder2);
			handler.AppendLiteral("\\brdrr\\brdrs\\brdrw");
			handler.AppendFormatted(value5);
			handler.AppendLiteral("\\brdrcf");
			handler.AppendFormatted(value2);
			stringBuilder7.Append(ref handler);
		}
		if (par.Background != null && par.Background.Color != Colors.Transparent)
		{
			int value8 = 0;
			ISolidColorBrush background = par.Background;
			if (background != null && colorMap.TryGetValue(background.Color, out var value9))
			{
				value8 = value9;
			}
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder8 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(6, 1, stringBuilder2);
			handler.AppendLiteral("\\cbpat");
			handler.AppendFormatted(value8);
			stringBuilder8.Append(ref handler);
		}
		foreach (IEditable inline in par.Inlines)
		{
			stringBuilder.Append(GetIEditableRtf(inline, ref BoldOn, ref ItalicOn, ref UnderlineOn, ref SuperscriptOn, ref SubscriptOn, ref currentLang, fontMap, colorMap));
		}
		if (!isTablePar)
		{
			stringBuilder.Append("\\par ");
		}
		return stringBuilder.ToString();
	}

	internal static string GetRtfFromInlines(List<IEditable> inlines)
	{
		StringBuilder stringBuilder = new StringBuilder();
		Dictionary<string, int> fontMap = new Dictionary<string, int>();
		Dictionary<Color, int> colorMap = new Dictionary<Color, int>();
		stringBuilder.Append(GetFontAndColorTables(inlines, ref fontMap, ref colorMap));
		bool BoldOn = false;
		bool ItalicOn = false;
		bool UnderlineOn = false;
		bool SuperscriptOn = false;
		bool SubscriptOn = false;
		int currentLang = 1033;
		foreach (IEditable inline in inlines)
		{
			stringBuilder.Append(GetIEditableRtf(inline, ref BoldOn, ref ItalicOn, ref UnderlineOn, ref SuperscriptOn, ref SubscriptOn, ref currentLang, fontMap, colorMap));
			if (inline.InlineText.EndsWith("\r\n"))
			{
				stringBuilder.Append("\\par ");
			}
		}
		stringBuilder.Append('}');
		return stringBuilder.ToString();
	}

	private static string GetIEditableRtf(IEditable ied, ref bool BoldOn, ref bool ItalicOn, ref bool UnderlineOn, ref bool SuperscriptOn, ref bool SubscriptOn, ref int currentLang, Dictionary<string, int> fontMap, Dictionary<Color, int> colorMap)
	{
		StringBuilder stringBuilder = new StringBuilder();
		if (!(ied is EditableLineBreak))
		{
			if (!(ied is EditableInlineUIContainer editableInlineUIContainer))
			{
				if (ied is EditableRun editableRun)
				{
					if (!BoldOn && editableRun.FontWeight == FontWeight.Bold)
					{
						stringBuilder.Append("\\b ");
						BoldOn = true;
					}
					if (!ItalicOn && editableRun.FontStyle == FontStyle.Italic)
					{
						stringBuilder.Append("\\i ");
						ItalicOn = true;
					}
					if (!UnderlineOn && editableRun.TextDecorations == TextDecorations.Underline)
					{
						stringBuilder.Append("\\ul ");
						UnderlineOn = true;
					}
					if (!SuperscriptOn && editableRun.BaselineAlignment == BaselineAlignment.Superscript)
					{
						stringBuilder.Append("\\super ");
						SuperscriptOn = true;
					}
					if (!SubscriptOn && editableRun.BaselineAlignment == BaselineAlignment.Subscript)
					{
						stringBuilder.Append("\\sub ");
						SubscriptOn = true;
					}
					if (BoldOn && editableRun.FontWeight == FontWeight.Normal)
					{
						stringBuilder.Append("\\b0 ");
						BoldOn = false;
					}
					if (ItalicOn && editableRun.FontStyle == FontStyle.Normal)
					{
						stringBuilder.Append("\\i0 ");
						ItalicOn = false;
					}
					if (UnderlineOn && editableRun.TextDecorations != TextDecorations.Underline)
					{
						stringBuilder.Append("\\ul0 ");
						UnderlineOn = false;
					}
					if (SuperscriptOn && editableRun.BaselineAlignment != BaselineAlignment.Superscript)
					{
						stringBuilder.Append("\\nosupersub ");
						SuperscriptOn = false;
					}
					if (SubscriptOn && editableRun.BaselineAlignment != BaselineAlignment.Subscript)
					{
						stringBuilder.Append("\\nosupersub ");
						SubscriptOn = false;
					}
					if (editableRun.FontSize > 0.0)
					{
						StringBuilder stringBuilder2 = stringBuilder;
						StringBuilder stringBuilder3 = stringBuilder2;
						StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(4, 1, stringBuilder2);
						handler.AppendLiteral("\\fs");
						handler.AppendFormatted((int)(editableRun.FontSize * 2.0));
						handler.AppendLiteral(" ");
						stringBuilder3.Append(ref handler);
					}
					if (fontMap.TryGetValue(editableRun.FontFamily.Name, out var value))
					{
						StringBuilder stringBuilder2 = stringBuilder;
						StringBuilder stringBuilder4 = stringBuilder2;
						StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(3, 1, stringBuilder2);
						handler.AppendLiteral("\\f");
						handler.AppendFormatted(value);
						handler.AppendLiteral(" ");
						stringBuilder4.Append(ref handler);
					}
					if (editableRun.Foreground is ISolidColorBrush solidColorBrush && colorMap.TryGetValue(solidColorBrush.Color, out var value2))
					{
						StringBuilder stringBuilder2 = stringBuilder;
						StringBuilder stringBuilder5 = stringBuilder2;
						StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(4, 1, stringBuilder2);
						handler.AppendLiteral("\\cf");
						handler.AppendFormatted(value2);
						handler.AppendLiteral(" ");
						stringBuilder5.Append(ref handler);
					}
					else
					{
						stringBuilder.Append("\\cf0 ");
					}
					if (editableRun.Background is ISolidColorBrush solidColorBrush2 && solidColorBrush2.Color != Colors.Transparent && colorMap.TryGetValue(solidColorBrush2.Color, out var value3))
					{
						StringBuilder stringBuilder2 = stringBuilder;
						StringBuilder stringBuilder6 = stringBuilder2;
						StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(11, 1, stringBuilder2);
						handler.AppendLiteral("\\highlight");
						handler.AppendFormatted(value3);
						handler.AppendLiteral(" ");
						stringBuilder6.Append(ref handler);
					}
					else
					{
						stringBuilder.Append("\\highlight0 ");
					}
					if (!string.IsNullOrEmpty(editableRun.Text))
					{
						stringBuilder.Append(GetRtfRunText(editableRun.Text, ref currentLang));
					}
				}
			}
			else if (editableInlineUIContainer.Child is Image { Source: Bitmap { PixelSize: { Width: var width }, PixelSize: { Height: var height } } source } image)
			{
				int value4 = (int)HelperMethods.PixToTwip(image.Width);
				int value5 = (int)HelperMethods.PixToTwip(image.Height);
				using MemoryStream memoryStream = new MemoryStream();
				RenderTargetBitmap renderTargetBitmap = new RenderTargetBitmap(new PixelSize(width, height));
				using (DrawingContext drawingContext = renderTargetBitmap.CreateDrawingContext())
				{
					drawingContext.DrawImage(source, new Rect(0.0, 0.0, width, height));
				}
				renderTargetBitmap.Save(memoryStream);
				memoryStream.Seek(0L, SeekOrigin.Begin);
				byte[] array = new byte[memoryStream.Length];
				memoryStream.Read(array, 0, array.Length);
				StringBuilder stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder7 = stringBuilder2;
				StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(42, 4, stringBuilder2);
				handler.AppendLiteral("{\\pict\\pngblip\\picw");
				handler.AppendFormatted(width);
				handler.AppendLiteral("\\pich");
				handler.AppendFormatted(height);
				handler.AppendLiteral("\\picwgoal");
				handler.AppendFormatted(value4);
				handler.AppendLiteral("\\pichgoal");
				handler.AppendFormatted(value5);
				stringBuilder7.AppendLine(ref handler);
				byte[] array2 = array;
				foreach (byte b in array2)
				{
					stringBuilder.Append(b.ToString("x2"));
				}
				stringBuilder.AppendLine("}");
			}
			return stringBuilder.ToString();
		}
		return "\\line";
	}

	private static string GetFontAndColorTables(IEnumerable<Block> allBlocks, ref Dictionary<string, int> fontMap, ref Dictionary<Color, int> colorMap)
	{
		StringBuilder stringBuilder = new StringBuilder();
		int fontIndex = 0;
		int colorIndex = 1;
		foreach (Block allBlock in allBlocks)
		{
			if (!(allBlock is Paragraph par))
			{
				if (!(allBlock is Table table))
				{
					continue;
				}
				ISolidColorBrush borderBrush = table.BorderBrush;
				if (borderBrush != null && borderBrush.Color != Colors.Transparent && !colorMap.ContainsKey(borderBrush.Color))
				{
					colorMap[borderBrush.Color] = colorIndex++;
				}
				foreach (Cell cell in table.Cells)
				{
					ISolidColorBrush borderBrush2 = cell.BorderBrush;
					if (borderBrush2 != null && borderBrush2.Color != Colors.Transparent && !colorMap.ContainsKey(borderBrush2.Color))
					{
						colorMap[borderBrush2.Color] = colorIndex++;
					}
					ISolidColorBrush cellBackground = cell.CellBackground;
					if (cellBackground != null && cellBackground.Color != Colors.Transparent && !colorMap.ContainsKey(cellBackground.Color))
					{
						colorMap[cellBackground.Color] = colorIndex++;
					}
					if (cell.CellContent is Paragraph par2)
					{
						GetParagraphColorFontMapping(par2, ref fontMap, ref colorMap, ref fontIndex, ref colorIndex);
					}
				}
			}
			else
			{
				GetParagraphColorFontMapping(par, ref fontMap, ref colorMap, ref fontIndex, ref colorIndex);
			}
		}
		stringBuilder.Append("{\\rtf1\\ansi\\deff0 {\\fonttbl");
		foreach (KeyValuePair<string, int> item in fontMap)
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder3 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(11, 2, stringBuilder2);
			handler.AppendLiteral("{\\f");
			handler.AppendFormatted(item.Value);
			handler.AppendLiteral("\\fnil ");
			handler.AppendFormatted(item.Key);
			handler.AppendLiteral(";}");
			stringBuilder3.Append(ref handler);
		}
		stringBuilder.Append('}');
		stringBuilder.Append("{\\colortbl;");
		foreach (KeyValuePair<Color, int> item2 in colorMap)
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder4 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(16, 3, stringBuilder2);
			handler.AppendLiteral("\\red");
			handler.AppendFormatted(item2.Key.R);
			handler.AppendLiteral("\\green");
			handler.AppendFormatted(item2.Key.G);
			handler.AppendLiteral("\\blue");
			handler.AppendFormatted(item2.Key.B);
			handler.AppendLiteral(";");
			stringBuilder4.Append(ref handler);
		}
		stringBuilder.Append('}');
		return stringBuilder.ToString();
	}

	private static void GetParagraphColorFontMapping(Paragraph par, ref Dictionary<string, int> fontMap, ref Dictionary<Color, int> colorMap, ref int fontIndex, ref int colorIndex)
	{
		ISolidColorBrush borderBrush = par.BorderBrush;
		if (borderBrush != null && borderBrush.Color != Colors.Transparent && !colorMap.ContainsKey(borderBrush.Color))
		{
			colorMap[borderBrush.Color] = colorIndex++;
		}
		ISolidColorBrush background = par.Background;
		if (background != null && background.Color != Colors.Transparent && !colorMap.ContainsKey(background.Color))
		{
			colorMap[background.Color] = colorIndex++;
		}
		foreach (IEditable inline in par.Inlines)
		{
			if (inline is EditableRun editableRun)
			{
				if (editableRun.FontFamily != null && !fontMap.ContainsKey(editableRun.FontFamily.Name))
				{
					fontMap[editableRun.FontFamily.Name] = fontIndex++;
				}
				if (editableRun.Foreground is ISolidColorBrush solidColorBrush && !colorMap.ContainsKey(solidColorBrush.Color))
				{
					colorMap[solidColorBrush.Color] = colorIndex++;
				}
				if (editableRun.Background is ISolidColorBrush solidColorBrush2 && !colorMap.ContainsKey(solidColorBrush2.Color))
				{
					colorMap[solidColorBrush2.Color] = colorIndex++;
				}
			}
		}
	}

	private static string GetFontAndColorTables(IEnumerable<IEditable> inlinesToMap, ref Dictionary<string, int> fontMap, ref Dictionary<Color, int> colorMap)
	{
		int num = 0;
		int num2 = 1;
		foreach (IEditable item in inlinesToMap)
		{
			if (item is EditableRun editableRun)
			{
				if (editableRun.FontFamily != null && !fontMap.ContainsKey(editableRun.FontFamily.Name))
				{
					fontMap[editableRun.FontFamily.Name] = num++;
				}
				if (editableRun.Foreground is ISolidColorBrush solidColorBrush && !colorMap.ContainsKey(solidColorBrush.Color))
				{
					colorMap[solidColorBrush.Color] = num2++;
				}
				if (editableRun.Background is ISolidColorBrush solidColorBrush2 && !colorMap.ContainsKey(solidColorBrush2.Color))
				{
					colorMap[solidColorBrush2.Color] = num2++;
				}
			}
		}
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.Append("{\\rtf1\\ansi\\deff0 {\\fonttbl");
		foreach (KeyValuePair<string, int> item2 in fontMap)
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder3 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(11, 2, stringBuilder2);
			handler.AppendLiteral("{\\f");
			handler.AppendFormatted(item2.Value);
			handler.AppendLiteral("\\fnil ");
			handler.AppendFormatted(item2.Key);
			handler.AppendLiteral(";}");
			stringBuilder3.Append(ref handler);
		}
		stringBuilder.Append('}');
		stringBuilder.Append("{\\colortbl;");
		foreach (KeyValuePair<Color, int> item3 in colorMap)
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder4 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(16, 3, stringBuilder2);
			handler.AppendLiteral("\\red");
			handler.AppendFormatted(item3.Key.R);
			handler.AppendLiteral("\\green");
			handler.AppendFormatted(item3.Key.G);
			handler.AppendLiteral("\\blue");
			handler.AppendFormatted(item3.Key.B);
			handler.AppendLiteral(";");
			stringBuilder4.Append(ref handler);
		}
		stringBuilder.Append('}');
		return stringBuilder.ToString();
	}

	private static string GetRtfRunText(string text, ref int currentLang)
	{
		StringBuilder stringBuilder = new StringBuilder();
		foreach (char c in text)
		{
			int languageForChar = HelperMethods.GetLanguageForChar(c);
			if (languageForChar != currentLang)
			{
				StringBuilder stringBuilder2 = stringBuilder;
				StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(6, 1, stringBuilder2);
				handler.AppendLiteral("\\lang");
				handler.AppendFormatted(languageForChar);
				handler.AppendLiteral(" ");
				stringBuilder2.Append(ref handler);
				currentLang = languageForChar;
			}
			if ((c == '\\' || c == '{' || c == '}') ? true : false)
			{
				stringBuilder.Append("\\" + c);
			}
			else if (c > '\u007f')
			{
				int num = c;
				stringBuilder.Append("\\u" + num + "?");
			}
			else
			{
				stringBuilder.Append(c);
			}
		}
		return stringBuilder.ToString();
	}

	internal static List<IEditable> GetInlinesFromRtf(RTFDomDocument rtfdoc)
	{
		List<IEditable> list = new List<IEditable>();
		rtfdoc.Elements.OfType<RTFDomParagraph>().Count();
		foreach (RTFDomElement element in rtfdoc.Elements)
		{
			if (!(element is RTFDomParagraph rTFDomParagraph))
			{
				if (element is RTFDomTable)
				{
				}
			}
			else if (rTFDomParagraph.Elements.Count > 0)
			{
				List<IEditable> rtfTextElementsAsInlines = GetRtfTextElementsAsInlines(rTFDomParagraph.Elements);
				rtfTextElementsAsInlines.Last().InlineText += Environment.NewLine;
				list.AddRange(rtfTextElementsAsInlines);
			}
		}
		return list;
	}

	internal static void GetFlowDocumentFromRtf(RTFDomDocument rtfdoc, FlowDocument fdoc)
	{
		double left = Math.Round(HelperMethods.TwipToPix(rtfdoc.LeftMargin));
		double top = Math.Round(HelperMethods.TwipToPix(rtfdoc.TopMargin));
		double right = Math.Round(HelperMethods.TwipToPix(rtfdoc.RightMargin));
		double bottom = Math.Round(HelperMethods.TwipToPix(rtfdoc.BottomMargin));
		fdoc.PagePadding = new Thickness(left, top, right, bottom);
		foreach (RTFDomElement element in rtfdoc.Elements)
		{
			if (!(element is RTFDomParagraph rtfpar))
			{
				if (element is RTFDomTable rtftable)
				{
					Table tableFromRtfDom = GetTableFromRtfDom(rtftable, fdoc, rtfdoc.ColorTable);
					fdoc.Blocks.Add(tableFromRtfDom);
				}
			}
			else
			{
				Paragraph paragraphFromRtfDom = GetParagraphFromRtfDom(rtfpar, fdoc);
				fdoc.Blocks.Add(paragraphFromRtfDom);
			}
		}
		fdoc.PagePadding = new Thickness(HelperMethods.TwipToPix(rtfdoc.LeftMargin), HelperMethods.TwipToPix(rtfdoc.TopMargin), HelperMethods.TwipToPix(rtfdoc.RightMargin), HelperMethods.TwipToPix(rtfdoc.BottomMargin));
	}

	private static Table GetTableFromRtfDom(RTFDomTable rtftable, FlowDocument fdoc, RTFColorTable cTable)
	{
		Table newtable = new Table(fdoc);
		for (int i = 0; i < rtftable.Columns.Count; i++)
		{
			_ = rtftable.Columns[i];
			newtable.ColDefs.Add(new ColumnDefinition());
		}
		int num = 20;
		int num2 = 0;
		foreach (RTFDomTableRow item in rtftable.Elements.OfType<RTFDomTableRow>())
		{
			newtable.RowDefs.Add(new RowDefinition());
			foreach (RTFAttribute attribute in item.Attributes)
			{
				switch (attribute.Name)
				{
				case "trql":
					newtable.TableAlignment = HorizontalAlignment.Left;
					break;
				case "trqc":
					newtable.TableAlignment = HorizontalAlignment.Center;
					break;
				case "trqr":
					newtable.TableAlignment = HorizontalAlignment.Right;
					break;
				}
			}
			int num3 = 0;
			BorderType borderType = BorderType.Left;
			foreach (RTFDomTableCell item2 in item.Elements.OfType<RTFDomTableCell>())
			{
				Cell cell = new Cell(newtable)
				{
					RowNo = num2,
					ColNo = num3,
					ColSpan = item2.ColSpan,
					RowSpan = item2.RowSpan
				};
				double left = 1.0;
				double top = 1.0;
				double right = 1.0;
				double bottom = 1.0;
				double left2 = 1.0;
				double top2 = 1.0;
				double right2 = 1.0;
				double bottom2 = 1.0;
				bool flag = false;
				foreach (RTFAttribute attribute2 in item2.Attributes)
				{
					switch (attribute2.Name)
					{
					case "clvertalc":
						cell.CellVerticalAlignment = VerticalAlignment.Center;
						break;
					case "clvertalt":
						cell.CellVerticalAlignment = VerticalAlignment.Top;
						break;
					case "clvertalb":
						cell.CellVerticalAlignment = VerticalAlignment.Bottom;
						break;
					case "clvmrg":
						flag = true;
						break;
					case "brdrw":
						switch (borderType)
						{
						case BorderType.Left:
							left = Math.Round(HelperMethods.TwipToPix(attribute2.Value));
							break;
						case BorderType.Right:
							right = Math.Round(HelperMethods.TwipToPix(attribute2.Value));
							break;
						case BorderType.Top:
							top = Math.Round(HelperMethods.TwipToPix(attribute2.Value));
							break;
						case BorderType.Bottom:
							bottom = Math.Round(HelperMethods.TwipToPix(attribute2.Value));
							break;
						}
						break;
					case "clbrdrb":
						borderType = BorderType.Bottom;
						break;
					case "clbrdrt":
						borderType = BorderType.Top;
						break;
					case "clbrdrr":
						borderType = BorderType.Right;
						break;
					case "clbrdrl":
						borderType = BorderType.Left;
						break;
					case "brdrcf":
						cell.BorderBrush = new SolidColorBrush(cTable.GetColor(attribute2.Value, Colors.Black));
						break;
					case "clcbpat":
						cell.CellBackground = new SolidColorBrush(cTable.GetColor(attribute2.Value, Colors.Black));
						break;
					case "cellx":
						num = Math.Max(num, attribute2.Value);
						break;
					case "clpadl":
						left2 = Math.Round(HelperMethods.TwipToPix(attribute2.Value));
						break;
					case "clpadt":
						top2 = Math.Round(HelperMethods.TwipToPix(attribute2.Value));
						break;
					case "clpadr":
						right2 = Math.Round(HelperMethods.TwipToPix(attribute2.Value));
						break;
					case "clpadb":
						bottom2 = Math.Round(HelperMethods.TwipToPix(attribute2.Value));
						break;
					}
				}
				if (!flag)
				{
					foreach (RTFDomParagraph item3 in item2.Elements.OfType<RTFDomParagraph>())
					{
						cell.CellContent = GetParagraphFromRtfDom(item3, fdoc);
					}
					cell.BorderThickness = new Thickness(left, top, right, bottom);
					cell.Padding = new Thickness(left2, top2, right2, bottom2);
					if (cell.CellContent != null)
					{
						newtable.Cells.Add(cell);
					}
				}
				num3++;
			}
			num2++;
		}
		newtable.Width = HelperMethods.TwipToPix(num);
		int cols = newtable.ColDefs.Count;
		newtable.ColDefs.ToList().ForEach(delegate(ColumnDefinition cd)
		{
			cd.Width = new GridLength(newtable.Width / (double)cols, GridUnitType.Pixel);
		});
		return newtable;
	}

	private static Paragraph GetParagraphFromRtfDom(RTFDomParagraph rtfpar, FlowDocument fdoc)
	{
		Paragraph paragraph = new Paragraph(fdoc);
		switch (rtfpar.Format.Align)
		{
		case RTFAlignment.Left:
			paragraph.TextAlignment = TextAlignment.Left;
			break;
		case RTFAlignment.Center:
			paragraph.TextAlignment = TextAlignment.Center;
			break;
		case RTFAlignment.Right:
			paragraph.TextAlignment = TextAlignment.Right;
			break;
		case RTFAlignment.Justify:
			paragraph.TextAlignment = TextAlignment.Justify;
			break;
		}
		paragraph.Background = new SolidColorBrush(rtfpar.Format.BackColor);
		paragraph.BorderBrush = new SolidColorBrush(rtfpar.Format.BorderColor);
		paragraph.BorderThickness = new Thickness(HelperMethods.TwipToPix(rtfpar.Format.BorderWidth));
		paragraph.FontFamily = new FontFamily(rtfpar.Format.FontName);
		List<IEditable> rtfTextElementsAsInlines = GetRtfTextElementsAsInlines(rtfpar.Elements);
		paragraph.Inlines.AddRange(rtfTextElementsAsInlines);
		if (paragraph.Inlines.Count == 0)
		{
			paragraph.Inlines.Add(new EditableRun(""));
		}
		double num = (double)rtfpar.Format.LineSpacing / 240.0;
		double lineHeight = paragraph.Inlines.First().InlineHeight * num * 1.25;
		paragraph.LineHeight = lineHeight;
		return paragraph;
	}

	private static List<IEditable> GetRtfTextElementsAsInlines(RTFDomElementList elements)
	{
		List<IEditable> list = new List<IEditable>();
		foreach (RTFDomElement element in elements)
		{
			if (element is RTFDomField rTFDomField)
			{
				foreach (RTFDomElement element2 in rTFDomField.Result.Elements)
				{
					if (element2 is RTFDomText rTFDomText)
					{
						EditableRun editableRun = new EditableRun(rTFDomText.Text);
						editableRun.FontSize = rTFDomText.Format.FontSize;
						list.Add(editableRun);
					}
				}
			}
			else if (element is RTFDomLineBreak)
			{
				EditableLineBreak item = new EditableLineBreak();
				list.Add(item);
			}
			else if (element is RTFDomImage rTFDomImage)
			{
				EditableInlineUIContainer editableInlineUIContainer = new EditableInlineUIContainer(null)
				{
					FontFamily = "Image"
				};
				Image image = new Image
				{
					Width = HelperMethods.TwipToPix(rTFDomImage.Width),
					Height = HelperMethods.TwipToPix(rTFDomImage.Height),
					Stretch = Stretch.Fill
				};
				MemoryStream stream = new MemoryStream(rTFDomImage.Data)
				{
					Position = 0L
				};
				image.Source = new Bitmap(stream);
				editableInlineUIContainer.Child = image;
				list.Add(editableInlineUIContainer);
			}
			else if (element is RTFDomText rTFDomText2)
			{
				EditableRun editableRun2 = new EditableRun(rTFDomText2.Text)
				{
					FontSize = rTFDomText2.Format.FontSize
				};
				if (rTFDomText2.Format.Bold)
				{
					editableRun2.FontWeight = FontWeight.Bold;
				}
				if (rTFDomText2.Format.Italic)
				{
					editableRun2.FontStyle = FontStyle.Italic;
				}
				if (rTFDomText2.Format.Underline)
				{
					editableRun2.TextDecorations = TextDecorations.Underline;
				}
				if (rTFDomText2.Format.Strikeout)
				{
					editableRun2.TextDecorations = TextDecorations.Strikethrough;
				}
				if (rTFDomText2.Format.Subscript)
				{
					editableRun2.BaselineAlignment = BaselineAlignment.Subscript;
				}
				if (rTFDomText2.Format.Superscript)
				{
					editableRun2.BaselineAlignment = BaselineAlignment.Superscript;
				}
				editableRun2.Foreground = new SolidColorBrush(rTFDomText2.Format.TextColor);
				editableRun2.Background = new SolidColorBrush(rTFDomText2.Format.BackColor);
				editableRun2.FontFamily = new FontFamily(rTFDomText2.Format.FontName);
				list.Add(editableRun2);
			}
		}
		return list;
	}

	private static string DecodeRtfUnicode(string rtfText)
	{
		return RtfUnicodeRegex().Replace(rtfText, (Match match) => char.ConvertFromUtf32(int.Parse(match.Groups[1].Value)));
	}

	[GeneratedRegex("\\\\u(-?\\d+)\\?")]
	[GeneratedCode("System.Text.RegularExpressions.Generator", "10.0.14.15411")]
	private static Regex RtfUnicodeRegex()
	{
		return <RegexGenerator_g>F0338A28AE0D740519125F99FE91ED2E2A886FDAFB89901337C90E33E98CB422E__RtfUnicodeRegex_2.Instance;
	}
}
