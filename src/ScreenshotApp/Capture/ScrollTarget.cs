using System.Drawing;
using System.Runtime.InteropServices;
using ScreenshotApp.Services;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenshotApp.Capture;

/// <summary>How a scroll area is moved, in the order they are tried.</summary>
internal enum ScrollMethod
{
    /// <summary>UI Automation ScrollPattern: no input injected.</summary>
    Automation,
    /// <summary>WM_VSCROLL to a classic Win32 control with a scrollbar.</summary>
    ScrollMessage,
    /// <summary>Injected mouse wheel with the cursor over the area (last resort; stops when the user moves the mouse).</summary>
    Wheel,
}

/// <summary>
/// A scrollable area picked for a scrolling capture: the top-level window, the area in physical screen pixels, and
/// what can scroll it (a UI Automation element with a ScrollPattern and/or a child window with a vertical
/// scrollbar). Holds a COM reference to the element until disposed.
/// <para>All UI Automation calls are blocking cross-process calls and must not run on the UI thread (and never
/// against this process's own windows); <see cref="ScrollAutomation"/> runs them on thread-pool threads.</para>
/// </summary>
internal sealed unsafe class ScrollTarget : IDisposable
{
    private nint _element; // IUIAutomationElement*, or 0

    public HWND Window { get; }
    public Rectangle Area { get; }
    public HWND ScrollWindow { get; }

    /// <summary>True when UI Automation found the scrollable element (the area is exact, not just the window).</summary>
    public bool HasElement => _element != 0;

    internal ScrollTarget(HWND window, Rectangle area, IUIAutomationElement* element, HWND scrollWindow)
    {
        Window = window;
        Area = area;
        _element = (nint)element;
        ScrollWindow = scrollWindow;
    }

    internal IUIAutomationElement* Element => (IUIAutomationElement*)_element;

    public void Dispose()
    {
        var e = Interlocked.Exchange(ref _element, 0);
        if (e != 0) ((IUIAutomationElement*)e)->Release();
    }
}

/// <summary>Finds the scrollable area under a point and scrolls it.</summary>
internal static unsafe class ScrollAutomation
{
    private const int MinArea = 40;              // physical pixels; smaller scrollables are ignored
    private const double NoScroll = -1;          // UIA_ScrollPatternNoScroll
    private static nint s_automation;            // IUIAutomation*, created once (free-threaded)
    private static readonly object s_lock = new();

    /// <summary>
    /// The innermost vertically scrollable element of <paramref name="window"/> under <paramref name="point"/>
    /// (screen pixels). Walks the window's UI Automation tree by bounding rectangles, so it works while the
    /// capture overlay covers the screen. Falls back to a child window with a vertical scrollbar, then to the
    /// window's client area. Blocking: call on a background thread.
    /// </summary>
    public static ScrollTarget Find(HWND window, Point point)
    {
        var frame = WindowEnumerator.GetFrameBounds(window) ?? Rectangle.Empty;
        var scrollWindow = FindScrollWindow(window, point);

        IUIAutomationElement* element = null;
        Rectangle area = Rectangle.Empty, document = Rectangle.Empty;
        try
        {
            element = FindElement(window, point, out area, out document);
        }
        catch (Exception ex)
        {
            Log.Info("Scroll capture: UI Automation lookup failed: " + ex.Message);
        }
        area = Rectangle.Intersect(area, frame);
        if (element is not null && (area.Width < MinArea || area.Height < MinArea))
        {
            element->Release();
            element = null;
        }

        if (element is null)
        {
            // No ScrollPattern (e.g. Firefox): the page's Document element still gives the right area (without the
            // browser's toolbars); it is then scrolled with WM_VSCROLL or the wheel.
            document = Rectangle.Intersect(document, frame);
            area = document.Width >= MinArea && document.Height >= MinArea ? document
                : scrollWindow != HWND.Null ? ClientBounds(scrollWindow) : ClientBounds(window);
            area = Rectangle.Intersect(area, frame);
            if (area.Width < MinArea || area.Height < MinArea) area = frame;
        }
        return new ScrollTarget(window, AvoidRoundedCorners(window, area, frame), element, scrollWindow);
    }

    /// <summary>
    /// Windows 11 rounds window corners, leaving transparent pixels there; an area reaching the window's top or
    /// bottom edge would repeat them in every stitched step. Moves such an edge in by the corner radius.
    /// </summary>
    private static Rectangle AvoidRoundedCorners(HWND window, Rectangle area, Rectangle frame)
    {
        int radius = (int)Math.Ceiling(8 * PInvoke.GetDpiForWindow(window) / 96.0);
        // Only where the area also reaches into a corner horizontally.
        if (area.Left >= frame.Left + radius && area.Right <= frame.Right - radius) return area;
        int top = area.Top, bottom = area.Bottom;
        if (bottom > frame.Bottom - radius) bottom = frame.Bottom - radius;
        if (top < frame.Top + radius) top = frame.Top + radius;
        return bottom - top >= MinArea ? Rectangle.FromLTRB(area.Left, top, area.Right, bottom) : area;
    }

    private static IUIAutomation* Automation()
    {
        PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED); // S_FALSE when already; thread-pool threads are MTA
        lock (s_lock)
        {
            if (s_automation == 0)
            {
                PInvoke.CoCreateInstance(typeof(CUIAutomation).GUID, null, CLSCTX.CLSCTX_INPROC_SERVER, out IUIAutomation* a)
                    .ThrowOnFailure();
                s_automation = (nint)a;
            }
            return (IUIAutomation*)s_automation;
        }
    }

    /// <param name="documentRect">The outermost Document element under the point (a web page), or empty.</param>
    private static IUIAutomationElement* FindElement(HWND window, Point point, out Rectangle bestRect, out Rectangle documentRect)
    {
        bestRect = documentRect = Rectangle.Empty;
        var automation = Automation();
        var root = automation->ElementFromHandle(window);
        if (root is null) return null;
        var condition = automation->CreateTrueCondition();
        var cache = automation->CreateCacheRequest();
        IUIAutomationElement* best = null;
        var current = root;
        try
        {
            cache->AddProperty(UIA_PROPERTY_ID.UIA_BoundingRectanglePropertyId);
            cache->AddProperty(UIA_PROPERTY_ID.UIA_IsOffscreenPropertyId);
            for (int depth = 0; depth < 48 && current is not null; depth++)
            {
                if (IsVerticallyScrollable(current))
                {
                    if (best is not null) best->Release();
                    current->AddRef();
                    best = current;
                    bestRect = ToRectangle(current->CurrentBoundingRectangle);
                }
                if (documentRect.IsEmpty && IsDocument(current))
                    documentRect = ToRectangle(current->CurrentBoundingRectangle);

                // Step into the smallest child that contains the point.
                IUIAutomationElement* next = null;
                long nextArea = long.MaxValue;
                var children = current->FindAllBuildCache(TreeScope.TreeScope_Children, condition, cache);
                if (children is not null)
                {
                    try
                    {
                        int n = Math.Min(children->Length, 2000);
                        for (int i = 0; i < n; i++)
                        {
                            var child = children->GetElement(i);
                            if (child is null) continue;
                            var r = ToRectangle(child->CachedBoundingRectangle);
                            long size = (long)r.Width * r.Height;
                            // Hidden popups (Firefox lists its closed menus with bounds over the page) are offscreen.
                            if (r.Contains(point) && size > 0 && size < nextArea && !child->CachedIsOffscreen)
                            {
                                if (next is not null) next->Release();
                                next = child;
                                nextArea = size;
                            }
                            else child->Release();
                        }
                    }
                    finally { children->Release(); }
                }
                current->Release();
                current = next;
            }
        }
        finally
        {
            if (current is not null) current->Release();
            cache->Release();
            condition->Release();
        }
        return best;
    }

    private static bool IsVerticallyScrollable(IUIAutomationElement* element)
    {
        try
        {
            var pattern = (IUIAutomationScrollPattern*)element->GetCurrentPatternAs(UIA_PATTERN_ID.UIA_ScrollPatternId, IUIAutomationScrollPattern.IID_Guid);
            if (pattern is null) return false;
            try { return pattern->CurrentVerticallyScrollable; }
            finally { pattern->Release(); }
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static bool IsDocument(IUIAutomationElement* element)
    {
        try { return element->CurrentControlType == UIA_CONTROLTYPE_ID.UIA_DocumentControlTypeId; }
        catch (COMException) { return false; }
    }

    private static Rectangle ToRectangle(RECT r) => Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);

    /// <summary>The deepest child window under the point that has a vertical scrollbar, or null.</summary>
    private static HWND FindScrollWindow(HWND window, Point screen)
    {
        var deepest = window;
        for (int i = 0; i < 32; i++)
        {
            var p = screen;
            PInvoke.ScreenToClient(deepest, ref p);
            var child = PInvoke.ChildWindowFromPointEx(deepest, p,
                CWP_FLAGS.CWP_SKIPINVISIBLE | CWP_FLAGS.CWP_SKIPDISABLED | CWP_FLAGS.CWP_SKIPTRANSPARENT);
            if (child == HWND.Null || child == deepest) break;
            deepest = child;
        }
        for (var h = deepest; h != HWND.Null && h != window; h = PInvoke.GetParent(h))
        {
            uint style = (uint)PInvoke.GetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
            if ((style & (uint)WINDOW_STYLE.WS_VSCROLL) != 0) return h;
        }
        return HWND.Null;
    }

    private static Rectangle ClientBounds(HWND hwnd)
    {
        if (!PInvoke.GetClientRect(hwnd, out var rc)) return Rectangle.Empty;
        var origin = new Point(0, 0);
        PInvoke.ClientToScreen(hwnd, ref origin);
        return new Rectangle(origin.X, origin.Y, rc.right - rc.left, rc.bottom - rc.top);
    }

    // ---------------------------------------------------------------- scrolling

    /// <summary>
    /// Scrolls the target down once. <paramref name="amount"/> is a fraction of the visible height for
    /// <see cref="ScrollMethod.Automation"/>, and a count of lines / wheel notches otherwise. False when the method
    /// is unavailable, failed, or there is nothing left to scroll. Blocking: call on a background thread.
    /// </summary>
    /// <param name="up">Scroll back up by the same amount (undoing a step that went too far).</param>
    public static bool Step(ScrollTarget target, ScrollMethod method, double amount, bool up = false)
    {
        try
        {
            return method switch
            {
                ScrollMethod.Automation => StepAutomation(target, amount, up),
                ScrollMethod.ScrollMessage => StepMessage(target, amount, up),
                _ => StepWheel(target, amount, up),
            };
        }
        catch (Exception ex)
        {
            Log.Info($"Scroll capture: {method} step failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>What a step of <paramref name="amount"/> really sends: lines and notches are whole numbers.</summary>
    public static double Units(ScrollMethod method, double amount) =>
        method == ScrollMethod.Automation ? amount : Math.Max(1, Math.Round(amount));

    private static bool StepAutomation(ScrollTarget target, double fraction, bool up)
    {
        if (!target.HasElement) return false;
        Automation();
        var pattern = (IUIAutomationScrollPattern*)target.Element->GetCurrentPatternAs(UIA_PATTERN_ID.UIA_ScrollPatternId, IUIAutomationScrollPattern.IID_Guid);
        if (pattern is null) return false;
        try
        {
            double view = pattern->CurrentVerticalViewSize, percent = pattern->CurrentVerticalScrollPercent;
            if (percent < 0 || view <= 0 || view >= 100) return false;
            if (!up && percent >= 99.999) return false; // at the bottom
            // Percent covers the scroll range (total - view), view size is a percentage of the total.
            double delta = fraction * 100 * view / (100 - view);
            double next = Math.Clamp(up ? percent - delta : percent + delta, 0, 100);
            try
            {
                pattern->SetScrollPercent(NoScroll, next);
            }
            catch (COMException)
            {
                int lines = Math.Max(1, (int)Math.Round(fraction * 10));
                for (int i = 0; i < lines; i++)
                    pattern->Scroll(ScrollAmount.ScrollAmount_NoAmount,
                        up ? ScrollAmount.ScrollAmount_SmallDecrement : ScrollAmount.ScrollAmount_SmallIncrement);
            }
            return true;
        }
        finally
        {
            pattern->Release();
        }
    }

    private static bool StepMessage(ScrollTarget target, double lines, bool up)
    {
        if (target.ScrollWindow == HWND.Null) return false;
        int n = Math.Max(1, (int)Math.Round(lines));
        var command = up ? SCROLLBAR_COMMAND.SB_LINEUP : SCROLLBAR_COMMAND.SB_LINEDOWN;
        for (int i = 0; i < n; i++)
            if (!PInvoke.PostMessage(target.ScrollWindow, PInvoke.WM_VSCROLL, (nuint)command, 0)) return false;
        return true;
    }

    /// <summary>Where the wheel method last put the cursor; the user moving it away stops the capture.</summary>
    public static Point? WheelCursor { get; private set; }

    private static bool StepWheel(ScrollTarget target, double notches, bool up)
    {
        var a = target.Area;
        var centre = new Point(a.X + a.Width / 2, a.Y + a.Height / 2);
        // The wheel goes to whatever window is at the cursor: only scroll when that is the target.
        var hit = PInvoke.WindowFromPoint(centre);
        if (hit == HWND.Null || PInvoke.GetAncestor(hit, GET_ANCESTOR_FLAGS.GA_ROOT) != target.Window)
        {
            Log.Info("Scroll capture: the area is covered by another window; the wheel cannot be used.");
            return false;
        }
        PInvoke.SetCursorPos(centre.X, centre.Y);
        WheelCursor = centre;
        // One event per notch: some apps clamp or mishandle a single large delta.
        int n = Math.Max(1, (int)Math.Round(notches));
        var inputs = new INPUT[n];
        for (int i = 0; i < n; i++)
        {
            inputs[i].type = INPUT_TYPE.INPUT_MOUSE;
            inputs[i].mi.dwFlags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_WHEEL;
            inputs[i].mi.mouseData = unchecked((uint)(up ? 120 : -120));
        }
        return PInvoke.SendInput(inputs, sizeof(INPUT)) == n;
    }

    public static void ResetWheel() => WheelCursor = null;
}
