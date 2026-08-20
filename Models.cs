using System.Collections.ObjectModel;
using System.IO;

namespace JianRead;

public sealed class FileNode
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public string RootPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public bool IsLibraryRoot { get; init; }
    public string ExtensionLabel { get; init; } = string.Empty;
    public ObservableCollection<FileNode> Children { get; } = [];
}

public sealed class HistoryEntry
{
    public string RootPath { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DateTime LastOpened { get; set; }

    public string Title => Path.GetFileNameWithoutExtension(FilePath);
    public string TimeLabel
    {
        get
        {
            var local = LastOpened.ToLocalTime();
            var today = DateTime.Today;
            if (local.Date == today) return local.ToString("HH:mm");
            if (local.Date == today.AddDays(-1)) return "昨天";
            if (local.Date >= today.AddDays(-6)) return local.ToString("dddd");
            return local.ToString("M月d日");
        }
    }
}

public sealed class HistoryGroup
{
    public string Name { get; init; } = string.Empty;
    public string RootPath { get; init; } = string.Empty;
    public ObservableCollection<HistoryEntry> Items { get; } = [];
}

public sealed class AppState
{
    public List<string> Libraries { get; set; } = [];
    public List<HistoryEntry> History { get; set; } = [];
    public string? LastFile { get; set; }
    public double FontSize { get; set; } = 16;
    public double SidebarWidth { get; set; } = 280;
    public string Theme { get; set; } = "Dark";
}
