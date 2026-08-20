using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace JianRead;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".txt"
    };

    private static readonly string StateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JianRead");
    private static readonly string StatePath = Path.Combine(StateDirectory, "state.json");

    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly List<HistoryEntry> _history = [];
    private readonly DispatcherTimer _previewTimer;
    private AppState _state = new();
    private FileNode? _selectedNode;
    private string? _currentFilePath;
    private string? _currentRootPath;
    private string _currentContent = string.Empty;
    private bool _currentIsMarkdown;
    private Encoding _currentEncoding = new UTF8Encoding(false);
    private string _currentEncodingName = "UTF-8";
    private bool _currentHasBom;
    private double _fontSize = 16;
    private bool _isDark = true;
    private bool _editMode;
    private bool _editorDirty;
    private bool _suppressEditorTextChanged;
    private bool _loaded;

    public ObservableCollection<FileNode> Libraries { get; } = [];
    public ObservableCollection<HistoryGroup> HistoryGroups { get; } = [];

    public MainWindow()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            UpdateEditorPreview();
        };
        InitializeComponent();
        DataContext = this;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        LoadState();
        _fontSize = Math.Clamp(_state.FontSize, 13, 23);
        _isDark = !string.Equals(_state.Theme, "Light", StringComparison.OrdinalIgnoreCase);
        SidebarColumn.Width = new GridLength(Math.Clamp(_state.SidebarWidth, 220, 520));
        ApplyTheme(refreshDocument: false);
        ReaderScrollViewer.Document = MarkdownRenderer.Welcome(_fontSize);

        foreach (var libraryPath in _state.Libraries.Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
        {
            if (Directory.Exists(libraryPath))
                await AddLibraryAsync(libraryPath, save: false, showEmptyMessage: false);
        }

        _history.AddRange(_state.History
            .Where(entry => File.Exists(entry.FilePath))
            .OrderByDescending(entry => entry.LastOpened)
            .Take(100));
        RebuildHistoryGroups();
        UpdateSidebarState();

        _loaded = true;
        if (!string.IsNullOrWhiteSpace(_state.LastFile) && File.Exists(_state.LastFile))
        {
            var rootPath = ResolveRootPath(_state.LastFile);
            if (rootPath is not null)
                await OpenFileAsync(_state.LastFile, rootPath, addHistory: false);
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_editMode && _editorDirty)
        {
            var result = ShowUnsavedDialog();
            if (result == GlassDialogResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (result == GlassDialogResult.Primary && !SaveEditSynchronously())
            {
                e.Cancel = true;
                return;
            }
        }
        SaveState();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e) => UpdateTitleBarTheme();

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择一个文件夹作为阅读节点",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true) return;
        DirectoryTab.IsChecked = true;
        await AddLibraryAsync(dialog.FolderName, save: true, showEmptyMessage: true);
    }

    private async Task AddLibraryAsync(string path, bool save, bool showEmptyMessage)
    {
        var normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var existing = Libraries.FirstOrDefault(node =>
            string.Equals(node.FullPath, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _selectedNode = existing;
            RemoveNodeButton.IsEnabled = true;
            LibraryStatusText.Text = "这个阅读节点已经在目录中";
            return;
        }

        LibraryStatusText.Text = "正在生成目录…";
        var rootNode = await Task.Run(() => BuildDirectoryNode(normalized, normalized, isRoot: true));
        Libraries.Add(rootNode);
        _selectedNode = rootNode;
        RemoveNodeButton.IsEnabled = true;

        if (CountFiles(rootNode) == 0 && showEmptyMessage)
        {
            GlassDialog.Show(this, "没有可阅读的文件",
                "这个文件夹中暂时没有 .md、.markdown 或 .txt 文件。阅读节点仍已添加，之后可以随时刷新。",
                primaryText: "知道了", cancelText: null, tone: GlassDialogTone.Information, isDark: _isDark);
        }

        UpdateSidebarState();
        if (save) SaveState();
    }

    private static FileNode BuildDirectoryNode(string path, string rootPath, bool isRoot)
    {
        var node = new FileNode
        {
            Name = GetDisplayName(path),
            FullPath = path,
            RootPath = rootPath,
            IsDirectory = true,
            IsLibraryRoot = isRoot
        };

        try
        {
            var directoryInfo = new DirectoryInfo(path);
            foreach (var directory in directoryInfo.EnumerateDirectories()
                         .Where(item => (item.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) == 0)
                         .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var child = BuildDirectoryNode(directory.FullName, rootPath, isRoot: false);
                if (child.Children.Count > 0)
                    node.Children.Add(child);
            }

            foreach (var file in directoryInfo.EnumerateFiles()
                         .Where(item => SupportedExtensions.Contains(item.Extension))
                         .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                node.Children.Add(new FileNode
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    RootPath = rootPath,
                    IsDirectory = false,
                    ExtensionLabel = file.Extension.TrimStart('.').ToUpperInvariant()
                });
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Inaccessible subfolders are omitted from the reading tree.
        }
        catch (IOException)
        {
            // A transient or disconnected path should not stop the other nodes.
        }

        return node;
    }

    private async void LibraryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not FileNode node) return;
        _selectedNode = node;
        RemoveNodeButton.IsEnabled = true;
        RefreshNodeButton.IsEnabled = true;

        if (!node.IsDirectory)
            await OpenFileAsync(node.FullPath, node.RootPath, addHistory: true);
    }

    private void TreeViewItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem item
            || item.DataContext is not FileNode { IsDirectory: true }
            || e.ChangedButton != MouseButton.Left
            || e.OriginalSource is not DependencyObject source)
            return;

        var clickedItem = FindContainingTreeViewItem(source);
        if (!ReferenceEquals(clickedItem, item)) return;

        item.IsSelected = true;
        item.Focus();
        item.IsExpanded = !item.IsExpanded;
        e.Handled = true;
    }

    private static TreeViewItem? FindContainingTreeViewItem(DependencyObject source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is TreeViewItem item) return item;
            current = current is FrameworkContentElement contentElement
                ? contentElement.Parent
                : VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private async void RenameNode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { CommandParameter: FileNode node }) return;
        _selectedNode = node;
        RemoveNodeButton.IsEnabled = true;

        var selectionLength = node.IsDirectory
            ? node.Name.Length
            : Path.GetFileNameWithoutExtension(node.Name).Length;
        var prompt = node.IsDirectory
            ? "输入新的文件夹名称。"
            : "输入新的文件名；不填写扩展名时会保留原扩展名。";
        var rename = GlassDialog.ShowInput(this, node.IsDirectory ? "重命名文件夹" : "重命名文件",
            prompt, node.Name, selectionLength, primaryText: "重命名", cancelText: "取消", isDark: _isDark);
        if (rename.Result != GlassDialogResult.Primary) return;

        var newName = rename.Value.Trim();
        if (!node.IsDirectory
            && !newName.EndsWith(".", StringComparison.Ordinal)
            && string.IsNullOrEmpty(Path.GetExtension(newName)))
        {
            newName += Path.GetExtension(node.Name);
        }

        var validationError = ValidateRename(node, newName);
        if (validationError is not null)
        {
            ShowRenameError(validationError);
            return;
        }

        if (string.Equals(node.Name, newName, StringComparison.OrdinalIgnoreCase)) return;
        var parentPath = Path.GetDirectoryName(node.FullPath);
        if (string.IsNullOrWhiteSpace(parentPath))
        {
            ShowRenameError("这个位置不能重命名。");
            return;
        }

        var destinationPath = Path.Combine(parentPath, newName);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            ShowRenameError("同一位置已经存在同名文件或文件夹。");
            return;
        }

        var affectsCurrentFile = _currentFilePath is not null && IsSameOrInside(_currentFilePath, node.FullPath);
        if (affectsCurrentFile && _editMode && _editorDirty && !await ResolvePendingEditsAsync()) return;

        var libraryPathChanges = Libraries
            .Select(library => (OldPath: library.RootPath,
                NewPath: IsSameOrInside(library.RootPath, node.FullPath)
                    ? ReplacePathPrefix(library.RootPath, node.FullPath, destinationPath)
                    : library.RootPath))
            .ToArray();

        try
        {
            if (node.IsDirectory)
                Directory.Move(node.FullPath, destinationPath);
            else
                File.Move(node.FullPath, destinationPath);

            foreach (var entry in _history)
            {
                if (IsSameOrInside(entry.FilePath, node.FullPath))
                    entry.FilePath = ReplacePathPrefix(entry.FilePath, node.FullPath, destinationPath);
                if (IsSameOrInside(entry.RootPath, node.FullPath))
                    entry.RootPath = ReplacePathPrefix(entry.RootPath, node.FullPath, destinationPath);
            }

            if (_currentFilePath is not null && IsSameOrInside(_currentFilePath, node.FullPath))
                _currentFilePath = ReplacePathPrefix(_currentFilePath, node.FullPath, destinationPath);
            if (_currentRootPath is not null && IsSameOrInside(_currentRootPath, node.FullPath))
                _currentRootPath = ReplacePathPrefix(_currentRootPath, node.FullPath, destinationPath);

            var rebuiltLibraries = await Task.WhenAll(libraryPathChanges.Select(change =>
                Task.Run(() => BuildDirectoryNode(change.NewPath, change.NewPath, isRoot: true))));
            for (var index = 0; index < rebuiltLibraries.Length; index++)
                Libraries[index] = rebuiltLibraries[index];

            _selectedNode = rebuiltLibraries
                .Select(root => FindNode(root, destinationPath))
                .FirstOrDefault(found => found is not null)
                ?? rebuiltLibraries.FirstOrDefault();

            RebuildHistoryGroups();
            UpdateSidebarState();

            if (affectsCurrentFile && _currentFilePath is not null && _currentRootPath is not null)
                await OpenFileAsync(_currentFilePath, _currentRootPath, addHistory: false);
            else
                SaveState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ShowRenameError($"无法完成重命名。\n\n{ex.Message}");
        }
    }

    private static string? ValidateRename(FileNode node, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return "名称不能为空。";
        if (newName is "." or "..") return "不能使用这个名称。";
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "名称中包含 Windows 不允许使用的字符。";
        if (newName.EndsWith(".", StringComparison.Ordinal)) return "名称不能以句点结尾。";
        if (!node.IsDirectory && !SupportedExtensions.Contains(Path.GetExtension(newName)))
            return "文件扩展名仅支持 .md、.markdown 或 .txt。";
        return null;
    }

    private void ShowRenameError(string message) => GlassDialog.Show(this, "无法重命名", message,
        primaryText: "知道了", cancelText: null, tone: GlassDialogTone.Warning, isDark: _isDark);

    private static FileNode? FindNode(FileNode node, string fullPath)
    {
        if (string.Equals(node.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)) return node;
        foreach (var child in node.Children)
        {
            var found = FindNode(child, fullPath);
            if (found is not null) return found;
        }
        return null;
    }

    private static bool IsSameOrInside(string path, string parentPath)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedParent = Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedPath, normalizedParent, StringComparison.OrdinalIgnoreCase)
               || normalizedPath.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReplacePathPrefix(string path, string oldPrefix, string newPrefix)
    {
        var relativePath = Path.GetRelativePath(oldPrefix, path);
        return relativePath == "." ? newPrefix : Path.Combine(newPrefix, relativePath);
    }

    private async Task OpenFileAsync(string filePath, string rootPath, bool addHistory)
    {
        if (_editMode && _editorDirty && !await ResolvePendingEditsAsync())
            return;

        if (!File.Exists(filePath))
        {
            GlassDialog.Show(this, "无法打开", "文件已经移动或删除。",
                primaryText: "知道了", cancelText: null, tone: GlassDialogTone.Warning, isDark: _isDark);
            RemoveHistoryEntry(filePath);
            return;
        }

        try
        {
            if (new FileInfo(filePath).Length > 25 * 1024 * 1024)
            {
                GlassDialog.Show(this, "文件过大",
                    "这个文件超过 25 MB。为保持阅读器流畅，当前版本暂不打开它。",
                    primaryText: "知道了", cancelText: null, tone: GlassDialogTone.Information, isDark: _isDark);
                return;
            }

            var fileData = await ReadTextAsync(filePath);
            var extension = Path.GetExtension(filePath);
            var markdown = extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
                           || extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase);

            SetEditMode(false);
            _currentFilePath = filePath;
            _currentRootPath = rootPath;
            _currentContent = fileData.Content;
            _currentIsMarkdown = markdown;
            _currentEncoding = fileData.Encoding;
            _currentEncodingName = fileData.EncodingName;
            _currentHasBom = fileData.HasBom;
            ReaderScrollViewer.Document = MarkdownRenderer.Render(_currentContent, markdown, _fontSize);

            BreadcrumbRoot.Text = GetDisplayName(rootPath);
            var relativeDirectory = Path.GetDirectoryName(Path.GetRelativePath(rootPath, filePath));
            BreadcrumbFolder.Text = string.IsNullOrWhiteSpace(relativeDirectory) || relativeDirectory == "."
                ? "根目录"
                : relativeDirectory;
            BreadcrumbFile.Text = Path.GetFileName(filePath);
            Title = $"{Path.GetFileNameWithoutExtension(filePath)} — 阿利宙斯阅读";

            EditModeButton.IsEnabled = markdown;
            UpdateDocumentStatus();

            if (addHistory)
                AddHistory(filePath, rootPath);

            SaveState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            GlassDialog.Show(this, "读取失败", $"无法读取这个文件。\n\n{ex.Message}",
                primaryText: "知道了", cancelText: null, tone: GlassDialogTone.Warning, isDark: _isDark);
        }
    }

    private static async Task<TextFileData> ReadTextAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new TextFileData(Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "UTF-8", new UTF8Encoding(true), true);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return new TextFileData(Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 LE", Encoding.Unicode, true);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return new TextFileData(Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 BE", Encoding.BigEndianUnicode, true);

        try
        {
            var strictUtf8 = new UTF8Encoding(false, true);
            return new TextFileData(strictUtf8.GetString(bytes), "UTF-8", new UTF8Encoding(false), false);
        }
        catch (DecoderFallbackException)
        {
            var gb18030 = Encoding.GetEncoding("GB18030");
            return new TextFileData(gb18030.GetString(bytes), "GB18030", gb18030, false);
        }
    }

    private async void EditMode_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFilePath is null || !_currentIsMarkdown) return;
        if (!_editMode)
        {
            SetEditMode(true);
            return;
        }

        if (await ResolvePendingEditsAsync())
            SetEditMode(false);
    }

    private void SetEditMode(bool enabled)
    {
        _previewTimer.Stop();
        _editMode = enabled;
        if (enabled)
        {
            _suppressEditorTextChanged = true;
            SourceEditor.Text = _currentContent;
            _suppressEditorTextChanged = false;
            _editorDirty = false;
            ReaderScrollViewer.Visibility = Visibility.Collapsed;
            EditHost.Visibility = Visibility.Visible;
            EditPreviewScrollViewer.Document = MarkdownRenderer.Render(_currentContent, true, _fontSize);
            SaveEditButton.Visibility = Visibility.Visible;
            SaveEditButton.IsEnabled = false;
            EditModeText.Text = "完成";
            EditModeButton.Background = GetBrush("SelectedBrush");
            UnsavedDotText.Visibility = Visibility.Collapsed;
            SourceEditor.Focus();
        }
        else
        {
            ReaderScrollViewer.Visibility = Visibility.Visible;
            EditHost.Visibility = Visibility.Collapsed;
            SaveEditButton.Visibility = Visibility.Collapsed;
            EditModeText.Text = "编辑";
            EditModeButton.Background = Brushes.Transparent;
            UnsavedDotText.Visibility = Visibility.Collapsed;
            _editorDirty = false;
        }
        UpdateDocumentStatus();
    }

    private void SourceEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEditorTextChanged || !_editMode) return;
        _editorDirty = !string.Equals(SourceEditor.Text, _currentContent, StringComparison.Ordinal);
        UnsavedDotText.Visibility = _editorDirty ? Visibility.Visible : Visibility.Collapsed;
        SaveEditButton.IsEnabled = _editorDirty;
        CharacterCountText.Text = $"{SourceEditor.Text.Length:N0} 字符";
        UpdateDocumentStatus();
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void UpdateEditorPreview()
    {
        if (_editMode)
            EditPreviewScrollViewer.Document = MarkdownRenderer.Render(SourceEditor.Text, true, _fontSize);
    }

    private async void SaveEdit_Click(object sender, RoutedEventArgs e) => await SaveEditAsync();

    private async Task<bool> SaveEditAsync()
    {
        if (_currentFilePath is null || !_editMode) return false;
        try
        {
            await WriteTextAsync(_currentFilePath, SourceEditor.Text);
            CompleteEditSave(SourceEditor.Text);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EncoderFallbackException)
        {
            GlassDialog.Show(this, "保存失败", $"无法保存这个文件。\n\n{ex.Message}",
                primaryText: "知道了", cancelText: null, tone: GlassDialogTone.Warning, isDark: _isDark);
            return false;
        }
    }

    private bool SaveEditSynchronously()
    {
        if (_currentFilePath is null) return false;
        try
        {
            WriteText(_currentFilePath, SourceEditor.Text);
            CompleteEditSave(SourceEditor.Text);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EncoderFallbackException)
        {
            GlassDialog.Show(this, "保存失败", $"无法保存这个文件。\n\n{ex.Message}",
                primaryText: "知道了", cancelText: null, tone: GlassDialogTone.Warning, isDark: _isDark);
            return false;
        }
    }

    private async Task WriteTextAsync(string path, string text) => await File.WriteAllBytesAsync(path, EncodeCurrentText(text));
    private void WriteText(string path, string text) => File.WriteAllBytes(path, EncodeCurrentText(text));

    private byte[] EncodeCurrentText(string text)
    {
        var contentBytes = _currentEncoding.GetBytes(text);
        if (!_currentHasBom) return contentBytes;
        var preamble = _currentEncoding.GetPreamble();
        if (preamble.Length == 0) return contentBytes;
        var result = new byte[preamble.Length + contentBytes.Length];
        Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
        Buffer.BlockCopy(contentBytes, 0, result, preamble.Length, contentBytes.Length);
        return result;
    }

    private void CompleteEditSave(string text)
    {
        _currentContent = text;
        _editorDirty = false;
        UnsavedDotText.Visibility = Visibility.Collapsed;
        SaveEditButton.IsEnabled = false;
        ReaderScrollViewer.Document = MarkdownRenderer.Render(_currentContent, true, _fontSize);
        EditPreviewScrollViewer.Document = MarkdownRenderer.Render(_currentContent, true, _fontSize);
        UpdateDocumentStatus();
        SaveState();
    }

    private async Task<bool> ResolvePendingEditsAsync()
    {
        if (!_editMode || !_editorDirty) return true;
        var result = ShowUnsavedDialog();
        if (result == GlassDialogResult.Cancel) return false;
        if (result == GlassDialogResult.Primary && !await SaveEditAsync()) return false;
        if (result == GlassDialogResult.Secondary)
        {
            _suppressEditorTextChanged = true;
            SourceEditor.Text = _currentContent;
            _suppressEditorTextChanged = false;
            _editorDirty = false;
        }
        return true;
    }

    private GlassDialogResult ShowUnsavedDialog() => GlassDialog.Show(this, "保存修改？",
        $"“{Path.GetFileName(_currentFilePath)}”包含尚未保存的修改。",
        primaryText: "保存", secondaryText: "不保存", cancelText: "取消",
        tone: GlassDialogTone.Question, isDark: _isDark);

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        _isDark = !_isDark;
        ApplyTheme(refreshDocument: true);
        SaveState();
    }

    private void ApplyTheme(bool refreshDocument)
    {
        var palette = _isDark
            ? new Dictionary<string, string>
            {
                ["SidebarBrush"] = "#282624", ["ContentBrush"] = "#1F1E1C", ["SurfaceBrush"] = "#302E2B",
                ["HoverBrush"] = "#373431", ["SelectedBrush"] = "#403B37", ["BorderBrush"] = "#3C3935",
                ["TextBrush"] = "#ECE8E3", ["MutedBrush"] = "#AAA39B", ["AccentBrush"] = "#C7AE98",
                ["DeepSurfaceBrush"] = "#211F1D", ["EditorBackgroundBrush"] = "#1B1A18", ["SelectionBrush"] = "#68584B",
                ["TreeActiveSelectedBrush"] = "#0878D1", ["TreeInactiveSelectedBrush"] = "#263B52"
            }
            : new Dictionary<string, string>
            {
                ["SidebarBrush"] = "#F1EFEA", ["ContentBrush"] = "#FCFBF9", ["SurfaceBrush"] = "#FFFFFF",
                ["HoverBrush"] = "#E8E4DE", ["SelectedBrush"] = "#DDD7CF", ["BorderBrush"] = "#D8D2CA",
                ["TextBrush"] = "#292622", ["MutedBrush"] = "#746E67", ["AccentBrush"] = "#765B46",
                ["DeepSurfaceBrush"] = "#E9E5DF", ["EditorBackgroundBrush"] = "#F7F5F1", ["SelectionBrush"] = "#C8B29F",
                ["TreeActiveSelectedBrush"] = "#0F6CBD", ["TreeInactiveSelectedBrush"] = "#DCEAF7"
            };

        foreach (var (key, color) in palette)
            Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

        Resources[SystemColors.InactiveSelectionHighlightBrushKey] = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(_isDark ? "#263B52" : "#DCEAF7"));
        Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = GetBrush("TextBrush");

        Background = GetBrush("ContentBrush");
        MarkdownRenderer.SetTheme(_isDark);
        ThemeIconText.Text = _isDark ? "\uE706" : "\uE708";
        ThemeToggleButton.ToolTip = _isDark ? "切换白天模式" : "切换暗黑模式";
        UpdateTitleBarTheme();

        if (!refreshDocument) return;
        ReaderScrollViewer.Document = _currentFilePath is null
            ? MarkdownRenderer.Welcome(_fontSize)
            : MarkdownRenderer.Render(_currentContent, _currentIsMarkdown, _fontSize);
        if (_editMode) UpdateEditorPreview();
    }

    private SolidColorBrush GetBrush(string key) => (SolidColorBrush)Resources[key];

    private void UpdateTitleBarTheme()
    {
        if (!IsInitialized) return;
        try
        {
            var value = _isDark ? 1 : 0;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref value, sizeof(int));
        }
        catch
        {
            // The native title bar falls back safely on older Windows builds.
        }
    }

    private void AddHistory(string filePath, string rootPath)
    {
        _history.RemoveAll(entry => string.Equals(entry.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        _history.Insert(0, new HistoryEntry { FilePath = filePath, RootPath = rootPath, LastOpened = DateTime.UtcNow });
        if (_history.Count > 100) _history.RemoveRange(100, _history.Count - 100);
        RebuildHistoryGroups();
    }

    private void RemoveHistoryEntry(string filePath)
    {
        _history.RemoveAll(entry => string.Equals(entry.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        RebuildHistoryGroups();
        SaveState();
    }

    private void RebuildHistoryGroups()
    {
        HistoryGroups.Clear();
        foreach (var grouping in _history
                     .Where(entry => File.Exists(entry.FilePath))
                     .GroupBy(entry => entry.RootPath, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(group => group.Max(entry => entry.LastOpened)))
        {
            var group = new HistoryGroup { Name = GetDisplayName(grouping.Key), RootPath = grouping.Key };
            foreach (var entry in grouping.OrderByDescending(entry => entry.LastOpened)) group.Items.Add(entry);
            HistoryGroups.Add(group);
        }
        UpdateSidebarState();
    }

    private async void HistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: HistoryEntry entry })
            await OpenFileAsync(entry.FilePath, entry.RootPath, addHistory: true);
    }

    private void DirectoryTab_Checked(object sender, RoutedEventArgs e)
    {
        if (LibraryTree is null) return;
        LibraryTree.Visibility = Visibility.Visible;
        HistoryScroll.Visibility = Visibility.Collapsed;
        NodeActions.Visibility = Visibility.Visible;
        ClearHistoryButton.Visibility = Visibility.Collapsed;
        PanelHeadingText.Text = "阅读节点";
        UpdateSidebarState();
    }

    private void HistoryTab_Checked(object sender, RoutedEventArgs e)
    {
        if (LibraryTree is null) return;
        LibraryTree.Visibility = Visibility.Collapsed;
        HistoryScroll.Visibility = Visibility.Visible;
        NodeActions.Visibility = Visibility.Collapsed;
        ClearHistoryButton.Visibility = Visibility.Visible;
        PanelHeadingText.Text = "最近阅读";
        UpdateSidebarState();
    }

    private async void RefreshNode_Click(object sender, RoutedEventArgs e)
    {
        var rootPath = _selectedNode?.RootPath ?? Libraries.FirstOrDefault()?.RootPath;
        if (rootPath is null) return;
        var oldNode = Libraries.FirstOrDefault(node => string.Equals(node.RootPath, rootPath, StringComparison.OrdinalIgnoreCase));
        if (oldNode is null || !Directory.Exists(rootPath)) return;

        LibraryStatusText.Text = "正在刷新目录…";
        var index = Libraries.IndexOf(oldNode);
        var refreshed = await Task.Run(() => BuildDirectoryNode(rootPath, rootPath, isRoot: true));
        Libraries[index] = refreshed;
        _selectedNode = refreshed;
        UpdateSidebarState();
    }

    private async void RemoveNode_Click(object sender, RoutedEventArgs e)
    {
        var rootPath = _selectedNode?.RootPath;
        if (rootPath is null) return;
        var node = Libraries.FirstOrDefault(item => string.Equals(item.RootPath, rootPath, StringComparison.OrdinalIgnoreCase));
        if (node is null) return;
        if (_currentRootPath is not null
            && string.Equals(_currentRootPath, rootPath, StringComparison.OrdinalIgnoreCase)
            && !await ResolvePendingEditsAsync()) return;

        var result = GlassDialog.Show(this, "移除阅读节点？",
            $"将从阿利宙斯阅读中移除“{node.Name}”及相关历史。\n\n磁盘上的原文件不会被删除。",
            primaryText: "移除", cancelText: "取消", tone: GlassDialogTone.Question, isDark: _isDark);
        if (result != GlassDialogResult.Primary) return;

        Libraries.Remove(node);
        _history.RemoveAll(entry => string.Equals(entry.RootPath, rootPath, StringComparison.OrdinalIgnoreCase));
        _selectedNode = null;
        RemoveNodeButton.IsEnabled = false;
        RebuildHistoryGroups();
        UpdateSidebarState();
        SaveState();
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Count == 0) return;
        var result = GlassDialog.Show(this, "清空阅读历史？",
            "全部阅读记录将被清空。阅读节点和磁盘上的原文件不会受到影响。",
            primaryText: "清空", cancelText: "取消", tone: GlassDialogTone.Question, isDark: _isDark);
        if (result != GlassDialogResult.Primary) return;

        _history.Clear();
        RebuildHistoryGroups();
        SaveState();
    }

    private void IncreaseFont_Click(object sender, RoutedEventArgs e) => ChangeFontSize(1);
    private void DecreaseFont_Click(object sender, RoutedEventArgs e) => ChangeFontSize(-1);

    private void ChangeFontSize(double delta)
    {
        _fontSize = Math.Clamp(_fontSize + delta, 13, 23);
        ReaderScrollViewer.Document = _currentFilePath is null
            ? MarkdownRenderer.Welcome(_fontSize)
            : MarkdownRenderer.Render(_currentContent, _currentIsMarkdown, _fontSize);
        if (_editMode) UpdateEditorPreview();
        SaveState();
    }

    private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_currentFilePath is not null && File.Exists(_currentFilePath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_currentFilePath}\"") { UseShellExecute = true });
            else if (_selectedNode is not null && Directory.Exists(_selectedNode.RootPath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_selectedNode.RootPath}\"") { UseShellExecute = true });
        }
        catch
        {
            // Explorer may be unavailable in restricted desktop sessions.
        }
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (e.Key == Key.O)
        {
            ChooseFolder_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.H)
        {
            HistoryTab.IsChecked = true;
            e.Handled = true;
        }
        else if (e.Key == Key.S && _editMode)
        {
            await SaveEditAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.E && _currentIsMarkdown)
        {
            EditMode_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key is Key.Add or Key.OemPlus)
        {
            ChangeFontSize(1);
            e.Handled = true;
        }
        else if (e.Key is Key.Subtract or Key.OemMinus)
        {
            ChangeFontSize(-1);
            e.Handled = true;
        }
    }

    private void LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
                _state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(StatePath), _jsonOptions) ?? new AppState();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _state = new AppState();
        }
    }

    private void SaveState()
    {
        if (!_loaded && Libraries.Count == 0 && _history.Count == 0) return;
        try
        {
            Directory.CreateDirectory(StateDirectory);
            _state = new AppState
            {
                Libraries = Libraries.Select(node => node.RootPath).ToList(),
                History = _history.Take(100).ToList(),
                LastFile = _currentFilePath,
                FontSize = _fontSize,
                SidebarWidth = Math.Clamp(SidebarColumn.ActualWidth, 220, 520),
                Theme = _isDark ? "Dark" : "Light"
            };
            File.WriteAllText(StatePath, JsonSerializer.Serialize(_state, _jsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reading remains available even when settings cannot be persisted.
        }
    }

    private void UpdateDocumentStatus()
    {
        if (_currentFilePath is null)
        {
            DocumentStatusText.Text = "本地阅读 · 未打开文件";
            CharacterCountText.Text = string.Empty;
            return;
        }

        var typeLabel = _currentIsMarkdown ? "Markdown" : "纯文本";
        DocumentStatusText.Text = _editMode
            ? $"编辑模式 · {(_editorDirty ? "未保存" : "已保存")} · {_currentEncodingName}"
            : $"本地阅读 · {typeLabel} · {_currentEncodingName}";
        CharacterCountText.Text = $"{(_editMode ? SourceEditor.Text.Length : _currentContent.Length):N0} 字符";
    }

    private void UpdateSidebarState()
    {
        if (EmptyLibraryPanel is null || EmptyHistoryPanel is null) return;
        var historyMode = HistoryTab?.IsChecked == true;
        EmptyLibraryPanel.Visibility = !historyMode && Libraries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHistoryPanel.Visibility = historyMode && HistoryGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var files = Libraries.Sum(CountFiles);
        LibraryStatusText.Text = historyMode ? $"{_history.Count} 条阅读记录" : $"{Libraries.Count} 个阅读节点 · {files} 个文档";
    }

    private string? ResolveRootPath(string filePath) => Libraries
        .Select(node => node.RootPath)
        .Where(root => IsPathInside(filePath, root))
        .OrderByDescending(root => root.Length)
        .FirstOrDefault();

    private static bool IsPathInside(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountFiles(FileNode node) => node.IsDirectory ? node.Children.Sum(CountFiles) : 1;

    private static string GetDisplayName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }

    private sealed record TextFileData(string Content, string EncodingName, Encoding Encoding, bool HasBom);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
