using System.Text.Json;
using MixFrame.Models;

namespace MixFrame.Services;

public sealed class PresetStoreService
{
    private sealed class PresetDocument
    {
        public int Version { get; set; } = 1;
        public string? ImageImportDefaultId { get; set; }
        public string? VideoImportDefaultId { get; set; }
        public List<ImageConversionPreset> ImagePresets { get; set; } = [];
        public List<VideoConversionPreset> VideoPresets { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _filePath;
    private PresetDocument _document;

    public static PresetStoreService Instance { get; } = new();

    private PresetStoreService()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _filePath = Path.Combine(localData, "MixFrame", "presets.json");
        _document = Load();
    }

    public IReadOnlyList<ImageConversionPreset> GetImagePresets()
    {
        lock (_gate) return _document.ImagePresets.OrderBy(preset => preset.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public IReadOnlyList<VideoConversionPreset> GetVideoPresets()
    {
        lock (_gate) return _document.VideoPresets.OrderBy(preset => preset.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public string? ImageImportDefaultId { get { lock (_gate) return _document.ImageImportDefaultId; } }
    public string? VideoImportDefaultId { get { lock (_gate) return _document.VideoImportDefaultId; } }

    public bool TryGetImageImportDefault(out ImageConversionPreset? preset)
    {
        lock (_gate)
        {
            preset = _document.ImagePresets.FirstOrDefault(item => item.Id == _document.ImageImportDefaultId);
            return preset is not null;
        }
    }

    public bool TryGetVideoImportDefault(out VideoConversionPreset? preset)
    {
        lock (_gate)
        {
            preset = _document.VideoPresets.FirstOrDefault(item => item.Id == _document.VideoImportDefaultId);
            return preset is not null;
        }
    }

    public ImageConversionPreset AddImagePreset(string name, ImageAsset asset)
    {
        lock (_gate)
        {
            EnsureNameAvailable(name, _document.ImagePresets.Select(item => item.Name));
            var preset = ImageConversionPreset.Capture(name, asset);
            _document.ImagePresets.Add(preset);
            Save();
            return preset;
        }
    }

    public VideoConversionPreset AddVideoPreset(string name, VideoAsset asset)
    {
        lock (_gate)
        {
            EnsureNameAvailable(name, _document.VideoPresets.Select(item => item.Name));
            var preset = VideoConversionPreset.Capture(name, asset);
            _document.VideoPresets.Add(preset);
            Save();
            return preset;
        }
    }

    public ImageConversionPreset UpdateImagePreset(string id, ImageAsset asset)
    {
        lock (_gate)
        {
            var index = _document.ImagePresets.FindIndex(item => item.Id == id);
            if (index < 0) throw new InvalidOperationException("预设已不存在");
            var updated = ImageConversionPreset.Capture(_document.ImagePresets[index].Name, asset, id);
            _document.ImagePresets[index] = updated;
            Save();
            return updated;
        }
    }

    public VideoConversionPreset UpdateVideoPreset(string id, VideoAsset asset)
    {
        lock (_gate)
        {
            var index = _document.VideoPresets.FindIndex(item => item.Id == id);
            if (index < 0) throw new InvalidOperationException("预设已不存在");
            var updated = VideoConversionPreset.Capture(_document.VideoPresets[index].Name, asset, id);
            _document.VideoPresets[index] = updated;
            Save();
            return updated;
        }
    }

    public void RenameImagePreset(string id, string name)
    {
        lock (_gate)
        {
            var index = _document.ImagePresets.FindIndex(item => item.Id == id);
            if (index < 0) throw new InvalidOperationException("预设已不存在");
            EnsureNameAvailable(name, _document.ImagePresets.Where(item => item.Id != id).Select(item => item.Name));
            _document.ImagePresets[index] = _document.ImagePresets[index] with { Name = name.Trim() };
            Save();
        }
    }

    public void RenameVideoPreset(string id, string name)
    {
        lock (_gate)
        {
            var index = _document.VideoPresets.FindIndex(item => item.Id == id);
            if (index < 0) throw new InvalidOperationException("预设已不存在");
            EnsureNameAvailable(name, _document.VideoPresets.Where(item => item.Id != id).Select(item => item.Name));
            _document.VideoPresets[index] = _document.VideoPresets[index] with { Name = name.Trim() };
            Save();
        }
    }

    public void DeleteImagePreset(string id)
    {
        lock (_gate)
        {
            _document.ImagePresets.RemoveAll(item => item.Id == id);
            if (_document.ImageImportDefaultId == id) _document.ImageImportDefaultId = null;
            Save();
        }
    }

    public void DeleteVideoPreset(string id)
    {
        lock (_gate)
        {
            _document.VideoPresets.RemoveAll(item => item.Id == id);
            if (_document.VideoImportDefaultId == id) _document.VideoImportDefaultId = null;
            Save();
        }
    }

    public void SetImageImportDefault(string? id)
    {
        lock (_gate)
        {
            _document.ImageImportDefaultId = id is not null && _document.ImagePresets.Any(item => item.Id == id) ? id : null;
            Save();
        }
    }

    public void SetVideoImportDefault(string? id)
    {
        lock (_gate)
        {
            _document.VideoImportDefaultId = id is not null && _document.VideoPresets.Any(item => item.Id == id) ? id : null;
            Save();
        }
    }

    private PresetDocument Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new PresetDocument();
            return JsonSerializer.Deserialize<PresetDocument>(File.ReadAllText(_filePath), JsonOptions) ?? new PresetDocument();
        }
        catch
        {
            return new PresetDocument();
        }
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_filePath) ?? throw new InvalidOperationException("预设目录无效");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"presets-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_document, JsonOptions));
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch { /* Keep the original persistence error. */ }
        }
    }

    private static void EnsureNameAvailable(string name, IEnumerable<string> existingNames)
    {
        var value = name.Trim();
        if (value.Length is < 1 or > 40) throw new InvalidOperationException("预设名称必须为 1 到 40 个字符");
        if (string.Equals(value, "默认设置", StringComparison.CurrentCultureIgnoreCase))
            throw new InvalidOperationException("“默认设置”是系统保留名称");
        if (existingNames.Any(existing => string.Equals(existing, value, StringComparison.CurrentCultureIgnoreCase)))
            throw new InvalidOperationException("已有同名预设");
    }
}
