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
    private void ToggleNavigation()
    {
        _isNavCollapsed = !_isNavCollapsed;
        AnimateNavigationWidth(_isNavCollapsed ? 85 : 220);
        _navTitle.Visibility = _isNavCollapsed ? Visibility.Collapsed : Visibility.Visible;
        RenderNavButton(_dashboardNav, "\uE80F", T("nav.dashboard"), _isNavCollapsed);
        RenderNavButton(_membersNav, "\uE716", T("nav.members"), _isNavCollapsed);
        RenderNavButton(_commandsNav, "\uE720", T("nav.commands"), _isNavCollapsed);
        RenderNavButton(_settingsNav, "\uE713", T("nav.settings"), _isNavCollapsed);
        RenderNavButton(_logNav, "\uE8A5", T("nav.log"), _isNavCollapsed);
        _logNav.Visibility = _config.DebugMode ? Visibility.Visible : Visibility.Collapsed;
        ApplyNavigationState();
    }

    private async void AnimateNavigationWidth(double targetWidth)
    {
        var startWidth = _navColumn.ActualWidth;
        const int durationMs = 180;
        const int steps = 12;
        for (var step = 1; step <= steps; step++)
        {
            var t = step / (double)steps;
            var eased = 1 - Math.Pow(1 - t, 3);
            _navColumn.Width = new GridLength(startWidth + ((targetWidth - startWidth) * eased));
            await Task.Delay(durationMs / steps);
        }

        _navColumn.Width = new GridLength(targetWidth);
    }

    private void ApplyNavigationState()
    {
        ApplyNavButton(_dashboardNav, _activePage == "dashboard");
        ApplyNavButton(_membersNav, _activePage == "members");
        ApplyNavButton(_commandsNav, _activePage == "commands");
        ApplyNavButton(_settingsNav, _activePage == "settings");
        ApplyNavButton(_logNav, _activePage == "log");
    }

    private static void ApplyNavButton(Button button, bool active)
    {
        button.Background = Brush(active ? "#273244" : "#1A1A1D");
        button.BorderBrush = Brush(active ? "#3F5F8F" : "#2A2A2D");
        button.Foreground = Brush(active ? "#FFFFFF" : "#D4D4D8");
        if (button.Content is Grid grid && grid.Children.OfType<Border>().FirstOrDefault(item => item.Tag as string == "indicator") is { } indicator)
        {
            indicator.Background = active ? Brush("#60A5FA") : Brushes.Transparent;
        }
    }

    private Border BuildMembersPanel()
    {
        var dock = new DockPanel { LastChildFill = true };
        Detach(_reconnectDiscord);
        Detach(_discordMessage);
        _reconnectDiscord.Margin = new Thickness(0, 0, 0, 12);
        if (!_reconnectDiscordConfigured)
        {
            _reconnectDiscord.Click += ReconnectDiscord_Click;
            _reconnectDiscordConfigured = true;
        }
        DockPanel.SetDock(_reconnectDiscord, Dock.Top);
        dock.Children.Add(_reconnectDiscord);
        _discordMessage.TextWrapping = TextWrapping.Wrap;
        _discordMessage.Foreground = Brush("#A1A1AA");
        _discordMessage.Margin = new Thickness(0, 0, 0, 16);
        DockPanel.SetDock(_discordMessage, Dock.Top);
        dock.Children.Add(_discordMessage);
        var localMic = BuildDashboardLocalMicPanel();
        DockPanel.SetDock(localMic, Dock.Top);
        dock.Children.Add(localMic);
        var label = new TextBlock { Text = T("members.title"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(label, Dock.Top);
        dock.Children.Add(label);
        ConfigureUsersList(_usersList, _users);
        if (!_usersListConfigured)
        {
            _usersList.SelectionChanged += UsersList_SelectionChanged;
            _usersListConfigured = true;
        }
        Detach(_usersList);
        dock.Children.Add(_usersList);
        return Panel(dock);
    }

    private UIElement BuildDashboardLocalMicPanel()
    {
        Detach(_dashboardVadStatus);
        Detach(_dashboardHotKey);
        Detach(_dashboardLocalMicMute);
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        stack.Children.Add(new TextBlock { Text = T("dashboard.localMic"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        _dashboardVadStatus.Foreground = Brush("#A1A1AA");
        _dashboardVadStatus.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(_dashboardVadStatus);
        _dashboardHotKey.Foreground = Brush("#71717A");
        _dashboardHotKey.FontSize = 11;
        _dashboardHotKey.Margin = new Thickness(0, 4, 0, 8);
        stack.Children.Add(_dashboardHotKey);
        _dashboardLocalMicMute.Click -= LocalMicMute_Click;
        _dashboardLocalMicMute.Click += LocalMicMute_Click;
        stack.Children.Add(_dashboardLocalMicMute);
        UpdateVadStatus();
        return stack;
    }

    private Border BuildKnownMembersPanel()
    {
        var dock = new DockPanel { LastChildFill = true };
        var title = new TextBlock { Text = T("members.all"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(title, Dock.Top);
        dock.Children.Add(title);

        var emulationHint = new TextBlock
        {
            Text = T("members.previewHint"),
            Foreground = Brush("#A1A1AA"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(emulationHint, Dock.Top);
        dock.Children.Add(emulationHint);

        var emulation = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var state in Enum.GetValues<AnimationState>())
        {
            var button = Button(StateLabel(state));
            button.Tag = state;
            button.Margin = new Thickness(0, 0, 6, 6);
            button.MinHeight = 30;
            button.Padding = new Thickness(8, 4, 8, 4);
            button.ToolTip = T("members.previewTooltip", StateLabel(state));
            button.Click += EmulateState_Click;
            emulation.Children.Add(button);
        }
        DockPanel.SetDock(emulation, Dock.Top);
        dock.Children.Add(emulation);

        ConfigureUsersList(_knownUsersList, _knownUsers);
        if (!_knownUsersListConfigured)
        {
            _knownUsersList.SelectionChanged += KnownUsersList_SelectionChanged;
            _knownUsersListConfigured = true;
        }
        Detach(_knownUsersList);
        dock.Children.Add(_knownUsersList);
        return Panel(dock);
    }

    private static void ConfigureUsersList(ListBox list, ObservableCollection<VoiceUser> source)
    {
        list.Background = Brush("#09090B");
        list.BorderBrush = Brush("#27272A");
        list.Foreground = Brush("#F4F4F5");
        list.Padding = new Thickness(6);
        list.ItemTemplate ??= BuildUserItemTemplate();
        list.ItemContainerStyle ??= BuildUserItemStyle();
        list.ItemsSource ??= source;
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
    }

    private void ConfigureVoiceCommandRulesList()
    {
        _voiceCommandRulesList.Background = Brush("#09090B");
        _voiceCommandRulesList.BorderBrush = Brush("#27272A");
        _voiceCommandRulesList.Foreground = Brush("#F4F4F5");
        _voiceCommandRulesList.Padding = new Thickness(6);
        _voiceCommandRulesList.ItemTemplate ??= BuildVoiceCommandItemTemplate();
        _voiceCommandRulesList.ItemContainerStyle ??= BuildUserItemStyle();
        _voiceCommandRulesList.ItemsSource = _voiceCommandRules;
        _voiceCommandRulesList.MinHeight = 280;
        _voiceCommandRulesList.VerticalAlignment = VerticalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(_voiceCommandRulesList, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_voiceCommandRulesList, ScrollBarVisibility.Auto);
    }

    private static DataTemplate BuildVoiceCommandItemTemplate()
    {
        var template = new DataTemplate(typeof(VoiceCommandRule));

        var row = new FrameworkElementFactory(typeof(DockPanel));
        row.SetValue(FrameworkElement.MinHeightProperty, 54.0);
        row.SetValue(DockPanel.LastChildFillProperty, true);
        row.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var icon = new FrameworkElementFactory(typeof(Border));
        icon.SetValue(Border.WidthProperty, 34.0);
        icon.SetValue(Border.HeightProperty, 34.0);
        icon.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        icon.SetValue(Border.BackgroundProperty, Brush("#20262D"));
        icon.SetValue(Border.BorderBrushProperty, Brush("#35414D"));
        icon.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0));
        icon.SetValue(DockPanel.DockProperty, Dock.Left);
        var iconText = new FrameworkElementFactory(typeof(TextBlock));
        iconText.SetValue(TextBlock.TextProperty, "\uE720");
        iconText.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe MDL2 Assets"));
        iconText.SetValue(TextBlock.FontSizeProperty, 15.0);
        iconText.SetValue(TextBlock.ForegroundProperty, Brush("#E5E7EB"));
        iconText.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        iconText.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        icon.AppendChild(iconText);
        row.AppendChild(icon);

        var textStack = new FrameworkElementFactory(typeof(StackPanel));
        textStack.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(VoiceCommandRule.ActionName)));
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        name.SetValue(TextBlock.FontSizeProperty, 13.0);
        name.SetValue(TextBlock.ForegroundProperty, Brush("#F4F4F5"));
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        textStack.AppendChild(name);

        var phrase = new FrameworkElementFactory(typeof(TextBlock));
        phrase.SetBinding(TextBlock.TextProperty, new Binding(nameof(VoiceCommandRule.Phrase)) { StringFormat = T("voice.item.phrase") });
        phrase.SetValue(TextBlock.FontSizeProperty, 11.0);
        phrase.SetValue(TextBlock.ForegroundProperty, Brush("#8B8B94"));
        phrase.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        phrase.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 0));
        textStack.AppendChild(phrase);

        var hotKey = new FrameworkElementFactory(typeof(TextBlock));
        hotKey.SetBinding(TextBlock.TextProperty, new Binding { Converter = new VoiceCommandHotKeyConverter() });
        hotKey.SetValue(TextBlock.FontSizeProperty, 11.0);
        hotKey.SetValue(TextBlock.ForegroundProperty, Brush("#71717A"));
        hotKey.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        hotKey.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 0));
        textStack.AppendChild(hotKey);

        row.AppendChild(textStack);
        template.VisualTree = row;
        return template;
    }

    private static DataTemplate BuildUserItemTemplate()
    {
        var template = new DataTemplate(typeof(VoiceUser));

        var row = new FrameworkElementFactory(typeof(DockPanel));
        row.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
        row.SetValue(FrameworkElement.MinHeightProperty, 54.0);
        row.SetValue(DockPanel.LastChildFillProperty, true);
        row.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var avatar = new FrameworkElementFactory(typeof(Border));
        avatar.SetValue(Border.WidthProperty, 34.0);
        avatar.SetValue(Border.HeightProperty, 34.0);
        avatar.SetValue(Border.CornerRadiusProperty, new CornerRadius(17));
        avatar.SetBinding(Border.BackgroundProperty, new Binding(nameof(VoiceUser.AvatarUrl)) { Converter = new AvatarBrushConverter() });
        avatar.SetValue(Border.BorderBrushProperty, Brush("#3F4A5A"));
        avatar.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        avatar.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        avatar.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0));
        avatar.SetValue(DockPanel.DockProperty, Dock.Left);

        var initial = new FrameworkElementFactory(typeof(TextBlock));
        initial.SetBinding(TextBlock.TextProperty, new Binding(nameof(VoiceUser.DisplayName)) { Converter = new InitialConverter() });
        initial.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(VoiceUser.AvatarUrl)) { Converter = new MissingAvatarVisibilityConverter() });
        initial.SetValue(TextBlock.ForegroundProperty, Brush("#E5E7EB"));
        initial.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        initial.SetValue(TextBlock.FontSizeProperty, 13.0);
        initial.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        initial.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        initial.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        avatar.AppendChild(initial);
        row.AppendChild(avatar);

        var stateBadge = new FrameworkElementFactory(typeof(Border));
        stateBadge.SetValue(DockPanel.DockProperty, Dock.Right);
        stateBadge.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        stateBadge.SetValue(Border.PaddingProperty, new Thickness(8, 3, 8, 4));
        stateBadge.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        stateBadge.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        stateBadge.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        stateBadge.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
        stateBadge.SetBinding(Border.BackgroundProperty, new Binding(nameof(VoiceUser.State)) { Converter = new StateBackgroundConverter() });
        stateBadge.SetBinding(Border.BorderBrushProperty, new Binding(nameof(VoiceUser.State)) { Converter = new StateBorderConverter() });

        var stateText = new FrameworkElementFactory(typeof(TextBlock));
        stateText.SetBinding(TextBlock.TextProperty, new Binding(nameof(VoiceUser.State)) { Converter = new StateTextConverter() });
        stateText.SetValue(TextBlock.ForegroundProperty, Brush("#E4E4E7"));
        stateText.SetValue(TextBlock.FontSizeProperty, 11.0);
        stateText.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        stateBadge.AppendChild(stateText);
        row.AppendChild(stateBadge);

        var textStack = new FrameworkElementFactory(typeof(StackPanel));
        textStack.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(VoiceUser.DisplayName)));
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        name.SetValue(TextBlock.FontSizeProperty, 13.0);
        name.SetValue(TextBlock.ForegroundProperty, Brush("#F4F4F5"));
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        textStack.AppendChild(name);

        var meta = new FrameworkElementFactory(typeof(TextBlock));
        meta.SetBinding(TextBlock.TextProperty, new Binding { Converter = new UserMetaConverter() });
        meta.SetValue(TextBlock.FontSizeProperty, 11.0);
        meta.SetValue(TextBlock.ForegroundProperty, Brush("#8B8B94"));
        meta.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        meta.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 0));
        textStack.AppendChild(meta);
        row.AppendChild(textStack);

        template.VisualTree = row;
        return template;
    }

    private static Style BuildUserItemStyle()
    {
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 6, 8, 6)));
        itemStyle.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0, 0, 0, 4)));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        itemStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        itemStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "ItemBorder";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
        border.AppendChild(presenter);

        var controlTemplate = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border };
        controlTemplate.Triggers.Add(new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true,
            Setters =
            {
                new Setter(Control.BackgroundProperty, Brush("#1F1F23"), "ItemBorder"),
                new Setter(Control.BorderBrushProperty, Brush("#303036"), "ItemBorder")
            }
        });
        controlTemplate.Triggers.Add(new Trigger
        {
            Property = ListBoxItem.IsSelectedProperty,
            Value = true,
            Setters =
            {
                new Setter(Control.BackgroundProperty, Brush("#263244"), "ItemBorder"),
                new Setter(Control.BorderBrushProperty, Brush("#3F5F8F"), "ItemBorder")
            }
        });
        itemStyle.Setters.Add(new Setter(Control.TemplateProperty, controlTemplate));
        return itemStyle;
    }

    private void UsersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectUserFromList(_usersList.SelectedItem as VoiceUser);
    }

    private void KnownUsersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectUserFromList(_knownUsersList.SelectedItem as VoiceUser);
    }

    private void SelectUserFromList(VoiceUser? selected)
    {
        if (_isSyncingUsers)
        {
            return;
        }

        if (selected?.Id == _selectedUser?.Id)
        {
            return;
        }

        _selectedUser = selected;
        RenderSelectedUser(updatePreview: true);
    }

    private async void EmulateState_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AnimationState state })
        {
            await EmulatePreviewStateAsync(state);
        }
    }

    private async void DurationInput_Commit(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AnimationState state })
        {
            await SaveFrameDurationAsync(state);
        }
    }

    private async void DurationInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || sender is not FrameworkElement { Tag: AnimationState state })
        {
            return;
        }

        e.Handled = true;
        await SaveFrameDurationAsync(state);
        if (sender is Control control)
        {
            control.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
    }

    private Border BuildLogPage()
    {
        var dock = new DockPanel { LastChildFill = true };
        var top = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(new TextBlock { Text = T("log.title"), FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var clear = Button(T("common.clear"));
        clear.Click += (_, _) => _logs.Clear();
        Grid.SetColumn(clear, 1);
        top.Children.Add(clear);
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);

        _logList.ItemsSource = _logs;
        _logList.Background = Brush("#101012");
        _logList.BorderBrush = Brush("#2A2A2D");
        _logList.Foreground = Brush("#D4D4D8");
        _logList.FontFamily = new FontFamily("Consolas");
        _logList.FontSize = 12;
        dock.Children.Add(_logList);
        return Panel(dock);
    }

    private Border BuildCenterPanel()
    {
        var dock = new DockPanel { LastChildFill = true };
        var selected = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        Detach(_selectedName);
        Detach(_selectedId);
        Detach(_stateRows);
        _selectedName.FontSize = 20;
        _selectedName.FontWeight = FontWeights.SemiBold;
        _selectedId.Foreground = Brush("#71717A");
        _selectedId.Margin = new Thickness(0, 4, 0, 0);
        selected.Children.Add(_selectedName);
        selected.Children.Add(_selectedId);
        DockPanel.SetDock(selected, Dock.Top);
        dock.Children.Add(selected);
        dock.Children.Add(_stateRows);
        return Panel(dock);
    }

    private Border BuildRightPanel()
    {
        var stack = new StackPanel();
        Detach(_overlayUrl);
        Detach(_preview);
        Detach(_previewPlaceholder);
        Detach(_previewFrame);
        _previewFrame.Child = null;
        stack.Children.Add(new TextBlock { Text = T("preview.obsSource"), FontWeight = FontWeights.SemiBold });
        _overlayUrl.IsReadOnly = true;
        _overlayUrl.TextWrapping = TextWrapping.Wrap;
        _overlayUrl.Margin = new Thickness(0, 10, 0, 8);
        stack.Children.Add(_overlayUrl);
        var copy = Button(T("common.copy"));
        copy.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(_overlayUrl.Text)) Clipboard.SetText(_overlayUrl.Text); };
        stack.Children.Add(copy);
        stack.Children.Add(new TextBlock { Text = T("preview.title"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 10) });
        _preview.Height = 186;
        _preview.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        if (!_previewConfigured)
        {
            _preview.NavigationCompleted += (_, _) => _previewNavigation?.TrySetResult(true);
            _previewConfigured = true;
        }
        _previewPlaceholder.Text = T("common.selectUser");
        _previewPlaceholder.Foreground = Brush("#71717A");
        _previewPlaceholder.HorizontalAlignment = HorizontalAlignment.Center;
        _previewPlaceholder.VerticalAlignment = VerticalAlignment.Center;

        var previewGrid = new Grid();
        previewGrid.Children.Add(_preview);
        previewGrid.Children.Add(_previewPlaceholder);
        _previewFrame.Height = 186;
        _previewFrame.BorderBrush = Brush("#2A2A2D");
        _previewFrame.BorderThickness = new Thickness(1);
        _previewFrame.CornerRadius = new CornerRadius(8);
        _previewFrame.Background = CheckerBrush();
        _previewFrame.Child = previewGrid;
        stack.Children.Add(_previewFrame);

        var archiveActions = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        archiveActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        archiveActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        archiveActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // archiveActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // archiveActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        // archiveActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var export = Button(T("common.export"));
        export.Click += ExportOverlay_Click;
        archiveActions.Children.Add(export);
        var import = Button(T("common.import"));
        import.Click += ImportOverlay_Click;
        Grid.SetColumn(import, 2);
        archiveActions.Children.Add(import);
        stack.Children.Add(archiveActions);
        return Panel(stack);
    }

    private static void Detach(UIElement element)
    {
        var parent = LogicalTreeHelper.GetParent(element) ?? VisualTreeHelper.GetParent(element);
        switch (parent)
        {
            case Panel panel:
                panel.Children.Remove(element);
                break;
            case Decorator decorator when ReferenceEquals(decorator.Child, element):
                decorator.Child = null;
                break;
            case ContentControl contentControl when ReferenceEquals(contentControl.Content, element):
                contentControl.Content = null;
                break;
        }
    }

    private void RenderSelectedUser(bool updatePreview)
    {
        _selectedName.Text = _selectedUser?.DisplayName ?? T("common.selectUser");
        _selectedId.Text = _selectedUser?.Id ?? "";
        _overlayUrl.Text = _selectedUser is null || _server is null ? "" : $"{OverlayBaseUrl}/overlay/user/{Uri.EscapeDataString(_selectedUser.Id)}";
        SyncSelectedItems();
        if (updatePreview)
        {
            UpdatePreview();
        }
        RenderStateRows();
    }

    private void SyncSelectedItems()
    {
        _isSyncingUsers = true;
        try
        {
            var userId = _selectedUser?.Id;
            var current = userId is null ? null : _users.FirstOrDefault(user => user.Id == userId);
            var known = userId is null ? null : _knownUsers.FirstOrDefault(user => user.Id == userId);
            if (!ReferenceEquals(_usersList.SelectedItem, current))
            {
                _usersList.SelectedItem = current;
            }

            if (!ReferenceEquals(_knownUsersList.SelectedItem, known))
            {
                _knownUsersList.SelectedItem = known;
            }
        }
        finally
        {
            _isSyncingUsers = false;
        }
    }

    private void UpdatePreview(bool force = false)
    {
        _previewPlaceholder.Visibility = string.IsNullOrWhiteSpace(_overlayUrl.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (string.IsNullOrWhiteSpace(_overlayUrl.Text))
        {
            _previewUserId = null;
            if (_preview.CoreWebView2 is not null)
            {
                _preview.NavigateToString("<!doctype html><html><body style=\"margin:0;background:transparent\"></body></html>");
            }
            return;
        }

        if (!force && _selectedUser?.Id == _previewUserId)
        {
            return;
        }

        _previewUserId = _selectedUser?.Id;
        var separator = _overlayUrl.Text.Contains('?') ? "&" : "?";
        _previewNavigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _preview.Source = new Uri($"{_overlayUrl.Text}{separator}preview=1");
    }

    private async Task EmulatePreviewStateAsync(AnimationState state)
    {
        if (_selectedUser is null)
        {
            return;
        }

        UpdatePreview();
        var user = _selectedUser with
        {
            State = state,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
        };
        var json = JsonSerializer.Serialize(user, Json.Options);
        try
        {
            if (_preview.CoreWebView2 is null)
            {
                await _preview.EnsureCoreWebView2Async();
            }

            if (_previewNavigation is not null)
            {
                await _previewNavigation.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }

            var core = _preview.CoreWebView2;
            if (core is not null)
            {
                await core.ExecuteScriptAsync($"window.__setPreviewUser && window.__setPreviewUser({json});");
            }
        }
        catch (Exception error)
        {
            AddLog($"Preview emulation failed: {error.Message}");
        }
    }

    private void RenderStateRows()
    {
        _stateRows.Children.Clear();
        _durationInputs.Clear();
        foreach (var state in Enum.GetValues<AnimationState>())
        {
            var settings = _selectedUser is not null && _config.Overlays.TryGetValue(_selectedUser.Id, out var overlay) ? overlay : null;
            OverlayAnimation? animation = null;
            settings?.Animations.TryGetValue(state, out animation);
            var duration = new TextBox
            {
                Text = (animation?.FrameDurationMs ?? 120).ToString(),
                Width = 72,
                Height = 34,
                Padding = new Thickness(8, 4, 8, 4),
                BorderBrush = Brush("#3A3A3D"),
                Background = Brush("#151518"),
                Foreground = Brush("#F4F4F5"),
                HorizontalContentAlignment = HorizontalAlignment.Right
            };
            duration.Tag = state;
            duration.LostFocus += DurationInput_Commit;
            duration.KeyDown += DurationInput_KeyDown;
            _durationInputs[state] = duration;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(122) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = Brush("#202024"),
                BorderBrush = Brush("#34343A"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 5, 10, 5),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = StateLabel(state),
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 12,
                    TextWrapping = TextWrapping.NoWrap
                }
            });

            var assetPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            assetPanel.Children.Add(new TextBlock
            {
                Text = animation?.Frames.Count > 0 ? T("asset.frames", animation.Frames.Count, animation.Frames[0].FileName) : T("asset.none"),
                Foreground = Brush("#A1A1AA"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            assetPanel.Children.Add(new TextBlock
            {
                Text = T("asset.formats"),
                Foreground = Brush("#5F636D"),
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(assetPanel, 3);
            row.Children.Add(assetPanel);

            var durationPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Width = 112 };
            durationPanel.Children.Add(duration);
            durationPanel.Children.Add(new TextBlock { Text = T("unit.ms"), Foreground = Brush("#71717A"), Margin = new Thickness(8, 0, 0, 0), MinWidth = 22, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
            Grid.SetColumn(durationPanel, 2);
            row.Children.Add(durationPanel);

            var upload = IconButton("\uE8E5");
            upload.Tag = state;
            upload.Click += ImportAsset_Click;
            upload.Width = 44;
            upload.Height = 36;
            upload.MinHeight = 36;
            upload.ToolTip = T("asset.pickFiles");
            Grid.SetColumn(upload, 1);
            row.Children.Add(upload);
            _stateRows.Children.Add(new Border { CornerRadius = new CornerRadius(10), BorderBrush = Brush("#2A2A2D"), BorderThickness = new Thickness(1), Background = Brush("#121214"), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 10), Child = row });
        }
    }
}
