using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.RegularExpressions.Generated;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.Markup.Xaml.XamlIl.Runtime;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.VisualTree;
using CompiledAvaloniaXaml;
using RtfDomParserAv;

namespace AvRichTextBox;

public class RichTextBox : UserControl
{
	public class RichTextBoxTextInputClient(RichTextBox owner) : TextInputMethodClient
	{
		private readonly RichTextBox _owner = owner;

		public override Rect CursorRectangle
		{
			get
			{
				//IL_0006: Unknown result type (might be due to invalid IL or missing references)
				//IL_000b: Unknown result type (might be due to invalid IL or missing references)
				//IL_0024: Unknown result type (might be due to invalid IL or missing references)
				//IL_0029: Unknown result type (might be due to invalid IL or missing references)
				//IL_0048: Unknown result type (might be due to invalid IL or missing references)
				Point caretPosition = _owner.CaretPosition;
				double num = ((Point)(ref caretPosition)).X + 12.0;
				caretPosition = _owner.CaretPosition;
				return new Rect(num, GetAdjustedCaretY(((Point)(ref caretPosition)).Y), 1.0, 0.0);
			}
		}

		public override bool SupportsPreedit => true;

		public override bool SupportsSurroundingText => false;

		public override string SurroundingText => "";

		public override TextSelection Selection
		{
			get
			{
				//IL_002a: Unknown result type (might be due to invalid IL or missing references)
				return new TextSelection(_owner.FlowDoc.Selection.Start, _owner.FlowDoc.Selection.End);
			}
			set
			{
			}
		}

		public override Visual TextViewVisual => null;

		private void RichTextBoxTextInputClient_TextViewVisualChanged(object? sender, EventArgs e)
		{
		}

		private void RichTextBoxTextInputClient_SelectionChanged(object? sender, EventArgs e)
		{
		}

		private double GetAdjustedCaretY(double yval)
		{
			//IL_0007: Unknown result type (might be due to invalid IL or missing references)
			//IL_000c: Unknown result type (might be due to invalid IL or missing references)
			Rect bounds = ((Visual)_owner).Bounds;
			if (!(yval > ((Rect)(ref bounds)).Bottom - 200.0))
			{
				return yval + 22.0;
			}
			return yval - 200.0;
		}

		public void UpdateCaretPosition()
		{
			((TextInputMethodClient)this).RaiseCursorRectangleChanged();
		}

		public override void SetPreeditText(string? preeditText)
		{
			_owner.InsertPreeditText(preeditText);
		}
	}

	[CompilerGenerated]
	private class XamlClosure_2
	{
		public static object Build_1(IServiceProvider P_0)
		{
			//IL_0008: Unknown result type (might be due to invalid IL or missing references)
			//IL_0012: Expected O, but got Unknown
			//IL_002b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0030: Unknown result type (might be due to invalid IL or missing references)
			//IL_0049: Unknown result type (might be due to invalid IL or missing references)
			//IL_004e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0067: Unknown result type (might be due to invalid IL or missing references)
			//IL_006c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0085: Unknown result type (might be due to invalid IL or missing references)
			//IL_008a: Unknown result type (might be due to invalid IL or missing references)
			//IL_0099: Unknown result type (might be due to invalid IL or missing references)
			//IL_009e: Unknown result type (might be due to invalid IL or missing references)
			//IL_00a0: Expected O, but got Unknown
			//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
			//IL_00a6: Expected O, but got Unknown
			//IL_00ab: Expected O, but got Unknown
			//IL_00b8: Unknown result type (might be due to invalid IL or missing references)
			//IL_00bd: Unknown result type (might be due to invalid IL or missing references)
			XamlIlContext.Context<RichTextBox> context = CreateContext(P_0);
			context.IntermediateRoot = (object)new Border();
			object obj = context.IntermediateRoot;
			((ISupportInitialize)obj).BeginInit();
			AvaloniaObjectExtensions.Bind((AvaloniaObject)obj, (AvaloniaProperty)(object)Border.BackgroundProperty, new TemplateBinding((AvaloniaProperty)(object)TemplatedControl.BackgroundProperty).ProvideValue(), (object)null);
			AvaloniaObjectExtensions.Bind((AvaloniaObject)obj, (AvaloniaProperty)(object)Border.BorderBrushProperty, new TemplateBinding((AvaloniaProperty)(object)TemplatedControl.BorderBrushProperty).ProvideValue(), (object)null);
			AvaloniaObjectExtensions.Bind((AvaloniaObject)obj, (AvaloniaProperty)(object)Border.BorderThicknessProperty, new TemplateBinding((AvaloniaProperty)(object)TemplatedControl.BorderThicknessProperty).ProvideValue(), (object)null);
			AvaloniaObjectExtensions.Bind((AvaloniaObject)obj, (AvaloniaProperty)(object)Decorator.PaddingProperty, new TemplateBinding((AvaloniaProperty)(object)TemplatedControl.PaddingProperty).ProvideValue(), (object)null);
			ItemsPresenter val = new ItemsPresenter();
			ItemsPresenter val2 = val;
			((ISupportInitialize)val).BeginInit();
			((Decorator)obj).Child = (Control)val;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)val2, (AvaloniaProperty)(object)ItemsPresenter.ItemsPanelProperty, new TemplateBinding((AvaloniaProperty)(object)ItemsControl.ItemsPanelProperty).ProvideValue(), (object)null);
			((ISupportInitialize)val2).EndInit();
			((ISupportInitialize)obj).EndInit();
			return obj;
		}

		public static XamlIlContext.Context<RichTextBox> CreateContext(IServiceProvider P_0)
		{
			//IL_003d: Unknown result type (might be due to invalid IL or missing references)
			XamlIlContext.Context<RichTextBox> context = new XamlIlContext.Context<RichTextBox>(P_0, new object[1] { !AvaloniaResources.NamespaceInfo:/RichTextBox/RichTextBox.axaml.Singleton }, "avares://AvRichTextBox/RichTextBox/RichTextBox.axaml");
			if (P_0 != null)
			{
				object service = P_0.GetService(typeof(IRootObjectProvider));
				if (service != null)
				{
					service = ((IRootObjectProvider)service).RootObject;
					context.RootObject = (RichTextBox)service;
				}
			}
			return context;
		}

		public static object Build_2(IServiceProvider P_0)
		{
			//IL_0008: Unknown result type (might be due to invalid IL or missing references)
			//IL_0012: Expected O, but got Unknown
			XamlIlContext.Context<RichTextBox> context = CreateContext(P_0);
			context.IntermediateRoot = (object)new StackPanel();
			object obj = context.IntermediateRoot;
			((ISupportInitialize)obj).BeginInit();
			((AvaloniaObject)obj).SetValue<Orientation>(StackPanel.OrientationProperty, (Orientation)1, (BindingPriority)2);
			((ISupportInitialize)obj).EndInit();
			return obj;
		}

		public unsafe static object Build_3(IServiceProvider P_0)
		{
			//IL_0008: Unknown result type (might be due to invalid IL or missing references)
			//IL_0012: Expected O, but got Unknown
			//IL_002a: Unknown result type (might be due to invalid IL or missing references)
			//IL_002f: Unknown result type (might be due to invalid IL or missing references)
			//IL_005c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0061: Unknown result type (might be due to invalid IL or missing references)
			//IL_0063: Expected O, but got Unknown
			//IL_0097: Expected O, but got Unknown
			//IL_009c: Unknown result type (might be due to invalid IL or missing references)
			//IL_00a1: Unknown result type (might be due to invalid IL or missing references)
			//IL_00a3: Expected O, but got Unknown
			//IL_00d7: Expected O, but got Unknown
			XamlIlContext.Context<RichTextBox> context = CreateContext(P_0);
			context.IntermediateRoot = (object)new ContentControl();
			object obj = context.IntermediateRoot;
			((ISupportInitialize)obj).BeginInit();
			ContentControl val = (ContentControl)obj;
			context.PushParent(val);
			ReflectionBindingExtension val2 = new ReflectionBindingExtension();
			context.ProvideTargetProperty = ContentControl.ContentProperty;
			Binding obj2 = val2.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			XamlDynamicSetters.<>XamlDynamicSetter_4(val, obj2);
			((Layoutable)val).HorizontalAlignment = (HorizontalAlignment)0;
			DataTemplates dataTemplates = ((Control)val).DataTemplates;
			DataTemplate val3 = new DataTemplate();
			DataTemplate val4 = val3;
			context.PushParent(val4);
			DataTemplate obj3 = val4;
			obj3.DataType = typeof(Paragraph);
			obj3.Content = XamlIlRuntimeHelpers.DeferredTransformationFactoryV3<Control>((IntPtr)(nint)(delegate*<IServiceProvider, object>)(&Build_4), (IServiceProvider)context);
			context.PopParent();
			((AvaloniaList<IDataTemplate>)(object)dataTemplates).Add((IDataTemplate)val3);
			DataTemplates dataTemplates2 = ((Control)val).DataTemplates;
			DataTemplate val5 = new DataTemplate();
			val4 = val5;
			context.PushParent(val4);
			DataTemplate obj4 = val4;
			obj4.DataType = typeof(Table);
			obj4.Content = XamlIlRuntimeHelpers.DeferredTransformationFactoryV3<Control>((IntPtr)(nint)(delegate*<IServiceProvider, object>)(&Build_5), (IServiceProvider)context);
			context.PopParent();
			((AvaloniaList<IDataTemplate>)(object)dataTemplates2).Add((IDataTemplate)val5);
			context.PopParent();
			((ISupportInitialize)obj).EndInit();
			return obj;
		}

		public static object Build_4(IServiceProvider P_0)
		{
			//IL_0008: Unknown result type (might be due to invalid IL or missing references)
			//IL_0012: Expected O, but got Unknown
			//IL_0034: Unknown result type (might be due to invalid IL or missing references)
			//IL_0039: Unknown result type (might be due to invalid IL or missing references)
			//IL_0065: Unknown result type (might be due to invalid IL or missing references)
			//IL_006a: Unknown result type (might be due to invalid IL or missing references)
			//IL_00b0: Unknown result type (might be due to invalid IL or missing references)
			//IL_00c5: Unknown result type (might be due to invalid IL or missing references)
			//IL_00ca: Unknown result type (might be due to invalid IL or missing references)
			//IL_011e: Unknown result type (might be due to invalid IL or missing references)
			//IL_01c6: Unknown result type (might be due to invalid IL or missing references)
			//IL_01cb: Unknown result type (might be due to invalid IL or missing references)
			//IL_01f7: Unknown result type (might be due to invalid IL or missing references)
			//IL_01fc: Unknown result type (might be due to invalid IL or missing references)
			//IL_0228: Unknown result type (might be due to invalid IL or missing references)
			//IL_022d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0259: Unknown result type (might be due to invalid IL or missing references)
			//IL_025e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0289: Unknown result type (might be due to invalid IL or missing references)
			//IL_028e: Unknown result type (might be due to invalid IL or missing references)
			XamlIlContext.Context<RichTextBox> context = CreateContext(P_0);
			context.IntermediateRoot = (object)new Border();
			object obj = context.IntermediateRoot;
			((ISupportInitialize)obj).BeginInit();
			Border val = (Border)obj;
			context.PushParent(val);
			StyledProperty<Thickness> borderThicknessProperty = Border.BorderThicknessProperty;
			ReflectionBindingExtension val2 = new ReflectionBindingExtension("BorderThickness");
			context.ProvideTargetProperty = Border.BorderThicknessProperty;
			Binding obj2 = val2.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)val, (AvaloniaProperty)(object)borderThicknessProperty, (IBinding)(object)obj2, (object)null);
			StyledProperty<IBrush> borderBrushProperty = Border.BorderBrushProperty;
			ReflectionBindingExtension val3 = new ReflectionBindingExtension("BorderBrush");
			context.ProvideTargetProperty = Border.BorderBrushProperty;
			Binding obj3 = val3.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)val, (AvaloniaProperty)(object)borderBrushProperty, (IBinding)(object)obj3, (object)null);
			val.CornerRadius = new CornerRadius(3.0, 3.0, 3.0, 3.0);
			StyledProperty<Thickness> marginProperty = Layoutable.MarginProperty;
			ReflectionBindingExtension val4 = new ReflectionBindingExtension("Margin");
			context.ProvideTargetProperty = Layoutable.MarginProperty;
			Binding obj4 = val4.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)val, (AvaloniaProperty)(object)marginProperty, (IBinding)(object)obj4, (object)null);
			((Layoutable)val).VerticalAlignment = (VerticalAlignment)1;
			((Layoutable)val).HorizontalAlignment = (HorizontalAlignment)0;
			((Decorator)val).Padding = new Thickness(0.0, 0.0, 0.0, 0.0);
			EditableParagraph editableParagraph2;
			EditableParagraph editableParagraph = (editableParagraph2 = new EditableParagraph());
			((ISupportInitialize)editableParagraph).BeginInit();
			((Decorator)val).Child = (Control)(object)editableParagraph;
			EditableParagraph editableParagraph4;
			EditableParagraph editableParagraph3 = (editableParagraph4 = editableParagraph2);
			context.PushParent(editableParagraph4);
			((StyledElement)editableParagraph4).Classes.Add("paragraphBindings");
			editableParagraph4.MouseMove += context.RootObject.EditableParagraph_MouseMove;
			((Interactive)editableParagraph4).AddHandler((RoutedEvent)(object)((InputElement)editableParagraph4).LostFocusEvent, (Delegate)new EventHandler<RoutedEventArgs>(context.RootObject.EditableParagraph_LostFocus), (RoutingStrategies)5, false);
			editableParagraph4.SelectionStartRect_Changed += context.RootObject.SelectionStart_RectChanged;
			editableParagraph4.SelectionEndRect_Changed += context.RootObject.SelectionEnd_RectChanged;
			StyledProperty<bool> textLayoutInfoStartRequestedProperty = EditableParagraph.TextLayoutInfoStartRequestedProperty;
			ReflectionBindingExtension val5 = new ReflectionBindingExtension("RequestTextLayoutInfoStart");
			context.ProvideTargetProperty = EditableParagraph.TextLayoutInfoStartRequestedProperty;
			Binding obj5 = val5.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)editableParagraph4, (AvaloniaProperty)(object)textLayoutInfoStartRequestedProperty, (IBinding)(object)obj5, (object)null);
			StyledProperty<bool> textLayoutInfoEndRequestedProperty = EditableParagraph.TextLayoutInfoEndRequestedProperty;
			ReflectionBindingExtension val6 = new ReflectionBindingExtension("RequestTextLayoutInfoEnd");
			context.ProvideTargetProperty = EditableParagraph.TextLayoutInfoEndRequestedProperty;
			Binding obj6 = val6.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)editableParagraph4, (AvaloniaProperty)(object)textLayoutInfoEndRequestedProperty, (IBinding)(object)obj6, (object)null);
			AttachedProperty<bool> textBoxFocusRequestedProperty = RequestExtensions.TextBoxFocusRequestedProperty;
			ReflectionBindingExtension val7 = new ReflectionBindingExtension("RequestTextBoxFocus");
			context.ProvideTargetProperty = RequestExtensions.TextBoxFocusRequestedProperty;
			Binding obj7 = val7.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)editableParagraph4, (AvaloniaProperty)(object)textBoxFocusRequestedProperty, (IBinding)(object)obj7, (object)null);
			AttachedProperty<bool> isInlineUpdateRequestedProperty = RequestExtensions.IsInlineUpdateRequestedProperty;
			ReflectionBindingExtension val8 = new ReflectionBindingExtension("RequestInlinesUpdate");
			context.ProvideTargetProperty = RequestExtensions.IsInlineUpdateRequestedProperty;
			Binding obj8 = val8.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)editableParagraph4, (AvaloniaProperty)(object)isInlineUpdateRequestedProperty, (IBinding)(object)obj8, (object)null);
			AttachedProperty<bool> invalidateVisualRequestedProperty = RequestExtensions.InvalidateVisualRequestedProperty;
			ReflectionBindingExtension val9 = new ReflectionBindingExtension("RequestInvalidateVisual");
			context.ProvideTargetProperty = RequestExtensions.InvalidateVisualRequestedProperty;
			Binding obj9 = val9.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)editableParagraph4, (AvaloniaProperty)(object)invalidateVisualRequestedProperty, (IBinding)(object)obj9, (object)null);
			context.PopParent();
			((ISupportInitialize)editableParagraph3).EndInit();
			context.PopParent();
			((ISupportInitialize)obj).EndInit();
			return obj;
		}

		public unsafe static object Build_5(IServiceProvider P_0)
		{
			//IL_002f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0039: Expected O, but got Unknown
			//IL_0044: Unknown result type (might be due to invalid IL or missing references)
			//IL_0049: Unknown result type (might be due to invalid IL or missing references)
			//IL_0075: Unknown result type (might be due to invalid IL or missing references)
			//IL_007a: Unknown result type (might be due to invalid IL or missing references)
			//IL_00a6: Unknown result type (might be due to invalid IL or missing references)
			//IL_00ab: Unknown result type (might be due to invalid IL or missing references)
			//IL_00d7: Unknown result type (might be due to invalid IL or missing references)
			//IL_00dc: Unknown result type (might be due to invalid IL or missing references)
			//IL_0108: Unknown result type (might be due to invalid IL or missing references)
			//IL_010d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0139: Unknown result type (might be due to invalid IL or missing references)
			//IL_013e: Unknown result type (might be due to invalid IL or missing references)
			//IL_016a: Unknown result type (might be due to invalid IL or missing references)
			//IL_016f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0191: Unknown result type (might be due to invalid IL or missing references)
			//IL_0196: Unknown result type (might be due to invalid IL or missing references)
			//IL_0198: Expected O, but got Unknown
			//IL_01bc: Expected O, but got Unknown
			//IL_01bd: Unknown result type (might be due to invalid IL or missing references)
			//IL_01c2: Unknown result type (might be due to invalid IL or missing references)
			//IL_01c4: Expected O, but got Unknown
			//IL_01f8: Expected O, but got Unknown
			XamlIlContext.Context<RichTextBox> context = CreateContext(P_0);
			context.IntermediateRoot = new EditableTable();
			object obj = context.IntermediateRoot;
			((ISupportInitialize)obj).BeginInit();
			EditableTable editableTable = (EditableTable)obj;
			context.PushParent(editableTable);
			((TemplatedControl)editableTable).Background = (IBrush)new ImmutableSolidColorBrush(4294506751u);
			StyledProperty<IBrush> borderBrushProperty = TemplatedControl.BorderBrushProperty;
			ReflectionBindingExtension val = new ReflectionBindingExtension("BorderBrush");
			context.ProvideTargetProperty = TemplatedControl.BorderBrushProperty;
			Binding obj2 = val.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)editableTable, (AvaloniaProperty)(object)borderBrushProperty, (IBinding)(object)obj2, (object)null);
			StyledProperty<Thickness> borderThicknessProperty = TemplatedControl.BorderThicknessProperty;
			ReflectionBindingExtension val2 = new ReflectionBindingExtension("BorderThickness");
			context.ProvideTargetProperty = TemplatedControl.BorderThicknessProperty;
			Binding obj3 = val2.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)editableTable, (AvaloniaProperty)(object)borderThicknessProperty, (IBinding)(object)obj3, (object)null);
			StyledProperty<Thickness> marginProperty = Layoutable.MarginProperty;
			ReflectionBindingExtension val3 = new ReflectionBindingExtension("Margin");
			context.ProvideTargetProperty = Layoutable.MarginProperty;
			Binding obj4 = val3.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)editableTable, (AvaloniaProperty)(object)marginProperty, (IBinding)(object)obj4, (object)null);
			StyledProperty<HorizontalAlignment> horizontalAlignmentProperty = Layoutable.HorizontalAlignmentProperty;
			ReflectionBindingExtension val4 = new ReflectionBindingExtension("TableAlignment");
			context.ProvideTargetProperty = Layoutable.HorizontalAlignmentProperty;
			Binding obj5 = val4.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)editableTable, (AvaloniaProperty)(object)horizontalAlignmentProperty, (IBinding)(object)obj5, (object)null);
			StyledProperty<double> widthProperty = Layoutable.WidthProperty;
			ReflectionBindingExtension val5 = new ReflectionBindingExtension("Width");
			context.ProvideTargetProperty = Layoutable.WidthProperty;
			Binding obj6 = val5.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)editableTable, (AvaloniaProperty)(object)widthProperty, (IBinding)(object)obj6, (object)null);
			StyledProperty<double> heightProperty = Layoutable.HeightProperty;
			ReflectionBindingExtension val6 = new ReflectionBindingExtension("Height");
			context.ProvideTargetProperty = Layoutable.HeightProperty;
			Binding obj7 = val6.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)editableTable, (AvaloniaProperty)(object)heightProperty, (IBinding)(object)obj7, (object)null);
			StyledProperty<IEnumerable> itemsSourceProperty = ItemsControl.ItemsSourceProperty;
			ReflectionBindingExtension val7 = new ReflectionBindingExtension("Cells");
			context.ProvideTargetProperty = ItemsControl.ItemsSourceProperty;
			Binding obj8 = val7.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)editableTable, (AvaloniaProperty)(object)itemsSourceProperty, (IBinding)(object)obj8, (object)null);
			ItemsPanelTemplate val8 = new ItemsPanelTemplate();
			ItemsPanelTemplate val9 = val8;
			context.PushParent(val9);
			val9.Content = XamlIlRuntimeHelpers.DeferredTransformationFactoryV3<Control>((IntPtr)(nint)(delegate*<IServiceProvider, object>)(&Build_6), (IServiceProvider)context);
			context.PopParent();
			((ItemsControl)editableTable).ItemsPanel = (ITemplate<Panel>)val8;
			DataTemplate val10 = new DataTemplate();
			DataTemplate val11 = val10;
			context.PushParent(val11);
			val11.DataType = typeof(Cell);
			val11.Content = XamlIlRuntimeHelpers.DeferredTransformationFactoryV3<Control>((IntPtr)(nint)(delegate*<IServiceProvider, object>)(&Build_7), (IServiceProvider)context);
			context.PopParent();
			((ItemsControl)editableTable).ItemTemplate = (IDataTemplate)val10;
			context.PopParent();
			((ISupportInitialize)obj).EndInit();
			return obj;
		}

		public static object Build_6(IServiceProvider P_0)
		{
			//IL_0034: Unknown result type (might be due to invalid IL or missing references)
			//IL_0039: Unknown result type (might be due to invalid IL or missing references)
			//IL_0064: Unknown result type (might be due to invalid IL or missing references)
			//IL_0069: Unknown result type (might be due to invalid IL or missing references)
			XamlIlContext.Context<RichTextBox> context = CreateContext(P_0);
			context.IntermediateRoot = new BindableGrid();
			object obj = context.IntermediateRoot;
			((ISupportInitialize)obj).BeginInit();
			BindableGrid bindableGrid = (BindableGrid)obj;
			context.PushParent(bindableGrid);
			StyledProperty<RowDefinitions> rowDefsProperty = BindableGrid.RowDefsProperty;
			ReflectionBindingExtension val = new ReflectionBindingExtension("RowDefs");
			context.ProvideTargetProperty = BindableGrid.RowDefsProperty;
			Binding obj2 = val.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)bindableGrid, (AvaloniaProperty)(object)rowDefsProperty, (IBinding)(object)obj2, (object)null);
			StyledProperty<ColumnDefinitions> colDefsProperty = BindableGrid.ColDefsProperty;
			ReflectionBindingExtension val2 = new ReflectionBindingExtension("ColDefs");
			context.ProvideTargetProperty = BindableGrid.ColDefsProperty;
			Binding obj3 = val2.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)bindableGrid, (AvaloniaProperty)(object)colDefsProperty, (IBinding)(object)obj3, (object)null);
			context.PopParent();
			((ISupportInitialize)obj).EndInit();
			return obj;
		}

		public static object Build_7(IServiceProvider P_0)
		{
			//IL_0008: Unknown result type (might be due to invalid IL or missing references)
			//IL_0012: Expected O, but got Unknown
			//IL_0059: Unknown result type (might be due to invalid IL or missing references)
			//IL_005e: Unknown result type (might be due to invalid IL or missing references)
			//IL_008a: Unknown result type (might be due to invalid IL or missing references)
			//IL_008f: Unknown result type (might be due to invalid IL or missing references)
			//IL_00bb: Unknown result type (might be due to invalid IL or missing references)
			//IL_00c0: Unknown result type (might be due to invalid IL or missing references)
			//IL_00ec: Unknown result type (might be due to invalid IL or missing references)
			//IL_00f1: Unknown result type (might be due to invalid IL or missing references)
			//IL_014c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0151: Unknown result type (might be due to invalid IL or missing references)
			//IL_01e0: Unknown result type (might be due to invalid IL or missing references)
			//IL_01e5: Unknown result type (might be due to invalid IL or missing references)
			//IL_0211: Unknown result type (might be due to invalid IL or missing references)
			//IL_0216: Unknown result type (might be due to invalid IL or missing references)
			//IL_0242: Unknown result type (might be due to invalid IL or missing references)
			//IL_0247: Unknown result type (might be due to invalid IL or missing references)
			//IL_0273: Unknown result type (might be due to invalid IL or missing references)
			//IL_0278: Unknown result type (might be due to invalid IL or missing references)
			//IL_02a3: Unknown result type (might be due to invalid IL or missing references)
			//IL_02a8: Unknown result type (might be due to invalid IL or missing references)
			//IL_02e5: Unknown result type (might be due to invalid IL or missing references)
			//IL_02ea: Unknown result type (might be due to invalid IL or missing references)
			//IL_02ed: Expected O, but got Unknown
			//IL_02ed: Unknown result type (might be due to invalid IL or missing references)
			//IL_02f3: Expected O, but got Unknown
			//IL_02f8: Expected O, but got Unknown
			//IL_0313: Unknown result type (might be due to invalid IL or missing references)
			//IL_0318: Unknown result type (might be due to invalid IL or missing references)
			//IL_0352: Unknown result type (might be due to invalid IL or missing references)
			//IL_0357: Unknown result type (might be due to invalid IL or missing references)
			XamlIlContext.Context<RichTextBox> context = CreateContext(P_0);
			context.IntermediateRoot = (object)new Grid();
			object obj = context.IntermediateRoot;
			((ISupportInitialize)obj).BeginInit();
			Grid val = (Grid)obj;
			context.PushParent(val);
			Controls children = ((Panel)val).Children;
			EditableCell editableCell2;
			EditableCell editableCell = (editableCell2 = new EditableCell());
			((ISupportInitialize)editableCell).BeginInit();
			((AvaloniaList<Control>)(object)children).Add((Control)(object)editableCell);
			EditableCell editableCell4;
			EditableCell editableCell3 = (editableCell4 = editableCell2);
			context.PushParent(editableCell4);
			StyledProperty<Thickness> paddingProperty = Decorator.PaddingProperty;
			ReflectionBindingExtension val2 = new ReflectionBindingExtension("Padding");
			context.ProvideTargetProperty = Decorator.PaddingProperty;
			Binding obj2 = val2.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)editableCell4, (AvaloniaProperty)(object)paddingProperty, (IBinding)(object)obj2, (object)null);
			StyledProperty<Thickness> borderThicknessProperty = Border.BorderThicknessProperty;
			ReflectionBindingExtension val3 = new ReflectionBindingExtension("BorderThickness");
			context.ProvideTargetProperty = Border.BorderThicknessProperty;
			Binding obj3 = val3.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)editableCell4, (AvaloniaProperty)(object)borderThicknessProperty, (IBinding)(object)obj3, (object)null);
			StyledProperty<IBrush> borderBrushProperty = Border.BorderBrushProperty;
			ReflectionBindingExtension val4 = new ReflectionBindingExtension("BorderBrush");
			context.ProvideTargetProperty = Border.BorderBrushProperty;
			Binding obj4 = val4.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)editableCell4, (AvaloniaProperty)(object)borderBrushProperty, (IBinding)(object)obj4, (object)null);
			StyledProperty<IBrush> backgroundProperty = Border.BackgroundProperty;
			ReflectionBindingExtension val5 = new ReflectionBindingExtension("CellBackground");
			context.ProvideTargetProperty = Border.BackgroundProperty;
			Binding obj5 = val5.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)editableCell4, (AvaloniaProperty)(object)backgroundProperty, (IBinding)(object)obj5, (object)null);
			EditableParagraph editableParagraph2;
			EditableParagraph editableParagraph = (editableParagraph2 = new EditableParagraph());
			((ISupportInitialize)editableParagraph).BeginInit();
			((Decorator)editableCell4).Child = (Control)(object)editableParagraph;
			EditableParagraph editableParagraph4;
			EditableParagraph editableParagraph3 = (editableParagraph4 = editableParagraph2);
			context.PushParent(editableParagraph4);
			((StyledElement)editableParagraph4).Classes.Add("paragraphBindings");
			ReflectionBindingExtension val6 = new ReflectionBindingExtension("CellContent");
			context.ProvideTargetProperty = StyledElement.DataContextProperty;
			Binding obj6 = val6.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			XamlDynamicSetters.<>XamlDynamicSetter_1((StyledElement)(object)editableParagraph4, obj6);
			editableParagraph4.MouseMove += context.RootObject.EditableParagraph_MouseMove;
			((Interactive)editableParagraph4).AddHandler((RoutedEvent)(object)((InputElement)editableParagraph4).LostFocusEvent, (Delegate)new EventHandler<RoutedEventArgs>(context.RootObject.EditableParagraph_LostFocus), (RoutingStrategies)5, false);
			editableParagraph4.SelectionStartRect_Changed += context.RootObject.SelectionStart_RectChanged;
			editableParagraph4.SelectionEndRect_Changed += context.RootObject.SelectionEnd_RectChanged;
			StyledProperty<bool> textLayoutInfoStartRequestedProperty = EditableParagraph.TextLayoutInfoStartRequestedProperty;
			ReflectionBindingExtension val7 = new ReflectionBindingExtension("RequestTextLayoutInfoStart");
			context.ProvideTargetProperty = EditableParagraph.TextLayoutInfoStartRequestedProperty;
			Binding obj7 = val7.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)editableParagraph4, (AvaloniaProperty)(object)textLayoutInfoStartRequestedProperty, (IBinding)(object)obj7, (object)null);
			StyledProperty<bool> textLayoutInfoEndRequestedProperty = EditableParagraph.TextLayoutInfoEndRequestedProperty;
			ReflectionBindingExtension val8 = new ReflectionBindingExtension("RequestTextLayoutInfoEnd");
			context.ProvideTargetProperty = EditableParagraph.TextLayoutInfoEndRequestedProperty;
			Binding obj8 = val8.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)editableParagraph4, (AvaloniaProperty)(object)textLayoutInfoEndRequestedProperty, (IBinding)(object)obj8, (object)null);
			AttachedProperty<bool> textBoxFocusRequestedProperty = RequestExtensions.TextBoxFocusRequestedProperty;
			ReflectionBindingExtension val9 = new ReflectionBindingExtension("RequestTextBoxFocus");
			context.ProvideTargetProperty = RequestExtensions.TextBoxFocusRequestedProperty;
			Binding obj9 = val9.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)editableParagraph4, (AvaloniaProperty)(object)textBoxFocusRequestedProperty, (IBinding)(object)obj9, (object)null);
			AttachedProperty<bool> isInlineUpdateRequestedProperty = RequestExtensions.IsInlineUpdateRequestedProperty;
			ReflectionBindingExtension val10 = new ReflectionBindingExtension("RequestInlinesUpdate");
			context.ProvideTargetProperty = RequestExtensions.IsInlineUpdateRequestedProperty;
			Binding obj10 = val10.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)editableParagraph4, (AvaloniaProperty)(object)isInlineUpdateRequestedProperty, (IBinding)(object)obj10, (object)null);
			AttachedProperty<bool> invalidateVisualRequestedProperty = RequestExtensions.InvalidateVisualRequestedProperty;
			ReflectionBindingExtension val11 = new ReflectionBindingExtension("RequestInvalidateVisual");
			context.ProvideTargetProperty = RequestExtensions.InvalidateVisualRequestedProperty;
			Binding obj11 = val11.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)editableParagraph4, (AvaloniaProperty)(object)invalidateVisualRequestedProperty, (IBinding)(object)obj11, (object)null);
			context.PopParent();
			((ISupportInitialize)editableParagraph3).EndInit();
			context.PopParent();
			((ISupportInitialize)editableCell3).EndInit();
			Controls children2 = ((Panel)val).Children;
			Rectangle val12 = new Rectangle();
			Rectangle val13 = val12;
			((ISupportInitialize)val12).BeginInit();
			((AvaloniaList<Control>)(object)children2).Add((Control)val12);
			Rectangle val14;
			Rectangle obj12 = (val14 = val13);
			context.PushParent(val14);
			StyledProperty<IBrush> fillProperty = Shape.FillProperty;
			ReflectionBindingExtension val15 = new ReflectionBindingExtension("SelectionBrush");
			context.ProvideTargetProperty = Shape.FillProperty;
			Binding obj13 = val15.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)val14, (AvaloniaProperty)(object)fillProperty, (IBinding)(object)obj13, (object)null);
			((Visual)val14).Opacity = 0.45;
			StyledProperty<bool> isVisibleProperty = Visual.IsVisibleProperty;
			ReflectionBindingExtension val16 = new ReflectionBindingExtension("Selected");
			context.ProvideTargetProperty = Visual.IsVisibleProperty;
			Binding obj14 = val16.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)val14, (AvaloniaProperty)(object)isVisibleProperty, (IBinding)(object)obj14, (object)null);
			context.PopParent();
			((ISupportInitialize)obj12).EndInit();
			context.PopParent();
			((ISupportInitialize)obj).EndInit();
			return obj;
		}
	}

	private Animation blinkAnimation;

	private EditableParagraph? currentMouseOverEP;

	internal int SelectionOrigin;

	private bool PointerDownOverRTB;

	private RichTextBoxTextInputClient client;

	private string _preeditText = "";

	private readonly Rectangle? _CaretRect = new Rectangle
	{
		StrokeThickness = 2.0,
		Stroke = (IBrush)(object)Brushes.Black,
		Height = 20.0,
		Width = 1.5,
		IsVisible = false,
		HorizontalAlignment = (HorizontalAlignment)1,
		VerticalAlignment = (VerticalAlignment)1,
		IsHitTestVisible = false
	};

	private static ScaleTransform strans = new ScaleTransform(0.75, 0.75);

	internal static TransformGroup SubscriptTG = new TransformGroup();

	internal static TransformGroup SuperscriptTG = new TransformGroup();

	public static readonly StyledProperty<FlowDocument> FlowDocumentProperty = AvaloniaProperty.Register<RichTextBox, FlowDocument>("FlowDocument", (FlowDocument)null, false, (BindingMode)2, (Func<FlowDocument, bool>)null, (Func<AvaloniaObject, FlowDocument, FlowDocument>)null, false);

	public static readonly StyledProperty<bool> IsReadOnlyProperty = AvaloniaProperty.Register<RichTextBox, bool>("IsReadOnly", false, false, (BindingMode)1, (Func<bool, bool>)null, (Func<AvaloniaObject, bool, bool>)null, false);

	public static readonly StyledProperty<bool> DisableUserCopyProperty = AvaloniaProperty.Register<RichTextBox, bool>("DisableUserCopy", false, false, (BindingMode)1, (Func<bool, bool>)null, (Func<AvaloniaObject, bool, bool>)null, false);

	public static readonly StyledProperty<bool> LineBreakOnShiftEnterProperty = AvaloniaProperty.Register<RichTextBox, bool>("LineBreakOnShiftEnter", false, false, (BindingMode)1, (Func<bool, bool>)null, (Func<AvaloniaObject, bool, bool>)null, false);

	public static readonly StyledProperty<bool> ShowDebuggerPanelInDebugModeProperty = AvaloniaProperty.Register<RichTextBox, bool>("ShowDebuggerPanelInDebugMode", false, false, (BindingMode)1, (Func<bool, bool>)null, (Func<AvaloniaObject, bool, bool>)null, false);

	public static readonly StyledProperty<IBrush> SelectionBrushProperty = AvaloniaProperty.Register<RichTextBox, IBrush>("SelectionBrush", (IBrush)(object)Brushes.LightSteelBlue, false, (BindingMode)1, (Func<IBrush, bool>)null, (Func<AvaloniaObject, IBrush, IBrush>)null, false);

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "11.3.12.0")]
	internal DockPanel MainDP;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "11.3.12.0")]
	internal ScrollViewer FlowDocSV;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "11.3.12.0")]
	internal ItemsControl DocIC;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "11.3.12.0")]
	internal TextBlock PreeditOverlay;

	[CompilerGenerated]
	private static Action<object> !XamlIlPopulateOverride;

	internal FlowDocument FlowDoc => RtbVm.FlowDoc;

	private RichTextBoxViewModel RtbVm { get; set; } = new RichTextBoxViewModel();

	internal Point CaretPosition { get; set; }

	public FlowDocument FlowDocument
	{
		get
		{
			return ((AvaloniaObject)this).GetValue<FlowDocument>(FlowDocumentProperty);
		}
		set
		{
			((AvaloniaObject)this).SetValue<FlowDocument>(FlowDocumentProperty, value, (BindingPriority)0);
		}
	}

	public bool IsReadOnly
	{
		get
		{
			return ((AvaloniaObject)this).GetValue<bool>(IsReadOnlyProperty);
		}
		set
		{
			((AvaloniaObject)this).SetValue<bool>(IsReadOnlyProperty, value, (BindingPriority)0);
		}
	}

	public bool DisableUserCopy
	{
		get
		{
			return ((AvaloniaObject)this).GetValue<bool>(DisableUserCopyProperty);
		}
		set
		{
			((AvaloniaObject)this).SetValue<bool>(DisableUserCopyProperty, value, (BindingPriority)0);
		}
	}

	public bool LineBreakOnShiftEnter
	{
		get
		{
			return ((AvaloniaObject)this).GetValue<bool>(LineBreakOnShiftEnterProperty);
		}
		set
		{
			((AvaloniaObject)this).SetValue<bool>(LineBreakOnShiftEnterProperty, value, (BindingPriority)0);
		}
	}

	public bool ShowDebuggerPanelInDebugMode
	{
		get
		{
			return ((AvaloniaObject)this).GetValue<bool>(ShowDebuggerPanelInDebugModeProperty);
		}
		set
		{
			((AvaloniaObject)this).SetValue<bool>(ShowDebuggerPanelInDebugModeProperty, value, (BindingPriority)0);
		}
	}

	public IBrush SelectionBrush
	{
		get
		{
			return ((AvaloniaObject)this).GetValue<IBrush>(SelectionBrushProperty);
		}
		set
		{
			((AvaloniaObject)this).SetValue<IBrush>(SelectionBrushProperty, value, (BindingPriority)0);
		}
	}

	public void ScrollToSelection()
	{
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0011: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_0036: Unknown result type (might be due to invalid IL or missing references)
		RichTextBoxViewModel rtbVm = RtbVm;
		Vector rTBScrollOffset = RtbVm.RTBScrollOffset;
		Rect startRect = FlowDoc.Selection.StartRect;
		rtbVm.RTBScrollOffset = ((Vector)(ref rTBScrollOffset)).WithY(((Rect)(ref startRect)).Y - 50.0);
	}

	public RichTextBox()
	{
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0036: Unknown result type (might be due to invalid IL or missing references)
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		//IL_0054: Unknown result type (might be due to invalid IL or missing references)
		//IL_005b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0062: Unknown result type (might be due to invalid IL or missing references)
		//IL_0069: Unknown result type (might be due to invalid IL or missing references)
		//IL_0075: Expected O, but got Unknown
		//IL_0170: Unknown result type (might be due to invalid IL or missing references)
		//IL_017a: Expected O, but got Unknown
		//IL_018c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0196: Expected O, but got Unknown
		//IL_01a8: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b2: Expected O, but got Unknown
		//IL_01c9: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ce: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e1: Unknown result type (might be due to invalid IL or missing references)
		//IL_01eb: Expected O, but got Unknown
		//IL_01eb: Expected O, but got Unknown
		//IL_01eb: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f6: Expected O, but got Unknown
		//IL_01fb: Expected O, but got Unknown
		//IL_0200: Unknown result type (might be due to invalid IL or missing references)
		//IL_0205: Unknown result type (might be due to invalid IL or missing references)
		//IL_0218: Unknown result type (might be due to invalid IL or missing references)
		//IL_0222: Expected O, but got Unknown
		//IL_0222: Expected O, but got Unknown
		//IL_0222: Unknown result type (might be due to invalid IL or missing references)
		//IL_022d: Expected O, but got Unknown
		//IL_0232: Expected O, but got Unknown
		InitializeComponent();
		((AvaloniaObject)this).PropertyChanged += RichTextBox_PropertyChanged;
		((Control)this).Loaded += RichTextBox_Loaded;
		((StyledElement)this).Initialized += RichTextBox_Initialized;
		((InputElement)this).TextInput += RichTextBox_TextInput;
		((InputElement)this).GotFocus += RichTextBox_GotFocus;
		((InputElement)this).LostFocus += RichTextBox_LostFocus;
		RtbVm.FlowDocChanged += RtbVM_FlowDocChanged;
		((StyledElement)MainDP).DataContext = RtbVm;
		((Control)FlowDocSV).SizeChanged += FlowDocSV_SizeChanged;
		AdornerLayer.SetAdorner((Visual)(object)DocIC, (Control)(object)_CaretRect);
		InitializeBlinkAnimation();
		blinkAnimation.RunAsync((Animatable)(object)_CaretRect, default(CancellationToken));
		((AvaloniaObject)_CaretRect).Bind((AvaloniaProperty)(object)Visual.IsVisibleProperty, (IBinding)new Binding("CaretVisible", (BindingMode)0));
		((AvaloniaObject)_CaretRect).Bind((AvaloniaProperty)(object)Layoutable.MarginProperty, (IBinding)new Binding("CaretMargin", (BindingMode)0));
		((AvaloniaObject)_CaretRect).Bind((AvaloniaProperty)(object)Layoutable.HeightProperty, (IBinding)new Binding("CaretHeight", (BindingMode)0));
		((StyledElement)_CaretRect).DataContext = RtbVm;
		TransformGroup subscriptTG = SubscriptTG;
		Transforms val = new Transforms();
		((AvaloniaList<Transform>)val).Add((Transform)new TranslateTransform(0.0, 4.8));
		((AvaloniaList<Transform>)val).Add((Transform)(object)strans);
		subscriptTG.Children = val;
		TransformGroup superscriptTG = SuperscriptTG;
		Transforms val2 = new Transforms();
		((AvaloniaList<Transform>)val2).Add((Transform)new TranslateTransform(0.0, -4.8));
		((AvaloniaList<Transform>)val2).Add((Transform)(object)strans);
		superscriptTG.Children = val2;
		((InputElement)this).Focusable = true;
	}

	private void RichTextBox_Initialized(object? sender, EventArgs e)
	{
	}

	private void RichTextBox_Loaded(object? sender, RoutedEventArgs e)
	{
		if (FlowDocument == null)
		{
			FlowDocument = new FlowDocument();
			FlowDoc.NewDocument();
		}
		((InputElement)this).Focus((NavigationMethod)0, (KeyModifiers)0);
	}

	private void RtbVM_FlowDocChanged()
	{
		((StyledElement)DocIC).DataContext = RtbVm.FlowDoc;
		UpdateAllInlines();
	}

	private void RichTextBox_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
	{
		if (e.Property == (AvaloniaProperty)(object)FlowDocumentProperty)
		{
			if (FlowDoc != null)
			{
				FlowDoc.ScrollInDirection -= RtbVm.FlowDoc_ScrollInDirection;
				FlowDoc.UpdateRTBCaret -= RtbVm.FlowDoc_UpdateRTBCaret;
			}
			RtbVm.FlowDoc = FlowDocument;
			RtbVm.FlowDoc.ScrollInDirection += RtbVm.FlowDoc_ScrollInDirection;
			RtbVm.FlowDoc.UpdateRTBCaret += RtbVm.FlowDoc_UpdateRTBCaret;
			RtbVm.FlowDoc.SelectionBrush = SelectionBrush;
			RtbVm.FlowDoc.InitializeDocument();
			CreateClient();
		}
		else
		{
			if (!(e.Property == (AvaloniaProperty)(object)SelectionBrushProperty) || FlowDoc == null)
			{
				return;
			}
			foreach (Block block in FlowDoc.Blocks)
			{
				if (!(block is Paragraph paragraph))
				{
					if (!(block is Table table))
					{
						continue;
					}
					foreach (Cell cell in table.Cells)
					{
						cell.SelectionBrush = SelectionBrush;
						if (cell.CellContent is Paragraph paragraph2)
						{
							paragraph2.SelectionBrush = SelectionBrush;
						}
					}
				}
				else
				{
					paragraph.SelectionBrush = SelectionBrush;
				}
			}
		}
	}

	private void RichTextBox_GotFocus(object? sender, GotFocusEventArgs e)
	{
	}

	private void RichTextBox_LostFocus(object? sender, RoutedEventArgs e)
	{
	}

	internal void UpdateAllInlines()
	{
		foreach (Paragraph allParagraph in FlowDoc.AllParagraphs)
		{
			allParagraph.CallRequestInlinesUpdate();
			allParagraph.CallRequestInvalidateVisual();
		}
	}

	public void InvalidateCaret()
	{
		RtbVm.CaretVisible = true;
	}

	public void NewDocument()
	{
		FlowDoc.NewDocument();
	}

	public void CreateNewDocument()
	{
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		FlowDoc.NewDocument();
		RtbVm.RTBScrollOffset = new Vector(0.0, 0.0);
	}

	public void LoadRtf(string rtf)
	{
		FlowDoc.LoadRtf(rtf);
	}

	public void LoadRtfDoc(string fileName)
	{
		FlowDoc.LoadRtfFromFile(fileName);
	}

	public string SaveRtf()
	{
		return FlowDoc.SaveRtf();
	}

	public void SaveRtfDoc(string fileName)
	{
		FlowDoc.SaveRtfToFile(fileName);
	}

	public void LoadWordDoc(string fileName)
	{
		FlowDoc.LoadWordDocFromFile(fileName);
	}

	public void SaveWordDoc(string filename)
	{
		FlowDoc.SaveWordDocToFile(filename);
	}

	public void LoadHtml(string html)
	{
		FlowDoc.LoadHtml(html);
	}

	public string SaveHtml()
	{
		return FlowDoc.SaveHtml();
	}

	public void LoadHtmlDoc(string fileName)
	{
		FlowDoc.LoadHtmlDocFromFile(fileName);
	}

	public void SaveHtmlDoc(string filename)
	{
		FlowDoc.SaveHtmlDocToFile(filename);
	}

	public void LoadXaml(string fileName)
	{
		FlowDoc.LoadXamlFromFile(fileName);
	}

	public void SaveXamlPackage(string fileName)
	{
		FlowDoc.SaveXamlPackage(fileName);
	}

	public void LoadXamlString(string xaml)
	{
		FlowDoc.LoadXaml(xaml);
	}

	public string SaveXamlString()
	{
		return FlowDoc.SaveXaml();
	}

	public void SaveXaml(string fileName)
	{
		FlowDoc.SaveXamlToFile(fileName);
	}

	public void LoadXamlPackage(string fileName)
	{
		FlowDoc.LoadXamlPackage(fileName);
	}

	private void MovePage(int direction, bool extend)
	{
		//IL_003b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_006d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0072: Unknown result type (might be due to invalid IL or missing references)
		//IL_0088: Unknown result type (might be due to invalid IL or missing references)
		//IL_008d: Unknown result type (might be due to invalid IL or missing references)
		//IL_009c: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ea: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ef: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0057: Unknown result type (might be due to invalid IL or missing references)
		//IL_005c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0187: Unknown result type (might be due to invalid IL or missing references)
		//IL_018c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0190: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c9: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ce: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d2: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d7: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ec: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f1: Unknown result type (might be due to invalid IL or missing references)
		//IL_020d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0212: Unknown result type (might be due to invalid IL or missing references)
		double num = 0.0;
		Rect val;
		switch (FlowDoc.SelectionExtendMode)
		{
		case AvRichTextBox.FlowDocument.ExtendMode.ExtendModeNone:
		case AvRichTextBox.FlowDocument.ExtendMode.ExtendModeRight:
			val = FlowDoc.Selection.EndRect;
			num = ((Rect)(ref val)).Y;
			break;
		case AvRichTextBox.FlowDocument.ExtendMode.ExtendModeLeft:
			val = FlowDoc.Selection.StartRect;
			num = ((Rect)(ref val)).Y;
			break;
		}
		double num2 = num;
		Vector rTBScrollOffset = RtbVm.RTBScrollOffset;
		double num3 = num2 - ((Vector)(ref rTBScrollOffset)).Y;
		val = FlowDoc.Selection.StartRect;
		double x = ((Rect)(ref val)).X;
		Thickness margin = ((Layoutable)FlowDocSV).Margin;
		double num4 = x + ((Thickness)(ref margin)).Left;
		rTBScrollOffset = RtbVm.RTBScrollOffset;
		double y = ((Vector)(ref rTBScrollOffset)).Y;
		val = ((Visual)FlowDocSV).Bounds;
		double newScrollY = y + ((Rect)(ref val)).Height * (double)direction;
		RichTextBoxViewModel rtbVm = RtbVm;
		rTBScrollOffset = RtbVm.RTBScrollOffset;
		rtbVm.RTBScrollOffset = ((Vector)(ref rTBScrollOffset)).WithY(newScrollY);
		double num5 = newScrollY + num3;
		EditableParagraph editableParagraph = VisualExtensions.GetVisualDescendants((Visual)(object)DocIC).OfType<EditableParagraph>().Where(delegate(EditableParagraph ep)
		{
			//IL_0002: Unknown result type (might be due to invalid IL or missing references)
			//IL_0007: Unknown result type (might be due to invalid IL or missing references)
			//IL_000a: Unknown result type (might be due to invalid IL or missing references)
			//IL_0022: Unknown result type (might be due to invalid IL or missing references)
			//IL_0027: Unknown result type (might be due to invalid IL or missing references)
			Rect bounds = ((Visual)ep).Bounds;
			Point value = VisualExtensions.TranslatePoint((Visual)(object)ep, ((Rect)(ref bounds)).Position, (Visual)(object)DocIC).Value;
			return ((Point)(ref value)).Y <= newScrollY;
		})
			.LastOrDefault();
		if (editableParagraph == null)
		{
			if (direction == -1)
			{
				if (FlowDoc.SelectionExtendMode == AvRichTextBox.FlowDocument.ExtendMode.ExtendModeRight)
				{
					FlowDoc.Select(0, 0);
					FlowDoc.SelectionExtendMode = AvRichTextBox.FlowDocument.ExtendMode.ExtendModeNone;
				}
				else
				{
					FlowDoc.MovePageSelection(-1, extend, 0);
				}
				((InputElement)this).Focus((NavigationMethod)0, (KeyModifiers)0);
			}
		}
		else
		{
			val = ((Visual)editableParagraph).Bounds;
			Point val2 = VisualExtensions.TranslatePoint((Visual)(object)editableParagraph, ((Rect)(ref val)).Position, (Visual)(object)DocIC).Value;
			double num6 = num5 - ((Point)(ref val2)).Y + 18.0;
			TextLayout textLayout = ((TextBlock)editableParagraph).TextLayout;
			val2 = new Point(num4, num6);
			TextHitTestResult val3 = textLayout.HitTestPoint(ref val2);
			int startInDoc = ((Paragraph)((StyledElement)editableParagraph).DataContext).StartInDoc;
			CharacterHit characterHit = ((TextHitTestResult)(ref val3)).CharacterHit;
			int num7 = startInDoc + ((CharacterHit)(ref characterHit)).FirstCharacterIndex;
			FlowDocument flowDoc = FlowDoc;
			val = ((Visual)FlowDocSV).Bounds;
			flowDoc.MovePageSelection(direction, extend, num7 + (int)(((Rect)(ref val)).Height / 2.0));
		}
	}

	private void FlowDocSV_SizeChanged(object? sender, SizeChangedEventArgs e)
	{
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		RichTextBoxViewModel rtbVm = RtbVm;
		Size newSize = e.NewSize;
		rtbVm.ScrollViewerHeight = ((Size)(ref newSize)).Height;
	}

	private void ScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
	{
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		RtbVm.RTBScrollOffset = FlowDocSV.Offset;
	}

	private void InitializeBlinkAnimation()
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_002c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_0041: Unknown result type (might be due to invalid IL or missing references)
		//IL_004b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0064: Unknown result type (might be due to invalid IL or missing references)
		//IL_006e: Expected O, but got Unknown
		//IL_0073: Expected O, but got Unknown
		//IL_0073: Unknown result type (might be due to invalid IL or missing references)
		//IL_0079: Unknown result type (might be due to invalid IL or missing references)
		//IL_007e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0088: Unknown result type (might be due to invalid IL or missing references)
		//IL_0092: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b5: Expected O, but got Unknown
		//IL_00ba: Expected O, but got Unknown
		//IL_00ba: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cf: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fc: Expected O, but got Unknown
		//IL_0101: Expected O, but got Unknown
		//IL_0106: Expected O, but got Unknown
		Animation val = new Animation
		{
			Duration = TimeSpan.FromSeconds(0.85),
			FillMode = (FillMode)1,
			IterationCount = IterationCount.Infinite
		};
		KeyFrames children = val.Children;
		KeyFrame val2 = new KeyFrame
		{
			Cue = new Cue(0.0)
		};
		val2.Setters.Add((IAnimationSetter)new Setter((AvaloniaProperty)(object)Visual.OpacityProperty, (object)0.0));
		((AvaloniaList<KeyFrame>)(object)children).Add(val2);
		KeyFrames children2 = val.Children;
		KeyFrame val3 = new KeyFrame
		{
			Cue = new Cue(0.5)
		};
		val3.Setters.Add((IAnimationSetter)new Setter((AvaloniaProperty)(object)Visual.OpacityProperty, (object)1.0));
		((AvaloniaList<KeyFrame>)(object)children2).Add(val3);
		KeyFrames children3 = val.Children;
		KeyFrame val4 = new KeyFrame
		{
			Cue = new Cue(1.0)
		};
		val4.Setters.Add((IAnimationSetter)new Setter((AvaloniaProperty)(object)Visual.OpacityProperty, (object)0.0));
		((AvaloniaList<KeyFrame>)(object)children3).Add(val4);
		blinkAnimation = val;
	}

	private void RichTextBox_TextInput(object? sender, TextInputEventArgs e)
	{
		if (!IsReadOnly)
		{
			FlowDoc.InsertText(e.Text);
			UpdateCurrentParagraphLayout();
			if (((Visual)PreeditOverlay).IsVisible)
			{
				HideIMEOverlay();
			}
		}
	}

	private void HideIMEOverlay()
	{
		_preeditText = "";
		((Visual)PreeditOverlay).IsVisible = false;
	}

	internal void UpdateCurrentParagraphLayout()
	{
		((Layoutable)this).UpdateLayout();
		RtbVm.UpdateCaretVisible();
	}

	internal void InsertParagraph()
	{
		if (!IsReadOnly)
		{
			FlowDoc.InsertParagraph(addUndo: true, FlowDoc.Selection.Start);
			UpdateCurrentParagraphLayout();
		}
	}

	internal void InsertLineBreak()
	{
		if (!IsReadOnly)
		{
			FlowDoc.InsertLineBreak();
			UpdateCurrentParagraphLayout();
		}
	}

	internal void InsertTab()
	{
		if (!IsReadOnly)
		{
			FlowDoc.InsertText("\t");
			UpdateCurrentParagraphLayout();
		}
	}

	private void PerformDelete(bool backspace)
	{
		if (IsReadOnly)
		{
			return;
		}
		if (FlowDoc.Selection.Length > 0)
		{
			FlowDoc.DeleteSelection();
		}
		else
		{
			if (backspace && (FlowDoc.Selection.Start == 0 || FlowDoc.Selection.Start >= FlowDoc.Selection.StartParagraph.StartInDoc + FlowDoc.Selection.StartParagraph.BlockLength))
			{
				return;
			}
			FlowDoc.DeleteChar(backspace);
		}
		UpdateCurrentParagraphLayout();
	}

	private void RichTextBox_KeyDown(object? sender, KeyEventArgs e)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0134: Unknown result type (might be due to invalid IL or missing references)
		//IL_0139: Unknown result type (might be due to invalid IL or missing references)
		//IL_013a: Unknown result type (might be due to invalid IL or missing references)
		//IL_013c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0156: Expected I4, but got Unknown
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_0029: Unknown result type (might be due to invalid IL or missing references)
		//IL_002c: Invalid comparison between Unknown and I4
		//IL_025a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0156: Unknown result type (might be due to invalid IL or missing references)
		//IL_0159: Unknown result type (might be due to invalid IL or missing references)
		//IL_0197: Expected I4, but got Unknown
		//IL_0076: Unknown result type (might be due to invalid IL or missing references)
		//IL_0079: Invalid comparison between Unknown and I4
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0031: Invalid comparison between Unknown and I4
		//IL_0408: Unknown result type (might be due to invalid IL or missing references)
		//IL_03e9: Unknown result type (might be due to invalid IL or missing references)
		//IL_02c7: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_0328: Unknown result type (might be due to invalid IL or missing references)
		//IL_0369: Unknown result type (might be due to invalid IL or missing references)
		//IL_02e7: Unknown result type (might be due to invalid IL or missing references)
		//IL_03a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_0197: Unknown result type (might be due to invalid IL or missing references)
		//IL_019a: Invalid comparison between Unknown and I4
		//IL_0086: Unknown result type (might be due to invalid IL or missing references)
		//IL_0089: Invalid comparison between Unknown and I4
		//IL_007b: Unknown result type (might be due to invalid IL or missing references)
		//IL_007e: Invalid comparison between Unknown and I4
		//IL_0058: Unknown result type (might be due to invalid IL or missing references)
		//IL_005b: Invalid comparison between Unknown and I4
		//IL_0033: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Invalid comparison between Unknown and I4
		//IL_01e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_008b: Unknown result type (might be due to invalid IL or missing references)
		//IL_008e: Invalid comparison between Unknown and I4
		//IL_0080: Unknown result type (might be due to invalid IL or missing references)
		//IL_0083: Invalid comparison between Unknown and I4
		//IL_0060: Unknown result type (might be due to invalid IL or missing references)
		//IL_0063: Unknown result type (might be due to invalid IL or missing references)
		//IL_0075: Expected I4, but got Unknown
		//IL_003a: Unknown result type (might be due to invalid IL or missing references)
		//IL_003d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0057: Expected I4, but got Unknown
		Key key;
		if (((Enum)e.KeyModifiers).HasFlag((Enum)(object)(KeyModifiers)2))
		{
			((RoutedEventArgs)e).Handled = true;
			key = e.Key;
			if ((int)key <= 46)
			{
				if ((int)key <= 25)
				{
					if ((int)key != 2)
					{
						switch (key - 21)
						{
						case 1:
							FlowDoc.MoveToDocStart();
							FlowDocSV.ScrollToHome();
							break;
						case 0:
							FlowDoc.MoveToDocEnd();
							break;
						case 4:
							FlowDoc.MoveRightWord();
							break;
						case 2:
							FlowDoc.MoveLeftWord();
							break;
						case 3:
							break;
						}
					}
					else
					{
						FlowDoc.DeleteWord(backspace: true);
					}
				}
				else if ((int)key != 32)
				{
					switch (key - 44)
					{
					case 1:
						ToggleBold();
						break;
					case 2:
						CopyToClipboard();
						break;
					case 0:
						FlowDoc.SelectAll();
						break;
					}
				}
				else if (!IsReadOnly)
				{
					FlowDoc.DeleteWord(backspace: false);
				}
			}
			else if ((int)key <= 64)
			{
				if ((int)key != 52)
				{
					if ((int)key == 64)
					{
						ToggleUnderlining();
					}
				}
				else
				{
					ToggleItalics();
				}
			}
			else if ((int)key != 65)
			{
				if ((int)key == 69 && !IsReadOnly)
				{
					FlowDoc.Undo();
				}
			}
			else
			{
				PasteFromClipboard();
			}
			return;
		}
		key = e.Key;
		switch (key - 2)
		{
		default:
			switch (key - 13)
			{
			default:
				if ((int)key == 32)
				{
					PerformDelete(backspace: false);
				}
				break;
			case 0:
				if (((Visual)PreeditOverlay).IsVisible)
				{
					HideIMEOverlay();
				}
				break;
			case 9:
				FlowDoc.MoveToStartOfLine(((Enum)e.KeyModifiers).HasFlag((Enum)(object)(KeyModifiers)4));
				break;
			case 8:
				FlowDoc.MoveToEndOfLine(((Enum)e.KeyModifiers).HasFlag((Enum)(object)(KeyModifiers)4));
				break;
			case 12:
				if (((Enum)e.KeyModifiers).HasFlag((Enum)(object)(KeyModifiers)4))
				{
					FlowDoc.ExtendSelectionRight();
				}
				else
				{
					FlowDoc.MoveSelectionRight(isTextInsertion: false);
				}
				FlowDoc.ResetInsertFormatting();
				break;
			case 10:
				if (((Enum)e.KeyModifiers).HasFlag((Enum)(object)(KeyModifiers)4))
				{
					FlowDoc.ExtendSelectionLeft();
				}
				else
				{
					FlowDoc.MoveSelectionLeft(biasForward: false);
				}
				FlowDoc.ResetInsertFormatting();
				break;
			case 11:
				if (((Enum)e.KeyModifiers).HasFlag((Enum)(object)(KeyModifiers)4))
				{
					FlowDoc.ExtendSelectionUp();
				}
				else
				{
					FlowDoc.MoveSelectionUp(biasForward: false);
				}
				break;
			case 13:
				if (((Enum)e.KeyModifiers).HasFlag((Enum)(object)(KeyModifiers)4))
				{
					FlowDoc.ExtendSelectionDown();
				}
				else
				{
					FlowDoc.MoveSelectionDown(biasForward: true);
				}
				break;
			case 7:
				MovePage(1, ((Enum)e.KeyModifiers).HasFlag((Enum)(object)(KeyModifiers)4));
				break;
			case 6:
				MovePage(-1, ((Enum)e.KeyModifiers).HasFlag((Enum)(object)(KeyModifiers)4));
				break;
			case 1:
			case 2:
			case 3:
			case 4:
			case 5:
				break;
			}
			break;
		case 1:
		{
			((RoutedEventArgs)e).Handled = true;
			Paragraph startPar = FlowDoc.Selection.GetStartPar();
			if (startPar != null && startPar.IsTableCellBlock)
			{
				if (((Enum)e.KeyModifiers).HasFlag((Enum)(object)(KeyModifiers)4))
				{
					Paragraph previousParagraph = FlowDoc.GetPreviousParagraph(startPar);
					if (previousParagraph != null)
					{
						FlowDoc.Select(previousParagraph.StartInDoc, 0);
					}
				}
				else
				{
					Paragraph nextParagraph = FlowDoc.GetNextParagraph(startPar);
					if (nextParagraph != null)
					{
						FlowDoc.Select(nextParagraph.StartInDoc, 0);
					}
				}
			}
			else
			{
				InsertTab();
			}
			break;
		}
		case 4:
			if (((Enum)e.KeyModifiers).HasFlag((Enum)(object)(KeyModifiers)4))
			{
				if (LineBreakOnShiftEnter)
				{
					InsertLineBreak();
				}
				else
				{
					InsertParagraph();
				}
			}
			else
			{
				InsertParagraph();
			}
			break;
		case 0:
			PerformDelete(backspace: true);
			break;
		case 2:
		case 3:
			break;
		}
		RtbVm.CaretVisible = RtbVm.FlowDoc.Selection.Length == 0;
		if (client != null)
		{
			UpdatePreeditOverlay();
		}
	}

	internal void EditableParagraph_MouseMove(EditableParagraph edPar, int charIndex)
	{
		if (!PointerDownOverRTB)
		{
			currentMouseOverEP = edPar;
		}
	}

	private void EditableParagraph_LostFocus(object? sender, RoutedEventArgs e)
	{
		((InputElement)this).Focus((NavigationMethod)0, (KeyModifiers)0);
	}

	private void FlowDocSV_PointerPressed(object? sender, PointerPressedEventArgs e)
	{
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_0027: Unknown result type (might be due to invalid IL or missing references)
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00eb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ef: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f8: Unknown result type (might be due to invalid IL or missing references)
		if (currentMouseOverEP == null)
		{
			return;
		}
		PointerDownOverRTB = true;
		TextLayout textLayout = ((TextBlock)currentMouseOverEP).TextLayout;
		Point position = ((PointerEventArgs)e).GetPosition((Visual)(object)currentMouseOverEP);
		TextHitTestResult val = textLayout.HitTestPoint(ref position);
		if (!(((StyledElement)currentMouseOverEP).DataContext is Paragraph paragraph))
		{
			return;
		}
		SelectionOrigin = paragraph.StartInDoc + ((TextHitTestResult)(ref val)).TextPosition;
		foreach (Paragraph item in FlowDoc.AllParagraphs.Where((Paragraph pp) => pp.SelectionLength != 0))
		{
			item.ClearSelection();
		}
		int num = SelectionOrigin;
		int end = SelectionOrigin;
		if (e.ClickCount > 1)
		{
			object source = ((RoutedEventArgs)e).Source;
			Visual val2 = (Visual)((source is Visual) ? source : null);
			if (val2 != null)
			{
				PointerPoint currentPoint = ((PointerEventArgs)e).GetCurrentPoint(val2);
				PointerPointProperties properties = ((PointerPoint)(ref currentPoint)).Properties;
				if ((int)((PointerPointProperties)(ref properties)).PointerUpdateKind == 0)
				{
					if (e.ClickCount == 2)
					{
						foreach (Match item2 in WordMatchesRegex().Matches(paragraph.Text))
						{
							int num2 = paragraph.StartInDoc + item2.Index;
							int num3 = num2 + item2.Length;
							if (SelectionOrigin >= num2 && SelectionOrigin <= num3)
							{
								num = num2;
								end = num3;
								break;
							}
						}
					}
					else if (e.ClickCount == 3)
					{
						num = paragraph.StartInDoc;
						end = num + paragraph.TextLength;
					}
				}
			}
		}
		FlowDoc.Selection.Start = num;
		FlowDoc.Selection.End = end;
	}

	private void FlowDocSV_PointerMoved(object? sender, PointerEventArgs e)
	{
		//IL_0016: Unknown result type (might be due to invalid IL or missing references)
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		//IL_001e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		//IL_0054: Unknown result type (might be due to invalid IL or missing references)
		//IL_0059: Unknown result type (might be due to invalid IL or missing references)
		//IL_005d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0062: Unknown result type (might be due to invalid IL or missing references)
		//IL_0068: Unknown result type (might be due to invalid IL or missing references)
		//IL_006d: Unknown result type (might be due to invalid IL or missing references)
		//IL_007c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0081: Unknown result type (might be due to invalid IL or missing references)
		//IL_008d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0092: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ad: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_011d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0122: Unknown result type (might be due to invalid IL or missing references)
		//IL_0126: Unknown result type (might be due to invalid IL or missing references)
		//IL_012b: Unknown result type (might be due to invalid IL or missing references)
		if (!PointerDownOverRTB)
		{
			return;
		}
		EditableParagraph editableParagraph = null;
		TransformedBounds value = VisualExtensions.GetTransformedBounds((Visual)(object)this).Value;
		Rect val = ((TransformedBounds)(ref value)).Clip;
		double y = ((Rect)(ref val)).Y;
		Rect val2 = default(Rect);
		foreach (KeyValuePair<EditableParagraph, Rect> visibleEditableParagraph in ((Visual?)(object)FlowDocSV).GetVisibleEditableParagraphs())
		{
			PointerPoint currentPoint = e.GetCurrentPoint((Visual)(object)FlowDocSV);
			Point position = ((PointerPoint)(ref currentPoint)).Position;
			val = visibleEditableParagraph.Value;
			double x = ((Rect)(ref val)).X;
			Thickness margin = ((Layoutable)DocIC).Margin;
			double num = x - ((Thickness)(ref margin)).Left;
			val = visibleEditableParagraph.Value;
			double y2 = ((Rect)(ref val)).Y;
			val = visibleEditableParagraph.Value;
			double width = ((Rect)(ref val)).Width;
			val = visibleEditableParagraph.Value;
			((Rect)(ref val2))..ctor(num, y2, width, ((Rect)(ref val)).Height);
			double num2 = ((Point)(ref position)).Y + y;
			if (((Rect)(ref val2)).Top <= num2 && ((Rect)(ref val2)).Bottom >= num2)
			{
				editableParagraph = visibleEditableParagraph.Key;
				break;
			}
		}
		if (editableParagraph == null)
		{
			return;
		}
		TextLayout textLayout = ((TextBlock)editableParagraph).TextLayout;
		Point position2 = e.GetPosition((Visual)(object)editableParagraph);
		TextHitTestResult val3 = textLayout.HitTestPoint(ref position2);
		int textPosition = ((TextHitTestResult)(ref val3)).TextPosition;
		if (((StyledElement)editableParagraph).DataContext is Paragraph paragraph)
		{
			if (paragraph.StartInDoc + textPosition < SelectionOrigin)
			{
				FlowDoc.SelectionExtendMode = AvRichTextBox.FlowDocument.ExtendMode.ExtendModeLeft;
				FlowDoc.Selection.End = SelectionOrigin;
				FlowDoc.Selection.Start = paragraph.StartInDoc + textPosition;
			}
			else
			{
				FlowDoc.SelectionExtendMode = AvRichTextBox.FlowDocument.ExtendMode.ExtendModeRight;
				FlowDoc.Selection.Start = SelectionOrigin;
				FlowDoc.Selection.End = paragraph.StartInDoc + textPosition;
			}
			FlowDoc.EnsureSelectionContinuity();
		}
	}

	private void RichTextBox_PointerReleased(object? sender, PointerReleasedEventArgs e)
	{
		PointerDownOverRTB = false;
	}

	private void FlowDocSV_PointerReleased(object? sender, PointerReleasedEventArgs e)
	{
		PointerDownOverRTB = false;
	}

	private void RichTextBox_PointerExited(object? sender, PointerEventArgs e)
	{
	}

	[GeneratedRegex("\\w+")]
	[GeneratedCode("System.Text.RegularExpressions.Generator", "10.0.14.15411")]
	private static Regex WordMatchesRegex()
	{
		return <RegexGenerator_g>F0338A28AE0D740519125F99FE91ED2E2A886FDAFB89901337C90E33E98CB422E__WordMatchesRegex_1.Instance;
	}

	internal void CreateClient()
	{
		InputMethod.SetIsInputMethodEnabled((InputElement)(object)this, true);
		((InputElement)this).TextInputMethodClientRequested += RichTextBox_TextInputMethodClientRequested;
		client = new RichTextBoxTextInputClient(this);
		((InputElement)this).Focus((NavigationMethod)0, (KeyModifiers)0);
	}

	private void RichTextBox_TextInputMethodClientRequested(object? sender, TextInputMethodClientRequestedEventArgs e)
	{
		if (((object)e).GetType() == typeof(TextInputMethodClientRequestedEventArgs))
		{
			if (client == null)
			{
				client = new RichTextBoxTextInputClient(this);
			}
			e.Client = (TextInputMethodClient)(object)client;
		}
	}

	internal void InsertPreeditText(string preeditText)
	{
		_preeditText = preeditText;
		UpdatePreeditOverlay();
	}

	public Point GetCurrentPosition()
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		return CaretPosition;
	}

	private void UpdatePreeditOverlay()
	{
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_0082: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00af: Unknown result type (might be due to invalid IL or missing references)
		if (!string.IsNullOrEmpty(_preeditText) && _CaretRect != null)
		{
			Thickness margin = ((Layoutable)_CaretRect).Margin;
			double num = ((Thickness)(ref margin)).Left - 2.0;
			margin = ((Layoutable)_CaretRect).Margin;
			double num2 = ((Thickness)(ref margin)).Top - 2.0;
			PreeditOverlay.Text = _preeditText;
			((Layoutable)PreeditOverlay).Margin = new Thickness(num, num2, 0.0, 0.0);
			((Visual)PreeditOverlay).IsVisible = true;
			Vector rTBScrollOffset = RtbVm.RTBScrollOffset;
			CaretPosition = new Point(num, num2 - ((Vector)(ref rTBScrollOffset)).Y);
			client.UpdateCaretPosition();
		}
		else
		{
			((Visual)PreeditOverlay).IsVisible = false;
		}
	}

	private void ToggleItalics()
	{
		if (!IsReadOnly)
		{
			FlowDoc.ToggleItalic();
		}
	}

	private void ToggleBold()
	{
		if (!IsReadOnly)
		{
			FlowDoc.ToggleBold();
		}
	}

	private void ToggleUnderlining()
	{
		if (!IsReadOnly)
		{
			FlowDoc.ToggleUnderlining();
		}
	}

	private void CopyToClipboard()
	{
		//IL_0009: Unknown result type (might be due to invalid IL or missing references)
		//IL_000f: Expected O, but got Unknown
		if (!DisableUserCopy)
		{
			DataObject val = new DataObject();
			string rtfFromInlines = RtfConversions.GetRtfFromInlines(FlowDoc.GetRangeInlines(FlowDoc.Selection));
			byte[] bytes = Encoding.Default.GetBytes(rtfFromInlines);
			val.Set("Rich Text Format", (object)bytes);
			val.Set("Text", (object)FlowDoc.Selection.GetText());
			TopLevel.GetTopLevel((Visual)(object)this).Clipboard.SetDataObjectAsync((IDataObject)(object)val);
		}
	}

	private async void PasteFromClipboard()
	{
		if (IsReadOnly)
		{
			return;
		}
		bool TextPasted = false;
		int start = FlowDoc.Selection.Start;
		int newSelPoint = start;
		string[] array = await TopLevel.GetTopLevel((Visual)(object)this).Clipboard.GetFormatsAsync();
		if (array.Contains("Rich Text Format"))
		{
			object obj = await TopLevel.GetTopLevel((Visual)(object)this).Clipboard.GetDataAsync("Rich Text Format");
			if (obj != null)
			{
				byte[] bytes = (byte[])obj;
				string rtfText = Encoding.Default.GetString(bytes);
				RTFDomDocument rTFDomDocument = new RTFDomDocument();
				rTFDomDocument.LoadRTFText(rtfText);
				List<IEditable> inlinesFromRtf = RtfConversions.GetInlinesFromRtf(rTFDomDocument);
				inlinesFromRtf.Reverse();
				int num = FlowDoc.PasteInlinesIntoRange(FlowDoc.Selection, inlinesFromRtf);
				newSelPoint = Math.Min(newSelPoint + num, FlowDoc.DocEndPoint - 1);
				TextPasted = true;
			}
		}
		else if (array.Contains("Text"))
		{
			object obj2 = await TopLevel.GetTopLevel((Visual)(object)this).Clipboard.GetDataAsync("Text");
			if (obj2 != null)
			{
				string text = obj2.ToString();
				if (text != null)
				{
					FlowDoc.SetRangeToText(FlowDoc.Selection, text);
					newSelPoint = Math.Min(newSelPoint + text.Length, FlowDoc.DocEndPoint - 1);
					TextPasted = true;
				}
			}
		}
		if (TextPasted)
		{
			((Layoutable)DocIC).UpdateLayout();
			await Task.Delay(100);
			FlowDoc.Selection.EndParagraph.CallRequestInlinesUpdate();
			FlowDoc.Selection.EndParagraph.UpdateEditableRunPositions();
			FlowDoc.Select(newSelPoint, 0);
			FlowDoc.UpdateSelection();
			FlowDoc.Selection.BiasForwardStart = false;
			FlowDoc.Selection.BiasForwardEnd = false;
			FlowDoc.SelectionExtendMode = AvRichTextBox.FlowDocument.ExtendMode.ExtendModeNone;
			CreateClient();
		}
	}

	internal void SelectionStart_RectChanged(EditableParagraph edPar)
	{
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_0029: Unknown result type (might be due to invalid IL or missing references)
		//IL_0033: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0059: Unknown result type (might be due to invalid IL or missing references)
		//IL_005e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_0083: Unknown result type (might be due to invalid IL or missing references)
		//IL_0088: Unknown result type (might be due to invalid IL or missing references)
		//IL_0098: Unknown result type (might be due to invalid IL or missing references)
		//IL_009c: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ba: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c4: Unknown result type (might be due to invalid IL or missing references)
		//IL_0111: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_013f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0144: Unknown result type (might be due to invalid IL or missing references)
		//IL_015f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0164: Unknown result type (might be due to invalid IL or missing references)
		//IL_0180: Unknown result type (might be due to invalid IL or missing references)
		//IL_0186: Invalid comparison between Unknown and I4
		//IL_0194: Unknown result type (might be due to invalid IL or missing references)
		//IL_0199: Unknown result type (might be due to invalid IL or missing references)
		((Layoutable)edPar).UpdateLayout();
		if (!(((StyledElement)edPar).DataContext is Paragraph paragraph))
		{
			return;
		}
		TextLayout textLayout = ((TextBlock)edPar).TextLayout;
		Rect val = textLayout.HitTestTextPosition(((SelectableTextBlock)edPar).SelectionStart);
		Rect val2 = textLayout.HitTestTextPosition(((SelectableTextBlock)edPar).SelectionStart - 1);
		Point? val3 = VisualExtensions.TranslatePoint((Visual)(object)edPar, ((Rect)(ref val)).Position, (Visual)(object)DocIC);
		if (!val3.HasValue)
		{
			return;
		}
		Point valueOrDefault = val3.GetValueOrDefault();
		val3 = VisualExtensions.TranslatePoint((Visual)(object)edPar, ((Rect)(ref val2)).Position, (Visual)(object)DocIC);
		if (!val3.HasValue)
		{
			return;
		}
		Point valueOrDefault2 = val3.GetValueOrDefault();
		FlowDoc.Selection.StartRect = new Rect(valueOrDefault, ((Rect)(ref val)).Size);
		FlowDoc.Selection.PrevCharRect = new Rect(valueOrDefault2, ((Rect)(ref val2)).Size);
		IReadOnlyList<TextLine> textLines = textLayout.TextLines;
		int lineIndexFromCharacterIndex = textLayout.GetLineIndexFromCharacterIndex(((SelectableTextBlock)edPar).SelectionStart, false);
		TextLine val4 = textLines[lineIndexFromCharacterIndex];
		double glyphRunHeight = val4.Height;
		int num = FlowDoc.Selection.Start - paragraph.StartInDoc;
		bool offsetTopFromHeight = false;
		BaselineAlignment balign = (BaselineAlignment)3;
		TextBounds val5 = val4.GetTextBounds(num, 1).FirstOrDefault();
		if (val5 != null && val5.TextRunBounds.Count > 0)
		{
			TextRunBounds val6 = val5.TextRunBounds[0];
			TextRun textRun = ((TextRunBounds)(ref val6)).TextRun;
			ShapedTextRun val7 = (ShapedTextRun)(object)((textRun is ShapedTextRun) ? textRun : null);
			if (val7 != null)
			{
				Rect bounds = val7.GlyphRun.Bounds;
				glyphRunHeight = ((Rect)(ref bounds)).Height - 2.0;
				offsetTopFromHeight = (int)((TextRun)val7).Properties.BaselineAlignment != 7;
				balign = ((TextRun)val7).Properties.BaselineAlignment;
			}
		}
		RtbVm.CalculateCaretHeightAndPosition(val4, ((Point)(ref valueOrDefault)).X, glyphRunHeight, offsetTopFromHeight, balign);
		RtbVm.UpdateCaretVisible();
		paragraph.DistanceSelectionStartFromLeft = ((Rect)(ref val)).Left;
		paragraph.IsStartAtFirstLine = lineIndexFromCharacterIndex == 0;
		paragraph.IsStartAtLastLine = lineIndexFromCharacterIndex == textLines.Count - 1;
		if (paragraph.IsStartAtFirstLine)
		{
			paragraph.CharPrevLineStart = ((SelectableTextBlock)edPar).SelectionStart;
		}
		else
		{
			paragraph.CharPrevLineStart = GetClosestIndex(edPar, lineIndexFromCharacterIndex, paragraph.DistanceSelectionStartFromLeft, -1);
		}
		if (paragraph.IsStartAtLastLine)
		{
			paragraph.CharNextLineStart = ((SelectableTextBlock)edPar).SelectionEnd - val4.FirstTextSourceIndex;
		}
		else
		{
			paragraph.CharNextLineStart = GetClosestIndex(edPar, lineIndexFromCharacterIndex, paragraph.DistanceSelectionStartFromLeft, 1);
		}
		paragraph.FirstIndexStartLine = (FlowDoc.Selection.IsAtEndOfLineSpace ? textLines[Math.Max(0, lineIndexFromCharacterIndex - 1)].FirstTextSourceIndex : val4.FirstTextSourceIndex);
		paragraph.FirstIndexLastLine = textLines[textLines.Count - 1].FirstTextSourceIndex;
	}

	internal void SelectionEnd_RectChanged(EditableParagraph edPar)
	{
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_0019: Unknown result type (might be due to invalid IL or missing references)
		//IL_001d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_004b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_007d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0082: Unknown result type (might be due to invalid IL or missing references)
		((Layoutable)edPar).UpdateLayout();
		TextLayout textLayout = ((TextBlock)edPar).TextLayout;
		Rect val = textLayout.HitTestTextPosition(((SelectableTextBlock)edPar).SelectionEnd);
		Point? val2 = VisualExtensions.TranslatePoint((Visual)(object)edPar, ((Rect)(ref val)).Position, (Visual)(object)DocIC);
		if (val2.HasValue)
		{
			FlowDoc.Selection.EndRect = new Rect(val2.Value, ((Rect)(ref val)).Size);
		}
		RtbVm.UpdateCaretVisible();
		if (!(((StyledElement)edPar).DataContext is Paragraph paragraph))
		{
			return;
		}
		Rect val3 = textLayout.HitTestTextPosition(((SelectableTextBlock)edPar).SelectionEnd);
		paragraph.DistanceSelectionEndFromLeft = ((Rect)(ref val3)).Left;
		int lineIndexFromCharacterIndex = textLayout.GetLineIndexFromCharacterIndex(((SelectableTextBlock)edPar).SelectionEnd, false);
		IReadOnlyList<TextLine> textLines = textLayout.TextLines;
		paragraph.IsEndAtLastLine = lineIndexFromCharacterIndex == textLines.Count - 1;
		paragraph.IsEndAtFirstLine = lineIndexFromCharacterIndex == 0;
		if (paragraph.IsEndAtLastLine)
		{
			paragraph.LastIndexEndLine = paragraph.BlockLength;
			paragraph.CharNextLineEnd = edPar.TextLength + 1 + ((SelectableTextBlock)edPar).SelectionEnd - textLines[lineIndexFromCharacterIndex].FirstTextSourceIndex;
		}
		else
		{
			TextLine val4 = textLines[lineIndexFromCharacterIndex];
			int num = 1;
			if (val4.TextRuns.Count > 0 && val4.TextRuns[val4.TextRuns.Count - 1].Text.ToString() == Environment.NewLine)
			{
				num++;
			}
			paragraph.LastIndexEndLine = textLines[lineIndexFromCharacterIndex + 1].FirstTextSourceIndex - num;
			paragraph.CharNextLineEnd = GetClosestIndex(edPar, lineIndexFromCharacterIndex, paragraph.DistanceSelectionEndFromLeft, 1);
		}
		if (!paragraph.IsEndAtFirstLine)
		{
			paragraph.CharPrevLineEnd = GetClosestIndex(edPar, lineIndexFromCharacterIndex, paragraph.DistanceSelectionEndFromLeft, -1);
		}
		paragraph.FirstIndexLastLine = textLines[textLines.Count - 1].FirstTextSourceIndex;
	}

	private static int GetClosestIndex(EditableParagraph edPar, int lineNo, double distanceFromLeft, int direction)
	{
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_0019: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		//IL_004b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		CharacterHit characterHitFromDistance = ((TextBlock)edPar).TextLayout.TextLines[lineNo + direction].GetCharacterHitFromDistance(distanceFromLeft);
		Rect val = ((TextBlock)edPar).TextLayout.HitTestTextPosition(((CharacterHit)(ref characterHitFromDistance)).FirstCharacterIndex);
		double num = Math.Abs(distanceFromLeft - ((Rect)(ref val)).Left);
		val = ((TextBlock)edPar).TextLayout.HitTestTextPosition(((CharacterHit)(ref characterHitFromDistance)).FirstCharacterIndex + 1);
		double num2 = Math.Abs(distanceFromLeft - ((Rect)(ref val)).Left);
		if (num > num2)
		{
			return ((CharacterHit)(ref characterHitFromDistance)).FirstCharacterIndex + 1;
		}
		return ((CharacterHit)(ref characterHitFromDistance)).FirstCharacterIndex;
	}

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "11.3.12.0")]
	[ExcludeFromCodeCoverage]
	public void InitializeComponent(bool loadXaml = true)
	{
		if (loadXaml)
		{
			!XamlIlPopulateTrampoline(this);
		}
		INameScope val = NameScopeExtensions.FindNameScope((ILogical)(object)this);
		MainDP = ((val != null) ? NameScopeExtensions.Find<DockPanel>(val, "MainDP") : null);
		FlowDocSV = ((val != null) ? NameScopeExtensions.Find<ScrollViewer>(val, "FlowDocSV") : null);
		DocIC = ((val != null) ? NameScopeExtensions.Find<ItemsControl>(val, "DocIC") : null);
		PreeditOverlay = ((val != null) ? NameScopeExtensions.Find<TextBlock>(val, "PreeditOverlay") : null);
	}

	[CompilerGenerated]
	private unsafe static void !XamlIlPopulate(IServiceProvider P_0, RichTextBox P_1)
	{
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		//IL_004f: Expected O, but got Unknown
		//IL_00b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c3: Expected O, but got Unknown
		//IL_00e8: Unknown result type (might be due to invalid IL or missing references)
		//IL_0117: Unknown result type (might be due to invalid IL or missing references)
		//IL_0128: Unknown result type (might be due to invalid IL or missing references)
		//IL_012d: Unknown result type (might be due to invalid IL or missing references)
		//IL_012e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0144: Unknown result type (might be due to invalid IL or missing references)
		//IL_0149: Unknown result type (might be due to invalid IL or missing references)
		//IL_014a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0155: Unknown result type (might be due to invalid IL or missing references)
		//IL_015a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0171: Expected O, but got Unknown
		//IL_0176: Expected O, but got Unknown
		//IL_017b: Expected O, but got Unknown
		//IL_0181: Unknown result type (might be due to invalid IL or missing references)
		//IL_0186: Unknown result type (might be due to invalid IL or missing references)
		//IL_0188: Expected O, but got Unknown
		//IL_01bb: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c2: Expected O, but got Unknown
		//IL_01da: Unknown result type (might be due to invalid IL or missing references)
		//IL_01df: Unknown result type (might be due to invalid IL or missing references)
		//IL_0209: Expected O, but got Unknown
		//IL_020a: Unknown result type (might be due to invalid IL or missing references)
		//IL_020f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0211: Expected O, but got Unknown
		//IL_0229: Unknown result type (might be due to invalid IL or missing references)
		//IL_022e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0258: Expected O, but got Unknown
		//IL_0259: Unknown result type (might be due to invalid IL or missing references)
		//IL_025e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0260: Expected O, but got Unknown
		//IL_0278: Unknown result type (might be due to invalid IL or missing references)
		//IL_027d: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a7: Expected O, but got Unknown
		//IL_02a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_02ac: Unknown result type (might be due to invalid IL or missing references)
		//IL_02ae: Expected O, but got Unknown
		//IL_02c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_02cb: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f5: Expected O, but got Unknown
		//IL_0300: Expected O, but got Unknown
		//IL_0305: Unknown result type (might be due to invalid IL or missing references)
		//IL_030a: Unknown result type (might be due to invalid IL or missing references)
		//IL_030c: Expected O, but got Unknown
		//IL_0335: Unknown result type (might be due to invalid IL or missing references)
		//IL_033a: Unknown result type (might be due to invalid IL or missing references)
		//IL_033b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0356: Expected O, but got Unknown
		//IL_0357: Unknown result type (might be due to invalid IL or missing references)
		//IL_035c: Unknown result type (might be due to invalid IL or missing references)
		//IL_035d: Unknown result type (might be due to invalid IL or missing references)
		//IL_038c: Unknown result type (might be due to invalid IL or missing references)
		//IL_03a0: Expected O, but got Unknown
		//IL_03a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_03a6: Unknown result type (might be due to invalid IL or missing references)
		//IL_03a8: Expected O, but got Unknown
		//IL_03c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_03c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_03ef: Expected O, but got Unknown
		//IL_03f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_03f5: Unknown result type (might be due to invalid IL or missing references)
		//IL_03f7: Expected O, but got Unknown
		//IL_040f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0414: Unknown result type (might be due to invalid IL or missing references)
		//IL_043e: Expected O, but got Unknown
		//IL_043f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0444: Unknown result type (might be due to invalid IL or missing references)
		//IL_0446: Expected O, but got Unknown
		//IL_045e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0463: Unknown result type (might be due to invalid IL or missing references)
		//IL_048d: Expected O, but got Unknown
		//IL_048e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0493: Unknown result type (might be due to invalid IL or missing references)
		//IL_0495: Expected O, but got Unknown
		//IL_04ad: Unknown result type (might be due to invalid IL or missing references)
		//IL_04b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_04dc: Expected O, but got Unknown
		//IL_04dd: Unknown result type (might be due to invalid IL or missing references)
		//IL_04e2: Unknown result type (might be due to invalid IL or missing references)
		//IL_04e4: Expected O, but got Unknown
		//IL_04fc: Unknown result type (might be due to invalid IL or missing references)
		//IL_0501: Unknown result type (might be due to invalid IL or missing references)
		//IL_052b: Expected O, but got Unknown
		//IL_052c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0531: Unknown result type (might be due to invalid IL or missing references)
		//IL_0533: Expected O, but got Unknown
		//IL_054b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0550: Unknown result type (might be due to invalid IL or missing references)
		//IL_057a: Expected O, but got Unknown
		//IL_057b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0580: Unknown result type (might be due to invalid IL or missing references)
		//IL_0582: Expected O, but got Unknown
		//IL_059a: Unknown result type (might be due to invalid IL or missing references)
		//IL_059f: Unknown result type (might be due to invalid IL or missing references)
		//IL_05c9: Expected O, but got Unknown
		//IL_05ca: Unknown result type (might be due to invalid IL or missing references)
		//IL_05cf: Unknown result type (might be due to invalid IL or missing references)
		//IL_05d1: Expected O, but got Unknown
		//IL_05e9: Unknown result type (might be due to invalid IL or missing references)
		//IL_05ee: Unknown result type (might be due to invalid IL or missing references)
		//IL_0618: Expected O, but got Unknown
		//IL_0619: Unknown result type (might be due to invalid IL or missing references)
		//IL_061e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0620: Expected O, but got Unknown
		//IL_0638: Unknown result type (might be due to invalid IL or missing references)
		//IL_063d: Unknown result type (might be due to invalid IL or missing references)
		//IL_066c: Expected O, but got Unknown
		//IL_066c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0671: Unknown result type (might be due to invalid IL or missing references)
		//IL_0673: Expected O, but got Unknown
		//IL_068b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0690: Unknown result type (might be due to invalid IL or missing references)
		//IL_06bf: Expected O, but got Unknown
		//IL_06ca: Expected O, but got Unknown
		//IL_06cb: Unknown result type (might be due to invalid IL or missing references)
		//IL_06d0: Unknown result type (might be due to invalid IL or missing references)
		//IL_06d3: Expected O, but got Unknown
		//IL_06d3: Unknown result type (might be due to invalid IL or missing references)
		//IL_06d9: Expected O, but got Unknown
		//IL_06de: Expected O, but got Unknown
		//IL_0719: Unknown result type (might be due to invalid IL or missing references)
		//IL_071e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0745: Unknown result type (might be due to invalid IL or missing references)
		//IL_074a: Unknown result type (might be due to invalid IL or missing references)
		//IL_074d: Expected O, but got Unknown
		//IL_074d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0753: Expected O, but got Unknown
		//IL_0758: Expected O, but got Unknown
		//IL_07b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_07e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_07f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_07fd: Unknown result type (might be due to invalid IL or missing references)
		//IL_08a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_08a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_08ac: Expected O, but got Unknown
		//IL_08ac: Unknown result type (might be due to invalid IL or missing references)
		//IL_08b2: Expected O, but got Unknown
		//IL_08b7: Expected O, but got Unknown
		//IL_08d5: Unknown result type (might be due to invalid IL or missing references)
		//IL_08da: Unknown result type (might be due to invalid IL or missing references)
		//IL_08dd: Expected O, but got Unknown
		//IL_08dd: Unknown result type (might be due to invalid IL or missing references)
		//IL_08e3: Expected O, but got Unknown
		//IL_08e8: Expected O, but got Unknown
		//IL_091e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0923: Unknown result type (might be due to invalid IL or missing references)
		//IL_0954: Unknown result type (might be due to invalid IL or missing references)
		//IL_0959: Unknown result type (might be due to invalid IL or missing references)
		//IL_099f: Unknown result type (might be due to invalid IL or missing references)
		//IL_09b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_09b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_09db: Unknown result type (might be due to invalid IL or missing references)
		//IL_09e0: Unknown result type (might be due to invalid IL or missing references)
		//IL_09f7: Expected O, but got Unknown
		//IL_09f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_09fd: Unknown result type (might be due to invalid IL or missing references)
		//IL_0a00: Expected O, but got Unknown
		//IL_0a26: Expected O, but got Unknown
		//IL_0a38: Unknown result type (might be due to invalid IL or missing references)
		//IL_0a3d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0a40: Expected O, but got Unknown
		//IL_0a40: Unknown result type (might be due to invalid IL or missing references)
		//IL_0a46: Expected O, but got Unknown
		//IL_0a4b: Expected O, but got Unknown
		//IL_0a94: Unknown result type (might be due to invalid IL or missing references)
		//IL_0ad0: Unknown result type (might be due to invalid IL or missing references)
		//IL_0ada: Expected O, but got Unknown
		//IL_0ae0: Unknown result type (might be due to invalid IL or missing references)
		//IL_0aea: Expected O, but got Unknown
		XamlIlContext.Context<RichTextBox> context = new XamlIlContext.Context<RichTextBox>(P_0, new object[1] { !AvaloniaResources.NamespaceInfo:/RichTextBox/RichTextBox.axaml.Singleton }, "avares://AvRichTextBox/RichTextBox/RichTextBox.axaml")
		{
			RootObject = P_1,
			IntermediateRoot = P_1
		};
		((ISupportInitialize)P_1).BeginInit();
		context.PushParent(P_1);
		((TemplatedControl)P_1).Background = (IBrush)new ImmutableSolidColorBrush(4294309365u);
		((Interactive)P_1).AddHandler<PointerReleasedEventArgs>(((InputElement)P_1).PointerReleasedEvent, (EventHandler<PointerReleasedEventArgs>)context.RootObject.RichTextBox_PointerReleased, (RoutingStrategies)5, false);
		((Interactive)P_1).AddHandler<PointerEventArgs>(((InputElement)P_1).PointerExitedEvent, (EventHandler<PointerEventArgs>)context.RootObject.RichTextBox_PointerExited, (RoutingStrategies)5, false);
		((Interactive)P_1).AddHandler<KeyEventArgs>(((InputElement)P_1).KeyDownEvent, (EventHandler<KeyEventArgs>)context.RootObject.RichTextBox_KeyDown, (RoutingStrategies)5, false);
		((InputElement)P_1).Focusable = true;
		((TemplatedControl)P_1).BorderBrush = (IBrush)new ImmutableSolidColorBrush(4278190080u);
		((TemplatedControl)P_1).BorderThickness = new Thickness(1.0, 1.0, 1.0, 1.0);
		((TemplatedControl)P_1).Padding = new Thickness(2.0, 2.0, 2.0, 2.0);
		Styles styles = ((StyledElement)P_1).Styles;
		Style val = new Style();
		val.Selector = Selectors.OfType((Selector)null, typeof(EditableTable));
		Setter val2 = new Setter();
		val2.Property = (AvaloniaProperty)(object)TemplatedControl.TemplateProperty;
		val2.Value = (object)new ControlTemplate
		{
			Content = XamlIlRuntimeHelpers.DeferredTransformationFactoryV3<Control>((IntPtr)(nint)(delegate*<IServiceProvider, object>)(&XamlClosure_2.Build_1), (IServiceProvider)context)
		};
		((StyleBase)val).Add((SetterBase)val2);
		styles.Add((IStyle)val);
		Styles styles2 = ((StyledElement)P_1).Styles;
		Style val3 = new Style();
		Style val4 = val3;
		context.PushParent(val4);
		Style obj = val4;
		obj.Selector = Selectors.OfType(Selectors.Child(Selectors.OfType((Selector)null, typeof(EditableTable))), typeof(ContentPresenter));
		Setter val5 = new Setter();
		Setter val6 = val5;
		context.PushParent(val6);
		Setter obj2 = val6;
		obj2.Property = (AvaloniaProperty)(object)Grid.RowProperty;
		ReflectionBindingExtension val7 = new ReflectionBindingExtension("RowNo");
		context.ProvideTargetProperty = XamlIlHelpers.Avalonia.Styling.Setter,Avalonia.Base.Value!Property();
		Binding value = val7.ProvideValue((IServiceProvider)context);
		context.ProvideTargetProperty = null;
		obj2.Value = value;
		context.PopParent();
		((StyleBase)obj).Add((SetterBase)val5);
		Setter val8 = new Setter();
		val6 = val8;
		context.PushParent(val6);
		Setter obj3 = val6;
		obj3.Property = (AvaloniaProperty)(object)Grid.ColumnProperty;
		ReflectionBindingExtension val9 = new ReflectionBindingExtension("ColNo");
		context.ProvideTargetProperty = XamlIlHelpers.Avalonia.Styling.Setter,Avalonia.Base.Value!Property();
		Binding value2 = val9.ProvideValue((IServiceProvider)context);
		context.ProvideTargetProperty = null;
		obj3.Value = value2;
		context.PopParent();
		((StyleBase)obj).Add((SetterBase)val8);
		Setter val10 = new Setter();
		val6 = val10;
		context.PushParent(val6);
		Setter obj4 = val6;
		obj4.Property = (AvaloniaProperty)(object)Grid.ColumnSpanProperty;
		ReflectionBindingExtension val11 = new ReflectionBindingExtension("ColSpan");
		context.ProvideTargetProperty = XamlIlHelpers.Avalonia.Styling.Setter,Avalonia.Base.Value!Property();
		Binding value3 = val11.ProvideValue((IServiceProvider)context);
		context.ProvideTargetProperty = null;
		obj4.Value = value3;
		context.PopParent();
		((StyleBase)obj).Add((SetterBase)val10);
		Setter val12 = new Setter();
		val6 = val12;
		context.PushParent(val6);
		Setter obj5 = val6;
		obj5.Property = (AvaloniaProperty)(object)Grid.RowSpanProperty;
		ReflectionBindingExtension val13 = new ReflectionBindingExtension("RowSpan");
		context.ProvideTargetProperty = XamlIlHelpers.Avalonia.Styling.Setter,Avalonia.Base.Value!Property();
		Binding value4 = val13.ProvideValue((IServiceProvider)context);
		context.ProvideTargetProperty = null;
		obj5.Value = value4;
		context.PopParent();
		((StyleBase)obj).Add((SetterBase)val12);
		context.PopParent();
		styles2.Add((IStyle)val3);
		Styles styles3 = ((StyledElement)P_1).Styles;
		Style val14 = new Style();
		val4 = val14;
		context.PushParent(val4);
		Style obj6 = val4;
		obj6.Selector = Selectors.Class(Selectors.OfType((Selector)null, typeof(EditableParagraph)), "paragraphBindings");
		Setter val15 = new Setter();
		val15.Property = (AvaloniaProperty)(object)TextBlock.TextWrappingProperty;
		val15.Value = (object)(TextWrapping)1;
		((StyleBase)obj6).Add((SetterBase)val15);
		Setter val16 = new Setter();
		val16.Property = (AvaloniaProperty)(object)Layoutable.MarginProperty;
		val16.Value = (object)new Thickness(5.0, 5.0, 5.0, 5.0);
		((StyleBase)obj6).Add((SetterBase)val16);
		Setter val17 = new Setter();
		val6 = val17;
		context.PushParent(val6);
		Setter obj7 = val6;
		obj7.Property = (AvaloniaProperty)(object)TextBlock.LineSpacingProperty;
		ReflectionBindingExtension val18 = new ReflectionBindingExtension("LineSpacing");
		context.ProvideTargetProperty = XamlIlHelpers.Avalonia.Styling.Setter,Avalonia.Base.Value!Property();
		Binding value5 = val18.ProvideValue((IServiceProvider)context);
		context.ProvideTargetProperty = null;
		obj7.Value = value5;
		context.PopParent();
		((StyleBase)obj6).Add((SetterBase)val17);
		Setter val19 = new Setter();
		val6 = val19;
		context.PushParent(val6);
		Setter obj8 = val6;
		obj8.Property = (AvaloniaProperty)(object)TextBlock.FontFamilyProperty;
		ReflectionBindingExtension val20 = new ReflectionBindingExtension("FontFamily");
		context.ProvideTargetProperty = XamlIlHelpers.Avalonia.Styling.Setter,Avalonia.Base.Value!Property();
		Binding value6 = val20.ProvideValue((IServiceProvider)context);
		context.ProvideTargetProperty = null;
		obj8.Value = value6;
		context.PopParent();
		((StyleBase)obj6).Add((SetterBase)val19);
		Setter val21 = new Setter();
		val6 = val21;
		context.PushParent(val6);
		Setter obj9 = val6;
		obj9.Property = (AvaloniaProperty)(object)TextBlock.FontWeightProperty;
		ReflectionBindingExtension val22 = new ReflectionBindingExtension("FontWeight");
		context.ProvideTargetProperty = XamlIlHelpers.Avalonia.Styling.Setter,Avalonia.Base.Value!Property();
		Binding value7 = val22.ProvideValue((IServiceProvider)context);
		context.ProvideTargetProperty = null;
		obj9.Value = value7;
		context.PopParent();
		((StyleBase)obj6).Add((SetterBase)val21);
		Setter val23 = new Setter();
		val6 = val23;
		context.PushParent(val6);
		Setter obj10 = val6;
		obj10.Property = (AvaloniaProperty)(object)TextBlock.FontSizeProperty;
		ReflectionBindingExtension val24 = new ReflectionBindingExtension("FontSize");
		context.ProvideTargetProperty = XamlIlHelpers.Avalonia.Styling.Setter,Avalonia.Base.Value!Property();
		Binding value8 = val24.ProvideValue((IServiceProvider)context);
		context.ProvideTargetProperty = null;
		obj10.Value = value8;
		context.PopParent();
		((StyleBase)obj6).Add((SetterBase)val23);
		Setter val25 = new Setter();
		val6 = val25;
		context.PushParent(val6);
		Setter obj11 = val6;
		obj11.Property = (AvaloniaProperty)(object)TextBlock.BackgroundProperty;
		ReflectionBindingExtension val26 = new ReflectionBindingExtension("Background");
		context.ProvideTargetProperty = XamlIlHelpers.Avalonia.Styling.Setter,Avalonia.Base.Value!Property();
		Binding value9 = val26.ProvideValue((IServiceProvider)context);
		context.ProvideTargetProperty = null;
		obj11.Value = value9;
		context.PopParent();
		((StyleBase)obj6).Add((SetterBase)val25);
		Setter val27 = new Setter();
		val6 = val27;
		context.PushParent(val6);
		Setter obj12 = val6;
		obj12.Property = (AvaloniaProperty)(object)TextBlock.TextAlignmentProperty;
		ReflectionBindingExtension val28 = new ReflectionBindingExtension("TextAlignment");
		context.ProvideTargetProperty = XamlIlHelpers.Avalonia.Styling.Setter,Avalonia.Base.Value!Property();
		Binding value10 = val28.ProvideValue((IServiceProvider)context);
		context.ProvideTargetProperty = null;
		obj12.Value = value10;
		context.PopParent();
		((StyleBase)obj6).Add((SetterBase)val27);
		Setter val29 = new Setter();
		val6 = val29;
		context.PushParent(val6);
		Setter obj13 = val6;
		obj13.Property = (AvaloniaProperty)(object)SelectableTextBlock.SelectionBrushProperty;
		ReflectionBindingExtension val30 = new ReflectionBindingExtension("SelectionBrush");
		context.ProvideTargetProperty = XamlIlHelpers.Avalonia.Styling.Setter,Avalonia.Base.Value!Property();
		Binding value11 = val30.ProvideValue((IServiceProvider)context);
		context.ProvideTargetProperty = null;
		obj13.Value = value11;
		context.PopParent();
		((StyleBase)obj6).Add((SetterBase)val29);
		Setter val31 = new Setter();
		val6 = val31;
		context.PushParent(val6);
		Setter obj14 = val6;
		obj14.Property = (AvaloniaProperty)(object)Layoutable.VerticalAlignmentProperty;
		ReflectionBindingExtension val32 = new ReflectionBindingExtension("VerticalAlignment");
		context.ProvideTargetProperty = XamlIlHelpers.Avalonia.Styling.Setter,Avalonia.Base.Value!Property();
		Binding value12 = val32.ProvideValue((IServiceProvider)context);
		context.ProvideTargetProperty = null;
		obj14.Value = value12;
		context.PopParent();
		((StyleBase)obj6).Add((SetterBase)val31);
		Setter val33 = new Setter();
		val6 = val33;
		context.PushParent(val6);
		Setter obj15 = val6;
		obj15.Property = (AvaloniaProperty)(object)SelectableTextBlock.SelectionStartProperty;
		ReflectionBindingExtension val34 = new ReflectionBindingExtension("SelectionStartInBlock")
		{
			Mode = (BindingMode)2
		};
		context.ProvideTargetProperty = XamlIlHelpers.Avalonia.Styling.Setter,Avalonia.Base.Value!Property();
		Binding value13 = val34.ProvideValue((IServiceProvider)context);
		context.ProvideTargetProperty = null;
		obj15.Value = value13;
		context.PopParent();
		((StyleBase)obj6).Add((SetterBase)val33);
		Setter val35 = new Setter();
		val6 = val35;
		context.PushParent(val6);
		Setter obj16 = val6;
		obj16.Property = (AvaloniaProperty)(object)SelectableTextBlock.SelectionEndProperty;
		ReflectionBindingExtension val36 = new ReflectionBindingExtension("SelectionEndInBlock")
		{
			Mode = (BindingMode)2
		};
		context.ProvideTargetProperty = XamlIlHelpers.Avalonia.Styling.Setter,Avalonia.Base.Value!Property();
		Binding value14 = val36.ProvideValue((IServiceProvider)context);
		context.ProvideTargetProperty = null;
		obj16.Value = value14;
		context.PopParent();
		((StyleBase)obj6).Add((SetterBase)val35);
		context.PopParent();
		styles3.Add((IStyle)val14);
		DockPanel val37 = new DockPanel();
		DockPanel val38 = val37;
		((ISupportInitialize)val37).BeginInit();
		((ContentControl)P_1).Content = (object)val37;
		DockPanel val39;
		DockPanel obj17 = (val39 = val38);
		context.PushParent(val39);
		((StyledElement)val39).Name = "MainDP";
		object obj18 = val39;
		context.AvaloniaNameScope.Register("MainDP", obj18);
		StyledProperty<double> minWidthProperty = Layoutable.MinWidthProperty;
		ReflectionBindingExtension val40 = new ReflectionBindingExtension("MinWidth");
		context.ProvideTargetProperty = Layoutable.MinWidthProperty;
		Binding obj19 = val40.ProvideValue((IServiceProvider)context);
		context.ProvideTargetProperty = null;
		AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)val39, (AvaloniaProperty)(object)minWidthProperty, (IBinding)(object)obj19, (object)null);
		Controls children = ((Panel)val39).Children;
		ScrollViewer val41 = new ScrollViewer();
		ScrollViewer val42 = val41;
		((ISupportInitialize)val41).BeginInit();
		((AvaloniaList<Control>)(object)children).Add((Control)val41);
		ScrollViewer val43;
		ScrollViewer obj20 = (val43 = val42);
		context.PushParent(val43);
		((StyledElement)val43).Name = "FlowDocSV";
		obj18 = val43;
		context.AvaloniaNameScope.Register("FlowDocSV", obj18);
		val43.HorizontalScrollBarVisibility = (ScrollBarVisibility)0;
		((Layoutable)val43).Margin = new Thickness(0.0, 0.0, 0.0, 0.0);
		((TemplatedControl)val43).Padding = new Thickness(0.0, 0.0, 0.0, 0.0);
		StyledProperty<Vector> offsetProperty = ScrollViewer.OffsetProperty;
		ReflectionBindingExtension val44 = new ReflectionBindingExtension("RTBScrollOffset")
		{
			Mode = (BindingMode)1
		};
		context.ProvideTargetProperty = ScrollViewer.OffsetProperty;
		Binding obj21 = val44.ProvideValue((IServiceProvider)context);
		context.ProvideTargetProperty = null;
		AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)val43, (AvaloniaProperty)(object)offsetProperty, (IBinding)(object)obj21, (object)null);
		((Interactive)val43).AddHandler<ScrollChangedEventArgs>(val43.ScrollChangedEvent, (EventHandler<ScrollChangedEventArgs>)context.RootObject.ScrollViewer_ScrollChanged, (RoutingStrategies)5, false);
		((Interactive)val43).AddHandler<PointerPressedEventArgs>(((InputElement)val43).PointerPressedEvent, (EventHandler<PointerPressedEventArgs>)context.RootObject.FlowDocSV_PointerPressed, (RoutingStrategies)5, false);
		((Interactive)val43).AddHandler<PointerEventArgs>(((InputElement)val43).PointerMovedEvent, (EventHandler<PointerEventArgs>)context.RootObject.FlowDocSV_PointerMoved, (RoutingStrategies)5, false);
		((Interactive)val43).AddHandler<PointerReleasedEventArgs>(((InputElement)val43).PointerReleasedEvent, (EventHandler<PointerReleasedEventArgs>)context.RootObject.FlowDocSV_PointerReleased, (RoutingStrategies)5, false);
		Grid val45 = new Grid();
		Grid val46 = val45;
		((ISupportInitialize)val45).BeginInit();
		((ContentControl)val43).Content = (object)val45;
		Grid val47;
		Grid obj22 = (val47 = val46);
		context.PushParent(val47);
		((Layoutable)val47).VerticalAlignment = (VerticalAlignment)1;
		Controls children2 = ((Panel)val47).Children;
		ItemsControl val48 = new ItemsControl();
		ItemsControl val49 = val48;
		((ISupportInitialize)val48).BeginInit();
		((AvaloniaList<Control>)(object)children2).Add((Control)val48);
		ItemsControl val50;
		ItemsControl obj23 = (val50 = val49);
		context.PushParent(val50);
		((StyledElement)val50).Name = "DocIC";
		obj18 = val50;
		context.AvaloniaNameScope.Register("DocIC", obj18);
		ReflectionBindingExtension val51 = new ReflectionBindingExtension("FlowDoc");
		context.ProvideTargetProperty = StyledElement.DataContextProperty;
		Binding obj24 = val51.ProvideValue((IServiceProvider)context);
		context.ProvideTargetProperty = null;
		XamlDynamicSetters.<>XamlDynamicSetter_1((StyledElement)(object)val50, obj24);
		((Layoutable)val50).VerticalAlignment = (VerticalAlignment)1;
		StyledProperty<IEnumerable> itemsSourceProperty = ItemsControl.ItemsSourceProperty;
		ReflectionBindingExtension val52 = new ReflectionBindingExtension("Blocks");
		context.ProvideTargetProperty = ItemsControl.ItemsSourceProperty;
		Binding obj25 = val52.ProvideValue((IServiceProvider)context);
		context.ProvideTargetProperty = null;
		AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)val50, (AvaloniaProperty)(object)itemsSourceProperty, (IBinding)(object)obj25, (object)null);
		((Layoutable)val50).Margin = new Thickness(0.0, 0.0, 0.0, 0.0);
		StyledProperty<Thickness> paddingProperty = TemplatedControl.PaddingProperty;
		ReflectionBindingExtension val53 = new ReflectionBindingExtension("PagePadding");
		context.ProvideTargetProperty = TemplatedControl.PaddingProperty;
		Binding obj26 = val53.ProvideValue((IServiceProvider)context);
		context.ProvideTargetProperty = null;
		AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)val50, (AvaloniaProperty)(object)paddingProperty, (IBinding)(object)obj26, (object)null);
		val50.ItemsPanel = (ITemplate<Panel>)new ItemsPanelTemplate
		{
			Content = XamlIlRuntimeHelpers.DeferredTransformationFactoryV3<Control>((IntPtr)(nint)(delegate*<IServiceProvider, object>)(&XamlClosure_2.Build_2), (IServiceProvider)context)
		};
		DataTemplate val54 = new DataTemplate();
		DataTemplate val55 = val54;
		context.PushParent(val55);
		val55.Content = XamlIlRuntimeHelpers.DeferredTransformationFactoryV3<Control>((IntPtr)(nint)(delegate*<IServiceProvider, object>)(&XamlClosure_2.Build_3), (IServiceProvider)context);
		context.PopParent();
		val50.ItemTemplate = (IDataTemplate)val54;
		context.PopParent();
		((ISupportInitialize)obj23).EndInit();
		Controls children3 = ((Panel)val47).Children;
		TextBlock val56 = new TextBlock();
		TextBlock val57 = val56;
		((ISupportInitialize)val56).BeginInit();
		((AvaloniaList<Control>)(object)children3).Add((Control)val56);
		((StyledElement)val57).Name = "PreeditOverlay";
		obj18 = val57;
		context.AvaloniaNameScope.Register("PreeditOverlay", obj18);
		val57.Padding = new Thickness(4.0, 0.0, 4.0, 2.0);
		((Layoutable)val57).Height = 24.0;
		val57.FontSize = 15.0;
		((Layoutable)val57).HorizontalAlignment = (HorizontalAlignment)1;
		((Layoutable)val57).VerticalAlignment = (VerticalAlignment)1;
		val57.Foreground = (IBrush)new ImmutableSolidColorBrush(4278190080u);
		val57.Background = (IBrush)new ImmutableSolidColorBrush(4292730333u);
		((InputElement)val57).IsHitTestVisible = false;
		((Visual)val57).IsVisible = false;
		((ISupportInitialize)val57).EndInit();
		context.PopParent();
		((ISupportInitialize)obj22).EndInit();
		context.PopParent();
		((ISupportInitialize)obj20).EndInit();
		context.PopParent();
		((ISupportInitialize)obj17).EndInit();
		context.PopParent();
		((ISupportInitialize)P_1).EndInit();
		StyledElement val58;
		if ((val58 = (StyledElement)(object)((P_1 is StyledElement) ? P_1 : null)) != null)
		{
			NameScope.SetNameScope(val58, context.AvaloniaNameScope);
		}
		context.AvaloniaNameScope.Complete();
	}

	[CompilerGenerated]
	private static void !XamlIlPopulateTrampoline(RichTextBox P_0)
	{
		if (!XamlIlPopulateOverride != null)
		{
			!XamlIlPopulateOverride(P_0);
		}
		else
		{
			!XamlIlPopulate(XamlIlRuntimeHelpers.CreateRootServiceProviderV3((IServiceProvider)null), P_0);
		}
	}
}
