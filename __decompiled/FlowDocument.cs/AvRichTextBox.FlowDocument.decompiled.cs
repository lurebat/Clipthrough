using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.RegularExpressions.Generated;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using DocumentFormat.OpenXml.Packaging;
using DynamicData;
using HtmlAgilityPack;
using RtfDomParserAv;

namespace AvRichTextBox;

public class FlowDocument : AvaloniaObject
{
	public delegate void ScrollInDirection_Handler(int direction);

	public delegate void SelectionChanged_Handler(TextRange selection);

	public delegate void UpdateRTBCaret_Handler();

	internal enum ExtendMode
	{
		ExtendModeNone,
		ExtendModeRight,
		ExtendModeLeft
	}

	private delegate void ToggleFormatRun(IEditable ied);

	internal delegate void FormatRunAction(IEditable ied, object value);

	internal delegate void FormatRunsAction(List<IEditable> ieds, object value);

	[CompilerGenerated]
	private static int <InlineIdCounter>k__BackingField;

	[CompilerGenerated]
	private static int <ParagraphIdCounter>k__BackingField;

	[CompilerGenerated]
	private static int <TableIdCounter>k__BackingField;

	internal List<TextRange> TextRanges = new List<TextRange>();

	internal bool disableRunTextUndo;

	public static readonly StyledProperty<ObservableCollection<Block>> BlocksProperty = AvaloniaProperty.Register<FlowDocument, ObservableCollection<Block>>("Blocks", new ObservableCollection<Block>(), false, (BindingMode)2, (Func<ObservableCollection<Block>, bool>)null, (Func<AvaloniaObject, ObservableCollection<Block>, ObservableCollection<Block>>)null, false);

	public static readonly DirectProperty<FlowDocument, Thickness> PagePaddingProperty = AvaloniaProperty.RegisterDirect<FlowDocument, Thickness>("PagePadding", (Func<FlowDocument, Thickness>)((FlowDocument o) => o.PagePadding), (Action<FlowDocument, Thickness>)delegate(FlowDocument o, Thickness v)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		o.PagePadding = v;
	}, default(Thickness), (BindingMode)1, false);

	[CompilerGenerated]
	private Thickness <PagePadding>k__BackingField;

	internal IBrush SelectionBrush = (IBrush)(object)Brushes.LightSteelBlue;

	internal List<Paragraph> AllParagraphs = new List<Paragraph>();

	private Dictionary<AvaloniaProperty, FormatRunsAction> formatRunsActions = new Dictionary<AvaloniaProperty, FormatRunsAction>();

	private Dictionary<AvaloniaProperty, FormatRunAction> formatRunActions = new Dictionary<AvaloniaProperty, FormatRunAction>();

	private bool BoldOn;

	private bool ItalicOn;

	private bool UnderliningOn;

	private bool InsertRunMode;

	private ToggleFormatRun? toggleFormatRun;

	internal static int InlineIdCounter
	{
		[CompilerGenerated]
		get
		{
			return <InlineIdCounter>k__BackingField;
		}
		set
		{
			<InlineIdCounter>k__BackingField = ((value != int.MaxValue) ? value : 0);
		}
	}

	internal static int ParagraphIdCounter
	{
		[CompilerGenerated]
		get
		{
			return <ParagraphIdCounter>k__BackingField;
		}
		set
		{
			<ParagraphIdCounter>k__BackingField = ((value != int.MaxValue) ? value : 0);
		}
	}

	internal static int TableIdCounter
	{
		[CompilerGenerated]
		get
		{
			return <TableIdCounter>k__BackingField;
		}
		set
		{
			<TableIdCounter>k__BackingField = ((value != int.MaxValue) ? value : 0);
		}
	}

	internal bool IsEditable { get; set; } = true;

	internal ObservableCollection<IUndo> Undos { get; set; } = new ObservableCollection<IUndo>();

	internal ObservableCollection<Paragraph> SelectionParagraphs { get; set; } = new ObservableCollection<Paragraph>();

	public List<Paragraph> GetSelectedParagraphs => (from b in AllParagraphs
		where b.StartInDoc <= Selection.Start && b.EndInDoc >= Selection.End
		select (b)).ToList();

	public ObservableCollection<Block> Blocks
	{
		get
		{
			return ((AvaloniaObject)this).GetValue<ObservableCollection<Block>>(BlocksProperty);
		}
		set
		{
			((AvaloniaObject)this).SetValue<ObservableCollection<Block>>(BlocksProperty, value, (BindingPriority)0);
		}
	}

	public Thickness PagePadding
	{
		[CompilerGenerated]
		get
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			return <PagePadding>k__BackingField;
		}
		set
		{
			//IL_000c: Unknown result type (might be due to invalid IL or missing references)
			((AvaloniaObject)this).SetAndRaise<Thickness>((DirectPropertyBase<Thickness>)(object)PagePaddingProperty, ref <PagePadding>k__BackingField, value);
		}
	}

	public string Text => string.Join("", Blocks.ToList().ConvertAll((Block b) => string.Join("", b.Text + Environment.NewLine)));

	public int DocEndPoint => ((Paragraph)Blocks.Last()).EndInDoc;

	public TextRange Selection { get; set; }

	internal IEnumerable<Paragraph> GetAllParagraphs => Blocks.SelectMany(delegate(Block b)
	{
		if (b is Paragraph item)
		{
			return new <>z__ReadOnlySingleElementList<Block>(item);
		}
		return (b is Table table) ? (table.Cells.Select((Cell c) => c.CellContent) ?? Enumerable.Empty<Paragraph>()) : Enumerable.Empty<Paragraph>();
	}).Cast<Paragraph>();

	internal ExtendMode SelectionExtendMode { get; set; }

	internal event ScrollInDirection_Handler? ScrollInDirection;

	public event SelectionChanged_Handler? Selection_Changed;

	internal event UpdateRTBCaret_Handler? UpdateRTBCaret;

	public void ScrollFlowDocInDirection(int direction)
	{
		this.ScrollInDirection?.Invoke(direction);
	}

	public FlowDocument()
	{
		Selection = new TextRange(this, 0, 0);
		Selection.Start_Changed += SelectionStart_Changed;
		Selection.End_Changed += SelectionEnd_Changed;
		DefineFormatRunActions();
		((AvaloniaObject)this).PropertyChanged += FlowDocument_PropertyChanged;
		InlineIdCounter = 0;
		Blocks.CollectionChanged += Blocks_CollectionChanged;
	}

	private void FlowDocument_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
	{
		if (e.Property == (AvaloniaProperty)(object)BlocksProperty)
		{
			Blocks.CollectionChanged -= Blocks_CollectionChanged;
			Blocks.CollectionChanged += Blocks_CollectionChanged;
		}
	}

	private void Blocks_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		foreach (Block block in Blocks)
		{
			block.MyFlowDoc = this;
			if (!(block is Table table))
			{
				continue;
			}
			foreach (Cell cell in table.Cells)
			{
				cell.CellContent.MyFlowDoc = this;
			}
		}
		AllParagraphs = GetAllParagraphs.ToList();
	}

	public void SelectAll()
	{
		Selection.Start = 0;
		Selection.End = 0;
		SelectionParagraphs.Clear();
		Selection.End = DocEndPoint - 1;
		EnsureSelectionContinuity();
		SelectionExtendMode = ExtendMode.ExtendModeRight;
	}

	public void Select(int Start, int Length)
	{
		SelectionParagraphs.Clear();
		Selection.Start = Start;
		Selection.End = Start + Length;
		EnsureSelectionContinuity();
		UpdateSelection();
	}

	internal void NewDocument()
	{
		ClearDocument();
		Paragraph paragraph = new Paragraph(this);
		EditableRun item = new EditableRun("");
		paragraph.Inlines.Add(item);
		Blocks.Add(paragraph);
		InitializeDocument();
	}

	internal void CreateTestDocument()
	{
		//IL_00e7: Unknown result type (might be due to invalid IL or missing references)
		ClearDocument();
		Paragraph paragraph = new Paragraph(this);
		paragraph.Inlines.Add(new EditableRun("A "));
		paragraph.Inlines.Add(new EditableRun("first"));
		paragraph.Inlines.Add(new EditableRun(" H"));
		ObservableCollection<IEditable> inlines = paragraph.Inlines;
		EditableRun editableRun = new EditableRun("2");
		((Inline)editableRun).BaselineAlignment = (BaselineAlignment)6;
		inlines.Add(editableRun);
		paragraph.Inlines.Add(new EditableRun("O"));
		ObservableCollection<IEditable> inlines2 = paragraph.Inlines;
		EditableRun editableRun2 = new EditableRun("3");
		((Inline)editableRun2).BaselineAlignment = (BaselineAlignment)7;
		inlines2.Add(editableRun2);
		paragraph.Inlines.Add(new EditableRun(" simple "));
		paragraph.Inlines.Add(new EditableRun("line."));
		Blocks.Add(paragraph);
		Blocks.Add(new Table(5, 4, this)
		{
			BorderThickness = new Thickness(1.0),
			BorderBrush = (ISolidColorBrush)(object)Brushes.ForestGreen,
			TableAlignment = (HorizontalAlignment)2
		});
		Paragraph paragraph2 = new Paragraph(this);
		paragraph2.Inlines.Add(new EditableRun("Some extra text after the table."));
		Blocks.Add(paragraph2);
		InitializeDocument();
	}

	internal void ClearDocument()
	{
		//IL_0063: Unknown result type (might be due to invalid IL or missing references)
		Blocks.Clear();
		ParagraphIdCounter = 0;
		InlineIdCounter = 0;
		for (int num = TextRanges.Count - 1; num >= 0; num--)
		{
			if (!TextRanges[num].Equals(Selection))
			{
				TextRanges[num].Dispose();
			}
		}
		PagePadding = new Thickness(0.0);
		Undos.Clear();
	}

	internal async void InitializeDocument()
	{
		Selection.Start = 0;
		Selection.CollapseToStart();
		UpdateBlockAndInlineStarts(0);
		Selection.BiasForwardStart = true;
		Selection.BiasForwardEnd = true;
		SelectionExtendMode = ExtendMode.ExtendModeNone;
		SelectionStart_Changed(Selection, 0);
		SelectionEnd_Changed(Selection, 0);
		await Task.Delay(70);
		Paragraph paragraph = AllParagraphs.ToList()[0];
		if (paragraph != null)
		{
			paragraph.CallRequestTextBoxFocus();
			paragraph.CallRequestTextLayoutInfoStart();
			paragraph.CallRequestTextLayoutInfoEnd();
		}
		this.UpdateRTBCaret?.Invoke();
	}

	internal string GetText(TextRange tRange)
	{
		return string.Join("", GetRangeInlines(tRange).ConvertAll((IEditable il) => il.InlineText));
	}

	internal List<Table> GetFullTablesInRange(TextRange trange)
	{
		return Blocks.Where((Block b) => b is Table table && table.StartInDoc > trange.Start && table.StartInDoc + table.BlockLength - 1 < trange.End).Cast<Table>().ToList();
	}

	internal List<Table> GetFulTablesInRange(int start, int end)
	{
		return Blocks.Where((Block b) => b is Table table && table.StartInDoc > start && table.StartInDoc + table.BlockLength - 1 < end).Cast<Table>().ToList();
	}

	internal List<Paragraph> GetFullParagraphsInRange(TextRange trange)
	{
		return AllParagraphs.Where((Paragraph b) => b.StartInDoc >= trange.Start && b.StartInDoc + b.BlockLength - 1 <= trange.End).ToList();
	}

	internal List<Paragraph> GetFullParagraphsInRange(int start, int end)
	{
		return AllParagraphs.Where((Paragraph b) => b.StartInDoc >= start && b.StartInDoc + b.BlockLength - 1 <= end).ToList();
	}

	internal List<Paragraph> GetOverlappingParagraphsInRange(TextRange trange)
	{
		return AllParagraphs.Where((Paragraph b) => b.StartInDoc <= trange.End && b.StartInDoc + b.BlockLength - 1 >= trange.Start).ToList();
	}

	internal List<Paragraph> GetOverlappingParagraphsInRange(int start, int end)
	{
		return AllParagraphs.Where((Paragraph b) => b.StartInDoc <= end && b.StartInDoc + b.BlockLength - 1 >= start).ToList();
	}

	internal Paragraph GetContainingParagraph(int charIndex)
	{
		return AllParagraphs.LastOrDefault((Paragraph p) => p.StartInDoc <= charIndex) ?? null;
	}

	internal void ResetSelectedParsLengthZero(Paragraph currPar)
	{
		if (Selection == null)
		{
			return;
		}
		foreach (Paragraph item in AllParagraphs.Where((Paragraph apar) => apar.StartInDoc >= Selection.StartParagraph.StartInDoc && apar.StartInDoc <= Selection.EndParagraph.StartInDoc))
		{
			if (item != currPar)
			{
				item.ClearSelection();
			}
		}
	}

	internal void DeleteChar(bool backspace)
	{
		int start = Selection.Start;
		if (Selection.StartParagraph.IsTableCellBlock && ((backspace && Selection.StartParagraph.SelectionStartInBlock == 0) || (!backspace && Selection.StartParagraph.SelectionStartInBlock >= Selection.StartParagraph.BlockLength - 1)))
		{
			return;
		}
		if (backspace)
		{
			MoveSelectionLeft(biasForward: true);
		}
		Selection.BiasForwardStart = true;
		Selection.BiasForwardEnd = true;
		IEditable startInline = Selection.GetStartInline();
		if (startInline == null)
		{
			return;
		}
		Paragraph startParagraph = Selection.StartParagraph;
		if (startParagraph.SelectionStartInBlock == startParagraph.TextLength)
		{
			MergeParagraphForward(Selection.Start, addUndo: true, start);
		}
		else
		{
			int num = startParagraph.Inlines.IndexOf(startInline);
			int num2 = 0;
			if (startInline is EditableInlineUIContainer editableInlineUIContainer)
			{
				bool emptyRunAdded = false;
				if (startParagraph.Inlines.Count == 1)
				{
					startParagraph.Inlines.Add(new EditableRun(""));
					emptyRunAdded = true;
				}
				Undos.Add(new DeleteImageUndo(startParagraph.Id, editableInlineUIContainer, num, this, start, emptyRunAdded));
				startParagraph.Inlines.Remove(editableInlineUIContainer);
			}
			else
			{
				bool flag = GetCharPosInInline(startInline, Selection.End) == startInline.InlineLength;
				EditableLineBreak editableLineBreak = GetNextInline(startInline) as EditableLineBreak;
				if (editableLineBreak != null && flag)
				{
					IEditable nextInline = GetNextInline(editableLineBreak);
					startParagraph.Inlines.Remove(editableLineBreak);
					if (nextInline != null && nextInline.IsEmpty)
					{
						startParagraph.Inlines.Remove(nextInline);
					}
					else if (startInline.IsEmpty)
					{
						startParagraph.Inlines.Remove(startInline);
					}
					Undos.Add(new DeleteLineBreakUndo(startParagraph.Id, editableLineBreak.Id, this, start));
				}
				else
				{
					if (startInline.InlineLength == 1 && !(GetNextInline(startInline) is EditableLineBreak))
					{
						if (startInline.CloneWithId() is EditableRun removedRunClone)
						{
							startParagraph.Inlines.Remove(startInline);
							Undos.Add(new DeleteRunUndo(startParagraph.Id, removedRunClone, num, this, start));
						}
					}
					else
					{
						num2 = GetCharPosInInline(startInline, Selection.Start);
						if (num2 < startInline.InlineLength)
						{
							startInline.InlineText = startInline.InlineText.Remove(num2, 1);
						}
					}
					if (startParagraph.Inlines.Count == 0)
					{
						startParagraph.Inlines.Add(new EditableRun(""));
					}
				}
			}
			UpdateTextRanges(Selection.Start, -1);
			UpdateBlockAndInlineStarts(AllParagraphs.ToList().IndexOf(startParagraph));
		}
		SelectionStart_Changed(Selection, Selection.Start);
		Selection.StartParagraph.CallRequestInlinesUpdate();
		Selection.StartParagraph.CallRequestTextLayoutInfoStart();
	}

	internal void DeleteSelection()
	{
		DeleteRange(Selection, addUndo: true);
		SelectionExtendMode = ExtendMode.ExtendModeNone;
		UpdateBlockAndInlineStarts(Selection.StartParagraph);
		Selection.CollapseToStart();
		Selection.BiasForwardStart = false;
		Selection.BiasForwardEnd = false;
	}

	internal void DeleteRange(TextRange trange, bool addUndo)
	{
		disableRunTextUndo = true;
		int start = Selection.Start;
		int length = trange.Length;
		List<Paragraph> overlappingParagraphsInRange = GetOverlappingParagraphsInRange(trange);
		List<Table> fullTablesInRange = GetFullTablesInRange(trange);
		List<Paragraph> fullParagraphsInRange = GetFullParagraphsInRange(trange);
		if (addUndo)
		{
			Undos.Add(new DeleteRangeUndo(overlappingParagraphsInRange.ConvertAll((Paragraph rpar) => rpar.FullClone()), overlappingParagraphsInRange[0].Id, this, start, length));
		}
		(int, int) edgeIds;
		foreach (IEditable toDeleteRun in GetRangeInlinesAndAddToDoc(trange, out edgeIds))
		{
			Paragraph paragraph = AllParagraphs.FirstOrDefault((Paragraph p) => p.Id == toDeleteRun.MyParagraphId);
			if (paragraph != null)
			{
				paragraph.Inlines.Remove(toDeleteRun);
				paragraph.CallRequestInlinesUpdate();
			}
		}
		foreach (Paragraph item in fullParagraphsInRange)
		{
			item.Inlines.Clear();
			item.Inlines.Add(new EditableRun(""));
			if (!item.IsTableCellBlock)
			{
				Blocks.Remove(item);
			}
		}
		ListEx.RemoveMany<Block>((IList<Block>)Blocks, (IEnumerable<Block>)fullTablesInRange);
		if (overlappingParagraphsInRange.Count == 1)
		{
			Paragraph paragraph2 = overlappingParagraphsInRange[0];
			if (paragraph2 != null && paragraph2.Inlines.Count == 0)
			{
				paragraph2.Inlines.Add(new EditableRun(""));
			}
		}
		if (overlappingParagraphsInRange.Count > 1)
		{
			Paragraph paragraph3 = overlappingParagraphsInRange[0];
			Paragraph paragraph4 = overlappingParagraphsInRange[overlappingParagraphsInRange.Count - 1];
			if (!paragraph3.IsTableCellBlock && !paragraph4.IsTableCellBlock)
			{
				List<IEditable> list = paragraph4.Inlines.ToList();
				ListEx.RemoveMany<IEditable>((IList<IEditable>)paragraph4.Inlines, (IEnumerable<IEditable>)list);
				paragraph4.CallRequestInlinesUpdate();
				ListEx.AddRange<IEditable>((IList<IEditable>)paragraph3.Inlines, (IEnumerable<IEditable>)list);
				paragraph3.CallRequestInlinesUpdate();
				Blocks.Remove(paragraph4);
			}
		}
		if (Blocks.Count == 1 && Blocks[0] is Paragraph paragraph5 && paragraph5.Inlines.Count == 0)
		{
			paragraph5.Inlines.Add(new EditableRun(""));
		}
		UpdateTextRanges(start, -length);
		UpdateSelection();
		trange.CollapseToStart();
		SelectionExtendMode = ExtendMode.ExtendModeNone;
		disableRunTextUndo = false;
	}

	internal void MergeParagraphForward(int mergeCharIndex, bool addUndo, int originalSelectionStart)
	{
		Paragraph containingParagraph = GetContainingParagraph(mergeCharIndex);
		if (containingParagraph == null)
		{
			return;
		}
		int num = Blocks.IndexOf(containingParagraph);
		if (num == Blocks.Count - 1)
		{
			return;
		}
		int origMergedParInlinesCount = containingParagraph.Inlines.Count;
		if (!(Blocks[num + 1] is Paragraph paragraph))
		{
			return;
		}
		bool num2 = paragraph.Inlines.Count == 1 && paragraph.Inlines[0].IsEmpty;
		bool flag = containingParagraph.Inlines.Count == 1 && containingParagraph.Inlines[0].IsEmpty;
		if (flag)
		{
			containingParagraph.Inlines.Clear();
			origMergedParInlinesCount = 0;
		}
		if (addUndo)
		{
			Undos.Add(new MergeParagraphUndo(origMergedParInlinesCount, containingParagraph.Id, paragraph.FullClone(), this, originalSelectionStart));
		}
		if (num2)
		{
			if (flag)
			{
				containingParagraph.Inlines.Add(new EditableRun(""));
			}
		}
		else
		{
			List<IEditable> list = paragraph.Inlines.ToList();
			paragraph.Inlines.Clear();
			paragraph.CallRequestInlinesUpdate();
			ListEx.AddRange<IEditable>((IList<IEditable>)containingParagraph.Inlines, (IEnumerable<IEditable>)list);
		}
		Blocks.Remove(paragraph);
		Selection.BiasForwardStart = true;
		Selection.BiasForwardEnd = true;
		UpdateTextRanges(mergeCharIndex, -1);
		containingParagraph.CallRequestInlinesUpdate();
		UpdateBlockAndInlineStarts(num);
		containingParagraph.CallRequestTextBoxFocus();
		UpdateSelectedParagraphs();
	}

	internal void DeleteWord(bool backspace)
	{
		if (backspace && (Selection.Start <= 0 || Selection.Start >= Selection.StartParagraph.StartInDoc + Selection.StartParagraph.BlockLength))
		{
			return;
		}
		int start = Selection.Start;
		if (backspace)
		{
			MoveLeftWord();
		}
		Selection.BiasForwardStart = true;
		Selection.BiasForwardEnd = true;
		Paragraph startParagraph = Selection.StartParagraph;
		if (startParagraph.SelectionStartInBlock == startParagraph.TextLength)
		{
			MergeParagraphForward(Selection.Start, addUndo: true, start);
		}
		else
		{
			int num = -1;
			IEditable startInline = Selection.GetStartInline();
			if (startInline != null && (startInline.IsUIContainer || startInline.IsLineBreak))
			{
				num = Selection.Start + 1;
			}
			else
			{
				int num2 = Selection.StartParagraph.Text.IndexOf(' ', Selection.Start - Selection.StartParagraph.StartInDoc);
				num2 = ((num2 != -1) ? (num2 + 1) : Selection.StartParagraph.TextLength);
				num = Selection.StartParagraph.StartInDoc + num2;
			}
			TextRange trange = new TextRange(this, Selection.Start, num);
			DeleteRange(trange, addUndo: true);
			UpdateBlockAndInlineStarts(AllParagraphs.IndexOf(startParagraph));
		}
		SelectionStart_Changed(Selection, Selection.Start);
		Selection.StartParagraph.CallRequestInlinesUpdate();
		Selection.StartParagraph.CallRequestTextLayoutInfoStart();
	}

	internal int PasteInlinesIntoRange(TextRange tRange, List<IEditable> newInlines)
	{
		disableRunTextUndo = true;
		int num = 0;
		Paragraph startParagraph = tRange.StartParagraph;
		int start = tRange.Start;
		int length = tRange.Length;
		int num2 = AllParagraphs.IndexOf(startParagraph);
		Undos.Add(new PasteUndo(GetOverlappingParagraphsInRange(tRange), num2, this, start, length - newInlines.Sum((IEditable nil) => nil.InlineLength)));
		if (tRange.Length > 0)
		{
			DeleteRange(tRange, addUndo: false);
		}
		IEditable startInline = tRange.GetStartInline();
		if (startInline == null)
		{
			return 0;
		}
		List<IEditable> list = SplitRunAtPos(tRange.Start, startInline, GetCharPosInInline(startInline, tRange.Start));
		startParagraph.Inlines.IndexOf(list[0]);
		int num3 = startParagraph.Inlines.IndexOf(list[0]) + 1;
		Paragraph paragraph = startParagraph;
		int num4 = 0;
		foreach (IEditable newInline in newInlines)
		{
			num4++;
			bool flag = false;
			if (newInline.InlineText.EndsWith("\r\n"))
			{
				string inlineText = newInline.InlineText;
				newInline.InlineText = inlineText.Substring(0, inlineText.Length - 1);
				flag = num4 > 1;
			}
			if (flag)
			{
				List<IEditable> list2 = paragraph.Inlines.Take(0..num3).ToList();
				ListEx.RemoveMany<IEditable>((IList<IEditable>)paragraph.Inlines, (IEnumerable<IEditable>)list2);
				paragraph = new Paragraph(this);
				ListEx.AddRange<IEditable>((IList<IEditable>)paragraph.Inlines, (IEnumerable<IEditable>)list2);
				num3 = paragraph.Inlines.Count;
				Blocks.Insert(num2, paragraph);
				paragraph.CallRequestInlinesUpdate();
				UpdateBlockAndInlineStarts(paragraph);
				num++;
			}
			paragraph.Inlines.Insert(num3, newInline);
			paragraph.CallRequestInlinesUpdate();
			UpdateBlockAndInlineStarts(paragraph);
			num += newInline.InlineLength;
		}
		if (list[0].InlineText == "")
		{
			startParagraph.Inlines.Remove(list[0]);
		}
		startParagraph.CallRequestInlinesUpdate();
		UpdateBlockAndInlineStarts(startParagraph);
		disableRunTextUndo = false;
		return num;
	}

	internal void SetRangeToText(TextRange tRange, string newText)
	{
		//IL_00d0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dd: Unknown result type (might be due to invalid IL or missing references)
		//IL_0111: Unknown result type (might be due to invalid IL or missing references)
		Paragraph startParagraph = tRange.StartParagraph;
		int start = tRange.Start;
		int length = tRange.Length;
		int parIndex = Blocks.IndexOf(startParagraph);
		Undos.Add(new PasteUndo(GetOverlappingParagraphsInRange(tRange), parIndex, this, start, length - newText.Length));
		if (tRange.Length > 0)
		{
			DeleteRange(tRange, addUndo: false);
			tRange.CollapseToStart();
			SelectionExtendMode = ExtendMode.ExtendModeNone;
		}
		IEditable startInline = tRange.GetStartInline();
		if (startInline == null)
		{
			return;
		}
		List<IEditable> list = SplitRunAtPos(tRange.Start, startInline, GetCharPosInInline(startInline, tRange.Start));
		int index = startParagraph.Inlines.IndexOf(list[0]) + 1;
		if (list[0] is EditableRun editableRun)
		{
			EditableRun editableRun2 = new EditableRun(newText);
			((TextElement)editableRun2).FontFamily = ((TextElement)editableRun).FontFamily;
			((TextElement)editableRun2).FontWeight = ((TextElement)editableRun).FontWeight;
			((TextElement)editableRun2).FontStyle = ((TextElement)editableRun).FontStyle;
			((TextElement)editableRun2).FontSize = ((TextElement)editableRun).FontSize;
			((Inline)editableRun2).TextDecorations = ((Inline)editableRun).TextDecorations;
			((TextElement)editableRun2).Background = ((TextElement)editableRun).Background;
			((Inline)editableRun2).BaselineAlignment = ((Inline)editableRun).BaselineAlignment;
			((TextElement)editableRun2).Foreground = ((TextElement)editableRun).Foreground;
			EditableRun item = editableRun2;
			startParagraph.Inlines.Insert(index, item);
			if (list[0].InlineText == "")
			{
				startParagraph.Inlines.Remove(list[0]);
			}
			startParagraph.CallRequestInlinesUpdate();
			UpdateBlockAndInlineStarts(startParagraph);
		}
	}

	internal void Undo()
	{
		if (Undos.Count > 0)
		{
			disableRunTextUndo = true;
			Undos.Last().PerformUndo();
			UpdateSelection();
			if (Undos.Last().UpdateTextRanges)
			{
				UpdateTextRanges(Selection.Start, Undos.Last().UndoEditOffset);
			}
			Undos.RemoveAt(Undos.Count - 1);
			UpdateSelectedParagraphs();
			this.ScrollInDirection?.Invoke(1);
			this.ScrollInDirection?.Invoke(-1);
			disableRunTextUndo = false;
		}
	}

	internal void RestoreDeletedBlocks(List<Paragraph> parClones, int blockIndex)
	{
		Blocks.RemoveAt(blockIndex);
		ListEx.AddOrInsertRange<Block>((IList<Block>)Blocks, (IEnumerable<Block>)parClones, blockIndex);
		foreach (Paragraph parClone in parClones)
		{
			parClone.CallRequestInlinesUpdate();
			parClone.ClearSelection();
		}
		UpdateBlockAndInlineStarts(blockIndex);
	}

	private void DefineFormatRunActions()
	{
		formatRunsActions = new Dictionary<AvaloniaProperty, FormatRunsAction>
		{
			{
				(AvaloniaProperty)(object)TextElement.FontFamilyProperty,
				ApplyFontFamilyRuns
			},
			{
				(AvaloniaProperty)(object)TextElement.FontWeightProperty,
				ApplyBoldRuns
			},
			{
				(AvaloniaProperty)(object)TextElement.FontStyleProperty,
				ApplyItalicRuns
			},
			{
				(AvaloniaProperty)(object)Inline.TextDecorationsProperty,
				ApplyTextDecorationRuns
			},
			{
				(AvaloniaProperty)(object)TextElement.FontSizeProperty,
				ApplyFontSizeRuns
			},
			{
				(AvaloniaProperty)(object)TextElement.BackgroundProperty,
				ApplyBackgroundRuns
			},
			{
				(AvaloniaProperty)(object)TextElement.ForegroundProperty,
				ApplyForegroundRuns
			},
			{
				(AvaloniaProperty)(object)TextElement.FontStretchProperty,
				ApplyFontStretchRuns
			},
			{
				(AvaloniaProperty)(object)Inline.BaselineAlignmentProperty,
				ApplyBaselineAlignmentRuns
			}
		};
		formatRunActions = new Dictionary<AvaloniaProperty, FormatRunAction>
		{
			{
				(AvaloniaProperty)(object)TextElement.FontFamilyProperty,
				ApplyFontFamilyRun
			},
			{
				(AvaloniaProperty)(object)TextElement.FontWeightProperty,
				ApplyBoldRun
			},
			{
				(AvaloniaProperty)(object)TextElement.FontStyleProperty,
				ApplyItalicRun
			},
			{
				(AvaloniaProperty)(object)Inline.TextDecorationsProperty,
				ApplyTextDecorationRun
			},
			{
				(AvaloniaProperty)(object)TextElement.FontSizeProperty,
				ApplyFontSizeRun
			},
			{
				(AvaloniaProperty)(object)TextElement.BackgroundProperty,
				ApplyBackgroundRun
			},
			{
				(AvaloniaProperty)(object)TextElement.ForegroundProperty,
				ApplyForegroundRun
			},
			{
				(AvaloniaProperty)(object)TextElement.FontStretchProperty,
				ApplyFontStretchRun
			},
			{
				(AvaloniaProperty)(object)Inline.BaselineAlignmentProperty,
				ApplyBaselineAlignmentRun
			}
		};
	}

	private void ToggleApplyBold(IEditable ied)
	{
		if (ied.GetType() == typeof(EditableRun))
		{
			((TextElement)(EditableRun)ied).FontWeight = (FontWeight)(BoldOn ? 700 : 400);
		}
	}

	private void ToggleApplyItalic(IEditable ied)
	{
		if (ied.GetType() == typeof(EditableRun))
		{
			((TextElement)(EditableRun)ied).FontStyle = (FontStyle)(ItalicOn ? 1 : 0);
		}
	}

	private void ToggleApplyUnderline(IEditable ied)
	{
		if (ied.GetType() == typeof(EditableRun))
		{
			((Inline)(EditableRun)ied).TextDecorations = (UnderliningOn ? TextDecorations.Underline : null);
		}
	}

	internal void ToggleItalic()
	{
		//IL_00d2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d8: Invalid comparison between Unknown and I4
		if (Selection.Length == 0)
		{
			ItalicOn = !ItalicOn;
			toggleFormatRun = ToggleApplyItalic;
			InsertRunMode = true;
			IEditable startInline = Selection.GetStartInline();
			if (startInline != null && startInline != Selection.StartParagraph.Inlines.Last() && GetCharPosInInline(startInline, Selection.Start) == startInline.InlineText.Length)
			{
				IEditable editable = Selection.StartParagraph.Inlines[Selection.StartParagraph.Inlines.IndexOf(startInline) + 1];
				bool flag = editable.GetType() == typeof(EditableRun) && (int)((TextElement)(EditableRun)editable).FontStyle == 1;
				InsertRunMode = ItalicOn != flag;
				Selection.BiasForwardStart = !InsertRunMode;
			}
		}
		else
		{
			Selection.ApplyFormatting((AvaloniaProperty)(object)TextElement.FontStyleProperty, (object)(FontStyle)1);
		}
	}

	internal void ToggleBold()
	{
		//IL_00d2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dc: Invalid comparison between Unknown and I4
		if (Selection.Length == 0)
		{
			toggleFormatRun = ToggleApplyBold;
			BoldOn = !BoldOn;
			InsertRunMode = true;
			IEditable startInline = Selection.GetStartInline();
			if (startInline != null && startInline != Selection.StartParagraph.Inlines.Last() && GetCharPosInInline(startInline, Selection.Start) == startInline.InlineText.Length)
			{
				IEditable editable = Selection.StartParagraph.Inlines[Selection.StartParagraph.Inlines.IndexOf(startInline) + 1];
				bool flag = editable.GetType() == typeof(EditableRun) && (int)((TextElement)(EditableRun)editable).FontWeight == 700;
				InsertRunMode = BoldOn != flag;
				Selection.BiasForwardStart = !InsertRunMode;
			}
		}
		else
		{
			Selection.ApplyFormatting((AvaloniaProperty)(object)TextElement.FontWeightProperty, (object)(FontWeight)700);
		}
	}

	internal void ToggleUnderlining()
	{
		if (Selection.Length == 0)
		{
			toggleFormatRun = ToggleApplyUnderline;
			UnderliningOn = !UnderliningOn;
			InsertRunMode = true;
			IEditable startInline = Selection.GetStartInline();
			if (startInline != null && startInline != Selection.StartParagraph.Inlines.Last() && GetCharPosInInline(startInline, Selection.Start) == startInline.InlineText.Length)
			{
				IEditable editable = Selection.StartParagraph.Inlines[Selection.StartParagraph.Inlines.IndexOf(startInline) + 1];
				bool flag = editable.GetType() == typeof(EditableRun) && ((Inline)(EditableRun)editable).TextDecorations == TextDecorations.Underline;
				InsertRunMode = UnderliningOn != flag;
				Selection.BiasForwardStart = !InsertRunMode;
			}
		}
		else
		{
			Selection.ApplyFormatting((AvaloniaProperty)(object)Inline.TextDecorationsProperty, TextDecorations.Underline);
		}
	}

	internal void ApplyFormattingRange(AvaloniaProperty avProperty, object value, TextRange textRange)
	{
		disableRunTextUndo = true;
		(int, int) edgeIds;
		List<IEditable> rangeInlinesAndAddToDoc = GetRangeInlinesAndAddToDoc(textRange, out edgeIds);
		List<IEditablePropertyAssociation> list = new List<IEditablePropertyAssociation>();
		foreach (EditableRun item in rangeInlinesAndAddToDoc.OfType<EditableRun>())
		{
			IEditablePropertyAssociation editablePropertyAssociation = new IEditablePropertyAssociation(item.MyParagraphId, item.Id, null, null);
			list.Add(editablePropertyAssociation);
			if (formatRunActions.TryGetValue(avProperty, out FormatRunAction value2))
			{
				editablePropertyAssociation.FormatRun = value2;
			}
			object value3 = ((AvaloniaObject)item).GetValue(avProperty);
			if (value3 != null)
			{
				editablePropertyAssociation.PropertyValue = value3;
			}
		}
		Undos.Add(new ApplyFormattingUndo(this, list, edgeIds, Selection.Start, textRange));
		if (formatRunsActions.TryGetValue(avProperty, out FormatRunsAction value4))
		{
			value4(rangeInlinesAndAddToDoc, value);
			UpdateBlockAndInlineStarts(AllParagraphs.IndexOf(AllParagraphs.LastOrDefault((Paragraph p) => p.StartInDoc <= textRange.Start)));
			foreach (Paragraph item2 in GetOverlappingParagraphsInRange(textRange).OfType<Paragraph>())
			{
				item2.CallRequestInlinesUpdate();
			}
			Selection.BiasForwardStart = true;
			Selection.BiasForwardEnd = true;
			Paragraph containingParagraph = GetContainingParagraph(Selection.Start);
			if (containingParagraph != null)
			{
				Selection.StartParagraph = containingParagraph;
				Selection.StartParagraph.SelectionStartInBlock = Selection.Start - Selection.StartParagraph.StartInDoc;
				Selection.EndParagraph.SelectionEndInBlock = Selection.End - Selection.EndParagraph.StartInDoc;
			}
			UpdateSelectedParagraphs();
			disableRunTextUndo = false;
			return;
		}
		throw new NotSupportedException("Formatting for " + avProperty.Name + " is not supported.");
	}

	internal void ApplyFormattingInline(FormatRunAction? formatRun, IEditable inlineItem, object value)
	{
		formatRun?.Invoke(inlineItem, value);
		Selection.BiasForwardStart = true;
		Selection.BiasForwardEnd = true;
	}

	private void ApplyFontFamilyRun(IEditable ied, object fontfamily)
	{
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0016: Expected O, but got Unknown
		if (ied is EditableRun editableRun)
		{
			((TextElement)editableRun).FontFamily = (FontFamily)fontfamily;
		}
	}

	private void ApplyBoldRun(IEditable ied, object fontWeight)
	{
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		if (ied is EditableRun editableRun)
		{
			((TextElement)editableRun).FontWeight = (FontWeight)fontWeight;
		}
	}

	private void ApplyItalicRun(IEditable ied, object fontStyle)
	{
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		if (ied is EditableRun editableRun)
		{
			((TextElement)editableRun).FontStyle = (FontStyle)fontStyle;
		}
	}

	private void ApplyTextDecorationRun(IEditable ied, object textDecoration)
	{
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0016: Expected O, but got Unknown
		if (ied is EditableRun editableRun)
		{
			((Inline)editableRun).TextDecorations = (TextDecorationCollection)textDecoration;
		}
	}

	private void ApplyFontSizeRun(IEditable ied, object fontsize)
	{
		if (ied is EditableRun editableRun)
		{
			((TextElement)editableRun).FontSize = (double)fontsize;
		}
	}

	private void ApplyBackgroundRun(IEditable ied, object background)
	{
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0016: Expected O, but got Unknown
		if (ied is EditableRun editableRun)
		{
			((TextElement)editableRun).Background = (IBrush)(ISolidColorBrush)background;
		}
	}

	private void ApplyForegroundRun(IEditable ied, object foreground)
	{
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0016: Expected O, but got Unknown
		if (ied is EditableRun editableRun)
		{
			((TextElement)editableRun).Foreground = (IBrush)(ISolidColorBrush)foreground;
		}
	}

	private void ApplyFontStretchRun(IEditable ied, object fontstretch)
	{
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		if (ied is EditableRun editableRun)
		{
			((TextElement)editableRun).FontStretch = (FontStretch)fontstretch;
		}
	}

	private void ApplyBaselineAlignmentRun(IEditable ied, object baselinealignment)
	{
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		if (ied is EditableRun editableRun)
		{
			((Inline)editableRun).BaselineAlignment = (BaselineAlignment)baselinealignment;
		}
	}

	private void ApplyFontFamilyRuns(List<IEditable> ieds, object fontfamily)
	{
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0025: Expected O, but got Unknown
		foreach (IEditable ied in ieds)
		{
			if (ied is EditableRun editableRun)
			{
				((TextElement)editableRun).FontFamily = (FontFamily)fontfamily;
			}
		}
	}

	private void ApplyBoldRuns(List<IEditable> ieds, object fontweight)
	{
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0019: Invalid comparison between Unknown and I4
		//IL_006e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0053: Unknown result type (might be due to invalid IL or missing references)
		FontWeight fontWeight = (FontWeight)400;
		if (fontweight is FontWeight && (int)(FontWeight)fontweight == 700)
		{
			fontWeight = (FontWeight)((!ieds.Where((IEditable ar) => ar is EditableRun editableRun2 && (int)((TextElement)editableRun2).FontWeight == 400).Any()) ? 400 : 700);
		}
		foreach (IEditable ied in ieds)
		{
			if (ied is EditableRun editableRun)
			{
				((TextElement)editableRun).FontWeight = fontWeight;
			}
		}
	}

	private void ApplyItalicRuns(List<IEditable> ieds, object fontstyle)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_000b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0011: Invalid comparison between Unknown and I4
		//IL_005e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0043: Unknown result type (might be due to invalid IL or missing references)
		FontStyle fontStyle = (FontStyle)0;
		if (fontstyle is FontStyle && (int)(FontStyle)fontstyle == 1)
		{
			fontStyle = (FontStyle)(ieds.Where((IEditable ar) => ar is EditableRun editableRun2 && (int)((TextElement)editableRun2).FontStyle == 0).Any() ? 1 : 0);
		}
		foreach (IEditable ied in ieds)
		{
			if (ied is EditableRun editableRun)
			{
				((TextElement)editableRun).FontStyle = fontStyle;
			}
		}
	}

	private void ApplyTextDecorationRuns(List<IEditable> ieds, object textdecoration)
	{
		TextDecorationCollection textDecorations = null;
		if (textdecoration == TextDecorations.Underline)
		{
			textDecorations = ((!ieds.Where((IEditable ar) => ar is EditableRun editableRun2 && ((Inline)editableRun2).TextDecorations == null).Any()) ? null : TextDecorations.Underline);
		}
		foreach (IEditable ied in ieds)
		{
			if (ied is EditableRun editableRun)
			{
				((Inline)editableRun).TextDecorations = textDecorations;
			}
		}
	}

	private void ApplyFontSizeRuns(List<IEditable> ieds, object fontsize)
	{
		foreach (IEditable ied in ieds)
		{
			if (ied is EditableRun editableRun)
			{
				((TextElement)editableRun).FontSize = (double)fontsize;
			}
		}
	}

	private void ApplyBackgroundRuns(List<IEditable> ieds, object background)
	{
		//IL_003d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0047: Expected O, but got Unknown
		if (background.GetType() != typeof(SolidColorBrush))
		{
			throw new Exception("Background must be set with a SolidColorBrush");
		}
		foreach (IEditable ied in ieds)
		{
			if (ied is EditableRun editableRun)
			{
				((TextElement)editableRun).Background = (IBrush)(SolidColorBrush)background;
			}
		}
	}

	private void ApplyForegroundRuns(List<IEditable> ieds, object foreground)
	{
		//IL_003d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0047: Expected O, but got Unknown
		if (foreground.GetType() != typeof(SolidColorBrush))
		{
			throw new Exception("Foreground must be set with a SolidColorBrush");
		}
		foreach (IEditable ied in ieds)
		{
			if (ied is EditableRun editableRun)
			{
				((TextElement)editableRun).Foreground = (IBrush)(SolidColorBrush)foreground;
			}
		}
	}

	private void ApplyFontStretchRuns(List<IEditable> ieds, object fontstretch)
	{
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		foreach (IEditable ied in ieds)
		{
			if (ied is EditableRun editableRun)
			{
				((TextElement)editableRun).FontStretch = (FontStretch)fontstretch;
			}
		}
	}

	private void ApplyBaselineAlignmentRuns(List<IEditable> ieds, object baselinealignment)
	{
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		foreach (IEditable ied in ieds)
		{
			if (ied is EditableRun editableRun)
			{
				((Inline)editableRun).BaselineAlignment = (BaselineAlignment)baselinealignment;
			}
		}
	}

	internal void ResetInsertFormatting()
	{
		InsertRunMode = false;
		BoldOn = false;
		ItalicOn = false;
		UnderliningOn = false;
	}

	internal int GetCharPosInInline(IEditable inline, int absPos)
	{
		Paragraph paragraph = AllParagraphs.FirstOrDefault((Paragraph p) => p.Id == inline.MyParagraphId);
		if (paragraph == null)
		{
			return -1;
		}
		return absPos - paragraph.StartInDoc - inline.TextPositionOfInlineInParagraph;
	}

	internal List<IEditable> GetRangeInlines(TextRange trange)
	{
		Paragraph startPar = trange.GetStartPar();
		if (startPar == null)
		{
			return new List<IEditable>();
		}
		Paragraph endPar = trange.GetEndPar();
		if (endPar == null)
		{
			return new List<IEditable>();
		}
		List<IEditable> list = AllParagraphs.SelectMany((Paragraph p) => p.Inlines.Where(delegate(IEditable iline)
		{
			double num4 = p.StartInDoc + iline.TextPositionOfInlineInParagraph;
			return (double)(p.StartInDoc + iline.TextPositionOfInlineInParagraph + iline.InlineLength) > (double)trange.Start && num4 < (double)trange.End;
		})).ToList().ConvertAll(delegate(IEditable il)
		{
			IEditable editable3 = il.Clone();
			if (il.IsLastInlineOfParagraph)
			{
				editable3.InlineText += Environment.NewLine;
			}
			return editable3;
		});
		if (list.Count == 0)
		{
			list = AllParagraphs.SelectMany((Paragraph p) => p.Inlines.Where((IEditable iline) => p.StartInDoc + iline.TextPositionOfInlineInParagraph + iline.InlineLength >= trange.Start && p.StartInDoc + iline.TextPositionOfInlineInParagraph < trange.End)).ToList().ConvertAll((IEditable il) => il.Clone());
		}
		IEditable editable = list[0];
		int num = Math.Min(trange.Start - startPar.StartInDoc - editable.TextPositionOfInlineInParagraph, editable.InlineText.Length);
		if (list.Count == 1)
		{
			int num2 = trange.End - endPar.StartInDoc - editable.TextPositionOfInlineInParagraph;
			object inlineText2;
			if (!editable.IsEmpty)
			{
				string inlineText = editable.InlineText;
				int num3 = num;
				inlineText2 = inlineText.Substring(num3, num2 - num3);
			}
			else
			{
				inlineText2 = "";
			}
			editable.InlineText = (string)inlineText2;
		}
		else
		{
			List<IEditable> list2 = list;
			IEditable editable2 = list2[list2.Count - 1];
			int length = trange.End - endPar.StartInDoc - editable2.TextPositionOfInlineInParagraph;
			string inlineText3 = editable.InlineText;
			int num3 = num;
			editable.InlineText = inlineText3.Substring(num3, inlineText3.Length - num3);
			editable2.InlineText = editable2.InlineText.Substring(0, length);
		}
		return list;
	}

	internal List<IEditable> GetRangeInlinesAndAddToDoc(TextRange trange, out (int idLeft, int idRight) edgeIds)
	{
		edgeIds = default((int, int));
		List<IEditable> list = AllParagraphs.SelectMany((Paragraph p) => p.Inlines.Where(delegate(IEditable iline)
		{
			int num5 = p.StartInDoc + iline.TextPositionOfInlineInParagraph;
			return num5 + iline.InlineLength > trange.Start && num5 < trange.End;
		})).ToList();
		if (list.Count == 0)
		{
			list = AllParagraphs.SelectMany((Paragraph p) => p.Inlines.Where(delegate(IEditable iline)
			{
				int num5 = p.StartInDoc + iline.TextPositionOfInlineInParagraph;
				return num5 + iline.InlineLength >= trange.Start && num5 < trange.End;
			})).ToList();
		}
		if (list.Count != 0)
		{
			Paragraph startPar = trange.GetStartPar();
			if (startPar != null)
			{
				Paragraph endPar = trange.GetEndPar();
				if (endPar != null)
				{
					IEditable editable = list[0];
					List<IEditable> list2 = list;
					IEditable editable2 = list2[list2.Count - 1];
					IEditable editable3 = editable2.Clone();
					IEditable editable4 = editable.Clone();
					edgeIds.idLeft = editable.Id;
					edgeIds.idRight = editable2.Id;
					int num = trange.End - endPar.StartInDoc - editable2.TextPositionOfInlineInParagraph;
					int num2 = trange.Start - startPar.StartInDoc - editable.TextPositionOfInlineInParagraph;
					bool flag = num >= editable2.InlineLength;
					string inlineText = editable2.InlineText;
					string inlineText2 = editable.InlineText;
					int index = endPar.Inlines.IndexOf(editable2);
					if (list.Count == 1)
					{
						if (!flag)
						{
							editable3.InlineText = inlineText.Substring(0, num);
							string text = inlineText;
							int num3 = num;
							editable2.InlineText = text.Substring(num3, text.Length - num3);
							list.Remove(editable2);
							list.Add(editable3);
							endPar.Inlines.Insert(index, editable3);
							inlineText2 = editable3.InlineText;
							editable4 = editable3.Clone();
							num2 = Math.Min(num2, inlineText2.Length);
						}
						if (num2 > 0)
						{
							editable4.InlineText = inlineText2.Substring(0, num2);
							string text = inlineText2;
							int num3 = num2;
							editable3.InlineText = text.Substring(num3, text.Length - num3);
							startPar.Inlines.Insert(index, editable4);
							edgeIds.idLeft = editable4.Id;
							if (flag)
							{
								text = inlineText2;
								num3 = num2;
								editable2.InlineText = text.Substring(num3, text.Length - num3);
							}
						}
					}
					else
					{
						if (!flag)
						{
							editable3.InlineText = inlineText.Substring(0, num);
							string text = inlineText;
							int num3 = num;
							editable2.InlineText = text.Substring(num3, text.Length - num3);
							list.Remove(editable2);
							list.Add(editable3);
							endPar.Inlines.Insert(index, editable3);
							num2 = Math.Min(num2, inlineText2.Length);
						}
						int num4 = startPar.Inlines.IndexOf(editable);
						if (num2 > 0)
						{
							editable.InlineText = inlineText2.Substring(0, num2);
							IEditable editable5 = editable4;
							string text = inlineText2;
							int num3 = num2;
							editable5.InlineText = text.Substring(num3, text.Length - num3);
							list.Remove(editable);
							list.Insert(0, editable4);
							startPar.Inlines.Insert(num4 + 1, editable4);
						}
					}
					startPar.CallRequestInlinesUpdate();
					endPar.CallRequestInlinesUpdate();
					UpdateBlockAndInlineStarts(AllParagraphs.IndexOf(startPar));
					return list;
				}
			}
		}
		return new List<IEditable>();
	}

	internal List<IEditable> SplitRunAtPos(int charPos, IEditable inlineToSplit, int splitPos)
	{
		Paragraph containingParagraph = GetContainingParagraph(charPos);
		if (containingParagraph == null)
		{
			return new List<IEditable>();
		}
		ObservableCollection<IEditable> inlines = containingParagraph.Inlines;
		int num = inlines.IndexOf(inlineToSplit);
		string inlineText = inlineToSplit.InlineText;
		int num2 = splitPos;
		string inlineText2 = inlineText.Substring(num2, inlineText.Length - num2);
		inlineToSplit.InlineText = inlineToSplit.InlineText.Substring(0, splitPos);
		IEditable editable = inlineToSplit.Clone();
		editable.InlineText = inlineText2;
		inlines.Insert(num + 1, editable);
		num2 = 2;
		List<IEditable> list = new List<IEditable>(num2);
		CollectionsMarshal.SetCount(list, num2);
		Span<IEditable> span = CollectionsMarshal.AsSpan(list);
		span[0] = inlineToSplit;
		span[1] = editable;
		return list;
	}

	internal Paragraph? GetNextParagraph(Paragraph par)
	{
		List<Paragraph> allParagraphs = AllParagraphs;
		int num = allParagraphs.IndexOf(par);
		if (num == allParagraphs.Count - 1)
		{
			return null;
		}
		return allParagraphs[num + 1] ?? null;
	}

	internal Paragraph? GetPreviousParagraph(Paragraph par)
	{
		List<Paragraph> allParagraphs = AllParagraphs;
		int num = allParagraphs.IndexOf(par);
		if (num != 0)
		{
			return allParagraphs[num - 1];
		}
		return null;
	}

	internal IEditable? GetStartInline(int charIndex)
	{
		List<Paragraph> allParagraphs = AllParagraphs;
		Paragraph startPar = allParagraphs.LastOrDefault((Paragraph b) => b.StartInDoc <= charIndex);
		if (startPar != null)
		{
			if (startPar != allParagraphs.Last() && startPar.EndInDoc == charIndex)
			{
				return null;
			}
			IEditable result = null;
			IEditable editable = startPar.Inlines.LastOrDefault((IEditable ied) => startPar.StartInDoc + ied.TextPositionOfInlineInParagraph <= charIndex);
			if (editable != null)
			{
				IEditable editable2 = startPar.Inlines.LastOrDefault((IEditable ied) => !ied.IsLineBreak && startPar.StartInDoc + ied.TextPositionOfInlineInParagraph <= charIndex);
				if (editable2 != null)
				{
					result = editable2;
				}
			}
			return result;
		}
		return null;
	}

	internal IEditable? GetNextInline(IEditable inline)
	{
		Paragraph paragraph = AllParagraphs.FirstOrDefault((Paragraph p) => p.Id == inline.MyParagraphId);
		if (paragraph == null)
		{
			return null;
		}
		IEditable result = null;
		int num = paragraph.Inlines.IndexOf(inline);
		if (num < paragraph.Inlines.Count - 1)
		{
			result = paragraph.Inlines[num + 1];
		}
		else
		{
			Paragraph nextParagraph = GetNextParagraph(paragraph);
			if (nextParagraph == null)
			{
				return null;
			}
			if (nextParagraph.Inlines.Count > 0)
			{
				result = nextParagraph.Inlines[0];
			}
		}
		return result;
	}

	internal IEditable? GetPreviousInline(IEditable inline)
	{
		Paragraph paragraph = AllParagraphs.FirstOrDefault((Paragraph p) => p.Id == inline.MyParagraphId);
		if (paragraph == null)
		{
			return null;
		}
		IEditable result = null;
		int num = paragraph.Inlines.IndexOf(inline);
		if (num > 0)
		{
			result = paragraph.Inlines[num - 1];
		}
		else
		{
			Paragraph previousParagraph = GetPreviousParagraph(paragraph);
			if (previousParagraph == null)
			{
				return null;
			}
			if (previousParagraph.Inlines.Count > 0)
			{
				result = previousParagraph.Inlines.Last();
			}
		}
		return result;
	}

	internal void InsertText(string? insertText)
	{
		IEditable editable = Selection.GetStartInline();
		if (editable == null || editable.GetType() == typeof(EditableInlineUIContainer) || insertText == null)
		{
			return;
		}
		if (Selection.Length > 0)
		{
			DeleteRange(Selection, addUndo: true);
			Selection.CollapseToStart();
			SelectionExtendMode = ExtendMode.ExtendModeNone;
			editable = Selection.GetStartInline() ?? editable;
		}
		int num = 0;
		if (InsertRunMode)
		{
			(int, int) edgeIds;
			List<IEditable> rangeInlinesAndAddToDoc = GetRangeInlinesAndAddToDoc(Selection, out edgeIds);
			if (rangeInlinesAndAddToDoc.Count == 0)
			{
				rangeInlinesAndAddToDoc.Add(new EditableRun(""));
				Selection.StartParagraph.Inlines.Insert(0, rangeInlinesAndAddToDoc[0]);
			}
			editable = rangeInlinesAndAddToDoc[0];
			editable.InlineText = insertText;
			toggleFormatRun(editable);
			InsertRunMode = false;
		}
		else
		{
			try
			{
				num = GetCharPosInInline(editable, Selection.Start);
				editable.InlineText = editable.InlineText.Insert(num, insertText);
			}
			catch
			{
			}
		}
		UpdateTextRanges(Selection.Start, insertText.Length);
		Selection.StartParagraph.CallRequestInlinesUpdate();
		UpdateBlockAndInlineStarts(Selection.StartParagraph);
		for (int i = 0; i < insertText.Length; i++)
		{
			MoveSelectionRight(isTextInsertion: true);
		}
	}

	internal void InsertLineBreak()
	{
		Paragraph startParagraph = Selection.StartParagraph;
		if (startParagraph.Inlines.Count != 1 || !(startParagraph.Inlines[0] is EditableInlineUIContainer))
		{
			IEditable startInline = Selection.GetStartInline();
			if (startInline != null)
			{
				int num = startParagraph.Inlines.IndexOf(startInline);
				IEditable origInlineClone = startInline.CloneWithId();
				List<IEditable> list = SplitRunAtPos(Selection.Start, startInline, GetCharPosInInline(startInline, Selection.Start));
				EditableLineBreak editableLineBreak = new EditableLineBreak();
				startParagraph.Inlines.Insert(num + 1, editableLineBreak);
				Undos.Add(new InsertLineBreakUndo(Selection.StartParagraph.Id, editableLineBreak.Id, (addedInlineLeftId: list[0].Id, addedInlineRightId: list[1].Id), num, origInlineClone, this, Selection.Start));
				UpdateTextRanges(Selection.Start, 1);
				SelectionExtendMode = ExtendMode.ExtendModeNone;
				startParagraph.UpdateEditableRunPositions();
				startParagraph.CallRequestInlinesUpdate();
				startParagraph.CallRequestTextLayoutInfoStart();
				startParagraph.CallRequestTextLayoutInfoEnd();
				Select(Selection.Start + 2, 0);
				Selection.BiasForwardStart = true;
				Selection.BiasForwardEnd = true;
				this.ScrollInDirection?.Invoke(1);
			}
		}
	}

	internal void InsertParagraph(bool addUndo, int insertCharIndex)
	{
		disableRunTextUndo = true;
		Paragraph containingParagraph = GetContainingParagraph(insertCharIndex);
		if (containingParagraph == null || containingParagraph.IsTableCellBlock || (containingParagraph.Inlines.Count == 1 && containingParagraph.Inlines[0] is EditableInlineUIContainer))
		{
			return;
		}
		List<IEditable> keepParInlines = containingParagraph.Inlines.Select((IEditable il) => il.CloneWithId()).ToList();
		int num = Blocks.IndexOf(containingParagraph);
		int num2 = 0;
		if (addUndo)
		{
			num2 = Selection.Length;
			if (Selection.Length > 0)
			{
				DeleteRange(Selection, addUndo: false);
				Selection.CollapseToStart();
				SelectionExtendMode = ExtendMode.ExtendModeNone;
			}
		}
		IEditable startInline = GetStartInline(insertCharIndex);
		if (startInline != null)
		{
			int num3 = containingParagraph.Inlines.IndexOf(startInline);
			List<IEditable> list = SplitRunAtPos(insertCharIndex, startInline, GetCharPosInInline(startInline, insertCharIndex));
			List<IEditable> list2 = containingParagraph.Inlines.Take(0..num3).ToList().ConvertAll((IEditable r) => r)
				.ToList();
			if (list[0].InlineText != "" || list2.Count == 0)
			{
				list2.Add(list[0]);
			}
			List<IEditable> list3 = containingParagraph.Inlines.Take((num3 + 1)..containingParagraph.Inlines.Count).ToList().ConvertAll((IEditable r) => r)
				.ToList();
			Paragraph paragraph = containingParagraph;
			paragraph.Inlines.Clear();
			ListEx.AddRange<IEditable>((IList<IEditable>)paragraph.Inlines, (IEnumerable<IEditable>)list2);
			paragraph.SelectionStartInBlock = 0;
			paragraph.CollapseToStart();
			if (paragraph.Inlines.Last() is EditableLineBreak)
			{
				paragraph.Inlines.Insert(paragraph.Inlines.Count, new EditableRun(""));
			}
			Paragraph paragraph2 = paragraph.PropertyClone();
			ListEx.AddRange<IEditable>((IList<IEditable>)paragraph2.Inlines, (IEnumerable<IEditable>)list3);
			Blocks.Insert(num + 1, paragraph2);
			if (paragraph2.Inlines.Count == 0)
			{
				EditableRun editableRun = (EditableRun)paragraph.Inlines.Last().Clone();
				((Run)editableRun).Text = "";
				paragraph2.Inlines.Add(editableRun);
			}
			UpdateTextRanges(insertCharIndex, 1);
			UpdateBlockAndInlineStarts(num);
			paragraph.CallRequestInlinesUpdate();
			paragraph2.CallRequestInlinesUpdate();
			if (addUndo)
			{
				Undos.Add(new InsertParagraphUndo(this, paragraph.Id, paragraph2.Id, keepParInlines, insertCharIndex, num2 - 1));
			}
			Selection.BiasForwardStart = true;
			Selection.BiasForwardEnd = true;
			Selection.End++;
			Selection.CollapseToEnd();
			paragraph.CallRequestTextLayoutInfoStart();
			paragraph2.CallRequestTextLayoutInfoStart();
			paragraph.CallRequestTextLayoutInfoEnd();
			paragraph2.CallRequestTextLayoutInfoEnd();
			this.ScrollInDirection?.Invoke(1);
			disableRunTextUndo = false;
		}
	}

	[GeneratedRegex("\\\\o \".*?\"")]
	[GeneratedCode("System.Text.RegularExpressions.Generator", "10.0.14.15411")]
	private static Regex RemoveOverstrikeRegex()
	{
		return <RegexGenerator_g>F0338A28AE0D740519125F99FE91ED2E2A886FDAFB89901337C90E33E98CB422E__RemoveOverstrikeRegex_0.Instance;
	}

	internal void LoadRtf(string rtfContent)
	{
		RTFDomDocument rTFDomDocument = new RTFDomDocument();
		if (rtfContent.Contains("\\o "))
		{
			rtfContent = rtfContent.Replace("\\o \"}", "\\o \"\"}").Replace(" \"}", " }");
			rtfContent = RemoveOverstrikeRegex().Replace(rtfContent, "\\o\"\"");
		}
		using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(rtfContent));
		using StreamReader streamReader = new StreamReader(stream);
		rTFDomDocument.Load(streamReader.BaseStream);
		try
		{
			ClearDocument();
			RtfConversions.GetFlowDocumentFromRtf(rTFDomDocument, this);
			InitializeDocument();
		}
		catch (Exception)
		{
		}
	}

	internal void LoadRtfFromFile(string fileName)
	{
		try
		{
			string rtfContent = File.ReadAllText(fileName);
			LoadRtf(rtfContent);
		}
		catch (Exception ex)
		{
			if (ex.HResult == -2147024864)
			{
				throw new IOException("The file:\n" + fileName + "\ncannot be opened because it is currently in use by another application.", ex);
			}
		}
	}

	internal void SaveRtfToFile(string fileName)
	{
		try
		{
			string contents = SaveRtf();
			File.WriteAllText(fileName, contents, Encoding.Default);
		}
		catch (Exception)
		{
		}
	}

	internal string SaveRtf()
	{
		return RtfConversions.GetRtfFromFlowDocument(this);
	}

	internal void SaveXamlToFile(string fileName)
	{
		File.WriteAllText(fileName, SaveXaml());
	}

	internal void LoadXamlFromFile(string fileName)
	{
		string xamlContent = File.ReadAllText(fileName);
		LoadXaml(xamlContent);
	}

	internal string SaveXaml()
	{
		return XamlConversions.GetDocXaml(isXamlPackage: false, this);
	}

	internal void LoadXaml(string xamlContent)
	{
		ClearDocument();
		XamlConversions.ProcessXamlString(xamlContent, this);
		InitializeDocument();
	}

	internal void SaveHtmlDocToFile(string fileName)
	{
		HtmlConversions.GetHtmlFromFlowDocument(this).Save(fileName);
	}

	internal string SaveHtml()
	{
		return HtmlConversions.GetHtmlFromFlowDocument(this).DocumentNode.OuterHtml;
	}

	internal void LoadHtmlDocFromFile(string fileName)
	{
		try
		{
			LoadHtml(File.ReadAllText(fileName));
		}
		catch (Exception ex)
		{
			if (ex.HResult == -2147024864)
			{
				throw new IOException("The file:\n" + fileName + "\ncannot be opened because it is currently in use by another application.\n" + ex.Message);
			}
		}
	}

	internal void LoadHtml(string htmlContent)
	{
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_000b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0018: Expected O, but got Unknown
		try
		{
			ClearDocument();
			HtmlDocument val = new HtmlDocument();
			val.LoadHtml(htmlContent);
			HtmlConversions.GetFlowDocumentFromHtml(val, this);
			InitializeDocument();
		}
		catch (Exception)
		{
		}
	}

	internal void SaveWordDocToFile(string fileName)
	{
		WordConversions.SaveWordDoc(fileName, this);
	}

	internal void LoadWordDocFromFile(string fileName)
	{
		try
		{
			WordprocessingDocument val = WordprocessingDocument.Open(fileName, false);
			try
			{
				ClearDocument();
				WordConversions.GetFlowDocument(val.MainDocumentPart, this);
				InitializeDocument();
			}
			catch (Exception)
			{
			}
			finally
			{
				((IDisposable)val)?.Dispose();
			}
		}
		catch (Exception ex2)
		{
			if (ex2.HResult == -2147024864)
			{
				throw new IOException("The file:\n" + fileName + "\ncannot be opened because it is currently in use by another application.", ex2);
			}
		}
	}

	internal void LoadXamlPackage(string fileName)
	{
		ClearDocument();
		XamlConversions.LoadXamlPackage(fileName, this);
		InitializeDocument();
	}

	internal void SaveXamlPackage(string fileName)
	{
		XamlConversions.SaveXamlPackage(fileName, this);
	}

	public void SaveRangeToXamlStream(TextRange trange, Stream stream)
	{
		StringBuilder stringBuilder = new StringBuilder(XamlConversions.SectionTextDefault);
		stringBuilder.Append(XamlConversions.GetParagraphRunsXaml(GetRangeInlinesAndAddToDoc(trange, out (int, int) _), isXamlPackage: false));
		stringBuilder.Append("</Section>");
		byte[] bytes = Encoding.UTF8.GetBytes(stringBuilder.ToString());
		stream.Write(bytes, 0, bytes.Length);
	}

	internal static void LoadXamlStreamIntoRange(Stream stream, TextRange trange)
	{
		byte[] array = new byte[stream.Length];
		stream.ReadExactly(array);
		Encoding.UTF8.GetString(array, 0, array.Length);
	}

	internal void UpdateSelection()
	{
		UpdateBlockAndInlineStarts(Selection.StartParagraph);
		Selection.StartParagraph.CallRequestInlinesUpdate();
		Selection.StartParagraph.CallRequestTextLayoutInfoStart();
		Selection.EndParagraph.CallRequestInlinesUpdate();
		Selection.EndParagraph.CallRequestTextLayoutInfoEnd();
	}

	internal void SelectionStart_Changed(TextRange selRange, int newStart)
	{
		Paragraph containingParagraph = GetContainingParagraph(newStart);
		if (containingParagraph != null)
		{
			selRange.StartParagraph = containingParagraph;
			containingParagraph.SelectionStartInBlock = newStart - containingParagraph.StartInDoc;
			containingParagraph.CallRequestTextLayoutInfoStart();
		}
		UpdateSelectedParagraphs();
		if (selRange.Length > 0 && selRange.StartParagraph.SelectionEndInBlock < selRange.StartParagraph.SelectionStartInBlock)
		{
			selRange.StartParagraph.SelectionEndInBlock = selRange.StartParagraph.SelectionStartInBlock;
		}
		selRange.GetStartInline();
		selRange.StartParagraph?.CallRequestTextLayoutInfoStart();
		this.Selection_Changed?.Invoke(selRange);
	}

	internal void SelectionEnd_Changed(TextRange selRange, int newEnd)
	{
		Paragraph containingParagraph = GetContainingParagraph(newEnd);
		if (containingParagraph != null)
		{
			selRange.EndParagraph = containingParagraph;
			containingParagraph.SelectionEndInBlock = newEnd - containingParagraph.StartInDoc;
			containingParagraph.CallRequestTextLayoutInfoEnd();
		}
		UpdateSelectedParagraphs();
		selRange.GetEndInline();
		selRange.EndParagraph?.CallRequestTextLayoutInfoEnd();
		this.Selection_Changed?.Invoke(selRange);
	}

	internal void ExtendSelectionRight()
	{
		Selection.BiasForwardEnd = true;
		switch (SelectionExtendMode)
		{
		case ExtendMode.ExtendModeNone:
		case ExtendMode.ExtendModeRight:
		{
			SelectionExtendMode = ExtendMode.ExtendModeRight;
			Paragraph endParagraph = Selection.EndParagraph;
			List<Paragraph> list = AllParagraphs.ToList();
			if (endParagraph == list[list.Count - 1] && Selection.EndParagraph.SelectionEndInBlock == Selection.EndParagraph.TextLength)
			{
				return;
			}
			Selection.End++;
			break;
		}
		case ExtendMode.ExtendModeLeft:
			Selection.Start++;
			if (Selection.Start == Selection.End)
			{
				SelectionExtendMode = ExtendMode.ExtendModeRight;
			}
			break;
		}
		this.ScrollInDirection?.Invoke(1);
	}

	internal void ExtendSelectionLeft()
	{
		Selection.BiasForwardEnd = false;
		switch (SelectionExtendMode)
		{
		case ExtendMode.ExtendModeNone:
		case ExtendMode.ExtendModeLeft:
			if (Selection.Start == 0)
			{
				return;
			}
			Selection.Start--;
			SelectionExtendMode = ExtendMode.ExtendModeLeft;
			break;
		case ExtendMode.ExtendModeRight:
			if (Selection.End == 0)
			{
				return;
			}
			Selection.End--;
			if (Selection.Start == Selection.End)
			{
				SelectionExtendMode = ExtendMode.ExtendModeLeft;
			}
			break;
		}
		this.ScrollInDirection?.Invoke(-1);
	}

	internal void ExtendSelectionDown()
	{
		Selection.BiasForwardEnd = true;
		List<Paragraph> allParagraphs = AllParagraphs;
		switch (SelectionExtendMode)
		{
		case ExtendMode.ExtendModeNone:
		case ExtendMode.ExtendModeRight:
		{
			SelectionExtendMode = ExtendMode.ExtendModeRight;
			if (Selection.EndParagraph == allParagraphs[allParagraphs.Count - 1] && Selection.End == Text.Length)
			{
				return;
			}
			Paragraph endParagraph = Selection.EndParagraph;
			int num2 = Selection.EndParagraph.StartInDoc + Selection.EndParagraph.CharNextLineEnd;
			if (Selection.EndParagraph.IsEndAtLastLine)
			{
				if (Selection.EndParagraph != allParagraphs[allParagraphs.Count - 1])
				{
					int index = Blocks.IndexOf(Selection.EndParagraph) + 1;
					Paragraph paragraph2 = allParagraphs[index];
					Selection.End = Math.Min(paragraph2.StartInDoc + paragraph2.BlockLength - 1, num2);
				}
			}
			else
			{
				Selection.End = num2;
			}
			if (Selection.EndParagraph != endParagraph)
			{
				endParagraph.SelectionEndInBlock = endParagraph.TextLength;
				Selection.EndParagraph.SelectionStartInBlock = 0;
			}
			break;
		}
		case ExtendMode.ExtendModeLeft:
		{
			if (Selection.StartParagraph == allParagraphs[allParagraphs.Count - 1] && Selection.StartParagraph.IsStartAtLastLine)
			{
				return;
			}
			int num = Selection.StartParagraph.StartInDoc + Selection.StartParagraph.CharNextLineStart;
			if (AllParagraphs.IndexOf(Selection.StartParagraph) < AllParagraphs.Count - 1)
			{
				Paragraph paragraph = allParagraphs[allParagraphs.IndexOf(Selection.StartParagraph) + 1];
				int val = Selection.StartParagraph.SelectionStartInBlock - Selection.StartParagraph.FirstIndexLastLine;
				if (Selection.StartParagraph.IsStartAtLastLine)
				{
					val = Math.Min(val, paragraph.TextLength);
					num = paragraph.StartInDoc + val;
					Selection.StartParagraph.CollapseToStart();
				}
			}
			if (num > Selection.End)
			{
				int end = Selection.End;
				Selection.End = num;
				Selection.Start = end;
				SelectionExtendMode = ExtendMode.ExtendModeRight;
			}
			else
			{
				Selection.Start = num;
			}
			break;
		}
		}
		this.ScrollInDirection?.Invoke(1);
	}

	internal void ExtendSelectionUp()
	{
		Paragraph paragraph = null;
		Selection.BiasForwardEnd = false;
		List<Paragraph> allParagraphs = AllParagraphs;
		switch (SelectionExtendMode)
		{
		case ExtendMode.ExtendModeNone:
		case ExtendMode.ExtendModeLeft:
		{
			if (Selection.StartParagraph == allParagraphs[0] && Selection.StartParagraph.IsStartAtFirstLine)
			{
				return;
			}
			Paragraph startParagraph = Selection.StartParagraph;
			if (Selection.StartParagraph.IsStartAtFirstLine)
			{
				paragraph = allParagraphs[allParagraphs.IndexOf(Selection.StartParagraph) - 1];
				Selection.Start = Math.Min(paragraph.StartInDoc + paragraph.BlockLength - 2, paragraph.StartInDoc + paragraph.FirstIndexLastLine + Selection.StartParagraph.CharPrevLineStart);
			}
			else
			{
				Selection.Start = Selection.StartParagraph.StartInDoc + Selection.StartParagraph.CharPrevLineStart;
			}
			SelectionExtendMode = ExtendMode.ExtendModeLeft;
			if (Selection.StartParagraph != startParagraph)
			{
				startParagraph.SelectionStartInBlock = 0;
				Selection.StartParagraph.SelectionEndInBlock = Selection.StartParagraph.TextLength;
			}
			break;
		}
		case ExtendMode.ExtendModeRight:
		{
			int num = Selection.EndParagraph.StartInDoc + Selection.EndParagraph.CharPrevLineEnd;
			if (AllParagraphs.IndexOf(Selection.EndParagraph) > 0)
			{
				paragraph = allParagraphs[allParagraphs.IndexOf(Selection.EndParagraph) - 1];
				int selectionEndInBlock = Selection.EndParagraph.SelectionEndInBlock;
				if (Selection.EndParagraph.IsEndAtFirstLine)
				{
					selectionEndInBlock = Math.Min(selectionEndInBlock, paragraph.TextLength);
					num = paragraph.StartInDoc + paragraph.FirstIndexLastLine + selectionEndInBlock;
					Selection.EndParagraph.CollapseToStart();
				}
			}
			if (num < Selection.Start)
			{
				int start = Selection.Start;
				Selection.Start = num;
				Selection.End = start;
				SelectionExtendMode = ExtendMode.ExtendModeLeft;
			}
			else
			{
				Selection.End = num;
			}
			break;
		}
		}
		this.ScrollInDirection?.Invoke(-1);
	}

	internal void EnsureSelectionContinuity()
	{
		foreach (Paragraph item in AllParagraphs.Where((Paragraph p) => !SelectionParagraphs.Contains(p)))
		{
			item.ClearSelection();
		}
		if (SelectionParagraphs.Count > 1)
		{
			for (int num = 0; num < SelectionParagraphs.Count; num++)
			{
				Paragraph paragraph = SelectionParagraphs[num];
				int num2 = num;
				if (num2 == 0)
				{
					paragraph.SelectionEndInBlock = paragraph.BlockLength;
					continue;
				}
				if (num2 == SelectionParagraphs.Count - 1)
				{
					paragraph.SelectionStartInBlock = 0;
					continue;
				}
				paragraph.SelectionStartInBlock = 0;
				paragraph.SelectionEndInBlock = paragraph.BlockLength;
			}
		}
		foreach (Paragraph selectionParagraph in SelectionParagraphs)
		{
			if (selectionParagraph.IsTableCellBlock)
			{
				selectionParagraph.OwningCell.Selected = selectionParagraph.SelectionStartInBlock == 0 && selectionParagraph.SelectionEndInBlock == selectionParagraph.BlockLength;
			}
		}
	}

	internal void MoveSelectionRight(bool isTextInsertion)
	{
		if (Selection.Length > 0)
		{
			ResetSelectedParsLengthZero(Selection.EndParagraph);
		}
		Selection.BiasForwardStart = !isTextInsertion;
		switch (SelectionExtendMode)
		{
		case ExtendMode.ExtendModeNone:
		{
			Block containingParagraph = GetContainingParagraph(Selection.End);
			List<Paragraph> list = AllParagraphs.ToList();
			if (containingParagraph == list[list.Count - 1] && containingParagraph.SelectionEndInBlock == containingParagraph.BlockLength - 1)
			{
				return;
			}
			if (!isTextInsertion && (Selection.IsAtLineBreak || Selection.IsAtCellBreak))
			{
				Selection.End++;
				Selection.CollapseToEnd();
				Selection.BiasForwardStart = !isTextInsertion;
				Selection.BiasForwardEnd = Selection.BiasForwardStart;
			}
			Selection.End++;
			break;
		}
		case ExtendMode.ExtendModeRight:
			Selection.End = Math.Min(Selection.End, DocEndPoint - 1);
			break;
		}
		Selection.CollapseToEnd();
		SelectionExtendMode = ExtendMode.ExtendModeNone;
		this.ScrollInDirection?.Invoke(1);
		Selection.BiasForwardStart = !isTextInsertion;
		Selection.BiasForwardEnd = Selection.BiasForwardStart;
	}

	internal void MoveSelectionLeft(bool biasForward)
	{
		Selection.BiasForwardStart = true;
		if (Selection.Length > 0)
		{
			ResetSelectedParsLengthZero(Selection.StartParagraph);
		}
		switch (SelectionExtendMode)
		{
		case ExtendMode.ExtendModeNone:
			if (Selection.Start == 0)
			{
				return;
			}
			Selection.Start--;
			if (Selection.IsAtLineBreak || Selection.IsAtCellBreak)
			{
				Selection.Start--;
				Selection.CollapseToStart();
			}
			break;
		case ExtendMode.ExtendModeRight:
		case ExtendMode.ExtendModeLeft:
			Selection.CollapseToStart();
			break;
		}
		Selection.BiasForwardEnd = Selection.BiasForwardStart;
		Selection.CollapseToStart();
		SelectionExtendMode = ExtendMode.ExtendModeNone;
		this.ScrollInDirection?.Invoke(-1);
	}

	internal int GetRelativeTextPos(IEditable inline, int absTextPos)
	{
		Block block = AllParagraphs.FirstOrDefault((Paragraph p) => p.Id == inline.MyParagraphId);
		if (block == null)
		{
			return -1;
		}
		return absTextPos - block.StartInDoc - inline.TextPositionOfInlineInParagraph;
	}

	internal void MoveRightWord()
	{
		if (Selection.Start >= Selection.StartParagraph.StartInDoc + Selection.StartParagraph.BlockLength)
		{
			return;
		}
		Selection.BiasForwardStart = true;
		Selection.BiasForwardEnd = true;
		Paragraph paragraph = Selection.StartParagraph;
		if (paragraph.SelectionStartInBlock == paragraph.TextLength)
		{
			Selection.End++;
		}
		else
		{
			IEditable editable = Selection.GetStartInline();
			if (editable != null)
			{
				if (editable.IsUIContainer || editable.IsLineBreak)
				{
					Selection.End++;
				}
				else
				{
					int num = Selection.Start;
					int num2 = GetRelativeTextPos(editable, num);
					bool flag = false;
					do
					{
						int? num3 = editable?.InlineText.IndexOf(' ', num2);
						if (!num3.HasValue)
						{
							break;
						}
						int valueOrDefault = num3.GetValueOrDefault();
						if (valueOrDefault == -1)
						{
							num += editable.InlineLength - num2;
							editable = GetNextInline(editable) ?? null;
							if (editable == null)
							{
								continue;
							}
							if (!(editable is EditableRun))
							{
								if (editable is EditableLineBreak)
								{
									editable = GetNextInline(editable) ?? null;
									num += 2;
									num2 = 0;
									flag = true;
								}
								else
								{
									num2 = editable.InlineLength;
									flag = true;
								}
							}
							else if (paragraph.Id != editable.MyParagraphId)
							{
								paragraph = GetNextParagraph(paragraph);
								editable = paragraph.Inlines[0];
								num = paragraph.StartInDoc + editable.TextPositionOfInlineInParagraph;
								num2 = 0;
								flag = true;
							}
							else
							{
								num2 = GetRelativeTextPos(editable, num);
							}
							continue;
						}
						num += valueOrDefault;
						num2 = valueOrDefault;
						break;
					}
					while (!flag);
					if (!flag)
					{
						num2++;
					}
					int num4 = 0;
					num4 = ((editable == null) ? (paragraph.StartInDoc + paragraph.BlockLength) : (paragraph.StartInDoc + editable.TextPositionOfInlineInParagraph + num2));
					Selection.Start = num4;
					Selection.End = num4;
				}
			}
		}
		Selection.CollapseToEnd();
		this.ScrollInDirection?.Invoke(1);
	}

	internal void MoveLeftWord()
	{
		if (Selection.Start <= 0)
		{
			return;
		}
		Selection.BiasForwardStart = false;
		Selection.BiasForwardEnd = false;
		int num = -1;
		Paragraph startParagraph = Selection.StartParagraph;
		if (startParagraph.SelectionStartInBlock == 0)
		{
			Selection.Start--;
		}
		else
		{
			Selection.Start--;
			Selection.CollapseToStart();
			startParagraph = Selection.StartParagraph;
			IEditable startInline = Selection.GetStartInline();
			if (startInline != null && !startInline.IsUIContainer)
			{
				num = startParagraph.Text.LastIndexOfAny(" \n".ToCharArray(), startParagraph.SelectionStartInBlock - 1);
				num = ((num != -1) ? (num + 1) : 0);
				int start = Selection.StartParagraph.StartInDoc + num;
				Selection.Start = start;
			}
		}
		Selection.CollapseToStart();
		this.ScrollInDirection?.Invoke(-1);
	}

	internal void MoveSelectionDown(bool biasForward)
	{
		Selection.BiasForwardStart = biasForward;
		if (Selection.Length > 0)
		{
			ResetSelectedParsLengthZero(Selection.EndParagraph);
			Selection.CollapseToEnd();
		}
		int num = Selection.EndParagraph.StartInDoc + Selection.EndParagraph.CharNextLineEnd;
		if (Selection.EndParagraph.IsEndAtLastLine)
		{
			List<Paragraph> allParagraphs = AllParagraphs;
			if (Selection.EndParagraph != allParagraphs[allParagraphs.Count - 1])
			{
				int index = allParagraphs.IndexOf(Selection.EndParagraph) + 1;
				Paragraph paragraph = allParagraphs[index];
				Selection.End = Math.Min(paragraph.StartInDoc + paragraph.BlockLength - 1, num);
			}
		}
		else
		{
			Selection.End = num;
		}
		Selection.CollapseToEnd();
		SelectionExtendMode = ExtendMode.ExtendModeNone;
		this.ScrollInDirection?.Invoke(1);
	}

	internal void MoveSelectionUp(bool biasForward)
	{
		Selection.BiasForwardStart = biasForward;
		if (Selection.Length > 0)
		{
			ResetSelectedParsLengthZero(Selection.StartParagraph);
			Selection.CollapseToStart();
		}
		if (Selection.StartParagraph.IsStartAtFirstLine)
		{
			List<Paragraph> allParagraphs = AllParagraphs;
			if (Selection.StartParagraph != allParagraphs[0])
			{
				int index = allParagraphs.IndexOf(Selection.StartParagraph) - 1;
				Paragraph paragraph = allParagraphs[index];
				if (paragraph != null)
				{
					Selection.Start = Math.Min(paragraph.StartInDoc + paragraph.BlockLength - 1, paragraph.StartInDoc + paragraph.FirstIndexLastLine + Selection.StartParagraph.CharPrevLineStart);
				}
			}
		}
		else
		{
			Selection.Start = Selection.StartParagraph.StartInDoc + Selection.StartParagraph.CharPrevLineStart;
		}
		Selection.CollapseToStart();
		SelectionExtendMode = ExtendMode.ExtendModeNone;
		this.ScrollInDirection?.Invoke(-1);
	}

	internal void MoveToDocStart()
	{
		Selection.BiasForwardStart = true;
		Selection.BiasForwardEnd = true;
		Selection.Start = 0;
		Selection.CollapseToStart();
		SelectionExtendMode = ExtendMode.ExtendModeNone;
		this.ScrollInDirection?.Invoke(-1);
		List<Paragraph> allParagraphs = AllParagraphs;
		foreach (Paragraph item in allParagraphs)
		{
			item.ClearSelection();
		}
		Paragraph paragraph = allParagraphs[0];
		if (paragraph != null)
		{
			paragraph.CallRequestTextLayoutInfoStart();
			paragraph.CallRequestTextLayoutInfoEnd();
		}
	}

	internal void MoveToDocEnd()
	{
		Selection.BiasForwardStart = false;
		Selection.BiasForwardEnd = false;
		List<Paragraph> allParagraphs = AllParagraphs;
		Selection.End = allParagraphs[allParagraphs.Count - 1].StartInDoc + allParagraphs[allParagraphs.Count - 1].BlockLength - 1;
		Selection.CollapseToEnd();
		SelectionExtendMode = ExtendMode.ExtendModeNone;
		this.ScrollInDirection?.Invoke(1);
		foreach (Paragraph item in allParagraphs)
		{
			item.ClearSelection();
		}
		Paragraph paragraph = allParagraphs[allParagraphs.Count - 1];
		if (paragraph != null)
		{
			paragraph.SelectionStartInBlock = paragraph.BlockLength - 1;
			paragraph.SelectionEndInBlock = paragraph.BlockLength - 1;
		}
		Selection.Start = 0;
		Selection.CollapseToStart();
		Select(DocEndPoint - 1, 0);
		this.UpdateRTBCaret?.Invoke();
	}

	internal void MoveToStartOfLine(bool selExtend)
	{
		Selection.BiasForwardStart = true;
		Selection.BiasForwardEnd = true;
		Selection.Start = Selection.StartParagraph.StartInDoc + Selection.StartParagraph.FirstIndexStartLine;
		if (!selExtend)
		{
			if (Selection.Length > 0)
			{
				ResetSelectedParsLengthZero(Selection.StartParagraph);
			}
			Selection.CollapseToStart();
		}
		else
		{
			SelectionExtendMode = ExtendMode.ExtendModeLeft;
		}
		this.ScrollInDirection?.Invoke(-1);
	}

	internal void MoveToEndOfLine(bool selExtend)
	{
		Selection.BiasForwardStart = false;
		Selection.BiasForwardEnd = false;
		if (Selection.StartParagraph.TextLength == 0)
		{
			return;
		}
		Paragraph endParagraph = Selection.EndParagraph;
		if (endParagraph.IsEndAtLastLine)
		{
			Selection.End = Selection.EndParagraph.StartInDoc + endParagraph.BlockLength - 1;
		}
		else
		{
			Selection.End = Selection.EndParagraph.StartInDoc + endParagraph.LastIndexEndLine;
		}
		string text = endParagraph.Text;
		if (endParagraph.LastIndexEndLine <= text.Length && (text[endParagraph.LastIndexEndLine] == ' ' || HelperMethods.IsCJKChar(text[endParagraph.LastIndexEndLine])))
		{
			Selection.IsAtEndOfLineSpace = true;
			Selection.End++;
		}
		if (!selExtend)
		{
			if (Selection.Length > 0)
			{
				ResetSelectedParsLengthZero(Selection.EndParagraph);
			}
			Selection.CollapseToEnd();
		}
		else
		{
			SelectionExtendMode = ExtendMode.ExtendModeRight;
		}
		this.ScrollInDirection?.Invoke(1);
		Selection.BiasForwardStart = false;
		Selection.BiasForwardEnd = Selection.BiasForwardStart;
		Selection.IsAtEndOfLineSpace = false;
		IEditable startInline = Selection.GetStartInline();
		IEditable? obj = ((startInline == null) ? null : GetNextInline(startInline));
		Selection.IsAtLineBreak = obj?.IsLineBreak ?? false;
	}

	internal void MovePageSelection(int direction, bool extend, int newIndexInDoc)
	{
		newIndexInDoc = Math.Min(newIndexInDoc, DocEndPoint - 1);
		switch (direction)
		{
		case 1:
			if (extend)
			{
				switch (SelectionExtendMode)
				{
				case ExtendMode.ExtendModeNone:
				case ExtendMode.ExtendModeRight:
					Selection.End = newIndexInDoc;
					SelectionExtendMode = ExtendMode.ExtendModeRight;
					break;
				case ExtendMode.ExtendModeLeft:
					if (newIndexInDoc > Selection.End)
					{
						SelectionExtendMode = ExtendMode.ExtendModeRight;
					}
					Selection.Start = newIndexInDoc;
					break;
				}
				EnsureSelectionContinuity();
			}
			else
			{
				Selection.End = newIndexInDoc;
				Selection.CollapseToEnd();
			}
			break;
		case -1:
			if (extend)
			{
				switch (SelectionExtendMode)
				{
				case ExtendMode.ExtendModeNone:
				case ExtendMode.ExtendModeLeft:
					Selection.Start = newIndexInDoc;
					SelectionExtendMode = ExtendMode.ExtendModeLeft;
					break;
				case ExtendMode.ExtendModeRight:
					if (newIndexInDoc < Selection.Start)
					{
						SelectionExtendMode = ExtendMode.ExtendModeLeft;
					}
					Selection.End = newIndexInDoc;
					break;
				}
				EnsureSelectionContinuity();
			}
			else
			{
				Selection.Start = newIndexInDoc;
				Selection.CollapseToStart();
			}
			break;
		}
	}

	internal void UpdateCaret()
	{
		Selection.StartParagraph.CallRequestTextLayoutInfoStart();
		Selection.StartParagraph.CallRequestTextLayoutInfoEnd();
		Selection.EndParagraph.CallRequestTextLayoutInfoStart();
		Selection.EndParagraph.CallRequestTextLayoutInfoEnd();
	}

	internal void UpdateBlockAndInlineStarts(int fromBlockIndex)
	{
		if (fromBlockIndex >= Blocks.Count)
		{
			return;
		}
		int num = ((fromBlockIndex != 0) ? (Blocks[fromBlockIndex - 1].StartInDoc + Blocks[fromBlockIndex - 1].BlockLength) : 0);
		for (int i = fromBlockIndex; i < Blocks.Count; i++)
		{
			Blocks[i].StartInDoc = num;
			Block block = Blocks[i];
			if (!(block is Paragraph paragraph))
			{
				if (block is Table table)
				{
					int num2 = 0;
					foreach (Cell cell in table.Cells)
					{
						if (cell.CellContent is Paragraph paragraph2)
						{
							paragraph2.StartInDoc = num + num2;
							paragraph2.UpdateEditableRunPositions();
							num2 += paragraph2.BlockLength;
						}
					}
				}
			}
			else
			{
				paragraph.UpdateEditableRunPositions();
			}
			num += Blocks[i].BlockLength;
		}
	}

	internal void UpdateBlockAndInlineStarts(Paragraph thisPar)
	{
		int num = -1;
		num = ((!thisPar.IsTableCellBlock) ? Blocks.IndexOf(thisPar) : Blocks.IndexOf(thisPar.OwningTable));
		if (num > -1)
		{
			UpdateBlockAndInlineStarts(num);
		}
	}

	internal void UpdateSelectedParagraphs()
	{
		SelectionParagraphs.Clear();
		ListEx.AddRange<Paragraph>((IList<Paragraph>)SelectionParagraphs, AllParagraphs.Where((Paragraph p) => p.StartInDoc + p.BlockLength > Selection.Start && p.StartInDoc <= Selection.End));
	}

	internal void UpdateTextRanges(int editCharIndexStart, int offset)
	{
		List<TextRange> list = new List<TextRange>();
		int num = ((offset == 1) ? editCharIndexStart : (editCharIndexStart - offset));
		foreach (TextRange textRange in TextRanges)
		{
			if (textRange.Equals(Selection))
			{
				continue;
			}
			if (textRange.Start >= editCharIndexStart && textRange.End <= num)
			{
				list.Add(textRange);
				continue;
			}
			if (textRange.Start >= editCharIndexStart)
			{
				if (textRange.Start >= num)
				{
					textRange.Start += offset;
				}
				else
				{
					textRange.Start = editCharIndexStart;
				}
			}
			if (textRange.End >= editCharIndexStart)
			{
				if (textRange.End >= num)
				{
					textRange.End += offset;
				}
				else
				{
					textRange.End = editCharIndexStart;
				}
			}
			if (textRange.Start > textRange.End)
			{
				textRange.End = textRange.Start;
			}
		}
		for (int num2 = list.Count - 1; num2 >= 0; num2--)
		{
			if (!list[num2].Equals(Selection))
			{
				list[num2].Dispose();
			}
		}
	}
}
