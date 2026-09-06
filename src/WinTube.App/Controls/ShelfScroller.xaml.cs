using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace WinTube.App.Controls;

/// Wraps one horizontal shelf row and sorts its scroll input. The row's own ScrollViewer keeps
/// horizontal panning enabled, so touchpad and touch pans — and a horizontal wheel tilt, and
/// Shift+wheel, which the ScrollViewer maps to horizontal natively — all feel native. The one
/// thing taken away from it is the PLAIN vertical wheel: intercepted on the row's content
/// (which sees the event before the row's ScrollViewer does) and steered to the page's
/// ScrollViewer instead, gliding toward an accumulated target so fast ticks stay smooth. The
/// hover chevrons page the row a card-aligned viewport at a time.
[ContentProperty(Name = nameof(Row))]
public sealed partial class ShelfScroller : UserControl
{
    /// Card spacing in the shelf rows (the ItemsPanel's StackPanel Spacing).
    private const double CardSpacing = 12;

    /// Where a page is heading under wheel input — ONE target per page ScrollViewer, shared
    /// by every shelf on it. Per-wrapper targets went stale the moment the pointer crossed
    /// from one row to the next mid-glide, snapping the page backwards a tick.
    private sealed class PageGlide { public double Target; }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScrollViewer, PageGlide>
        glides = [];

    private ScrollViewer? rowScroller;
    private ScrollViewer? pageScroller;
    private PageGlide? pageGlide;
    private bool pointerOver;

    public ShelfScroller()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// The row (a horizontal ListView). Named content property, so the row simply nests
    /// inside the scroller element in page XAML.
    public UIElement? Row
    {
        get => Host.Content as UIElement;
        set => Host.Content = value;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Row is { } row) rowScroller ??= FindDescendantScrollViewer(row);
        if (rowScroller is { } scroller)
        {
            scroller.ViewChanged += (_, _) => UpdateChevrons();
            // The content sits between the cards and the row's ScrollViewer in the event
            // route, so a handler here runs BEFORE the ScrollViewer can grab the wheel.
            if (scroller.Content is UIElement content)
                content.PointerWheelChanged += OnRowContentWheel;
        }
        if (pageScroller is null && FindAncestorScrollViewer(this) is { } page)
        {
            pageScroller = page;
            if (!glides.TryGetValue(page, out pageGlide))
            {
                // First shelf on this page: create the shared target and keep it honest
                // after any scroll the wheel didn't start (drag, keyboard, programmatic).
                var glide = new PageGlide { Target = page.VerticalOffset };
                glides.Add(page, glide);
                pageGlide = glide;
                page.ViewChanged += (_, args) =>
                {
                    if (!args.IsIntermediate) glide.Target = page.VerticalOffset;
                };
            }
        }
    }

    /// A plain vertical wheel belongs to the page — the row must never trap it. Everything
    /// else (horizontal tilt, Shift+wheel, touchpad pans) is left for the row's own
    /// ScrollViewer, which handles them natively.
    private void OnRowContentWheel(object sender, PointerRoutedEventArgs e)
    {
        if (pageScroller is not { } page || pageGlide is not { } glide) return;
        var point = e.GetCurrentPoint(this).Properties;
        if (point.IsHorizontalMouseWheel) return;
        if (e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift)) return;

        glide.Target = Math.Clamp(glide.Target - point.MouseWheelDelta, 0, page.ScrollableHeight);
        page.ChangeView(null, glide.Target, null);
        e.Handled = true;
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        pointerOver = true;
        UpdateChevrons();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        pointerOver = false;
        UpdateChevrons();
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        // Wheel events that missed the row content (edges of the wrapper) get the same
        // treatment, so the page never stalls anywhere over the shelf.
        OnRowContentWheel(sender, e);
    }

    private void OnLeftChevron(object sender, RoutedEventArgs e) => Page(-1);

    private void OnRightChevron(object sender, RoutedEventArgs e) => Page(+1);

    /// One chevron click moves a viewport, snapped to the card grid: the first card that was
    /// cut off (or just out of view) lands exactly where the row's first card sits at rest.
    private void Page(int direction)
    {
        if (rowScroller is not { } scroller) return;
        var pitch = CardPitch();
        var aligned = direction > 0
            ? Math.Floor((scroller.HorizontalOffset + scroller.ViewportWidth) / pitch) * pitch
            : Math.Ceiling((scroller.HorizontalOffset - scroller.ViewportWidth) / pitch) * pitch;
        scroller.ChangeView(Math.Clamp(aligned, 0, scroller.ScrollableWidth), null, null);
    }

    /// The horizontal distance from one card's left edge to the next — measured off the first
    /// realized container so video rows (320) and Shorts rows (150) both align correctly.
    private double CardPitch()
    {
        if (Row is ListView list && list.ContainerFromIndex(0) is FrameworkElement first
            && first.ActualWidth > 0)
            return first.ActualWidth + CardSpacing;
        return 332;
    }

    /// A chevron shows only while the pointer is over the row AND there is somewhere to go
    /// in its direction — mirroring where the hidden scrollbar would have had track left.
    private void UpdateChevrons()
    {
        var offset = rowScroller?.HorizontalOffset ?? 0;
        var scrollable = rowScroller?.ScrollableWidth ?? 0;
        LeftChevron.Visibility = pointerOver && offset > 2
            ? Visibility.Visible : Visibility.Collapsed;
        RightChevron.Visibility = pointerOver && scrollable > 0 && offset < scrollable - 2
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scroller) return scroller;
            if (FindDescendantScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject start)
    {
        for (var parent = VisualTreeHelper.GetParent(start); parent is not null;
             parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ScrollViewer scroller) return scroller;
        }
        return null;
    }
}
