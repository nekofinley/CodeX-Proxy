using System.Diagnostics;
using CodeXProxy.App;
using CodeXProxy.Apps;
using CodeXProxy.Configuration;
using CodeXProxy.Logging;
using CodeXProxy.Proxy;
using CodeXProxy.Security;
using CodeXProxy.UI;
using CodeXProxy.Windows;

namespace CodeXProxy;

internal sealed class MainForm : Form
{
    private readonly SystemProxyManager _systemProxy;
    private readonly ProxyRecoveryResult _recovery;
    private readonly ConfigStore _configStore = new();
    private readonly CredentialStore _credentialStore = new();
    private readonly ProxyLogStore _log = new();
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly List<Control> _editableControls = [];
    private readonly Icon _stoppedIcon;
    private readonly Icon _runningIcon;

    private AppConfig _config = new();
    private ProxyRuntime? _runtime;
    private bool _isBusy;
    private bool _isExiting;
    private bool _allowClose;
    private bool _trayHintShown;

    private NotifyIcon _trayIcon = null!;
    private ContextMenuStrip _trayMenu = null!;
    private ToolStripMenuItem _trayStatusItem = null!;
    private ToolStripMenuItem _trayToggleItem = null!;
    private System.Windows.Forms.Timer _connectionTimer = null!;

    private Label _statusTitle = null!;
    private Label _statusDetail = null!;
    private Label _proxyMetric = null!;
    private Label _pacMetric = null!;
    private Label _upstreamMetric = null!;
    private Label _connectionsMetric = null!;
    private FlatButton _primaryButton = null!;
    private FlatButton _saveButton = null!;
    private TabControl _tabs = null!;
    private TextBox _upstreamText = null!;
    private TextBox _usernameText = null!;
    private TextBox _passwordText = null!;
    private TextBox _testTargetText = null!;
    private TextBox _rulesText = null!;
    private TextBox _chatGptPathText = null!;
    private TextBox _chatGptArgsText = null!;
    private TextBox _codexPathText = null!;
    private TextBox _codexArgsText = null!;
    private FlatButton _testButton = null!;
    private FlatButton _chatGptBrowseButton = null!;
    private FlatButton _codexBrowseButton = null!;
    private Label _testResultLabel = null!;
    private Label _ruleCountLabel = null!;
    private DataGridView _logGrid = null!;

    public MainForm(SystemProxyManager systemProxy, ProxyRecoveryResult recovery)
    {
        _systemProxy = systemProxy;
        _recovery = recovery;
        _stoppedIcon = AppIcons.Create(running: false);
        _runningIcon = AppIcons.Create(running: true);
        InitializeComponent();
        LoadConfigurationAndLogs();
        _log.EntryAdded += OnLogEntryAdded;
        UpdateStoppedUi();
    }

    public void ShowFromTray()
    {
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Show();
        Activate();
        BringToFront();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_allowClose)
        {
            base.OnFormClosing(e);
            return;
        }

        if (e.CloseReason == CloseReason.UserClosing && _config.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _trayIcon.BalloonTipTitle = "CodeX Proxy 仍在后台运行";
                _trayIcon.BalloonTipText = "使用托盘图标重新打开，或右键退出。";
                _trayIcon.BalloonTipIcon = ToolTipIcon.Info;
                _trayIcon.ShowBalloonTip(2500);
            }

            return;
        }

        e.Cancel = true;
        BeginInvoke(async () => await ExitApplicationAsync());
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _log.EntryAdded -= OnLogEntryAdded;
            _connectionTimer?.Dispose();
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }

            _trayMenu?.Dispose();
            _transition.Dispose();
            _stoppedIcon.Dispose();
            _runningIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        SuspendLayout();

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.Canvas;
        ClientSize = new Size(1080, 780);
        Font = Theme.Body(9.5f);
        ForeColor = Theme.Ink;
        Icon = _stoppedIcon;
        MinimumSize = new Size(940, 700);
        StartPosition = FormStartPosition.CenterScreen;
        Text = "CodeX Proxy";
        DoubleBuffered = true;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Canvas,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(24, 18, 24, 20)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 124));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        root.Controls.Add(CreateHeader(), 0, 0);
        root.Controls.Add(CreateStatusCard(), 0, 1);
        root.Controls.Add(CreateTabs(), 0, 2);
        root.Controls.Add(CreateLogCard(), 0, 3);
        Controls.Add(root);

        _connectionTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _connectionTimer.Tick += (_, _) =>
        {
            _connectionsMetric.Text = _runtime?.ActiveConnections.ToString() ?? "0";
        };

        CreateTrayIcon();
        ResumeLayout(performLayout: true);
    }

    private Control CreateHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Canvas,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));

        var titles = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Canvas,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty
        };
        titles.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        titles.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        titles.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var eyebrow = new Label
        {
            AutoSize = true,
            Font = Theme.Body(8.5f, FontStyle.Bold),
            ForeColor = Theme.Accent,
            Text = "CODEX PROXY  /  WINDOWS",
            Margin = new Padding(1, 0, 0, 0)
        };
        var title = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Font = Theme.Display(22f),
            ForeColor = Theme.Ink,
            Text = "只给需要的流量，开一条通道",
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        };
        var subtitle = new Label
        {
            AutoSize = true,
            Font = Theme.Body(9f),
            ForeColor = Theme.Muted,
            Text = "ChatGPT 与 Codex 的选择性本地代理 · 不装证书 · 不改应用文件",
            Margin = new Padding(1, 2, 0, 0)
        };
        titles.Controls.Add(eyebrow, 0, 0);
        titles.Controls.Add(title, 0, 1);
        titles.Controls.Add(subtitle, 0, 2);

        var facts = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Theme.Canvas,
            Padding = new Padding(0, 17, 0, 0),
            Margin = Padding.Empty
        };
        facts.Controls.Add(new Label
        {
            AutoSize = true,
            Font = Theme.Body(8.5f, FontStyle.Bold),
            ForeColor = Theme.Running,
            Text = "●  PAC 精准分流",
            Margin = new Padding(0, 0, 0, 8)
        });
        facts.Controls.Add(new Label
        {
            AutoSize = true,
            Font = Theme.Body(8.5f),
            ForeColor = Theme.Muted,
            Text = "当前用户 · 无需管理员权限"
        });

        header.Controls.Add(titles, 0, 0);
        header.Controls.Add(facts, 1, 0);
        return header;
    }

    private Control CreateStatusCard()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 12, 0, 0),
            Padding = new Padding(18, 14, 18, 14)
        };
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));

        var status = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty
        };
        status.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        status.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _statusTitle = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Font = Theme.Display(13f),
            ForeColor = Theme.Muted,
            Text = "●  已停止",
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        };
        _statusDetail = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Font = Theme.Body(8.5f),
            ForeColor = Theme.Muted,
            Text = "开启后仅目标域名经过本地代理。",
            TextAlign = ContentAlignment.TopLeft,
            Margin = Padding.Empty
        };
        status.Controls.Add(_statusTitle, 0, 0);
        status.Controls.Add(_statusDetail, 0, 1);

        var metrics = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(12, 0, 12, 0)
        };
        for (var i = 0; i < 4; i++)
        {
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        }

        _proxyMetric = AddMetric(metrics, 0, "本地代理");
        _pacMetric = AddMetric(metrics, 1, "PAC 服务");
        _upstreamMetric = AddMetric(metrics, 2, "上游代理");
        _connectionsMetric = AddMetric(metrics, 3, "活动连接");

        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty
        };
        actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        _primaryButton = new FlatButton
        {
            Dock = DockStyle.Fill,
            Text = "开启代理",
            NormalColor = Theme.Accent,
            HoverColor = Theme.AccentHover,
            PressedColor = Color.FromArgb(166, 60, 31),
            Margin = new Padding(0, 0, 0, 8)
        };
        _primaryButton.Click += async (_, _) => await ToggleProxyAsync();
        _saveButton = new FlatButton
        {
            Dock = DockStyle.Fill,
            Font = Theme.Body(8.5f, FontStyle.Bold),
            Text = "保存配置",
            NormalColor = Theme.SurfaceMuted,
            HoverColor = Color.FromArgb(228, 222, 210),
            PressedColor = Color.FromArgb(218, 211, 198),
            TextColor = Theme.Ink,
            Margin = Padding.Empty
        };
        _saveButton.Click += (_, _) => SaveConfiguration(showError: true, showConfirmation: true);
        actions.Controls.Add(_primaryButton, 0, 0);
        actions.Controls.Add(_saveButton, 0, 1);

        grid.Controls.Add(status, 0, 0);
        grid.Controls.Add(metrics, 1, 0);
        grid.Controls.Add(actions, 2, 0);
        card.Controls.Add(grid);
        return card;
    }

    private static Label AddMetric(TableLayoutPanel parent, int column, string caption)
    {
        var cell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(6, 8, 6, 0)
        };
        cell.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        cell.RowStyles.Add(new RowStyle(SizeType.Percent, 40));

        var value = new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Font = Theme.Body(7.5f, FontStyle.Bold),
            ForeColor = Theme.Ink,
            Text = "—",
            TextAlign = ContentAlignment.BottomLeft,
            Margin = Padding.Empty
        };
        var label = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Font = Theme.Body(8f),
            ForeColor = Theme.Muted,
            Text = caption,
            TextAlign = ContentAlignment.TopLeft,
            Margin = Padding.Empty
        };
        cell.Controls.Add(value, 0, 0);
        cell.Controls.Add(label, 0, 1);
        parent.Controls.Add(cell, column, 0);
        return value;
    }

    private Control CreateTabs()
    {
        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = Theme.Body(9.5f, FontStyle.Bold),
            ItemSize = new Size(112, 32),
            SizeMode = TabSizeMode.Fixed,
            DrawMode = TabDrawMode.OwnerDrawFixed,
            Appearance = TabAppearance.Normal,
            Padding = new Point(18, 6),
            Margin = new Padding(0, 12, 0, 0)
        };
        _tabs.DrawItem += DrawTab;

        var upstreamPage = CreateTabPage("上游代理");
        upstreamPage.Controls.Add(CreateUpstreamPanel());
        var rulesPage = CreateTabPage("分流规则");
        rulesPage.Controls.Add(CreateRulesPanel());
        var appsPage = CreateTabPage("目标应用");
        appsPage.Controls.Add(CreateAppsPanel());
        _tabs.TabPages.Add(upstreamPage);
        _tabs.TabPages.Add(rulesPage);
        _tabs.TabPages.Add(appsPage);
        return _tabs;
    }

    private static TabPage CreateTabPage(string text)
    {
        return new TabPage
        {
            Text = text,
            BackColor = Theme.Canvas,
            Padding = new Padding(8, 10, 8, 8),
            AutoScroll = false,
            UseVisualStyleBackColor = false
        };
    }

    private void DrawTab(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0)
        {
            return;
        }

        var selected = e.Index == _tabs.SelectedIndex;
        var bounds = e.Bounds;
        using var background = new SolidBrush(selected ? Theme.Surface : Theme.Canvas);
        e.Graphics.FillRectangle(background, bounds);
        if (selected)
        {
            using var accent = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(accent, new Rectangle(bounds.Left + 14, bounds.Bottom - 3, bounds.Width - 28, 3));
        }

        using var tabFont = Theme.Body(9.5f, FontStyle.Bold);
        TextRenderer.DrawText(
            e.Graphics,
            _tabs.TabPages[e.Index].Text,
            tabFont,
            bounds,
            selected ? Theme.Ink : Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private Control CreateUpstreamPanel()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 6, 20, 6)
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 8,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var addressLabel = Theme.SectionLabel("上游代理地址");
        layout.Controls.Add(addressLabel, 0, 0);
        layout.SetColumnSpan(addressLabel, 2);

        _upstreamText = CreateTextBox();
        _upstreamText.PlaceholderText = "http://127.0.0.1:7890 或 socks5://127.0.0.1:1080";
        layout.Controls.Add(_upstreamText, 0, 1);
        layout.SetColumnSpan(_upstreamText, 2);

        layout.Controls.Add(Theme.SectionLabel("用户名（可选）"), 0, 2);
        layout.Controls.Add(Theme.SectionLabel("密码（可选）"), 1, 2);
        _usernameText = CreateTextBox();
        _usernameText.PlaceholderText = "可选";
        _usernameText.Margin = new Padding(0, 0, 8, 0);
        layout.Controls.Add(_usernameText, 0, 3);
        _passwordText = CreateTextBox(usePasswordChar: true);
        _passwordText.PlaceholderText = "可选，留空保留已保存密码";
        _passwordText.Margin = new Padding(8, 0, 0, 0);
        layout.Controls.Add(_passwordText, 1, 3);

        var credentialHint = new Label
        {
            AutoSize = true,
            Font = Theme.Body(8.5f),
            ForeColor = Theme.Muted,
            Text = "密码仅保存在 Windows 凭据管理器；支持 SOCKS5 域名解析，HTTPS 上游暂不支持。",
            Margin = new Padding(0, 2, 0, 0)
        };
        layout.Controls.Add(credentialHint, 0, 4);
        layout.SetColumnSpan(credentialHint, 2);

        var testTargetLabel = Theme.SectionLabel("测试目标");
        layout.Controls.Add(testTargetLabel, 0, 5);
        _testTargetText = CreateTextBox();
        _testTargetText.PlaceholderText = "chatgpt.com:443";
        _testTargetText.Margin = new Padding(0, 0, 8, 0);
        layout.Controls.Add(_testTargetText, 0, 6);
        _testButton = new FlatButton
        {
            Text = "测试上游代理",
            NormalColor = Theme.Ink,
            HoverColor = Color.FromArgb(48, 64, 63),
            PressedColor = Color.FromArgb(15, 29, 29),
            Dock = DockStyle.Fill,
            Margin = new Padding(8, 0, 0, 0)
        };
        _testButton.Click += async (_, _) => await TestUpstreamAsync();
        layout.Controls.Add(_testButton, 1, 6);

        _testResultLabel = new Label
        {
            AutoEllipsis = true,
            AutoSize = false,
            Dock = DockStyle.Fill,
            Font = Theme.Body(8.5f),
            ForeColor = Theme.Muted,
            Text = "测试会建立真实代理隧道，但不发送应用数据。",
            TextAlign = ContentAlignment.MiddleRight,
            Margin = new Padding(0, 2, 0, 0)
        };
        layout.Controls.Add(_testResultLabel, 1, 5);

        foreach (var control in new Control[]
                 {
                     _upstreamText, _usernameText, _passwordText, _testTargetText, _testButton
                 })
        {
            _editableControls.Add(control);
        }

        card.Controls.Add(layout);
        return card;
    }

    private Control CreateRulesPanel()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 16, 20, 16)
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));

        layout.Controls.Add(Theme.SectionLabel("目标域名规则（每行一条）"), 0, 0);
        var hint = Theme.HintLabel("普通域名同时匹配主域和子域；也支持 *.example.com 与 api-*.example.com。以 # 开头的行会被忽略。");
        layout.Controls.Add(hint, 0, 1);

        _rulesText = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Surface,
            ForeColor = Theme.Ink,
            Font = Theme.Mono(9.5f),
            Margin = Padding.Empty
        };
        _rulesText.TextChanged += (_, _) => UpdateRuleCount();
        layout.Controls.Add(_rulesText, 0, 2);

        _ruleCountLabel = new Label
        {
            AutoSize = true,
            Font = Theme.Body(8.5f),
            ForeColor = Theme.Muted,
            Text = "0 条规则",
            Margin = new Padding(0, 5, 0, 0)
        };
        layout.Controls.Add(_ruleCountLabel, 0, 3);

        _editableControls.Add(_rulesText);
        card.Controls.Add(layout);
        return card;
    }

    private Control CreateAppsPanel()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var chatGptCard = CreateAppCard(
            "ChatGPT 桌面端",
            "从工具启动时，进程及子进程会继承本地代理环境变量。",
            "chatgpt",
            out _chatGptPathText,
            out _chatGptArgsText,
            out _chatGptBrowseButton,
            out var launchChatGpt);
        chatGptCard.Margin = new Padding(0, 0, 6, 0);
        var codexCard = CreateAppCard(
            "Codex / 终端",
            "推荐填写 wt.exe 并在参数中写 codex；也可填写 codex.exe 或 .lnk。",
            "codex",
            out _codexPathText,
            out _codexArgsText,
            out _codexBrowseButton,
            out var launchCodex);
        codexCard.Margin = new Padding(6, 0, 0, 0);

        launchChatGpt.Click += async (_, _) => await LaunchTargetAsync(_config.ChatGptApp);
        launchCodex.Click += async (_, _) => await LaunchTargetAsync(_config.CodexApp);
        _chatGptBrowseButton.Click += (_, _) => BrowseApplication(_chatGptPathText);
        _codexBrowseButton.Click += (_, _) => BrowseApplication(_codexPathText);

        layout.Controls.Add(chatGptCard, 0, 0);
        layout.Controls.Add(codexCard, 1, 0);
        return layout;
    }

    private static Control CreateAppCard(
        string title,
        string hint,
        string tag,
        out TextBox pathText,
        out TextBox argsText,
        out FlatButton browseButton,
        out FlatButton launchButton)
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 10, 16, 10)
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 144));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));

        var titleLabel = new Label
        {
            AutoSize = true,
            Font = Theme.Body(10f, FontStyle.Bold),
            ForeColor = Theme.Ink,
            Text = title,
            Margin = new Padding(0, 3, 0, 0)
        };
        layout.Controls.Add(titleLabel, 0, 0);
        launchButton = new FlatButton
        {
            Dock = DockStyle.Fill,
            Text = "启动应用",
            NormalColor = tag == "chatgpt" ? Theme.Accent : Theme.Running,
            HoverColor = tag == "chatgpt" ? Theme.AccentHover : Color.FromArgb(31, 100, 81),
            PressedColor = tag == "chatgpt" ? Color.FromArgb(166, 60, 31) : Color.FromArgb(23, 80, 65),
            Font = Theme.Body(8.5f, FontStyle.Bold),
            Margin = new Padding(8, 0, 0, 0)
        };
        layout.Controls.Add(launchButton, 1, 0);

        var pathLabel = Theme.SectionLabel("程序或快捷方式");
        layout.Controls.Add(pathLabel, 0, 1);
        var pathRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        pathText = CreateTextBox();
        pathText.PlaceholderText = "填写 .exe / .cmd / .bat / .ps1 / .lnk";
        pathText.Margin = new Padding(0, 0, 8, 0);
        browseButton = new FlatButton
        {
            Dock = DockStyle.Fill,
            Text = "浏览",
            NormalColor = Theme.SurfaceMuted,
            HoverColor = Color.FromArgb(228, 222, 210),
            PressedColor = Color.FromArgb(218, 211, 198),
            TextColor = Theme.Ink,
            Font = Theme.Body(8.5f, FontStyle.Bold),
            Margin = Padding.Empty
        };
        pathRow.Controls.Add(pathText, 0, 0);
        pathRow.Controls.Add(browseButton, 1, 0);
        layout.SetColumnSpan(pathRow, 2);
        layout.Controls.Add(pathRow, 0, 2);

        var argsLabel = Theme.SectionLabel("启动参数（可选）");
        layout.Controls.Add(argsLabel, 0, 3);
        argsText = CreateTextBox();
        argsText.PlaceholderText = tag == "codex" ? "例如：. 或 -m gpt-5" : "通常留空";
        argsText.Margin = new Padding(0, 0, 0, 0);
        layout.SetColumnSpan(argsText, 2);
        layout.Controls.Add(argsText, 0, 4);

        var hintLabel = new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Font = Theme.Body(8f),
            ForeColor = Theme.Muted,
            Text = hint,
            TextAlign = ContentAlignment.BottomLeft,
            MaximumSize = new Size(420, 0),
            Margin = Padding.Empty
        };
        layout.Controls.Add(hintLabel, 0, 5);
        layout.SetColumnSpan(hintLabel, 2);

        card.Controls.Add(layout);
        return card;
    }

    private Control CreateLogCard()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 10, 16, 10),
            Margin = new Padding(0, 12, 0, 0)
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        var title = new Label
        {
            AutoSize = true,
            Font = Theme.Body(10f, FontStyle.Bold),
            ForeColor = Theme.Ink,
            Text = "连接日志",
            Margin = new Padding(0, 4, 0, 0)
        };
        header.Controls.Add(title, 0, 0);

        var links = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = new Padding(0, 3, 0, 0)
        };
        var openLogs = new LinkLabel
        {
            AutoSize = true,
            Font = Theme.Body(8.5f),
            LinkColor = Theme.Accent,
            ActiveLinkColor = Theme.AccentHover,
            Text = "打开日志目录",
            Margin = new Padding(14, 0, 0, 0)
        };
        openLogs.LinkClicked += (_, _) => OpenLogDirectory();
        var clearView = new LinkLabel
        {
            AutoSize = true,
            Font = Theme.Body(8.5f),
            LinkColor = Theme.Muted,
            ActiveLinkColor = Theme.Ink,
            Text = "清空视图",
            Margin = new Padding(0, 0, 0, 0)
        };
        clearView.LinkClicked += (_, _) => _logGrid.Rows.Clear();
        links.Controls.Add(openLogs);
        links.Controls.Add(clearView);
        header.Controls.Add(links, 1, 0);

        _logGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Surface,
            BackgroundColor = Theme.Surface,
            ForeColor = Theme.Ink,
            Font = Theme.Body(8.5f),
            Margin = Padding.Empty,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AllowUserToResizeColumns = false,
            RowHeadersVisible = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeight = 28,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            EnableHeadersVisualStyles = false,
            GridColor = Color.FromArgb(233, 228, 218),
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            ScrollBars = ScrollBars.Vertical,
            ShowCellErrors = false,
            ShowRowErrors = false,
            ShowCellToolTips = true,
            RowTemplate = { Height = 23 }
        };
        _logGrid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Surface,
            ForeColor = Theme.Ink,
            SelectionBackColor = Color.FromArgb(232, 238, 233),
            SelectionForeColor = Theme.Ink,
            Font = Theme.Body(8.5f),
            WrapMode = DataGridViewTriState.False,
            Padding = new Padding(4, 0, 4, 0)
        };
        _logGrid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Surface,
            ForeColor = Theme.Muted,
            SelectionBackColor = Theme.Surface,
            SelectionForeColor = Theme.Muted,
            Font = Theme.Body(8f, FontStyle.Bold),
            Alignment = DataGridViewContentAlignment.MiddleLeft,
            Padding = new Padding(4, 0, 4, 0)
        };
        _logGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "时间",
            FillWeight = 9,
            MinimumWidth = 62
        });
        _logGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "级别",
            FillWeight = 7,
            MinimumWidth = 50
        });
        _logGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "主机",
            FillWeight = 20,
            MinimumWidth = 100
        });
        _logGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "命中规则",
            FillWeight = 14,
            MinimumWidth = 80
        });
        _logGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "路由",
            FillWeight = 13,
            MinimumWidth = 72
        });
        _logGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "延迟",
            FillWeight = 7,
            MinimumWidth = 46,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight }
        });
        _logGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "说明",
            FillWeight = 38,
            MinimumWidth = 150
        });
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(_logGrid, 0, 1);
        card.Controls.Add(layout);
        return card;
    }

    private void CreateTrayIcon()
    {
        _trayMenu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            Font = Theme.Body(9f),
            BackColor = Theme.Surface,
            ForeColor = Theme.Ink,
            Padding = new Padding(6)
        };
        _trayStatusItem = new ToolStripMenuItem("已停止")
        {
            Enabled = false,
            Font = Theme.Body(9f, FontStyle.Bold)
        };
        var openItem = new ToolStripMenuItem("打开主界面");
        openItem.Click += (_, _) => ShowFromTray();
        _trayToggleItem = new ToolStripMenuItem("开启代理");
        _trayToggleItem.Click += async (_, _) => await ToggleProxyAsync();
        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += async (_, _) => await ExitApplicationAsync();

        _trayMenu.Items.Add(_trayStatusItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(openItem);
        _trayMenu.Items.Add(_trayToggleItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = _stoppedIcon,
            Text = "CodeX Proxy · 已停止",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    private void LoadConfigurationAndLogs()
    {
        var result = _configStore.Load();
        _config = result.Config;
        if (string.IsNullOrWhiteSpace(_config.CodexApp.Path))
        {
            var discoveredCodex = TargetAppLauncher.DiscoverCodex();
            if (!string.IsNullOrWhiteSpace(discoveredCodex.Path))
            {
                _config.CodexApp.Path = discoveredCodex.Path;
            }
        }

        PopulateConfigurationControls();
        UpdateCredentialPlaceholder();

        if (result.Warning is not null)
        {
            _log.Warning(result.Warning, route: "CONFIG");
        }

        if (_recovery.FileFound)
        {
            if (_recovery.Restored)
            {
                _log.Success(_recovery.Message, route: "RECOVERY");
            }
            else
            {
                _log.Warning(_recovery.Message, route: "RECOVERY");
            }
        }

        _log.Info("日志已启动；不会记录请求头、Cookie、令牌或代理密码。", route: "LOG");
        foreach (var entry in _log.Snapshot())
        {
            AppendLogEntry(entry);
        }
    }

    private void PopulateConfigurationControls()
    {
        _upstreamText.Text = _config.UpstreamProxy;
        _usernameText.Text = _config.UpstreamUsername;
        _passwordText.Clear();
        _testTargetText.Text = _config.TestTarget;
        _rulesText.Text = string.Join(Environment.NewLine, _config.DomainRules);
        _chatGptPathText.Text = _config.ChatGptApp.Path;
        _chatGptArgsText.Text = _config.ChatGptApp.Arguments;
        _codexPathText.Text = _config.CodexApp.Path;
        _codexArgsText.Text = _config.CodexApp.Arguments;
        UpdateRuleCount();
    }

    private void UpdateCredentialPlaceholder()
    {
        ProxyCredential? credential = null;
        try
        {
            credential = _credentialStore.Load();
        }
        catch (Exception ex)
        {
            _log.Warning($"读取 Windows 凭据失败：{ex.Message}", route: "CONFIG");
        }

        _passwordText.PlaceholderText = credential is null
            ? "可选，保存到 Windows 凭据管理器"
            : $"已保存 {credential.Username} 的密码，留空保持不变";
    }

    private void UpdateRuleCount()
    {
        if (_ruleCountLabel is null)
        {
            return;
        }

        var count = _rulesText.Lines
            .Select(DomainRules.NormalizeRule)
            .Where(rule => rule is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        _ruleCountLabel.Text = $"{count} 条有效规则";
    }

    private AppConfig ReadConfigurationFromUi()
    {
        var config = new AppConfig
        {
            SchemaVersion = _config.SchemaVersion,
            UpstreamProxy = _upstreamText.Text,
            UpstreamUsername = _usernameText.Text,
            DomainRules = _rulesText.Lines.ToList(),
            TestTarget = _testTargetText.Text,
            MinimizeToTray = _config.MinimizeToTray,
            ChatGptApp = new TargetAppConfig
            {
                DisplayName = "ChatGPT",
                Path = _chatGptPathText.Text,
                Arguments = _chatGptArgsText.Text,
                WorkingDirectory = _config.ChatGptApp.WorkingDirectory
            },
            CodexApp = new TargetAppConfig
            {
                DisplayName = "Codex",
                Path = _codexPathText.Text,
                Arguments = _codexArgsText.Text,
                WorkingDirectory = _config.CodexApp.WorkingDirectory
            }
        };
        config.Normalize();

        if (config.DomainRules.Count == 0)
        {
            throw new InvalidOperationException("至少需要一条有效的域名规则。");
        }

        return config;
    }

    private bool SaveConfiguration(bool showError, bool showConfirmation)
    {
        if (_runtime is not null || _isBusy)
        {
            return false;
        }

        AppConfig config;
        try
        {
            config = ReadConfigurationFromUi();
            SaveCredential(config.UpstreamUsername);
            _configStore.Save(config);
            _config = config;
            _passwordText.Clear();
            UpdateCredentialPlaceholder();
            _log.Success("配置已保存", route: "CONFIG");
            if (showConfirmation)
            {
                _statusDetail.Text = "配置已保存，点击开启代理即可应用。";
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"保存配置失败：{ex.Message}", route: "CONFIG");
            if (showError)
            {
                MessageBox.Show(
                    this,
                    ex.Message,
                    "无法保存配置",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            return false;
        }
    }

    private void SaveCredential(string username)
    {
        username = username.Trim();
        var password = _passwordText.Text;
        ProxyCredential? existing = null;
        try
        {
            existing = _credentialStore.Load();
        }
        catch
        {
        }

        if (username.Length == 0)
        {
            _credentialStore.Delete();
            return;
        }

        if (password.Length == 0)
        {
            if (existing is not null && string.Equals(existing.Username, username, StringComparison.Ordinal))
            {
                return;
            }

            throw new InvalidOperationException("设置上游用户名后，请输入密码以保存到 Windows 凭据管理器。");
        }

        _credentialStore.Save(username, password);
    }

    private ProxyCredential? LoadConfiguredCredential()
    {
        var username = _config.UpstreamUsername.Trim();
        if (username.Length == 0)
        {
            return null;
        }

        var credential = _credentialStore.Load()
            ?? throw new InvalidOperationException("配置了上游用户名，但 Windows 凭据管理器中找不到密码。");
        if (!string.Equals(credential.Username, username, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Windows 凭据中的用户名与配置不一致，请重新输入密码并保存。");
        }

        return credential;
    }

    private ProxyCredential? BuildTestCredential()
    {
        var username = _usernameText.Text.Trim();
        if (username.Length == 0)
        {
            return null;
        }

        if (_passwordText.Text.Length > 0)
        {
            return new ProxyCredential(username, _passwordText.Text);
        }

        var existing = _credentialStore.Load();
        if (existing is not null && string.Equals(existing.Username, username, StringComparison.Ordinal))
        {
            return existing;
        }

        throw new InvalidOperationException("请输入密码后再测试代理。");
    }

    private async Task TestUpstreamAsync()
    {
        if (_isBusy || _runtime is not null)
        {
            return;
        }

        _testButton.Enabled = false;
        _testButton.Text = "测试中…";
        _testResultLabel.ForeColor = Theme.Muted;
        _testResultLabel.Text = "正在连接上游代理…";
        try
        {
            var credential = BuildTestCredential();
            var upstream = UpstreamProxyConfig.Parse(_upstreamText.Text, credential);
            var target = TargetEndpoint.Parse(_testTargetText.Text, 443);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var stopwatch = Stopwatch.StartNew();
            await using var tunnel = await new UpstreamConnector(upstream)
                .OpenTunnelAsync(target.Host, target.Port, cancellation.Token);
            stopwatch.Stop();
            _testResultLabel.ForeColor = Theme.Running;
            _testResultLabel.Text = $"连接成功 · {stopwatch.ElapsedMilliseconds} ms · {upstream.EndpointDisplay}";
            _log.Success(
                $"上游测试成功：{upstream.EndpointDisplay}",
                target.Host,
                route: upstream.RouteLabel,
                latencyMs: (int)stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _testResultLabel.ForeColor = Theme.Error;
            _testResultLabel.Text = $"测试失败：{ex.Message}";
            _log.Error($"上游测试失败：{ex.Message}", route: "TEST");
        }
        finally
        {
            _testButton.Enabled = true;
            _testButton.Text = "测试上游代理";
        }
    }

    private async Task ToggleProxyAsync()
    {
        if (_runtime is not null)
        {
            await StopProxyAsync();
        }
        else
        {
            await StartProxyAsync();
        }
    }

    private async Task<bool> StartProxyAsync()
    {
        if (_runtime is not null)
        {
            return true;
        }

        if (_isExiting)
        {
            return false;
        }

        await _transition.WaitAsync();
        try
        {
            if (_runtime is not null)
            {
                return true;
            }

            if (!SaveConfiguration(showError: true, showConfirmation: false))
            {
                UpdateStoppedUi();
                return false;
            }

            SetBusyUi("正在开启代理…");
            var credential = LoadConfiguredCredential();
            _runtime = await ProxyRuntime.StartAsync(
                _config.Clone(),
                credential,
                _log,
                _systemProxy);
            UpdateRunningUi();
            return true;
        }
        catch (Exception ex)
        {
            _runtime = null;
            _log.Error($"开启代理失败：{ex.Message}", route: "SYSTEM");
            UpdateErrorUi("开启失败", ex.Message, "重试开启");
            MessageBox.Show(
                this,
                ex.Message,
                "无法开启代理",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task<bool> StopProxyAsync()
    {
        if (_runtime is null)
        {
            return true;
        }

        await _transition.WaitAsync();
        try
        {
            var runtime = _runtime;
            SetBusyUi("正在恢复系统代理…");
            await runtime.StopAsync();
            _runtime = null;
            UpdateStoppedUi();
            _log.Info("代理已关闭，Windows 代理设置已恢复", route: "SYSTEM");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"关闭代理时恢复 Windows 设置失败：{ex.Message}", route: "SYSTEM");
            UpdateRestoreErrorUi(ex.Message);
            MessageBox.Show(
                this,
                ex.Message + "\n\n可以再次点击“重试关闭”；若仍然失败，下次启动会自动重试恢复。",
                "恢复系统代理失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task LaunchTargetAsync(TargetAppConfig target)
    {
        if (_isBusy)
        {
            return;
        }

        if (_runtime is null)
        {
            var started = await StartProxyAsync();
            if (!started || _runtime is null)
            {
                return;
            }
        }

        try
        {
            TargetAppLauncher.Launch(target, _runtime.ProxyPort, _log);
        }
        catch (Exception ex)
        {
            _log.Error($"启动 {target.DisplayName} 失败：{ex.Message}", route: "APP");
            MessageBox.Show(
                this,
                ex.Message,
                $"无法启动 {target.DisplayName}",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void BrowseApplication(TextBox target)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择目标应用",
            Filter = "程序或快捷方式|*.exe;*.cmd;*.bat;*.ps1;*.lnk|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (target.Text.Length > 0)
        {
            try
            {
                dialog.InitialDirectory = Path.GetDirectoryName(
                    Environment.ExpandEnvironmentVariables(target.Text.Trim()));
            }
            catch
            {
            }
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            target.Text = dialog.FileName;
        }
    }

    private async Task ExitApplicationAsync()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        SetBusyUi("正在退出…");
        await _transition.WaitAsync();
        try
        {
            if (_runtime is not null)
            {
                await _runtime.StopAsync();
                _runtime = null;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"退出时恢复 Windows 代理失败：{ex.Message}", route: "SYSTEM");
            MessageBox.Show(
                this,
                $"退出前恢复 Windows 代理失败：{ex.Message}\n\n备份已保留，下次启动会优先尝试自动恢复。",
                "系统代理恢复失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            _transition.Release();
            _allowClose = true;
            _trayIcon.Visible = false;
            Close();
            Application.Exit();
        }
    }

    private void SetBusyUi(string detail)
    {
        _isBusy = true;
        _statusTitle.Text = "●  正在切换";
        _statusTitle.ForeColor = Theme.Warning;
        _statusDetail.Text = detail;
        _primaryButton.Enabled = false;
        _saveButton.Enabled = false;
        SetEditableControlsEnabled(false);
        _trayToggleItem.Enabled = false;
        _trayStatusItem.Text = detail;
        _connectionTimer.Stop();
    }

    private void UpdateRunningUi()
    {
        _isBusy = false;
        var runtime = _runtime!;
        _statusTitle.Text = "●  代理已开启";
        _statusTitle.ForeColor = Theme.Running;
        _statusDetail.Text = "系统 PAC 已启用；仅命中的域名进入本地代理，其余直连。";
        _primaryButton.Text = "关闭代理";
        _primaryButton.Enabled = true;
        _primaryButton.NormalColor = Theme.Ink;
        _primaryButton.HoverColor = Color.FromArgb(48, 64, 63);
        _primaryButton.PressedColor = Color.FromArgb(15, 29, 29);
        _saveButton.Enabled = false;
        SetEditableControlsEnabled(false);
        _proxyMetric.Text = $"127.0.0.1:{runtime.ProxyPort}";
        _pacMetric.Text = $"127.0.0.1:{runtime.PacPort}";
        _pacMetric.Tag = runtime.PacUrl;
        _upstreamMetric.Text = runtime.UpstreamDisplay
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("socks5://", string.Empty, StringComparison.OrdinalIgnoreCase);
        _connectionsMetric.Text = runtime.ActiveConnections.ToString();
        _connectionTimer.Start();
        Icon = _runningIcon;
        UpdateTrayState("已开启");
    }

    private void UpdateStoppedUi()
    {
        _isBusy = false;
        _statusTitle.Text = "●  已停止";
        _statusTitle.ForeColor = Theme.Muted;
        _statusDetail.Text = "开启后仅目标域名经过本地代理。";
        _primaryButton.Text = "开启代理";
        _primaryButton.Enabled = true;
        _primaryButton.NormalColor = Theme.Accent;
        _primaryButton.HoverColor = Theme.AccentHover;
        _primaryButton.PressedColor = Color.FromArgb(166, 60, 31);
        _saveButton.Enabled = true;
        SetEditableControlsEnabled(true);
        _proxyMetric.Text = "—";
        _pacMetric.Text = "—";
        _pacMetric.Tag = null;
        _upstreamMetric.Text = string.IsNullOrWhiteSpace(_upstreamText.Text)
            ? "—"
            : _upstreamText.Text.Trim()
                .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("socks5://", string.Empty, StringComparison.OrdinalIgnoreCase)
                .TrimEnd('/');
        _connectionsMetric.Text = "0";
        _connectionTimer.Stop();
        Icon = _stoppedIcon;
        UpdateTrayState("已停止");
    }

    private void UpdateRestoreErrorUi(string message)
    {
        _isBusy = false;
        _statusTitle.Text = "●  恢复失败";
        _statusTitle.ForeColor = Theme.Error;
        _statusDetail.Text = Truncate(message, 90);
        _primaryButton.Text = "重试关闭";
        _primaryButton.Enabled = true;
        _primaryButton.NormalColor = Theme.Error;
        _primaryButton.HoverColor = Color.FromArgb(152, 43, 37);
        _primaryButton.PressedColor = Color.FromArgb(128, 35, 30);
        _saveButton.Enabled = false;
        SetEditableControlsEnabled(false);
        _connectionTimer.Stop();
        Icon = _stoppedIcon;
        UpdateTrayState("恢复失败");
    }

    private void UpdateErrorUi(string title, string detail, string action)
    {
        _isBusy = false;
        _statusTitle.Text = $"●  {title}";
        _statusTitle.ForeColor = Theme.Error;
        _statusDetail.Text = Truncate(detail, 90);
        _primaryButton.Text = action;
        _primaryButton.Enabled = true;
        _primaryButton.NormalColor = Theme.Accent;
        _primaryButton.HoverColor = Theme.AccentHover;
        _primaryButton.PressedColor = Color.FromArgb(166, 60, 31);
        _saveButton.Enabled = true;
        SetEditableControlsEnabled(true);
        _proxyMetric.Text = "—";
        _pacMetric.Text = "—";
        _connectionsMetric.Text = "0";
        _connectionTimer.Stop();
        Icon = _stoppedIcon;
        UpdateTrayState("开启失败");
    }

    private void SetEditableControlsEnabled(bool enabled)
    {
        foreach (var control in _editableControls)
        {
            control.Enabled = enabled;
        }

        _testButton.Enabled = enabled;
    }

    private void UpdateTrayState(string status)
    {
        _trayStatusItem.Text = status;
        _trayToggleItem.Text = _runtime is null ? "开启代理" : "关闭代理";
        _trayToggleItem.Enabled = !_isBusy && !_isExiting;
        _trayIcon.Icon = _runtime is null ? _stoppedIcon : _runningIcon;
        var endpoint = _runtime is null ? string.Empty : $" · :{_runtime.ProxyPort}";
        _trayIcon.Text = $"CodeX Proxy · {status}{endpoint}";
    }

    private void OnLogEntryAdded(ProxyLogEntry entry)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (InvokeRequired)
        {
            if (IsHandleCreated)
            {
                BeginInvoke(() => AppendLogEntry(entry));
            }

            return;
        }

        AppendLogEntry(entry);
    }

    private void AppendLogEntry(ProxyLogEntry entry)
    {
        if (_logGrid.IsDisposed)
        {
            return;
        }

        var level = entry.Level switch
        {
            ProxyLogLevel.Success => "OK",
            ProxyLogLevel.Warning => "WARN",
            ProxyLogLevel.Error => "ERROR",
            _ => "INFO"
        };
        var rowIndex = _logGrid.Rows.Add(
            entry.Timestamp.ToString("HH:mm:ss"),
            level,
            entry.Host ?? "—",
            entry.Rule ?? "—",
            entry.Route ?? "—",
            entry.LatencyMs?.ToString() ?? "—",
            entry.Message.ReplaceLineEndings(" "));
        var row = _logGrid.Rows[rowIndex];
        row.DefaultCellStyle.ForeColor = entry.Level switch
        {
            ProxyLogLevel.Success => Theme.Running,
            ProxyLogLevel.Warning => Theme.Warning,
            ProxyLogLevel.Error => Theme.Error,
            _ => Theme.Ink
        };
        row.Cells[2].ToolTipText = entry.Host ?? string.Empty;
        row.Cells[6].ToolTipText = entry.Message;
        while (_logGrid.Rows.Count > 1500)
        {
            _logGrid.Rows.RemoveAt(0);
        }

        if (_logGrid.Rows.Count > 0)
        {
            try
            {
                _logGrid.FirstDisplayedScrollingRowIndex = _logGrid.Rows.Count - 1;
            }
            catch (InvalidOperationException)
            {
            }

            _logGrid.ClearSelection();
        }
    }

    private static void OpenLogDirectory()
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogDirectory}\"")
        {
            UseShellExecute = true
        });
    }

    private static TextBox CreateTextBox(bool usePasswordChar = false)
    {
        return new TextBox
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 32,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Surface,
            ForeColor = Theme.Ink,
            Font = Theme.Body(9.5f),
            UseSystemPasswordChar = usePasswordChar,
            Margin = Padding.Empty
        };
    }

    private static string Truncate(string value, int length)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= length)
        {
            return value;
        }

        return value[..Math.Max(0, length - 1)] + "…";
    }
}
