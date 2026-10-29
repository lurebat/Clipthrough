using System;
using Avalonia;
using Avalonia.Controls;
using Clipthrough.Models;
using Clipthrough.Services;

namespace Clipthrough.Views;

/// <summary>
/// How the main window fits its screen: the breakpoint layouts and the
/// remembered geometry. (docs/plans/2026-10-28-001-multi-monitor-fit-design.md)
/// </summary>
public partial class MainWindow
{
    private (WorkspaceWidth Width, bool Short)? m_appliedBreakpoint;
    private IWindowPlacementStore? m_placementStore;
    private WindowPlacement? m_normalPlacement;

    /// <summary>The breakpoint the workspace is laid out for, for tests.</summary>
    internal (WorkspaceWidth Width, bool Short)? AppliedBreakpoint => m_appliedBreakpoint;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Content is null while the base constructor sets the initial client
        // size, before InitializeComponent has built anything to rearrange.
        if (change.Property == ClientSizeProperty && Content is not null)
        {
            ApplyWorkspaceBreakpoint(WorkspaceBreakpoints.Classify(ClientSize));
            RememberNormalPlacement();
        }
        else if (change.Property == IsVisibleProperty && !IsVisible)
        {
            SavePlacement();
        }
    }

    private void InitializePlacement(IWindowPlacementStore? store)
    {
        m_placementStore = store;
        WindowFitter.Attach(this, fitOnOpen: false);
        PositionChanged += (_, _) => RememberNormalPlacement();
        Closing += (_, _) => SavePlacement();

        if (store?.Load() is not { } saved)
        {
            return;
        }

        // Applied before the window is first shown, so it opens where it was
        // rather than jumping there. The frame size is not known yet; the fit
        // in OnOpened corrects for it.
        WindowFitter.Restore(this, new WindowPlacement(new Size(saved.Width, saved.Height), new PixelPoint(saved.X, saved.Y)));
        if (saved.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// Tracks the last normal geometry, because a maximized window reports the
    /// screen's size and a minimized one an off-screen position.
    /// </summary>
    private void RememberNormalPlacement()
    {
        if (WindowState == WindowState.Normal && IsVisible)
        {
            m_normalPlacement = new WindowPlacement(WindowFitter.RequestedClientSize(this), Position);
        }
    }

    private void SavePlacement()
    {
        // Read now rather than trusting the last event: not every platform
        // raises PositionChanged for a move made in code.
        if (WindowState == WindowState.Normal)
        {
            m_normalPlacement = new WindowPlacement(WindowFitter.RequestedClientSize(this), Position);
        }

        if (m_placementStore is null || m_normalPlacement is not { } normal)
        {
            return;
        }

        m_placementStore.Save(new SavedWindowPlacement(
            normal.Position.X,
            normal.Position.Y,
            normal.ClientSize.Width,
            normal.ClientSize.Height,
            WindowState == WindowState.Maximized));
    }

    /// <summary>
    /// Lays the workspace out for <paramref name="breakpoint"/>. Grid row and
    /// column definitions are not styleable in Avalonia, so the parts that
    /// move between rows and columns are placed here; everything that only
    /// shows, hides or resizes is a style in MainWindow.axaml keyed on the
    /// bp-* classes set below.
    /// </summary>
    private void ApplyWorkspaceBreakpoint((WorkspaceWidth Width, bool Short) breakpoint)
    {
        if (m_appliedBreakpoint == breakpoint)
        {
            return;
        }

        m_appliedBreakpoint = breakpoint;
        var (width, isShort) = breakpoint;
        Classes.Set("bp-medium", width == WorkspaceWidth.Medium);
        Classes.Set("bp-narrow", width == WorkspaceWidth.Narrow);
        Classes.Set("bp-short", isShort);

        if (this.FindControl<Grid>("HeroSearchRow") is { } heroRow
            && this.FindControl<Control>("HeroSearchHost") is { } search
            && this.FindControl<Control>("HeroTypeSegments") is { } segments)
        {
            if (width == WorkspaceWidth.Narrow)
            {
                heroRow.ColumnDefinitions = new ColumnDefinitions("*");
                heroRow.RowDefinitions = new RowDefinitions("Auto,Auto");
                Place(search, 0, 0);
                Place(segments, 1, 0);
            }
            else
            {
                heroRow.ColumnDefinitions = new ColumnDefinitions("Auto,1000*,*,1000*");
                heroRow.RowDefinitions = new RowDefinitions("Auto");
                Place(search, 0, 1);
                Place(segments, 0, 3);
            }
            // Past these the search box and the type toggles only get emptier.
            // The thin spacer between them takes whatever the caps leave, so on a
            // 2560+ DIP screen the toggles stay at the right edge. (B18)
            if (width != WorkspaceWidth.Narrow)
            {
                heroRow.ColumnDefinitions[1].MaxWidth = WorkspaceBreakpoints.SearchMaxWidth;
                heroRow.ColumnDefinitions[3].MaxWidth = WorkspaceBreakpoints.TypeFilterMaxWidth;
            }
        }

        if (this.FindControl<Grid>("HeroFilterRow") is { } filterRow
            && this.FindControl<Control>("CategoryFilters") is { } categories
            && this.FindControl<Control>("SortComboBox") is { } sort
            && this.FindControl<Control>("SearchModifiers") is { } modifiers
            && this.FindControl<Control>("SearchOptionsButton") is { } options)
        {
            if (width == WorkspaceWidth.Wide)
            {
                filterRow.ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto");
                filterRow.RowDefinitions = new RowDefinitions("Auto");
                Place(categories, 0, 0);
                Place(sort, 0, 2);
                Place(modifiers, 0, 4);
                Place(options, 0, 4);
            }
            else
            {
                // Short collapses the modifiers into the Search options button,
                // which then shares the first row with the sort order.
                filterRow.ColumnDefinitions = new ColumnDefinitions(isShort ? "*,Auto,Auto" : "*,Auto");
                filterRow.RowDefinitions = new RowDefinitions(isShort ? "Auto" : "Auto,Auto");
                Place(categories, 0, 0);
                Place(sort, 0, 1);
                Place(modifiers, 1, 0, columnSpan: 2);
                Place(options, 0, 2);
            }
        }

        if (this.FindControl<Grid>("WorkspaceSplit") is { } split
            && this.FindControl<Control>("ClipListPane") is { } list
            && this.FindControl<GridSplitter>("WorkspaceSplitter") is { } splitter
            && this.FindControl<Control>("PreviewPane") is { } preview)
        {
            if (width == WorkspaceWidth.Narrow)
            {
                split.ColumnDefinitions = new ColumnDefinitions("*");
                split.RowDefinitions = new RowDefinitions("*,6,*");
                split.ColumnSpacing = 0;
                split.RowSpacing = 4;
                splitter.ResizeDirection = GridResizeDirection.Rows;
                Place(list, 0, 0);
                Place(splitter, 1, 0);
                Place(preview, 2, 0);
            }
            else
            {
                split.ColumnDefinitions = width == WorkspaceWidth.Wide
                    ? new ColumnDefinitions("*,6,2*")
                    : new ColumnDefinitions("2*,6,3*");

                // On a 2560 DIP or wider screen the preview's lines grow past
                // anything comfortable to read; the list takes the rest. (B18)
                split.ColumnDefinitions[2].MaxWidth = WorkspaceBreakpoints.PreviewMaxWidth;
                split.RowDefinitions = new RowDefinitions("*");
                split.ColumnSpacing = 4;
                split.RowSpacing = 0;
                splitter.ResizeDirection = GridResizeDirection.Columns;
                Place(list, 0, 0);
                Place(splitter, 0, 1);
                Place(preview, 0, 2);
            }
        }

        if (this.FindControl<Grid>("FooterGrid") is { } footer
            && this.FindControl<Control>("FooterStatus") is { } status
            && this.FindControl<Control>("FooterStats") is { } stats
            && this.FindControl<Control>("FooterActions") is { } actions)
        {
            if (width == WorkspaceWidth.Narrow)
            {
                footer.ColumnDefinitions = new ColumnDefinitions("*,Auto");
                footer.RowDefinitions = new RowDefinitions("Auto,Auto");
                Place(status, 0, 0);
                Place(actions, 0, 1);
                Place(stats, 1, 0, columnSpan: 2);
            }
            else
            {
                footer.ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto");
                footer.RowDefinitions = new RowDefinitions("Auto");
                Place(status, 0, 0);
                Place(stats, 0, 1);
                Place(actions, 0, 2);
            }
        }

        if (this.FindControl<Grid>("WelcomeColumns") is { } welcome
            && this.FindControl<Control>("WelcomeInfoColumn") is { } info
            && this.FindControl<Control>("WelcomeFormColumn") is { } form)
        {
            if (width == WorkspaceWidth.Wide)
            {
                welcome.ColumnDefinitions = new ColumnDefinitions("340,*");
                welcome.RowDefinitions = new RowDefinitions("Auto");
                welcome.MinWidth = WorkspaceBreakpoints.WelcomeTwoColumnMinWidth;
                Place(info, 0, 0);
                Place(form, 0, 1);
            }
            else
            {
                // The form first: it is what the user has to fill in, and the
                // explanation is still there below it.
                welcome.ColumnDefinitions = new ColumnDefinitions("*");
                welcome.RowDefinitions = new RowDefinitions("Auto,Auto");
                welcome.MinWidth = 0;
                Place(form, 0, 0);
                Place(info, 1, 0);
            }
        }

        static void Place(Control control, int row, int column, int columnSpan = 1)
        {
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            Grid.SetColumnSpan(control, columnSpan);
        }
    }
}
