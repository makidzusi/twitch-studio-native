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

public sealed partial class MainWindow : FluentWindow
{
    private readonly AppStorage _storage = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ObservableCollection<string> _logs = [];
    private readonly ObservableCollection<VoiceUser> _users = [];
    private readonly ObservableCollection<VoiceUser> _knownUsers = [];
    private readonly ObservableCollection<MicrophoneDevice> _microphoneDevices = [];
    private readonly ListBox _usersList = new();
    private readonly ListBox _knownUsersList = new();
    private readonly ListBox _logList = new();
    private readonly StackPanel _stateRows = new();
    private readonly TextBlock _discordStatus = new();
    private readonly Ellipse _discordDot = new();
    private readonly Border _discordBadge = new();
    private readonly TextBlock _discordMessage = new();
    private readonly TextBlock _headerTitle = new();
    private readonly ComboBox _languageSelect = new();
    private readonly Button _reconnectDiscord = Button(T("discord.connect"));
    private readonly TextBlock _selectedName = new();
    private readonly TextBlock _selectedId = new();
    private readonly TextBox _overlayUrl = new();
    private readonly WebView2 _preview = new();
    private readonly Border _previewFrame = new();
    private readonly TextBlock _previewPlaceholder = new();
    private readonly Dictionary<AnimationState, TextBox> _durationInputs = [];
    private readonly ColumnDefinition _navColumn = new() { Width = new GridLength(220) };
    private readonly Grid _contentHost = new();
    private readonly Button _hamburger = IconButton("\uE700");
    private readonly Button _dashboardNav = NavButton("\uE80F", T("nav.dashboard"));
    private readonly Button _membersNav = NavButton("\uE716", T("nav.members"));
    private readonly Button _commandsNav = NavButton("\uE720", T("nav.commands"));
    private readonly Button _settingsNav = NavButton("\uE713", T("nav.settings"));
    private readonly Button _logNav = NavButton("\uE8A5", T("nav.log"));
    private readonly Dictionary<Button, (string Icon, string Text)> _navItems = [];
    private readonly TextBlock _navTitle = new();
    private UIElement? _dashboardPage;
    private UIElement? _membersPage;
    private UIElement? _commandsPage;
    private UIElement? _settingsPage;
    private UIElement? _logPage;
    private bool _isNavCollapsed;
    private string _activePage = "dashboard";
    private AppConfig _config = new();
    private DiscordVoiceProvider? _voiceProvider;
    private OverlayServer? _server;
    private VoiceUser? _selectedUser;
    private string? _previewUserId;
    private bool _isSyncingUsers;
    private bool _usersListConfigured;
    private bool _knownUsersListConfigured;
    private bool _previewConfigured;
    private bool _reconnectDiscordConfigured;
    private bool _languageSelectConfigured;
    private bool _settingsControlsConfigured;
    private TaskCompletionSource<bool>? _previewNavigation;
    private readonly LocalMicrophoneVad _localVad = new();
    private readonly VoiceCommandRecognizer _voiceCommands = new();
    private readonly ComboBox _vadModeSelect = new();
    private readonly ComboBox _microphoneSelect = new();
    private readonly TextBox _vadSpeakingThreshold = new();
    private readonly TextBox _vadSilenceThreshold = new();
    private readonly Slider _vadSpeakingSlider = new();
    private readonly Slider _vadSilenceSlider = new();
    private readonly TextBox _vadAttackMs = new();
    private readonly TextBox _vadReleaseMs = new();
    private readonly ProgressBar _vadLevel = new();
    private readonly TextBlock _vadStatus = new();
    private readonly TextBlock _dashboardVadStatus = new();
    private readonly TextBlock _dashboardHotKey = new();
    private readonly Button _dashboardLocalMicMute = Button(T("settings.muteMic"));
    private readonly Button _localMicMute = Button(T("settings.muteMic"));
    private readonly Button _captureMuteHotKey = Button(T("voice.assignHotkey"));
    private readonly TextBox _discordClientId = new();
    private readonly TextBox _overlayPort = new();
    private readonly TextBlock _overlayServerStatus = new();
    private readonly ObservableCollection<VoiceCommandRule> _voiceCommandRules = [];
    private readonly ListBox _voiceCommandRulesList = new();
    private readonly ComboBox _voiceCommandMicrophoneSelect = new();
    private readonly TextBox _voiceCommandModelPath = new();
    private readonly Button _voiceCommandOpenModels = Button(T("voice.openModels"));
    private readonly CheckBox _voiceCommandUseGrammar = new();
    private readonly TextBox _voiceCommandPhrase = new();
    private readonly TextBox _voiceCommandActionName = new();
    private readonly TextBox _voiceCommandHoldMs = new();
    private readonly TextBox _voiceCommandFrameDurationMs = new();
    private readonly Button _voiceCommandToggle = Button(T("voice.enable"));
    private readonly Button _voiceCommandAdd = Button(T("voice.add"));
    private readonly Button _voiceCommandSave = Button(T("voice.save"));
    private readonly Button _voiceCommandRun = Button(T("voice.run"));
    private readonly Button _voiceCommandCaptureHotKey = Button(T("voice.assignHotkey"));
    private readonly Button _voiceCommandUpload = Button(T("voice.upload"));
    private readonly Button _voiceCommandDelete = Button(T("voice.delete"));
    private readonly TextBlock _voiceCommandStatus = new();
    private readonly TextBlock _voiceCommandAnimationInfo = new();
    private readonly TextBlock _voiceCommandDebug = new();
    private readonly ObservableCollection<string> _voiceCommandEvents = [];
    private readonly ListBox _voiceCommandEventsList = new();
    private VoiceCommandDebugEvent? _lastVoiceCommandDebug;
    private VoiceCommandDebugEvent? _lastVoiceCommandAudioDebug;
    private VoiceCommandDebugEvent? _lastVoiceCommandStartupDebug;
    private DispatcherTimer? _voiceCommandResetTimer;
    private readonly SemaphoreSlim _voiceCommandRestartLock = new(1, 1);
    private int _voiceCommandRestartVersion;
    private DateTimeOffset _voiceCommandActionLockedUntil = DateTimeOffset.MinValue;
    private bool _authenticatedUserMutedInDiscord;
    private bool _localMicSpeaking;
    private bool _isCapturingMuteHotKey;
    private bool _isCapturingVoiceCommandHotKey;
    private bool _isBuildingVoiceCommandsPage;
    private bool _isRestoringFromTray;
    private Forms.NotifyIcon? _trayIcon;
    private HwndSource? _hwndSource;
    private const int LocalMuteHotKeyId = 3847;
    private const int VoiceCommandHotKeyBaseId = 3900;

    static MainWindow()
    {
        LocalizationManager.Load();
    }

    public MainWindow()
    {
        Title = T("app.title");
        Width = 1180;
        Height = 760;
        MinWidth = 980;
        MinHeight = 620;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        Background = Brush("#0F0F10");
        Foreground = Brush("#F5F5F5");
        Content = BuildLayout();
        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
        StateChanged += MainWindow_StateChanged;
        Closing += MainWindow_Closing;
    }

    private static string T(string key, params object?[] args) => LocalizationManager.Text(key, args);

    private Grid BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        root.Children.Add(new TitleBar
        {
            Title = "Twitch Studio Native",
            Height = 48,
            ShowMinimize = true,
            ShowMaximize = true,
            ShowClose = true,
            CanMaximize = true,
            ButtonsForeground = Brush("#F5F5F5"),
            ButtonsBackground = Brushes.Transparent,
            Background = Brushes.Transparent
        });

        var header = new Grid();
        header.Margin = new Thickness(20, 8, 20, 0);
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel();
        _headerTitle.Text = T("app.header");
        _headerTitle.FontSize = 24;
        _headerTitle.FontWeight = FontWeights.SemiBold;
        title.Children.Add(_headerTitle);
        header.Children.Add(title);
        var headerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top
        };
        ConfigureLanguageSelect();
        headerActions.Children.Add(_languageSelect);
        _discordBadge.Child = BuildDiscordStatus();
        _discordBadge.Margin = new Thickness(10, 0, 0, 0);
        headerActions.Children.Add(_discordBadge);
        Grid.SetColumn(headerActions, 1);
        header.Children.Add(headerActions);
        var headerBorder = new Border { BorderBrush = Brush("#27272A"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 16), Child = header };
        Grid.SetRow(headerBorder, 1);
        root.Children.Add(headerBorder);

        var body = new Grid { Margin = new Thickness(20, 18, 20, 20) };
        body.ColumnDefinitions.Add(_navColumn);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 2);
        body.Children.Add(BuildNavigation());
        Grid.SetColumn(_contentHost, 2);
        body.Children.Add(_contentHost);
        root.Children.Add(body);
        UpdateDiscordStatus(ConnectionStatus.disconnected, T("discord.status.disconnected.cap"));
        ShowPage("dashboard");
        return root;
    }

    private void ConfigureLanguageSelect()
    {
        _languageSelect.ItemsSource = LocalizationManager.Languages;
        _languageSelect.DisplayMemberPath = nameof(LanguageOption.Name);
        _languageSelect.SelectedValuePath = nameof(LanguageOption.Code);
        _languageSelect.SelectedValue = LocalizationManager.CurrentCode;
        _languageSelect.MinWidth = 130;
        _languageSelect.MinHeight = 34;
        _languageSelect.Margin = new Thickness(0);
        if (!_languageSelectConfigured)
        {
            _languageSelect.SelectionChanged += LanguageSelect_SelectionChanged;
            _languageSelectConfigured = true;
        }
    }

    private async void LanguageSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_languageSelect.SelectedValue is not string languageCode || languageCode == LocalizationManager.CurrentCode)
        {
            return;
        }

        LocalizationManager.SetLanguage(languageCode);
        _config = _config with { Language = languageCode };
        App.CurrentConfig = _config;
        await _storage.WriteConfigAsync(_config, _shutdown.Token);
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        Title = T("app.title");
        _headerTitle.Text = T("app.header");
        _navTitle.Text = T("nav.menu");
        _navItems[_dashboardNav] = ("\uE80F", T("nav.dashboard"));
        _navItems[_membersNav] = ("\uE716", T("nav.members"));
        _navItems[_commandsNav] = ("\uE720", T("nav.commands"));
        _navItems[_settingsNav] = ("\uE713", T("nav.settings"));
        _navItems[_logNav] = ("\uE8A5", T("nav.log"));
        RenderNavButton(_dashboardNav, "\uE80F", T("nav.dashboard"), _isNavCollapsed);
        RenderNavButton(_membersNav, "\uE716", T("nav.members"), _isNavCollapsed);
        RenderNavButton(_commandsNav, "\uE720", T("nav.commands"), _isNavCollapsed);
        RenderNavButton(_settingsNav, "\uE713", T("nav.settings"), _isNavCollapsed);
        RenderNavButton(_logNav, "\uE8A5", T("nav.log"), _isNavCollapsed);
        _reconnectDiscord.Content = _voiceProvider?.Status.Status == ConnectionStatus.connected
            ? T("discord.reconnect")
            : T("discord.connect");
        UpdateDiscordStatus(_voiceProvider?.Status.Status ?? ConnectionStatus.disconnected, StatusLabel(_voiceProvider?.Status.Status ?? ConnectionStatus.disconnected));
        UpdateDiscordMessageForLanguage();
        ResetLocalizedTemplates();
        RenderSelectedUser(updatePreview: false);
        UpdateVadStatus();
        UpdateVoiceCommandStatus();
        ShowPage(_activePage);
    }

    private void UpdateDiscordMessageForLanguage()
    {
        if (_voiceProvider?.Status.Status is ConnectionStatus.connected or ConnectionStatus.connecting or ConnectionStatus.error)
        {
            return;
        }

        _discordMessage.Text = _server is null
            ? T("discord.overlayUnavailable", _config.OverlayPort)
            : T("discord.manual");
    }

    private void ResetLocalizedTemplates()
    {
        _usersList.ItemTemplate = null;
        _knownUsersList.ItemTemplate = null;
        _voiceCommandRulesList.ItemTemplate = null;
        _usersList.Items.Refresh();
        _knownUsersList.Items.Refresh();
        _voiceCommandRulesList.Items.Refresh();
    }

    private Border BuildNavigation()
    {
        var stack = new StackPanel();
        var top = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _hamburger.Width = 52;
        _hamburger.MinWidth = 52;
        _hamburger.Height = 44;
        _hamburger.Padding = new Thickness(0);
        _hamburger.HorizontalAlignment = HorizontalAlignment.Center;
        _hamburger.Click += (_, _) => ToggleNavigation();
        top.Children.Add(_hamburger);
        _navTitle.Text = T("nav.menu");
        _navTitle.FontSize = 18;
        _navTitle.FontWeight = FontWeights.SemiBold;
        _navTitle.VerticalAlignment = VerticalAlignment.Center;
        _navTitle.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(_navTitle, 1);
        top.Children.Add(_navTitle);
        stack.Children.Add(top);

        _dashboardNav.Click += (_, _) => ShowPage("dashboard");
        _membersNav.Click += (_, _) => ShowPage("members");
        _commandsNav.Click += (_, _) => ShowPage("commands");
        _settingsNav.Click += (_, _) => ShowPage("settings");
        _logNav.Click += (_, _) => ShowPage("log");
        _navItems[_dashboardNav] = ("\uE80F", T("nav.dashboard"));
        _navItems[_membersNav] = ("\uE716", T("nav.members"));
        _navItems[_commandsNav] = ("\uE720", T("nav.commands"));
        _navItems[_settingsNav] = ("\uE713", T("nav.settings"));
        _navItems[_logNav] = ("\uE8A5", T("nav.log"));
        _dashboardNav.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _membersNav.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _commandsNav.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _settingsNav.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _logNav.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _dashboardNav.Margin = new Thickness(0, 0, 0, 8);
        _membersNav.Margin = new Thickness(0, 0, 0, 8);
        _commandsNav.Margin = new Thickness(0, 0, 0, 8);
        _settingsNav.Margin = new Thickness(0, 0, 0, 8);
        _logNav.Margin = new Thickness(0, 0, 0, 8);
        _logNav.Visibility = _config.DebugMode ? Visibility.Visible : Visibility.Collapsed;
        stack.Children.Add(_dashboardNav);
        stack.Children.Add(_membersNav);
        stack.Children.Add(_commandsNav);
        stack.Children.Add(_settingsNav);
        stack.Children.Add(_logNav);
        return Panel(stack);
    }

    private UIElement BuildDashboardPage()
    {
        var grid = new Grid { MinWidth = 900 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        grid.Children.Add(BuildMembersPanel());
        var center = BuildCenterPanel();
        Grid.SetColumn(center, 2);
        grid.Children.Add(center);
        var right = BuildRightPanel();
        Grid.SetColumn(right, 4);
        grid.Children.Add(right);
        return new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    private void ShowPage(string page)
    {
        _activePage = page;
        if (page == "log" && !_config.DebugMode)
        {
            page = "settings";
            _activePage = page;
        }

        _contentHost.Children.Clear();
        if (page == "log")
        {
            _logPage ??= BuildLogPage();
            _contentHost.Children.Add(_logPage);
        }
        else if (page == "members")
        {
            _membersPage = BuildMembersManagementPage();
            _contentHost.Children.Add(_membersPage);
        }
        else if (page == "commands")
        {
            _commandsPage = BuildVoiceCommandsPage();
            _contentHost.Children.Add(_commandsPage);
        }
        else if (page == "settings")
        {
            _settingsPage = BuildSettingsPage();
            _contentHost.Children.Add(_settingsPage);
        }
        else
        {
            _dashboardPage = BuildDashboardPage();
            _contentHost.Children.Add(_dashboardPage);
        }
        ApplyNavigationState();
    }

    private Grid BuildMembersManagementPage()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        grid.Children.Add(BuildKnownMembersPanel());
        var center = BuildCenterPanel();
        Grid.SetColumn(center, 2);
        grid.Children.Add(center);
        var right = BuildRightPanel();
        Grid.SetColumn(right, 4);
        grid.Children.Add(right);
        return grid;
    }

}
