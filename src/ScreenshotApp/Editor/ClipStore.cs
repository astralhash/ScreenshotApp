using System.Text.Json;
using Microsoft.UI.Dispatching;
using ScreenshotApp.Capture;
using ScreenshotApp.Editor.Model;
using ScreenshotApp.Services;

namespace ScreenshotApp.Editor;

/// <summary>
/// Keeps the film strip across app restarts: each clip's image as a PNG plus one index.json with its order,
/// save state and annotations, all under %AppData%\ScreenshotApp\clips. Clips that were saved and have no
/// later edits are not worth keeping and are dropped on load.
/// </summary>
internal sealed class ClipStore
{
    private sealed class ClipDto
    {
        public Guid Id { get; set; }
        public string? SavedPath { get; set; }
        public bool Dirty { get; set; }

        /// <summary>Crop as left, top, width, height in image pixels; null when uncropped.</summary>
        public float[]? Crop { get; set; }
        public List<AnnotationDto> Annotations { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenshotApp", "clips");
    private readonly List<Task> _pendingImageWrites = new();
    private readonly DispatcherQueueTimer _saveTimer;
    private Func<IReadOnlyList<EditorDocument>>? _getShots;

    public ClipStore()
    {
        _saveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(700);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => SaveIndex();
    }

    private string IndexPath => Path.Combine(_dir, "index.json");
    private string ImagePath(Guid id) => Path.Combine(_dir, id + ".png");

    /// <summary>Tells the store how to read the current shots (newest first) when it writes the index.</summary>
    public void Attach(Func<IReadOnlyList<EditorDocument>> getShots) => _getShots = getShots;

    /// <summary>Loads the kept clips (newest first). Must run on the UI thread: documents create thumbnails.</summary>
    public async Task<List<EditorDocument>> LoadAsync()
    {
        var result = new List<EditorDocument>();
        try
        {
            if (!File.Exists(IndexPath)) return result;
            var dtos = JsonSerializer.Deserialize<List<ClipDto>>(await File.ReadAllTextAsync(IndexPath)) ?? new();

            foreach (var dto in dtos)
            {
                string png = ImagePath(dto.Id);
                if (!File.Exists(png)) continue;
                if (dto.SavedPath is not null && !dto.Dirty) continue; // already safe on disk

                try
                {
                    // The PNG bytes stay in memory as the clip's compressed copy; the decode is only needed
                    // for the thumbnail, so release the pixels again (except for the shot shown first).
                    var bytes = await File.ReadAllBytesAsync(png);
                    var doc = new EditorDocument(await ImageSaver.DecodeAsync(bytes), dto.Id, bytes)
                    {
                        SavedPath = dto.SavedPath,
                        Dirty = dto.Dirty,
                    };
                    if (dto.Crop is { Length: 4 } c) doc.Crop.Rect = new System.Drawing.RectangleF(c[0], c[1], c[2], c[3]);
                    foreach (var a in dto.Annotations.Select(AnnotationSerializer.FromDto).OfType<Annotation>())
                        doc.Annotations.Add(a);
                    if (result.Count > 0) doc.Unload();
                    result.Add(doc);
                }
                catch (Exception ex)
                {
                    Log.Error(ex); // a corrupt clip must not block the others
                }
            }

            // Delete images that are no longer referenced (saved clips, leftovers).
            var keep = result.Select(d => ImagePath(d.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(_dir, "*.png").Where(f => !keep.Contains(f)))
                TryDelete(file);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error(ex);
        }
        return result;
    }

    /// <summary>Writes a new clip's image in the background and schedules an index update.</summary>
    public void Add(EditorDocument doc)
    {
        string path = ImagePath(doc.Id);
        // Reuses the document's compressed copy (no second encode). Task.Run keeps this off the UI
        // thread, so waiting for it at exit cannot deadlock.
        var write = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(_dir);
                await File.WriteAllBytesAsync(path, await doc.GetPngAsync().ConfigureAwait(false)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        });
        lock (_pendingImageWrites) _pendingImageWrites.Add(write);
        ScheduleSave();
    }

    public void Remove(EditorDocument doc)
    {
        TryDelete(ImagePath(doc.Id));
        ScheduleSave();
    }

    public void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Flushes everything now; call at exit.</summary>
    public void Flush()
    {
        _saveTimer.Stop();
        Task[] pending;
        lock (_pendingImageWrites) pending = _pendingImageWrites.ToArray();
        try { Task.WaitAll(pending, TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        SaveIndex();
    }

    private void SaveIndex()
    {
        if (_getShots is null) return;
        try
        {
            Directory.CreateDirectory(_dir);
            // Everything is written while running; LoadAsync drops clips that are saved and unedited.
            var dtos = _getShots()
                .Select(d => new ClipDto
                {
                    Id = d.Id,
                    SavedPath = d.SavedPath,
                    Dirty = d.Dirty,
                    Crop = d.Crop.Rect is { } c ? new[] { c.X, c.Y, c.Width, c.Height } : null,
                    Annotations = d.Annotations.Select(AnnotationSerializer.ToDto).ToList(),
                })
                .ToList();
            File.WriteAllText(IndexPath, JsonSerializer.Serialize(dtos, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
