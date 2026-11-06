using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Native;
using LiveWallpaper.Services.Autostart;
using LiveWallpaper.Services.Server;
using LiveWallpaper.Services.Weather;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.App;

public sealed class SettingsForm : Form
{
    private readonly IConfigManager _configManager;
    private readonly IWeatherService _weatherService;
    private readonly IServerWidgetService _serverService;
    private readonly ILogger<SettingsForm> _logger;

    private TextBox _pinsDirBox = null!;
    private ComboBox _styleCombo = null!;
    private NumericUpDown _intervalNumeric = null!;
    private CheckBox _harmonyCheck = null!;

    private CheckBox _weatherCheck = null!;
    private TextBox _cityBox = null!;
    private Label _geoStatusLabel = null!;
    private double _resolvedLat;
    private double _resolvedLon;

    private CheckBox _pauseFullscreenCheck = null!;
    private CheckBox _pauseBatteryCheck = null!;
    private ComboBox _fpsCombo = null!;
    private CheckBox _autostartCheck = null!;
    private CheckBox _launcherEnabledCheck = null!;
    private CheckBox _hideDesktopIconsCheck = null!;

    private CheckBox _serverEnabledCheck = null!;
    private TextBox _sshAliasBox = null!;
    private CheckBox _timewebEnabledCheck = null!;
    private Label _timewebStatusLabel = null!;
    private Button _btnTimewebToken = null!;
    private ListBox _sitesListBox = null!;
    private Button _btnAddSite = null!;
    private Button _btnRemoveSite = null!;
    private Button _btnCheckConnection = null!;
    private List<ServerSiteConfig> _sitesList = new();

    public SettingsForm(
        IConfigManager configManager,
        IWeatherService weatherService,
        IServerWidgetService serverService,
        ILogger<SettingsForm> logger)
    {
        _configManager = configManager;
        _weatherService = weatherService;
        _serverService = serverService;
        _logger = logger;

        InitializeDarkTheme();
        InitializeLayout();
        LoadValues();
    }

    private void InitializeDarkTheme()
    {
        Text = "Настройки LiveWallpaper Engine";
        Size = new Size(540, 680);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(13, 17, 23);
        ForeColor = Color.FromArgb(201, 209, 217);
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
    }

    private void InitializeLayout()
    {
        var scrollPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(24)
        };
        Controls.Add(scrollPanel);

        var curY = 16;

        // --- 1. Секция: Коллаж ---
        AddSectionHeader(scrollPanel, "КОЛЛАЖ И ПИНЫ", ref curY);

        var lblDir = new Label { Text = "Папка с пинами:", Location = new Point(24, curY), AutoSize = true };
        scrollPanel.Controls.Add(lblDir);
        curY += 24;

        _pinsDirBox = CreateDarkTextBox(new Point(24, curY), 360);
        scrollPanel.Controls.Add(_pinsDirBox);

        var btnBrowse = CreateDarkButton("Обзор...", new Point(394, curY - 2), 90, 30);
        btnBrowse.Click += (s, e) =>
        {
            using var dlg = new FolderBrowserDialog();
            dlg.SelectedPath = Directory.Exists(_pinsDirBox.Text) ? _pinsDirBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                _pinsDirBox.Text = dlg.SelectedPath;
            }
        };
        scrollPanel.Controls.Add(btnBrowse);
        curY += 40;

        var lblStyle = new Label { Text = "Стиль раскладки:", Location = new Point(24, curY), AutoSize = true };
        scrollPanel.Controls.Add(lblStyle);
        curY += 24;

        _styleCombo = CreateDarkComboBox(new Point(24, curY), 240);
        _styleCombo.Items.AddRange(new object[]
        {
            "fan (Веер)",
            "bento (Бенто-сетка)",
            "hex (Соты)",
            "diagonal (Диагональ)",
            "polaroid (Полароид)",
            "masonry (Водопад)",
            "film (Кинопленка)"
        });
        scrollPanel.Controls.Add(_styleCombo);

        var lblInterval = new Label { Text = "Интервал смены (мин):", Location = new Point(270, curY - 24), AutoSize = true };
        scrollPanel.Controls.Add(lblInterval);

        _intervalNumeric = new NumericUpDown
        {
            Location = new Point(270, curY),
            Width = 120,
            Minimum = 1,
            Maximum = 1440,
            Value = 5,
            BackColor = Color.FromArgb(22, 27, 34),
            ForeColor = Color.White
        };
        scrollPanel.Controls.Add(_intervalNumeric);
        curY += 40;

        _harmonyCheck = CreateDarkCheckBox("Подбирать пины по гармонии цветов (Hue & K-Means)", new Point(24, curY));
        scrollPanel.Controls.Add(_harmonyCheck);
        curY += 45;

        // --- 2. Секция: Погода ---
        AddSectionHeader(scrollPanel, "ПОГОДА (Open-Meteo)", ref curY);

        _weatherCheck = CreateDarkCheckBox("Включить виджет погоды", new Point(24, curY));
        scrollPanel.Controls.Add(_weatherCheck);
        curY += 32;

        var lblCity = new Label { Text = "Город:", Location = new Point(24, curY), AutoSize = true };
        scrollPanel.Controls.Add(lblCity);
        curY += 24;

        _cityBox = CreateDarkTextBox(new Point(24, curY), 250);
        scrollPanel.Controls.Add(_cityBox);

        var btnGeo = CreateDarkButton("Проверить", new Point(284, curY - 2), 110, 30);
        btnGeo.Click += async (s, e) =>
        {
            btnGeo.Enabled = false;
            _geoStatusLabel.Text = "Поиск координат...";
            _geoStatusLabel.ForeColor = Color.FromArgb(88, 166, 255);

            var result = await _weatherService.GeocodeCityAsync(_cityBox.Text);
            btnGeo.Enabled = true;

            if (result != null)
            {
                _resolvedLat = result.Value.Latitude;
                _resolvedLon = result.Value.Longitude;
                _geoStatusLabel.Text = $"Найдено: {result.Value.ResolvedName} ({_resolvedLat:F2}, {_resolvedLon:F2})";
                _geoStatusLabel.ForeColor = Color.FromArgb(63, 185, 80);
            }
            else
            {
                _geoStatusLabel.Text = "Город не найден на Open-Meteo";
                _geoStatusLabel.ForeColor = Color.FromArgb(248, 81, 73);
            }
        };
        scrollPanel.Controls.Add(btnGeo);
        curY += 36;

        _geoStatusLabel = new Label
        {
            Location = new Point(24, curY),
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.FromArgb(139, 148, 158)
        };
        scrollPanel.Controls.Add(_geoStatusLabel);
        curY += 40;

        // --- 3. Секция: Производительность ---
        AddSectionHeader(scrollPanel, "ПРОИЗВОДИТЕЛЬНОСТЬ И ИГРЫ", ref curY);

        _pauseFullscreenCheck = CreateDarkCheckBox("Автопауза при запуске игр и полноэкранных окон (TrySuspend ~0% CPU/GPU)", new Point(24, curY));
        scrollPanel.Controls.Add(_pauseFullscreenCheck);
        curY += 32;

        _pauseBatteryCheck = CreateDarkCheckBox("Автопауза при работе от аккумулятора", new Point(24, curY));
        scrollPanel.Controls.Add(_pauseBatteryCheck);
        curY += 32;

        var lblFps = new Label { Text = "Целевой FPS анимации:", Location = new Point(24, curY), AutoSize = true };
        scrollPanel.Controls.Add(lblFps);
        curY += 24;

        _fpsCombo = CreateDarkComboBox(new Point(24, curY), 160);
        _fpsCombo.Items.AddRange(new object[] { "30 FPS (Рекомендуется)", "60 FPS (Плавнее)" });
        scrollPanel.Controls.Add(_fpsCombo);
        curY += 45;

        // --- 4. Секция: Лаунчер и рабочий стол ---
        AddSectionHeader(scrollPanel, "ЛАУНЧЕР И ЗНАЧКИ РАБОЧЕГО СТОЛА", ref curY);

        _launcherEnabledCheck = CreateDarkCheckBox("Включить виджет-лаунчер «папки как в Android»", new Point(24, curY));
        scrollPanel.Controls.Add(_launcherEnabledCheck);
        curY += 32;

        _hideDesktopIconsCheck = CreateDarkCheckBox("Скрывать стандартные значки рабочего стола Windows", new Point(24, curY));
        scrollPanel.Controls.Add(_hideDesktopIconsCheck);
        curY += 45;

        // --- 5. Секция: Сервер и продакшен ---
        AddSectionHeader(scrollPanel, "СЕРВЕР И ПРОДАКШЕН", ref curY);

        _serverEnabledCheck = CreateDarkCheckBox("Включить виджет «Сервер»", new Point(24, curY));
        scrollPanel.Controls.Add(_serverEnabledCheck);
        curY += 32;

        var lblAlias = new Label { Text = "SSH-алиас (из ~/.ssh/config):", Location = new Point(24, curY), AutoSize = true };
        scrollPanel.Controls.Add(lblAlias);
        curY += 24;

        _sshAliasBox = CreateDarkTextBox(new Point(24, curY), 240);
        scrollPanel.Controls.Add(_sshAliasBox);
        curY += 36;

        _timewebEnabledCheck = CreateDarkCheckBox("Опрашивать Timeweb Cloud API", new Point(24, curY));
        scrollPanel.Controls.Add(_timewebEnabledCheck);
        curY += 32;

        _timewebStatusLabel = new Label
        {
            Location = new Point(24, curY),
            AutoSize = true,
            Font = new Font("Segoe UI", 9.0f),
            ForeColor = Color.FromArgb(139, 148, 158)
        };
        scrollPanel.Controls.Add(_timewebStatusLabel);

        _btnTimewebToken = CreateDarkButton("Задать токен Timeweb...", new Point(290, curY - 4), 190, 28);
        _btnTimewebToken.Click += OnSetTimewebTokenClicked;
        scrollPanel.Controls.Add(_btnTimewebToken);
        curY += 38;

        var lblSites = new Label { Text = "Сайты для проверки доступности (HTTP + SSL):", Location = new Point(24, curY), AutoSize = true };
        scrollPanel.Controls.Add(lblSites);
        curY += 24;

        _sitesListBox = new ListBox
        {
            Location = new Point(24, curY),
            Size = new Size(360, 96),
            BackColor = Color.FromArgb(22, 27, 34),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        scrollPanel.Controls.Add(_sitesListBox);

        _btnAddSite = CreateDarkButton("Добавить...", new Point(394, curY), 95, 28);
        _btnAddSite.Click += OnAddSiteClicked;
        scrollPanel.Controls.Add(_btnAddSite);

        _btnRemoveSite = CreateDarkButton("Удалить", new Point(394, curY + 34), 95, 28);
        _btnRemoveSite.Click += OnRemoveSiteClicked;
        scrollPanel.Controls.Add(_btnRemoveSite);
        curY += 106;

        _btnCheckConnection = CreateDarkButton("Проверить подключение", new Point(24, curY), 220, 32);
        _btnCheckConnection.Click += OnCheckConnectionClicked;
        scrollPanel.Controls.Add(_btnCheckConnection);
        curY += 45;

        // --- 6. Секция: Система ---
        AddSectionHeader(scrollPanel, "СИСТЕМА И ЗАПУСК", ref curY);

        _autostartCheck = CreateDarkCheckBox("Запускать LiveWallpaper вместе с Windows", new Point(24, curY));
        scrollPanel.Controls.Add(_autostartCheck);
        curY += 50;

        // --- Нижняя панель кнопок ---
        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 64,
            BackColor = Color.FromArgb(22, 27, 34)
        };
        Controls.Add(bottomPanel);

        var btnSave = CreateAccentButton("Сохранить", new Point(280, 16), 110, 34);
        btnSave.Click += (s, e) => SaveValues();
        bottomPanel.Controls.Add(btnSave);

        var btnCancel = CreateDarkButton("Отмена", new Point(400, 16), 90, 34);
        btnCancel.Click += (s, e) => Close();
        bottomPanel.Controls.Add(btnCancel);
    }

    private static void AddSectionHeader(Panel parent, string title, ref int y)
    {
        var lbl = new Label
        {
            Text = title,
            Location = new Point(24, y),
            AutoSize = true,
            ForeColor = Color.FromArgb(88, 166, 255),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        parent.Controls.Add(lbl);
        y += 28;
    }

    private static TextBox CreateDarkTextBox(Point loc, int width) => new()
    {
        Location = loc,
        Width = width,
        BackColor = Color.FromArgb(22, 27, 34),
        ForeColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle
    };

    private static ComboBox CreateDarkComboBox(Point loc, int width) => new()
    {
        Location = loc,
        Width = width,
        DropDownStyle = ComboBoxStyle.DropDownList,
        BackColor = Color.FromArgb(22, 27, 34),
        ForeColor = Color.White
    };

    private static CheckBox CreateDarkCheckBox(string text, Point loc) => new()
    {
        Text = text,
        Location = loc,
        AutoSize = true,
        ForeColor = Color.FromArgb(201, 209, 217)
    };

    private static Button CreateDarkButton(string text, Point loc, int w, int h) => new()
    {
        Text = text,
        Location = loc,
        Size = new Size(w, h),
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(33, 38, 45),
        ForeColor = Color.White
    };

    private static Button CreateAccentButton(string text, Point loc, int w, int h)
    {
        var btn = CreateDarkButton(text, loc, w, h);
        btn.BackColor = Color.FromArgb(35, 134, 54);
        btn.FlatAppearance.BorderColor = Color.FromArgb(46, 160, 67);
        return btn;
    }

    private void LoadValues()
    {
        var cfg = _configManager.CurrentConfig;

        _pinsDirBox.Text = cfg.Collage.PinsDirectory;
        _intervalNumeric.Value = Math.Clamp(cfg.Collage.IntervalMinutes, 1, 1440);
        _harmonyCheck.Checked = cfg.Collage.EnableColorHarmony;

        _styleCombo.SelectedIndex = cfg.Collage.Style.ToLowerInvariant() switch
        {
            "bento" => 1,
            "hex" => 2,
            "diagonal" => 3,
            "polaroid" => 4,
            "masonry" => 5,
            "film" => 6,
            _ => 0
        };

        _weatherCheck.Checked = cfg.Weather.Enabled;
        _cityBox.Text = cfg.Weather.City;
        _resolvedLat = cfg.Weather.Latitude;
        _resolvedLon = cfg.Weather.Longitude;
        if (!string.IsNullOrWhiteSpace(cfg.Weather.City) && Math.Abs(_resolvedLat) > 0.001)
        {
            _geoStatusLabel.Text = $"Текущие координаты: ({_resolvedLat:F2}, {_resolvedLon:F2})";
        }

        _pauseFullscreenCheck.Checked = cfg.Performance.AutoPauseFullscreen;
        _pauseBatteryCheck.Checked = cfg.Performance.AutoPauseBattery;
        _fpsCombo.SelectedIndex = cfg.Performance.FpsIdle >= 60 ? 1 : 0;

        _launcherEnabledCheck.Checked = cfg.Launcher.Enabled;
        _hideDesktopIconsCheck.Checked = cfg.Launcher.HideDesktopIcons;

        _serverEnabledCheck.Checked = cfg.Server.Enabled;
        _sshAliasBox.Text = string.IsNullOrWhiteSpace(cfg.Server.SshAlias) ? "myserver" : cfg.Server.SshAlias;
        _timewebEnabledCheck.Checked = cfg.Server.TimewebEnabled;
        UpdateTimewebTokenStatus();

        _sitesList = cfg.Server.Sites.Select(s => new ServerSiteConfig { Name = s.Name, Url = s.Url }).ToList();
        RefreshSitesList();

        _autostartCheck.Checked = AutostartManager.IsAutostartEnabled();
    }

    private void UpdateTimewebTokenStatus()
    {
        bool exists = CredentialManager.Exists();
        if (exists)
        {
            _timewebStatusLabel.Text = "Токен: сохранён в Диспетчере Windows";
            _timewebStatusLabel.ForeColor = Color.FromArgb(63, 185, 80);
        }
        else
        {
            _timewebStatusLabel.Text = "Токен: не найден в Диспетчере Windows";
            _timewebStatusLabel.ForeColor = Color.FromArgb(139, 148, 158);
        }
    }

    private void RefreshSitesList()
    {
        _sitesListBox.Items.Clear();
        foreach (var s in _sitesList)
        {
            _sitesListBox.Items.Add($"{s.Name}  ({s.Url})");
        }
    }

    private void OnSetTimewebTokenClicked(object? sender, EventArgs e)
    {
        using var promptForm = new Form
        {
            Text = "Задать токен Timeweb Cloud",
            Size = new Size(460, 210),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Color.FromArgb(13, 17, 23),
            ForeColor = Color.FromArgb(201, 209, 217),
            Font = new Font("Segoe UI", 9.5f)
        };

        var lbl = new Label
        {
            Text = "Введите API-токен Timeweb Cloud (read-only):\nТокен будет надёжно сохранён в Диспетчере учётных данных Windows.",
            Location = new Point(20, 16),
            Size = new Size(400, 44)
        };
        promptForm.Controls.Add(lbl);

        var txt = CreateDarkTextBox(new Point(20, 68), 400);
        txt.PasswordChar = '•';
        promptForm.Controls.Add(txt);

        var btnOk = CreateAccentButton("Сохранить", new Point(210, 116), 100, 32);
        btnOk.Click += (s, ev) =>
        {
            string token = txt.Text.Trim();
            if (!string.IsNullOrEmpty(token))
            {
                CredentialManager.WriteSecret(token);
                UpdateTimewebTokenStatus();
                promptForm.DialogResult = DialogResult.OK;
                promptForm.Close();
            }
            else
            {
                MessageBox.Show("Токен не может быть пустым.", "Внимание", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        promptForm.Controls.Add(btnOk);

        var btnCancel = CreateDarkButton("Отмена", new Point(320, 116), 100, 32);
        btnCancel.Click += (s, ev) => promptForm.Close();
        promptForm.Controls.Add(btnCancel);

        promptForm.ShowDialog(this);
    }

    private void OnAddSiteClicked(object? sender, EventArgs e)
    {
        using var prompt = new Form
        {
            Text = "Добавить сайт",
            Size = new Size(420, 240),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Color.FromArgb(13, 17, 23),
            ForeColor = Color.FromArgb(201, 209, 217),
            Font = new Font("Segoe UI", 9.5f)
        };

        var lbl1 = new Label { Text = "Название сайта (напр. api.example.com, Портал):", Location = new Point(20, 16), AutoSize = true };
        prompt.Controls.Add(lbl1);
        var txtName = CreateDarkTextBox(new Point(20, 40), 360);
        prompt.Controls.Add(txtName);

        var lbl2 = new Label { Text = "URL сайта (https://...):", Location = new Point(20, 76), AutoSize = true };
        prompt.Controls.Add(lbl2);
        var txtUrl = CreateDarkTextBox(new Point(20, 100), 360);
        txtUrl.Text = "https://";
        prompt.Controls.Add(txtUrl);

        var btnOk = CreateAccentButton("Добавить", new Point(170, 150), 100, 32);
        btnOk.Click += (s, ev) =>
        {
            string name = txtName.Text.Trim();
            string url = txtUrl.Text.Trim();
            if (string.IsNullOrEmpty(name) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                MessageBox.Show("Укажите корректное имя и абсолютный URL (начиная с https://).", "Внимание", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _sitesList.Add(new ServerSiteConfig { Name = name, Url = url });
            RefreshSitesList();
            prompt.Close();
        };
        prompt.Controls.Add(btnOk);

        var btnCancel = CreateDarkButton("Отмена", new Point(280, 150), 100, 32);
        btnCancel.Click += (s, ev) => prompt.Close();
        prompt.Controls.Add(btnCancel);

        prompt.ShowDialog(this);
    }

    private void OnRemoveSiteClicked(object? sender, EventArgs e)
    {
        int idx = _sitesListBox.SelectedIndex;
        if (idx >= 0 && idx < _sitesList.Count)
        {
            _sitesList.RemoveAt(idx);
            RefreshSitesList();
        }
    }

    private async void OnCheckConnectionClicked(object? sender, EventArgs e)
    {
        _btnCheckConnection.Enabled = false;
        _btnCheckConnection.Text = "Проверка...";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            string result = await _serverService.RunDiagnosticsAsync(cts.Token);

            MessageBox.Show(
                result,
                "Результаты диагностики",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Ошибка во время проверки: {ex.Message}",
                "Ошибка",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _btnCheckConnection.Enabled = true;
            _btnCheckConnection.Text = "Проверить подключение";
        }
    }

    private void SaveValues()
    {
        try
        {
            var cfg = _configManager.CurrentConfig;

            cfg.Collage.PinsDirectory = _pinsDirBox.Text.Trim();
            cfg.Collage.IntervalMinutes = (int)_intervalNumeric.Value;
            cfg.Collage.EnableColorHarmony = _harmonyCheck.Checked;

            cfg.Collage.Style = _styleCombo.SelectedIndex switch
            {
                1 => "bento",
                2 => "hex",
                3 => "diagonal",
                4 => "polaroid",
                5 => "masonry",
                6 => "film",
                _ => "fan"
            };

            cfg.Weather.Enabled = _weatherCheck.Checked;
            cfg.Weather.City = _cityBox.Text.Trim();
            cfg.Weather.Latitude = _resolvedLat;
            cfg.Weather.Longitude = _resolvedLon;

            cfg.Performance.AutoPauseFullscreen = _pauseFullscreenCheck.Checked;
            cfg.Performance.AutoPauseBattery = _pauseBatteryCheck.Checked;
            cfg.Performance.FpsIdle = _fpsCombo.SelectedIndex == 1 ? 60 : 30;

            cfg.Launcher.Enabled = _launcherEnabledCheck.Checked;
            cfg.Launcher.HideDesktopIcons = _hideDesktopIconsCheck.Checked;
            DesktopIconManager.SetIconsVisible(!cfg.Launcher.HideDesktopIcons);

            cfg.Server.Enabled = _serverEnabledCheck.Checked;
            cfg.Server.SshAlias = string.IsNullOrWhiteSpace(_sshAliasBox.Text) ? "myserver" : _sshAliasBox.Text.Trim();
            cfg.Server.TimewebEnabled = _timewebEnabledCheck.Checked;
            cfg.Server.Sites = _sitesList.ToList();

            _configManager.Save(cfg);
            AutostartManager.SetAutostartEnabled(_autostartCheck.Checked);

            _logger.LogInformation("Settings saved successfully.");
            Close();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings.");
            MessageBox.Show(
                $"Ошибка сохранения настроек: {ex.Message}",
                "Ошибка",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
