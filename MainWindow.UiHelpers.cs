using System.IO;
using System.Net.Http;
using System.Globalization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using TwitchStudioNative.Audio;
using TwitchStudioNative.Discord;
using TwitchStudioNative.Server;
using TwitchStudioNative.Storage;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
using TitleBar = Wpf.Ui.Controls.TitleBar;
using WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType;
using Ellipse = System.Windows.Shapes.Ellipse;
using Forms = System.Windows.Forms;

namespace TwitchStudioNative;

public sealed partial class MainWindow
{
    private static UIElement SettingsInput(string label, string description, TextBox input, string value)
    {
        input.Text = value;
        input.Height = 34;
        input.Margin = new Thickness(0, 6, 0, 12);
        input.Padding = new Thickness(8, 4, 8, 4);
        input.BorderBrush = Brush("#3A3A3D");
        input.Background = Brush("#151518");
        input.Foreground = Brush("#F4F4F5");
        var panel = new StackPanel();
        panel.Children.Add(Label(label));
        panel.Children.Add(Description(description));
        panel.Children.Add(input);
        return panel;
    }

    private UIElement BuildVadLevelMeter()
    {
        Detach(_vadLevel);
        var maximum = Math.Max(0.001, _vadLevel.Maximum);
        var silence = Math.Clamp(_config.LocalMicrophone.SilenceThreshold / maximum, 0, 1);
        var speaking = Math.Clamp(_config.LocalMicrophone.SpeakingThreshold / maximum, 0, 1);
        var background = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        background.GradientStops.Add(new GradientStop(Color.FromArgb(70, 113, 113, 122), 0));
        background.GradientStops.Add(new GradientStop(Color.FromArgb(70, 113, 113, 122), silence));
        background.GradientStops.Add(new GradientStop(Color.FromArgb(70, 234, 179, 8), silence));
        background.GradientStops.Add(new GradientStop(Color.FromArgb(70, 234, 179, 8), speaking));
        background.GradientStops.Add(new GradientStop(Color.FromArgb(70, 34, 197, 94), speaking));
        background.GradientStops.Add(new GradientStop(Color.FromArgb(70, 34, 197, 94), 1));

        var grid = new Grid { Height = 18, Margin = new Thickness(0, 0, 0, 6) };
        grid.Children.Add(new Border
        {
            Height = 12,
            CornerRadius = new CornerRadius(6),
            Background = background,
            BorderBrush = Brush("#2A2A2D"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center
        });
        grid.Children.Add(_vadLevel);
        grid.Children.Add(ThresholdMarker(silence, "#EAB308"));
        grid.Children.Add(ThresholdMarker(speaking, "#22C55E"));

        var legend = new TextBlock
        {
            Text = T("settings.vadLegend"),
            Foreground = Brush("#71717A"),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 18)
        };
        var panel = new StackPanel();
        panel.Children.Add(grid);
        panel.Children.Add(legend);
        return panel;
    }

    private static UIElement ThresholdMarker(double position, string color)
    {
        return new Border
        {
            Width = 2,
            Height = 16,
            Background = Brush(color),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(position * 500, 0, 0, 0),
            Opacity = 0.85
        };
    }

    private static UIElement SettingsSliderInput(string label, string description, TextBox input, Slider slider, double value)
    {
        input.Text = value.ToString("0.000", CultureInfo.InvariantCulture);
        input.Height = 34;
        input.Width = 82;
        input.Padding = new Thickness(8, 4, 8, 4);
        input.BorderBrush = Brush("#3A3A3D");
        input.Background = Brush("#151518");
        input.Foreground = Brush("#F4F4F5");
        slider.Minimum = 0.001;
        slider.Maximum = 0.12;
        slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
        slider.TickFrequency = 0.005;
        slider.IsSnapToTickEnabled = false;
        slider.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid { Margin = new Thickness(0, 6, 0, 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(slider);
        Grid.SetColumn(input, 2);
        row.Children.Add(input);
        var panel = new StackPanel();
        panel.Children.Add(Label(label));
        panel.Children.Add(Description(description));
        panel.Children.Add(row);
        return panel;
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Foreground = Brush("#E4E4E7") };
    private static TextBlock Description(string text) => new()
    {
        Text = text,
        Foreground = Brush("#8B8B94"),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 3, 0, 8)
    };

    private UIElement BuildDiscordStatus()
    {
        _discordDot.Width = 8;
        _discordDot.Height = 8;
        _discordDot.VerticalAlignment = VerticalAlignment.Center;
        _discordDot.Margin = new Thickness(0, 0, 8, 0);
        _discordStatus.FontWeight = FontWeights.SemiBold;
        _discordStatus.FontSize = 12;

        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(_discordDot);
        stack.Children.Add(_discordStatus);

        _discordBadge.CornerRadius = new CornerRadius(16);
        _discordBadge.BorderThickness = new Thickness(1);
        _discordBadge.Padding = new Thickness(12, 7, 12, 7);

        return stack;
    }

    private void UpdateDiscordStatus(ConnectionStatus status, string text)
    {
        _discordStatus.Text = $"Discord: {text}";
        var color = status switch
        {
            ConnectionStatus.connected => "#22C55E",
            ConnectionStatus.connecting => "#38BDF8",
            ConnectionStatus.error => "#F97316",
            _ => "#71717A"
        };
        _discordDot.Fill = Brush(color);
        _discordBadge.Background = Brush(status == ConnectionStatus.connected ? "#14231A" : "#1B1B1D");
        _discordBadge.BorderBrush = Brush(status == ConnectionStatus.connected ? "#245B35" : "#303033");
    }

    private static Border Panel(UIElement child) => new() { CornerRadius = new CornerRadius(10), BorderBrush = Brush("#2A2A2D"), BorderThickness = new Thickness(1), Background = Brush("#1A1A1D"), Padding = new Thickness(14), Child = child };
    private static Button Button(string text) => new() { Content = text, MinHeight = 34, Padding = new Thickness(12, 6, 12, 6), BorderThickness = new Thickness(1), BorderBrush = Brush("#3A3A3D"), Background = Brush("#242427"), Foreground = Brush("#F4F4F5") };
    private static Button IconButton(string icon) => new()
    {
        Content = new Grid
        {
            Width = 44,
            Height = 36,
            Children =
            {
                new TextBlock { Text = icon, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center }
            }
        },
        MinHeight = 44,
        Padding = new Thickness(0),
        BorderThickness = new Thickness(1),
        BorderBrush = Brush("#3A3A3D"),
        Background = Brush("#242427"),
        Foreground = Brush("#F4F4F5")
    };

    private static Button NavButton(string icon, string text)
    {
        var button = Button("");
        button.MinHeight = 44;
        button.Height = 44;
        button.Padding = new Thickness(0);
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        RenderNavButton(button, icon, text, false);
        return button;
    }

    private static void RenderNavButton(Button button, string icon, string text, bool collapsed)
    {
        var grid = new Grid { Width = collapsed ? 52 : double.NaN, MinWidth = collapsed ? 52 : 180 };
        if (collapsed)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        grid.Children.Add(new Border
        {
            Tag = "indicator",
            Width = collapsed ? 0 : 3,
            Height = 18,
            CornerRadius = new CornerRadius(2),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        });

        var iconHost = new Grid
        {
            Width = collapsed ? 52 : 44,
            Height = 36,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconHost.Children.Add(new TextBlock
        {
            Text = icon,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 17,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            LineHeight = 17,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        });
        Grid.SetColumn(iconHost, collapsed ? 0 : 1);
        grid.Children.Add(iconHost);

        if (!collapsed)
        {
            var label = new TextBlock
            {
                Text = text,
                Margin = new Thickness(6, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold
            };
            Grid.SetColumn(label, 2);
            grid.Children.Add(label);
        }

        button.Content = grid;
    }
    private static string StatusLabel(ConnectionStatus status) => status switch
    {
        ConnectionStatus.connected => T("discord.status.connected"),
        ConnectionStatus.connecting => T("discord.status.connecting"),
        ConnectionStatus.error => T("discord.status.error"),
        _ => T("discord.status.disconnected")
    };

    private static string StateLabel(AnimationState state) => state switch
    {
        AnimationState.idle => T("state.idle"),
        AnimationState.speaking => T("state.speaking"),
        AnimationState.muted => T("state.muted"),
        AnimationState.deafened => T("state.deafened"),
        AnimationState.disconnected => T("state.disconnected"),
        _ => state.ToString()
    };
    private static string MimeFromExtension(string extension) => extension.ToLowerInvariant() switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".svg" => "image/svg+xml", _ => "application/octet-stream" };
    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var safe = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "overlay" : safe;
    }
    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));

    private static DrawingBrush CheckerBrush()
    {
        var light = Brush("#18181B");
        var dark = Brush("#101012");
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        group.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(0, 0, 12, 12))));
        group.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(12, 12, 12, 12))));
        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 24, 24),
            ViewportUnits = BrushMappingMode.Absolute
        };
    }

    private sealed class InitialConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var text = value as string;
            return string.IsNullOrWhiteSpace(text) ? "?" : text.Trim()[0].ToString().ToUpperInvariant();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    private sealed class AvatarBrushConverter : IValueConverter
    {
        private static readonly Dictionary<string, ImageBrush> Cache = [];

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string avatarUrl || string.IsNullOrWhiteSpace(avatarUrl))
            {
                return Brush("#2B3340");
            }

            if (Cache.TryGetValue(avatarUrl, out var cached))
            {
                return cached;
            }

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(avatarUrl, UriKind.Absolute);
                image.DecodePixelWidth = 68;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();

                if (image.CanFreeze)
                {
                    image.Freeze();
                }

                var brush = new ImageBrush(image)
                {
                    Stretch = Stretch.UniformToFill,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center
                };
                if (brush.CanFreeze)
                {
                    brush.Freeze();
                }

                Cache[avatarUrl] = brush;
                return brush;
            }
            catch
            {
                return Brush("#2B3340");
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    private sealed class MissingAvatarVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is string avatarUrl && !string.IsNullOrWhiteSpace(avatarUrl)
                ? Visibility.Collapsed
                : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    private sealed class UserMetaConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not VoiceUser user)
            {
                return "";
            }

            var username = string.IsNullOrWhiteSpace(user.Username) ? null : $"@{user.Username}";
            return string.IsNullOrWhiteSpace(username) ? user.Id : $"{username} · {user.Id}";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    private sealed class VoiceCommandHotKeyConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is VoiceCommandRule rule ? T("hotkey.label", VoiceCommandHotKeyLabel(rule)) : T("hotkey.none");

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    private sealed class StateTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is AnimationState state ? StateLabel(state) : "";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    private sealed class StateBackgroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is AnimationState state
                ? Brush(state switch
                {
                    AnimationState.speaking => "#183124",
                    AnimationState.muted => "#2A2433",
                    AnimationState.deafened => "#332821",
                    AnimationState.disconnected => "#262629",
                    _ => "#20262D"
                })
                : Brush("#20262D");

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    private sealed class StateBorderConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is AnimationState state
                ? Brush(state switch
                {
                    AnimationState.speaking => "#2F7D4D",
                    AnimationState.muted => "#5B4A77",
                    AnimationState.deafened => "#7A5130",
                    AnimationState.disconnected => "#3A3A3D",
                    _ => "#35414D"
                })
                : Brush("#35414D");

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    private sealed record VadModeOption(string Value, string Label);
    private sealed record AnimationStateOption(AnimationState State, string Label);
}
