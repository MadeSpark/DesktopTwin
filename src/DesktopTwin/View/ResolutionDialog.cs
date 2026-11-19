using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopTwin.Models;
using Wpf.Ui.Controls;

// WPF-UI 也有一个 TextBlock，同时引入两个命名空间会歧义。这里明确用 WPF 原生的。
using TextBlock = System.Windows.Controls.TextBlock;
using DesktopTwin.Localization;

namespace DesktopTwin.View;

/// <summary>
/// 自定义分辨率输入框。
///
/// 非模态：用 <c>Show()</c> 而不是 <c>ShowDialog()</c>，这样打开输入框时
/// 分身画面仍然可以操作（见 <see cref="Dialogs"/> 的说明）。
/// </summary>
internal static class ResolutionDialog
{
    /// <summary>
    /// 弹出自定义分辨率对话框。
    /// </summary>
    /// <param name="width">初始宽度。</param>
    /// <param name="height">初始高度。</param>
    /// <param name="scaleFactor">初始 DPI 缩放百分比。</param>
    /// <returns>用户确认且校验通过时返回新尺寸与 DPI，取消或关闭返回 null。</returns>
    internal static Task<(int Width, int Height, int ScaleFactor)?> ShowAsync(
        int width, int height, int scaleFactor)
    {
        var completion = new TaskCompletionSource<(int Width, int Height, int ScaleFactor)?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var widthBox = CreateSizeBox(width);
        var heightBox = CreateSizeBox(height);
        var errorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var dialog = Dialogs.CreateWindow(Locale.Get("Resolution.Title"), 400);

        var applyButton = new Wpf.Ui.Controls.Button
        {
            Content = Locale.Get("Common.Apply"),
            Appearance = ControlAppearance.Primary,
            MinWidth = 80,
            IsDefault = true
        };

        var cancelButton = new Wpf.Ui.Controls.Button
        {
            Content = Locale.Get("Common.Cancel"),
            MinWidth = 80,
            IsCancel = true,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var sizePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0)
        };
        sizePanel.Children.Add(widthBox);
        sizePanel.Children.Add(new TextBlock
        {
            Text = "×",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0)
        });
        sizePanel.Children.Add(heightBox);

        var dpiBox = new System.Windows.Controls.ComboBox { Width = 100, VerticalContentAlignment = VerticalAlignment.Center };
        foreach (var factor in new[] { 100, 125, 150, 175, 200 })
        {
            dpiBox.Items.Add(new System.Windows.Controls.ComboBoxItem
            {
                Content = $"{factor}%",
                IsSelected = factor == scaleFactor
            });
        }

        // 当前值不在预设里（之前自定义过）时补一项，避免显示成空。
        if (dpiBox.SelectedItem is null)
        {
            dpiBox.Items.Add(new System.Windows.Controls.ComboBoxItem
            {
                Content = $"{scaleFactor}%",
                IsSelected = true
            });
        }

        var dpiPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0)
        };
        dpiPanel.Children.Add(new TextBlock
        {
            Text = Locale.Get("Resolution.Dpi"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });
        dpiPanel.Children.Add(dpiBox);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        buttonPanel.Children.Add(cancelButton);
        buttonPanel.Children.Add(applyButton);

        var panel = new StackPanel { Margin = new Thickness(24, 16, 24, 20) };
        panel.Children.Add(new TextBlock
        {
            Text = Locale.Get("Resolution.Intro"),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(sizePanel);
        panel.Children.Add(dpiPanel);
        panel.Children.Add(new TextBlock
        {
            Text = Locale.Format(
                "Resolution.Range",
                SessionSettings.MinDesktopWidth,
                SessionSettings.MinDesktopHeight,
                SessionSettings.MaxDesktopWidth,
                SessionSettings.MaxDesktopHeight),
            Opacity = 0.7,
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 0)
        });
        panel.Children.Add(errorText);
        panel.Children.Add(buttonPanel);

        dialog.Content = panel;

        applyButton.Click += (_, _) =>
        {
            if (!TryParseSize(widthBox.Text, out var newWidth)
                || !TryParseSize(heightBox.Text, out var newHeight))
            {
                errorText.Text = Locale.Get("Resolution.Error.NotInteger");
                errorText.Visibility = Visibility.Visible;
                return;
            }

            if (!SessionSettings.IsValidDesktopSize(newWidth, newHeight))
            {
                errorText.Text = Locale.Format(
                    "Resolution.Error.OutOfRange",
                    SessionSettings.MinDesktopWidth,
                    SessionSettings.MinDesktopHeight,
                    SessionSettings.MaxDesktopWidth,
                    SessionSettings.MaxDesktopHeight);
                errorText.Visibility = Visibility.Visible;
                return;
            }

            var dpiText = (dpiBox.SelectedItem as System.Windows.Controls.ComboBoxItem)
                ?.Content as string;
            if (!int.TryParse(dpiText?.TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var newScale)
                || newScale is < 100 or > 500)
            {
                errorText.Text = Locale.Get("Resolution.Error.DpiRange");
                errorText.Visibility = Visibility.Visible;
                return;
            }

            completion.TrySetResult((newWidth, newHeight, newScale));
            dialog.Close();
        };

        cancelButton.Click += (_, _) =>
        {
            completion.TrySetResult(null);
            dialog.Close();
        };

        dialog.Closed += (_, _) => completion.TrySetResult(null);

        // 打开后直接选中宽度，用户敲数字就能改，不用先点一下输入框。
        dialog.Loaded += (_, _) =>
        {
            widthBox.Focus();
            widthBox.SelectAll();
        };

        Dialogs.Show(dialog);
        return completion.Task;
    }

    private static Wpf.Ui.Controls.TextBox CreateSizeBox(int value) => new()
    {
        Text = value.ToString(CultureInfo.InvariantCulture),
        Width = 100,
        MaxLength = 4,
        VerticalContentAlignment = VerticalAlignment.Center
    };

    private static bool TryParseSize(string? text, out int value) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
