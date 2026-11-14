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
/// Each frame is compared with the previous one by row hashes: rows that are identical at the top and bottom of
/// both frames are a fixed header/footer, and in the band between them the shift where most rows match is the
/// scroll distance. Only rows that are new at the bottom of that band are added, so a sticky header appears once
/// (from the first frame) and a sticky footer once (from the last).
/// </para>
/// </summary>
internal sealed class ScrollStitcher
{
    private const double MinMatchRatio = 0.6; // share of the non-plain overlapping rows that must match
    private const int MinMatchRows = 6;

    private readonly int _width, _height, _stride, _maxHeight, _hashWidth;
    private byte[] _body;   // the image so far; always ends with the last frame's bottom row
    private int _bodyRows;
    private int[] _prevHash = [], _prevStrip = [];
    private int _stripCompared, _stripDiffered; // right-edge strip rows checked / not scrolling with the content

    /// <summary>The scroll distance of the last added frame (a hint for the next one).</summary>
    public int LastOffset { get; private set; }

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
        _body = new byte[_stride * Math.Min(_maxHeight, height * 4)];
    }

    /// <summary>Adds the next frame (width × height BGRA, top-down).</summary>
    public StitchResult Add(byte[] frame)
    {
        if (frame.Length < _stride * _height) throw new ArgumentException("Frame has the wrong size.", nameof(frame));
        var (hash, plain, strip) = HashRows(frame);

        if (_bodyRows == 0)
        {
            Append(frame, 0, _height);
            (_prevHash, _prevStrip) = (hash, strip);
            return StitchResult.Added;
        }

        int h = _height;
        int top = 0;
        while (top < h && hash[top] == _prevHash[top]) top++;
        if (top == h) return StitchResult.NoChange;
        int bottom = 0;
        while (bottom < h && hash[h - 1 - bottom] == _prevHash[h - 1 - bottom]) bottom++;
        // Rows that only happen to be equal (blank space) can make these look bigger than they are; cap them so a
        // band to compare remains.
        top = Math.Min(top, h / 3);
        bottom = Math.Min(bottom, h / 3);

        int dy = FindOffset(hash, plain, top, h - bottom);
        if (dy <= 0)
        {
            // Blank stretches at the edges of the view look fixed too and may have left too little band for a
            // big step: give the blank rows at the bands' inner edges back to the compared band and retry.
            while (top > 0 && plain[top - 1]) top--;
            while (bottom > 0 && plain[h - bottom]) bottom--;
            dy = FindOffset(hash, plain, top, h - bottom);
        }
        if (dy <= 0) return StitchResult.NoMatch;

        // Does the right-edge strip move with the content?
        for (int y = top; y < h - bottom - dy; y++)
        {
            _stripCompared++;
            if (strip[y] != _prevStrip[y + dy]) _stripDiffered++;
        }

        // Drop the previous frame's footer from the end, then add the new rows and this frame's footer.
        _bodyRows -= bottom;
        int from = h - bottom - dy;
        int count = dy + bottom;
        bool full = false;
        if (_bodyRows + count > _maxHeight)
        {
            count = _maxHeight - _bodyRows;
            full = true;
        }
        Append(frame, from, count);
        (_prevHash, _prevStrip) = (hash, strip);
        LastOffset = dy;
        return full ? StitchResult.Full : StitchResult.Added;
    }

    /// <summary>
    /// The shift dy (content moved up by dy rows) in band [top, end) that matches most rows: row y of this frame
    /// equals row y + dy of the previous one. Plain rows (one colour) match anywhere and are not counted.
    /// Ties go to the shift closest to the last one. 0 when no shift is convincing.
    /// </summary>
    private int FindOffset(int[] hash, bool[] plain, int top, int end)
    {
        int band = end - top;
        int minOverlap = Math.Max(8, band / 10);
        int best = 0, bestMatches = 0;
        for (int dy = 1; dy <= band - minOverlap; dy++)
        {
            int matches = 0, candidates = 0;
            for (int y = top; y < end - dy; y++)
            {
                if (plain[y]) continue;
                candidates++;
                if (hash[y] == _prevHash[y + dy]) matches++;
            }
            if (matches < MinMatchRows || matches < candidates * MinMatchRatio) continue;
            bool better = matches > bestMatches
                || (matches == bestMatches && Math.Abs(dy - LastOffset) < Math.Abs(best - LastOffset));
            if (better)
            {
                best = dy;
                bestMatches = matches;
            }
        }
        return best;
    }

    /// <summary>Per row: hash of the compared columns, whether they are one colour, and a hash of the right strip.</summary>
    private (int[] Hash, bool[] Plain, int[] Strip) HashRows(byte[] frame)
    {
        var hash = new int[_height];
        var plain = new bool[_height];
        var strip = new int[_height];
        int bytes = _hashWidth * 4;
        for (int y = 0; y < _height; y++)
        {
            var row = frame.AsSpan(y * _stride, bytes);
            var hc = new HashCode();
            hc.AddBytes(row);
            hash[y] = hc.ToHashCode();
            var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(row);
            plain[y] = pixels.IndexOfAnyExcept(pixels[0]) < 0;
            if (bytes < _stride)
            {
                var hs = new HashCode();
                hs.AddBytes(frame.AsSpan(y * _stride + bytes, _stride - bytes));
                strip[y] = hs.ToHashCode();
            }
        }
        return (hash, plain, strip);
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
