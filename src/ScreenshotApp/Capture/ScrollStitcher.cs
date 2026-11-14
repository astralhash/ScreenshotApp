using System.Runtime.InteropServices;

namespace ScreenshotApp.Capture;

internal enum StitchResult
{
    /// <summary>The frame moved and its new rows were added.</summary>
    Added,
    /// <summary>The frame is the same as the previous one (nothing scrolled).</summary>
    NoChange,
    /// <summary>No reliable offset was found; the frame was not used.</summary>
    NoMatch,
    /// <summary>The height limit was reached; the image was cut there.</summary>
    Full,
}

/// <summary>
/// Joins the frames of a scrolling area into one tall image (vertical scrolling only). UI-independent: it only
/// sees top-down BGRA frames of a fixed size.
/// <para>
/// Each row is split into <see cref="Segments"/> column cells and every cell is hashed. Rows identical at the top
/// and bottom of two frames are a fixed header/footer; in the band between them the shift where most non-plain
/// cells match is the scroll distance. Matching cells rather than whole rows lets parts that move differently
/// (a sticky sidebar, a floating button) be outvoted instead of breaking every row. A bar fixed over the bottom
/// of the content (cells that stay put in columns that otherwise scroll) is treated as part of the footer.
/// Only rows that are new at the bottom of the band are added, so a sticky header appears once (from the first
/// frame) and a footer once (from the last).
/// </para>
/// </summary>
internal sealed class ScrollStitcher
{
    private const int Segments = 16;
    private const double MinMatchRatio = 0.5;    // share of the overlapping non-plain cells that must match
    private const double FollowRatio = 0.6;      // a column segment "scrolls with the content" above this share
    private const int MinMatchCells = 24;
    private const int MinStickyWidth = 8, StickyMargin = 64, StickyMergeGap = 40, HighlightBand = 240; // pixels (a 3-line entry at 200% is ~120)

    private readonly int _width, _height, _stride, _maxHeight, _hashWidth;
    private readonly int[] _segStart = new int[Segments + 1]; // byte offsets of the segments within a row
    private byte[] _body;   // the image so far; always ends with the last frame's bottom row
    private int _bodyRows;
    private Hashes _prev;
    private byte[] _prevFrame = [];
    private int _prevBottom; // footer rows the image ends with
    private int _stripCompared, _stripDiffered; // right-edge strip rows checked / not scrolling with the content
    private int _rawDy, _rawMatches, _rawCandidates; // best shift ignoring the thresholds (diagnostics)

    private sealed record Hashes(int[] Cells, bool[] Plain, int[] Strip);

    /// <summary>The scroll distance of the last added frame (a hint for the next one).</summary>
    public int LastOffset { get; private set; }

    /// <summary>How the last frame was matched (for the log).</summary>
    public string LastInfo { get; private set; } = "";

    /// <summary>Height of the image so far.</summary>
    public int Height => _bodyRows;

    /// <summary>
    /// True when the right-edge strip did not scroll along with the content (a scrollbar thumb or an overlay scroll
    /// indicator): <see cref="Build"/> then leaves it out, since stitched it would show thumb fragments.
    /// </summary>
    public bool StripIsScrollbar => _hashWidth < _width && _stripDiffered > Math.Max(2, _stripCompared / 100);

    /// <param name="ignoreRight">Columns at the right edge left out of the comparison (a scrollbar whose thumb
    /// moves between frames). They are cut from the image when they turn out not to scroll with the content.</param>
    public ScrollStitcher(int width, int height, int maxHeight, int ignoreRight = 0)
    {
        _width = width;
        _height = height;
        _stride = width * 4;
        _maxHeight = Math.Max(height, maxHeight);
        _hashWidth = Math.Max(1, width - Math.Clamp(ignoreRight, 0, width / 4));
        for (int s = 0; s <= Segments; s++) _segStart[s] = (int)((long)_hashWidth * s / Segments) * 4;
        _body = new byte[_stride * Math.Min(_maxHeight, height * 4)];
        _prev = new Hashes([], [], []);
    }

    /// <summary>Adds the next frame (width × height BGRA, top-down).</summary>
    public StitchResult Add(byte[] frame)
    {
        if (frame.Length < _stride * _height) throw new ArgumentException("Frame has the wrong size.", nameof(frame));
        var cur = HashFrame(frame);

        if (_bodyRows == 0)
        {
            Append(frame, 0, _height);
            _prev = cur;
            _prevFrame = frame;
            return StitchResult.Added;
        }

        int h = _height;
        int top = 0;
        while (top < h && RowEqual(cur, _prev, top, top)) top++;
        if (top == h) return StitchResult.NoChange;
        int bottom = 0;
        while (bottom < h && RowEqual(cur, _prev, h - 1 - bottom, h - 1 - bottom)) bottom++;
        // Rows that only happen to be equal (blank space) can make these look bigger than they are; cap them so a
        // band to compare remains.
        top = Math.Min(top, h / 3);
        bottom = Math.Min(bottom, h / 3);

        int dy = FindOffset(cur, top, h - bottom);
        if (dy <= 0)
        {
            // Blank stretches at the edges of the view look fixed too and may have left too little band for a
            // big step: give the blank rows at the bands' inner edges back to the compared band and retry.
            while (top > 0 && RowPlain(cur, top - 1)) top--;
            while (bottom > 0 && RowPlain(cur, h - bottom)) bottom--;
            dy = FindOffset(cur, top, h - bottom);
        }
        LastInfo = $"top {top}, bottom {bottom}, dy {dy} ({_bestInfo}); most matches at shift {_rawDy}: {_rawMatches}/{_rawCandidates} cells";
        if (dy <= 0) return StitchResult.NoMatch;

        // A bar fixed over the bottom of the content: extend the footer over it (never smaller than before; a
        // footer that is too big is harmless, the rows under it come in with the next step).
        var follows = Following(cur, dy, top, h - bottom);
        var slow = SlowShifts(cur, follows, dy, top, h - bottom);
        int fixedBottom = FixedBottomRows(cur, follows, Math.Min(h / 3, h - top - dy));
        if (fixedBottom > bottom)
        {
            LastInfo += $", fixed bar: footer {fixedBottom}";
            bottom = fixedBottom;
        }

        // Does the right-edge strip move with the content?
        for (int y = top; y < h - bottom - dy; y++)
        {
            _stripCompared++;
            if (cur.Strip[y] != _prev.Strip[y + dy]) _stripDiffered++;
        }

        var sticky = StickyRuns(frame, _prevFrame, top, h - bottom, dy, slow);

        // Drop the previous frame's footer from the end, then add the new rows and this frame's footer.
        _bodyRows -= bottom;
        // Rows of the previous footer that stay (this footer is smaller) are content now: fill them too.
        int leftover = Math.Clamp(_prevBottom - bottom, 0, _bodyRows);
        int leftoverSrcRow = h - _prevBottom; // those rows' place in the previous frame
        _prevBottom = bottom;
        int from = h - bottom - dy;
        int count = dy + bottom;
        bool full = false;
        if (_bodyRows + count > _maxHeight)
        {
            count = _maxHeight - _bodyRows;
            full = true;
        }
        int start = _bodyRows;
        Append(frame, from, count);
        if (sticky.Count > 0)
        {
            // The new rows (not the footer), and rows of a larger previous footer that stay and are content now.
            int rows = Math.Min(count, dy);
            var fills = sticky.Select(r => FillColour(frame, r, from, rows)).ToArray();
            FillSticky(sticky, fills, frame, _prevFrame, from, rows, start, slow, +1);
            FillSticky(sticky, fills, _prevFrame, frame, leftoverSrcRow, leftover, start - leftover, slow, -1);
            LastInfo += ", sticky columns " + string.Join(" ", sticky.Select(r => $"{r.X0}-{r.X1}"));
        }
        _prev = cur;
        _prevFrame = frame;
        LastOffset = dy;
        return full ? StitchResult.Full : StitchResult.Added;
    }

    /// <summary>
    /// Shifts smaller than <paramref name="dy"/> by which column segments that do not follow the page moved: a
    /// sticky panel taller than the view keeps scrolling, slower than the page, until its bottom reaches the
    /// view's bottom. Each needs >= 80% of the segment's changed cells (at least 8) to match.
    /// </summary>
    private List<int> SlowShifts(Hashes cur, bool[] follows, int dy, int top, int end)
    {
        var shifts = new List<int>();
        for (int s = 0; s < Segments; s++)
        {
            if (follows[s]) continue;
            int best = 0;
            double bestRatio = 0;
            for (int d = 1; d < dy && d < end - top - 8; d++)
            {
                int n = 0, m = 0;
                for (int y = top; y < end - d; y++)
                {
                    int i = y * Segments + s;
                    if (cur.Plain[i] || cur.Cells[i] == _prev.Cells[i]) continue;
                    n++;
                    if (cur.Cells[i] == _prev.Cells[i + d * Segments]) m++;
                }
                if (n >= 8 && m >= n * 0.8 && (double)m / n > bestRatio) (best, bestRatio) = (d, (double)m / n);
            }
            if (best > 0 && !shifts.Contains(best)) shifts.Add(best);
        }
        return shifts;
    }

    /// <summary>
    /// Column runs that do not scroll with the page: a sticky sidebar or table of contents, standing still or (when
    /// taller than the view) moving slower than the page. Stitched as they are they would repeat once per step, so
    /// <see cref="FillSticky"/> paints the new rows there with the background beside them; the column then shows
    /// once, where it first appeared.
    /// <para>A pixel column is sticky when it has at least 4 edges (rows differing from the one above), its edges
    /// do not line up with the page's shift <paramref name="dy"/> (that rule keeps a repetitive list scrolled by a
    /// multiple of its row height out), and either >= 85% of them stayed in place with at most 15% of rows changed
    /// (a highlight moving through a table of contents is allowed), or >= 85% of them line up with one of the
    /// <paramref name="slowShifts"/>. Runs of at least <see cref="MinStickyWidth"/> px are kept, widened over
    /// unchanged neighbours (the panel's padding and margin) by up to <see cref="StickyMargin"/> px, and merged
    /// across gaps up to <see cref="StickyMergeGap"/> px.</para>
    /// </summary>
    private List<(int X0, int X1)> StickyRuns(byte[] frame, byte[] prevFrame, int top, int end, int dy, List<int> slowShifts)
    {
        var runs = new List<(int, int)>();
        if (end - top < 32 || prevFrame.Length == 0) return runs;
        var cur = MemoryMarshal.Cast<byte, uint>(frame.AsSpan());
        var prev = MemoryMarshal.Cast<byte, uint>(prevFrame.AsSpan());
        int band = end - top;
        var core = new bool[_hashWidth];
        var unchanged = new bool[_hashWidth];
        for (int x = 0; x < _hashWidth; x++)
        {
            // Edges = rows where the pixel differs from the one above. A sticky column keeps its edges in place
            // (its text does not move); sparse scrolling content moves them. A repetitive list scrolled by a
            // multiple of its row height also keeps them in place, but there they also line up with the shift.
            int changed = 0, edges = 0, edgesInPlace = 0, edgesCheckable = 0, edgesShifted = 0;
            for (int y = top; y < end; y++)
            {
                int i = y * _width + x;
                uint c = cur[i];
                bool same = c == prev[i];
                if (!same) changed++;
                if (y == top || c == cur[i - _width]) continue;
                edges++;
                if (same && prev[i - _width] == cur[i - _width]) edgesInPlace++;
                if (y + dy < end)
                {
                    edgesCheckable++;
                    int j = i + dy * _width;
                    if (c == prev[j] && cur[i - _width] == prev[j - _width]) edgesShifted++;
                }
            }
            unchanged[x] = changed == 0;
            bool followsPage = edgesShifted * 2 >= Math.Max(1, edgesCheckable);
            bool standsStill = changed <= band * 15 / 100 && edgesInPlace * 100 >= edges * 85;
            core[x] = edges >= 4 && !followsPage && (standsStill || MovesBy(cur, prev, x, top, end, slowShifts));
        }
        for (int x = 0; x < _hashWidth;)
        {
            if (!core[x]) { x++; continue; }
            int coreStart = x;
            while (x < _hashWidth && core[x]) x++;
            int coreEnd = x;
            if (coreEnd - coreStart < MinStickyWidth) continue;
            int x0 = coreStart, x1 = coreEnd;
            int leftLimit = runs.Count > 0 ? runs[^1].Item2 : 0;
            while (x0 > leftLimit && coreStart - x0 < StickyMargin && unchanged[x0 - 1]) x0--;
            while (x1 < _hashWidth && x1 - coreEnd < StickyMargin && unchanged[x1] && !core[x1]) x1++;
            // Pieces of one panel with a small gap between them (a gap with few edges, or the rail a highlight
            // moves along) are one run: copying the gap would leave fragments.
            if (runs.Count > 0 && x0 - runs[^1].Item2 <= StickyMergeGap) runs[^1] = (runs[^1].Item1, x1);
            else runs.Add((x0, x1));
            x = x1;
        }
        return runs;
    }

    /// <summary>True when >= 85% of column x's checkable edges line up with one of the shifts (at least 4).</summary>
    private bool MovesBy(ReadOnlySpan<uint> cur, ReadOnlySpan<uint> prev, int x, int top, int end, List<int> shifts)
    {
        foreach (int d in shifts)
        {
            int checkable = 0, matched = 0;
            for (int y = top + 1; y < end - d; y++)
            {
                int i = y * _width + x;
                if (cur[i] == cur[i - _width]) continue;
                checkable++;
                int j = i + d * _width;
                if (cur[i] == prev[j] && cur[i - _width] == prev[j - _width]) matched++;
            }
            if (checkable >= 4 && matched * 100 >= checkable * 85) return true;
        }
        return false;
    }

    /// <summary>
    /// Paints image rows [bodyStart, bodyEnd), within each sticky run, with the colour most common in the run's
    /// outermost columns over <paramref name="rows"/> new rows of <paramref name="frame"/> (from
    /// <paramref name="frameRow"/>): the background beside the sticky panel.
    /// </summary>
    private uint FillColour(byte[] frame, (int X0, int X1) run, int frameRow, int rows)
    {
        var src = MemoryMarshal.Cast<byte, uint>(frame.AsSpan());
        var counts = new Dictionary<uint, int>();
        for (int y = frameRow; y < frameRow + rows; y++)
            foreach (int x in (int[])[run.X0, run.X1 - 1])
                counts[src[y * _width + x]] = counts.GetValueOrDefault(src[y * _width + x]) + 1;
        return counts.Count > 0 ? counts.MaxBy(kv => kv.Value).Key : 0xFF000000;
    }

    /// <summary>
    /// For image rows [bodyStart, bodyStart + rows), which came from rows [srcRow, …) of <paramref name="src"/>:
    /// paints a sticky run's part of a row with its fill colour when that part shows the sticky panel, i.e. when
    /// at least 70% of its visible pixels (those not of the fill colour) are found in <paramref name="other"/> at
    /// the same place or moved by one of the slow shifts (<paramref name="direction"/> +1: other is the previous
    /// frame, the panel came from below; -1: other is the next frame). Rows where new content scrolled into the
    /// column (the panel ended above them) are left as they are. A short band of changed rows between panel rows
    /// (the active entry of a table of contents changes colour) counts as panel.
    /// </summary>
    private void FillSticky(List<(int X0, int X1)> runs, uint[] fills, byte[] src, byte[] other, int srcRow, int rows,
        int bodyStart, List<int> slowShifts, int direction)
    {
        if (rows <= 0 || other.Length == 0) return;
        var s = MemoryMarshal.Cast<byte, uint>(src.AsSpan());
        var o = MemoryMarshal.Cast<byte, uint>(other.AsSpan());
        var body = MemoryMarshal.Cast<byte, uint>(_body.AsSpan());
        Span<int> candidates = stackalloc int[1 + Math.Min(slowShifts.Count, 8)];
        for (int r = 0; r < runs.Count; r++)
        {
            var (x0, x1) = runs[r];
            uint fill = fills[r];
            // Per row: 0 = blank (fill colour only), 1 = panel, 2 = content. Rows up to a highlight band before and
            // after the new ones are classified too, to see what surrounds a band at either end; only the new rows
            // (offset `lead` in kind[]) are painted.
            int lead = Math.Min(HighlightBand + 1, srcRow);
            int classified = lead + Math.Min(rows + HighlightBand + 1, _height - srcRow);
            var kind = new byte[classified];
            bool lastPanel = false;
            for (int k = 0; k < classified; k++)
            {
                int y = srcRow - lead + k;
                int n = 0;
                bool unverifiable = false; // a slow shift points outside the other frame
                candidates[n++] = y;
                foreach (int d in slowShifts)
                {
                    int oy = y + direction * d;
                    if (oy >= 0 && oy < _height) { if (n < candidates.Length) candidates[n++] = oy; }
                    else unverifiable = true;
                }
                int ink = 0, panel = 0;
                int row = y * _width;
                for (int x = x0; x < x1; x++)
                {
                    uint c = s[row + x];
                    if (c == fill) continue;
                    ink++;
                    for (int j = 0; j < n; j++)
                        if (o[candidates[j] * _width + x] == c) { panel++; break; }
                }
                // A row too close to the edge to check against a moving panel goes with the row above it (while a
                // panel moves slowly it is taller than the view, so the column there is still panel).
                kind[k] = ink == 0 ? (byte)0 : panel * 10 >= ink * 7 || (unverifiable && lastPanel) ? (byte)1 : (byte)2;
                if (kind[k] != 0) lastPanel = kind[k] == 1;
            }

            // A short band of changed rows with panel rows above and below is part of the panel: the active entry of
            // a table of contents changes colour. Content that scrolls in after a panel ends reaches down to the
            // bottom of the new rows, so it never has panel rows below it.
            for (int k = 0; k < lead + rows;)
            {
                if (kind[k] != 2) { k++; continue; }
                int a = k;
                while (k < classified && kind[k] != 1 && k - a <= HighlightBand) k++;
                int b = k; // first row after the band (a panel row if the band is closed)
                int above = a - 1;
                while (above >= 0 && kind[above] == 0) above--;
                if (b < classified && kind[b] == 1 && above >= 0 && kind[above] == 1 && b - a <= HighlightBand)
                    for (int j = a; j < b; j++) kind[j] = 1;
                else
                    while (k < classified && kind[k] != 1) k++;
            }
            int kept = 0, firstKept = -1;
            for (int k = 0; k < rows; k++)
            {
                if (kind[lead + k] != 2) body.Slice((bodyStart + k) * _width + x0, x1 - x0).Fill(fill);
                else if (kept++ == 0) firstKept = srcRow + k;
            }
            if (kept > 0) LastInfo += $", run {x0}-{x1} keeps {kept} rows from frame row {firstKept} ({(direction > 0 ? "new" : "old footer")})";
        }
    }

    /// <summary>
    /// The shift dy (content moved up by dy rows) in band [top, end) where the largest share of cells matches:
    /// cell (y, s) of this frame equals cell (y + dy, s) of the previous one. Plain cells (one colour) match
    /// anywhere and are not counted. The share, not the count, decides: on pages that repeat the same labels in
    /// every row (tables, shop listings) a short wrong shift can match more cells in total, because it overlaps more
    /// rows, while the true shift matches nearly all of its cells. Ties go to more matches, then to the shift
    /// closest to the last one. 0 when no shift is convincing.
    /// </summary>
    private int FindOffset(Hashes cur, int top, int end)
    {
        int band = end - top;
        int minOverlap = Math.Max(8, band / 10);
        int best = 0, bestMatches = 0;
        double bestRatio = 0;
        _rawDy = _rawMatches = _rawCandidates = 0;
        var cells = cur.Cells;
        var prev = _prev.Cells;
        // Cells that tell nothing about the shift: plain ones, and ones unchanged in place (a sticky column or a
        // fixed bar would otherwise weigh against every shift).
        var skip = new bool[cells.Length];
        for (int i = 0; i < cells.Length; i++) skip[i] = cur.Plain[i] || cells[i] == prev[i];
        for (int dy = 1; dy <= band - minOverlap; dy++)
        {
            int matches = 0, candidates = 0;
            int shift = dy * Segments;
            for (int i = top * Segments, last = (end - dy) * Segments; i < last; i++)
            {
                if (skip[i]) continue;
                candidates++;
                if (cells[i] == prev[i + shift]) matches++;
            }
            if (matches > _rawMatches) (_rawDy, _rawMatches, _rawCandidates) = (dy, matches, candidates);
            if (matches < MinMatchCells || matches < candidates * MinMatchRatio) continue;
            double ratio = (double)matches / candidates;
            bool better = ratio > bestRatio + 1e-9
                || (Math.Abs(ratio - bestRatio) <= 1e-9 && (matches > bestMatches
                    || (matches == bestMatches && Math.Abs(dy - LastOffset) < Math.Abs(best - LastOffset))));
            if (better)
            {
                best = dy;
                bestMatches = matches;
                bestRatio = ratio;
            }
        }
        _bestInfo = best > 0 ? $"{bestMatches} cells, {bestRatio:P0}" : "none";
        return best;
    }

    private string _bestInfo = "";

    /// <summary>Column segments whose cells mostly moved by <paramref name="dy"/> (the scrolling content).</summary>
    private bool[] Following(Hashes cur, int dy, int top, int end)
    {
        var follows = new bool[Segments];
        for (int s = 0; s < Segments; s++)
        {
            int n = 0, m = 0;
            for (int y = top; y < end - dy; y++)
            {
                int i = y * Segments + s;
                if (cur.Plain[i]) continue;
                n++;
                if (cur.Cells[i] == _prev.Cells[i + dy * Segments]) m++;
            }
            follows[s] = n >= 4 && m >= n * FollowRatio;
        }
        return follows;
    }

    /// <summary>
    /// Rows at the bottom that are fixed: identical to the previous frame, or holding a non-plain cell that stayed
    /// put in a column that otherwise scrolls (a floating bar over the content).
    /// </summary>
    private int FixedBottomRows(Hashes cur, bool[] follows, int max)
    {
        const int MaxGap = 32;  // content rows allowed below / inside a floating bar
        const int Margin = 24;  // a bar's plain or anti-aliased top edge does not count as fixed; cover it too
        int h = _height, rows = 0, gap = 0;
        bool bar = false;
        for (int n = 0; n < max && gap <= MaxGap; n++)
        {
            int y = h - 1 - n;
            bool fixedRow = RowEqual(cur, _prev, y, y), barRow = false;
            for (int s = 0; s < Segments && !fixedRow; s++)
            {
                int i = y * Segments + s;
                fixedRow = barRow = follows[s] && !cur.Plain[i] && cur.Cells[i] == _prev.Cells[i];
            }
            if (fixedRow)
            {
                rows = n + 1;
                gap = 0;
                bar |= barRow;
            }
            else gap++;
        }
        return bar ? Math.Min(max, rows + Margin) : rows;
    }

    private static bool RowEqual(Hashes a, Hashes b, int ya, int yb) =>
        a.Cells.AsSpan(ya * Segments, Segments).SequenceEqual(b.Cells.AsSpan(yb * Segments, Segments));

    private static bool RowPlain(Hashes a, int y) => a.Plain.AsSpan(y * Segments, Segments).IndexOf(false) < 0;

    /// <summary>Per cell: hash and whether it is one colour; per row: a hash of the right strip.</summary>
    private Hashes HashFrame(byte[] frame)
    {
        var cells = new int[_height * Segments];
        var plain = new bool[_height * Segments];
        var strip = new int[_height];
        int bytes = _hashWidth * 4;
        for (int y = 0; y < _height; y++)
        {
            int rowStart = y * _stride;
            for (int s = 0; s < Segments; s++)
            {
                var cell = frame.AsSpan(rowStart + _segStart[s], _segStart[s + 1] - _segStart[s]);
                var hc = new HashCode();
                hc.AddBytes(cell);
                hc.Add(s);
                cells[y * Segments + s] = hc.ToHashCode();
                var pixels = MemoryMarshal.Cast<byte, uint>(cell);
                plain[y * Segments + s] = pixels.Length == 0 || pixels.IndexOfAnyExcept(pixels[0]) < 0;
            }
            if (bytes < _stride)
            {
                var hs = new HashCode();
                hs.AddBytes(frame.AsSpan(rowStart + bytes, _stride - bytes));
                strip[y] = hs.ToHashCode();
            }
        }
        return new Hashes(cells, plain, strip);
    }

    private void Append(byte[] frame, int fromRow, int rows)
    {
        if (rows <= 0) return;
        long needed = (long)(_bodyRows + rows) * _stride;
        if (needed > _body.Length)
        {
            long size = Math.Max(needed, Math.Min((long)_body.Length * 2, (long)_maxHeight * _stride));
            Array.Resize(ref _body, (int)Math.Min(size, int.MaxValue));
        }
        Buffer.BlockCopy(frame, fromRow * _stride, _body, _bodyRows * _stride, rows * _stride);
        _bodyRows += rows;
    }

    /// <summary>The stitched image (opaque), placed at the scroll area's screen position; without the right strip
    /// when it is a scrollbar (<see cref="StripIsScrollbar"/>).</summary>
    public CapturedImage Build(int screenX, int screenY)
    {
        int width = StripIsScrollbar ? _hashWidth : _width, rowBytes = width * 4;
        var pixels = new byte[_bodyRows * rowBytes];
        for (int y = 0; y < _bodyRows; y++)
            Buffer.BlockCopy(_body, y * _stride, pixels, y * rowBytes, rowBytes);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 0xFF;
        return new CapturedImage(pixels, width, _bodyRows, screenX, screenY);
    }
}
