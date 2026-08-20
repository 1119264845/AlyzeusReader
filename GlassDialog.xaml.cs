using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace JianRead;

public enum GlassDialogResult
{
    Cancel,
    Primary,
    Secondary
}

public enum GlassDialogTone
{
    Question,
    Information,
    Warning
}

public partial class GlassDialog : Window
{
    private readonly bool _isDark;
    private double _ownerOpacity = 1;
    private UIElement? _ownerContent;
    private Effect? _ownerEffect;
    private bool _ownerVisualsRestored;

    public GlassDialogResult Result { get; private set; } = GlassDialogResult.Cancel;

    private GlassDialog(string title, string message, string primaryText, string? secondaryText,
        string? cancelText, GlassDialogTone tone, bool isDark)
    {
        InitializeComponent();
        _isDark = isDark;
        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primaryText;
        SecondaryButton.Content = secondaryText ?? string.Empty;
        SecondaryButton.Visibility = secondaryText is null ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Content = cancelText ?? string.Empty;
        CancelButton.Visibility = cancelText is null ? Visibility.Collapsed : Visibility.Visible;
        IconText.Text = tone switch
        {
            GlassDialogTone.Warning => "\uE7BA",
            GlassDialogTone.Information => "\uE946",
            _ => "\uE897"
        };
        ApplyTheme();
        Closed += (_, _) => RestoreOwnerVisuals();
    }

    public static GlassDialogResult Show(Window owner, string title, string message,
        string primaryText = "确定", string? secondaryText = null, string? cancelText = "取消",
        GlassDialogTone tone = GlassDialogTone.Question, bool isDark = true)
    {
        var dialog = new GlassDialog(title, message, primaryText, secondaryText, cancelText, tone, isDark)
        {
            Owner = owner
        };
        return dialog.ShowOwned(owner);
    }

    public static (GlassDialogResult Result, string Value) ShowInput(Window owner, string title, string message,
        string initialValue, int selectionLength, string primaryText = "确定", string? cancelText = "取消",
        bool isDark = true)
    {
        var dialog = new GlassDialog(title, message, primaryText, null, cancelText,
            GlassDialogTone.Information, isDark)
        {
            Owner = owner
        };
        dialog.MessageText.Margin = new Thickness(42, 17, 4, 13);
        dialog.InputSurface.Visibility = Visibility.Visible;
        dialog.InputTextBox.Text = initialValue;
        dialog.Loaded += (_, _) =>
        {
            dialog.InputTextBox.Focus();
            dialog.InputTextBox.Select(0, Math.Clamp(selectionLength, 0, initialValue.Length));
        };
        var result = dialog.ShowOwned(owner);
        return (result, dialog.InputTextBox.Text);
    }

    private GlassDialogResult ShowOwned(Window owner)
    {
        ApplyOwnerGlassBackdrop(owner);
        try
        {
            ShowDialog();
        }
        finally
        {
            RestoreOwnerVisuals();
        }
        return Result;
    }

    private void ApplyOwnerGlassBackdrop(Window owner)
    {
        _ownerOpacity = owner.Opacity;
        _ownerContent = owner.Content as UIElement;
        if (_ownerContent is not null)
        {
            _ownerEffect = _ownerContent.Effect;
            _ownerContent.Effect = new BlurEffect
            {
                Radius = 12,
                KernelType = KernelType.Gaussian,
                RenderingBias = RenderingBias.Quality
            };
        }

        owner.Opacity = _isDark ? 0.92 : 0.94;
    }

    private void RestoreOwnerVisuals()
    {
        if (_ownerVisualsRestored) return;
        _ownerVisualsRestored = true;
        if (_ownerContent is not null) _ownerContent.Effect = _ownerEffect;
        if (Owner is not null) Owner.Opacity = _ownerOpacity;
    }

    private void ApplyTheme()
    {
        DialogSurface.Background = Brush(_isDark ? "#B8242220" : "#C9F8F6F2");
        DialogSurface.BorderBrush = Brush(_isDark ? "#806E6761" : "#B8FFFFFF");
        GlassHighlight.BorderBrush = Brush(_isDark ? "#30FFFFFF" : "#B0FFFFFF");
        IconSurface.Background = Brush(_isDark ? "#403A35" : "#E9E1D8");
        IconText.Foreground = Brush(_isDark ? "#D8B99E" : "#765B46");
        TitleText.Foreground = Brush(_isDark ? "#F0ECE7" : "#282522");
        MessageText.Foreground = Brush(_isDark ? "#CFC9C2" : "#514B45");
        InputSurface.Background = Brush(_isDark ? "#702B2926" : "#AFFFFFFF");
        InputSurface.BorderBrush = Brush(_isDark ? "#665F5852" : "#B8D2CBC2");
        InputTextBox.Foreground = Brush(_isDark ? "#F0ECE7" : "#282522");
        InputTextBox.CaretBrush = Brush(_isDark ? "#D8B99E" : "#765B46");
        InputTextBox.SelectionBrush = Brush(_isDark ? "#68584B" : "#C8B29F");
        SecondaryButton.Background = Brush(_isDark ? "#45413D" : "#E5E0D9");
        SecondaryButton.Foreground = Brush(_isDark ? "#EDE9E4" : "#3A3530");
        CancelButton.Background = Brush(_isDark ? "#45413D" : "#E5E0D9");
        CancelButton.Foreground = Brush(_isDark ? "#EDE9E4" : "#3A3530");
        PrimaryButton.Background = Brush(_isDark ? "#C8AD96" : "#6F5846");
        PrimaryButton.Foreground = Brush(_isDark ? "#241F1B" : "#FFFFFF");
    }

    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        Result = GlassDialogResult.Primary;
        Close();
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        Result = GlassDialogResult.Secondary;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Result = GlassDialogResult.Primary;
            Close();
            e.Handled = true;
        }
    }

}
