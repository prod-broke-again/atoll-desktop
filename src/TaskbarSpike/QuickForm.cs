using System.Drawing.Drawing2D;

namespace TaskbarSpike;

/// <summary>
/// Own network and sound panel. The taskbar shows them as one button.
/// Windows draws these in one quick-settings window; that window is not used here.
/// </summary>
internal sealed class QuickForm : Form
{
    private const int PanelWidth = 360;
    private const int Pad = 14;
    private static Color Surface => Theme.Surface;
    private static Color Selected => Theme.SelectedA;
    private static Color MutedText => Theme.Text2;

    private readonly Font _icons;
    private readonly Font _titleFont;
    private Rectangle _dockBounds;
    private int _anchorX;
    private int _anchorTop;
    private long _openedAt;
    private string _signature = "";
    private LevelSlider? _master;
    private Button? _mute;
    private bool _muted;

    public event Action? Dismissed;

    public QuickForm()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Surface;
        ForeColor = Theme.Text;
        Font = ThemeFont.Text(9f);
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScroll = true;
        _icons = new Font("Segoe MDL2 Assets", 14f);
        _titleFont = new Font(Font, FontStyle.Bold);
        KeyPreview = true;
    }

    public bool IsDragging => _master?.Dragging == true || Controls.OfType<LevelSlider>().Any(slider => slider.Dragging);

    protected override CreateParams CreateParams
    {
        get
        {
            const int wsExToolWindow = 0x00000080;
            const int wsExTopMost = 0x00000008;
            var cp = base.CreateParams;
            cp.ExStyle |= wsExToolWindow | wsExTopMost;
            PopupChrome.UseShadow(cp);
            return cp;
        }
    }

    public void ShowFor(int anchorX, int anchorTop, Rectangle dockBounds)
    {
        _anchorX = anchorX;
        _anchorTop = anchorTop;
        _dockBounds = dockBounds;
        _openedAt = Environment.TickCount64;
        _signature = "";
        Reload();
        Show();
        NativeMethods.SetWindowPos(
            Handle,
            NativeMethods.HwndTopmost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpShowWindow);
    }

    public void Reload()
    {
        if (IsDragging)
        {
            return;
        }

        var network = NetworkStatus.Read();
        var audio = AudioDevices.Details();
        var signature = network.Kind + "|" + network.Name + "|" + network.Connected
            + "|" + string.Join(",", audio.Devices.Select(device => device.Id + (device.Active ? "*" : "")))
            + "|" + string.Join(",", audio.Sessions.Select(session => session.Key));
        if (signature != _signature || Controls.Count == 0)
        {
            _signature = signature;
            Build(network, audio);
        }
        else
        {
            Apply(network, audio);
        }

        Place(_anchorX, _anchorTop);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        PopupChrome.ClearSystemChrome(Handle);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (Environment.TickCount64 - _openedAt < 250 || Bounds.Contains(Cursor.Position) || _dockBounds.Contains(Cursor.Position))
        {
            return;
        }

        Hide();
        Dismissed?.Invoke();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            Hide();
            Dismissed?.Invoke();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        PopupChrome.Paint(e.Graphics, ClientSize, PopupChrome.PopupRadiusPx(this));
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (!IsHandleCreated || Width <= 1 || Height <= 1)
        {
            return;
        }

        PopupChrome.ApplyRegion(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icons.Dispose();
            _titleFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Build(NetworkInfo network, AudioDetails audio)
    {
        SuspendLayout();
        while (Controls.Count > 0)
        {
            Controls[0].Dispose();
        }

        _master = null;
        _mute = null;
        var inner = PanelWidth - (Pad * 2);
        var y = Pad;

        var icon = new Label
        {
            Font = _icons,
            Text = network.Kind == "wifi" ? "\uE701" : "\uE839",
            ForeColor = network.Connected ? Theme.Text : MutedText,
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(Pad, y, 32, 44),
        };
        var title = new Label
        {
            Text = network.Name,
            Font = _titleFont,
            ForeColor = Theme.Text,
            AutoEllipsis = true,
            Bounds = new Rectangle(Pad + 40, y + 2, inner - 40, 20),
        };
        var status = new Label
        {
            Text = network.Status,
            ForeColor = MutedText,
            AutoEllipsis = true,
            Bounds = new Rectangle(Pad + 40, y + 22, inner - 40, 18),
        };
        Controls.Add(icon);
        Controls.Add(title);
        Controls.Add(status);
        y += 52;

        if (!string.IsNullOrWhiteSpace(audio.DeviceId))
        {
            _muted = audio.Muted;
            _mute = new GlassButton
            {
                Font = _icons,
                Text = VolumeGlyph(audio.Muted, audio.Volume),
                ForeColor = Theme.Text,
                BackColor = Surface,
                Bounds = new Rectangle(Pad, y, 32, 32),
                Cursor = Cursors.Hand,
                Tag = audio.Muted,
            };
            _mute.Click += (_, _) => ToggleMute();
            _master = new LevelSlider
            {
                Value = audio.Volume,
                Bounds = new Rectangle(Pad + 40, y + 5, inner - 40, 22),
            };
            _master.ValueChanged += (_, _) =>
            {
                try
                {
                    AudioDevices.SetVolume(_master.Value);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[spike] volume: " + ex.Message);
                }
            };
            Controls.Add(_mute);
            Controls.Add(_master);
            y += 40;
        }
        else
        {
            Controls.Add(Hint("Нет устройства вывода", y, inner));
            y += 28;
        }

        y = AddCaption("Устройство вывода", y, inner);
        if (audio.Devices.Count == 0)
        {
            Controls.Add(Hint("Список пуст", y, inner));
            y += 24;
        }

        foreach (var device in audio.Devices)
        {
            var row = new ChoiceButton
            {
                Text = device.Name,
                Font = Font,
                Bounds = new Rectangle(Pad, y, inner, 32),
                BackColor = device.Active ? Selected : Surface,
                ForeColor = Theme.Text,
            };
            var id = device.Id;
            row.Click += (_, _) =>
            {
                try
                {
                    AudioDevices.SetDefault(id);
                    BeginInvoke(new Action(() =>
                    {
                        _signature = "";
                        Reload();
                    }));
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[spike] audio device: " + ex.Message);
                }
            };
            Controls.Add(row);
            y += 34;
        }

        y += 6;
        y = AddCaption("Микшер громкости", y, inner);
        if (audio.Sessions.Count == 0)
        {
            Controls.Add(Hint("Нет активных приложений", y, inner));
            y += 24;
        }

        foreach (var session in audio.Sessions)
        {
            var name = new Label
            {
                Text = session.Name,
                ForeColor = Theme.Text,
                AutoEllipsis = true,
                Bounds = new Rectangle(Pad, y + 2, 130, 22),
            };
            var slider = new LevelSlider
            {
                Key = session.Key,
                Value = session.Volume,
                Bounds = new Rectangle(Pad + 136, y + 2, inner - 136, 22),
            };
            slider.ValueChanged += (_, _) =>
            {
                try
                {
                    AudioDevices.SetSession(slider.Key, slider.Value);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[spike] mixer: " + ex.Message);
                }
            };
            Controls.Add(name);
            Controls.Add(slider);
            y += 28;
        }

        y += Pad;
        var screen = Screen.FromPoint(new Point(_anchorX, Math.Max(0, _anchorTop - 1)));
        var maxHeight = Math.Max(160, screen.WorkingArea.Height - 96);
        var height = Math.Min(y, maxHeight);
        AutoScrollMinSize = new Size(0, y);
        ClientSize = new Size(PanelWidth, height);
        ResumeLayout();
        Invalidate();
    }

    private void Apply(NetworkInfo network, AudioDetails audio)
    {
        _muted = audio.Muted;
        if (_master != null && !_master.Dragging)
        {
            _master.Value = audio.Volume;
        }

        if (_mute != null)
        {
            _mute.Text = VolumeGlyph(audio.Muted, _master?.Value ?? audio.Volume);
            _mute.Tag = audio.Muted;
        }

        foreach (var slider in Controls.OfType<LevelSlider>())
        {
            if (slider == _master || slider.Dragging || slider.Key.Length == 0)
            {
                continue;
            }

            var session = audio.Sessions.FirstOrDefault(item => item.Key == slider.Key);
            if (session != null)
            {
                slider.Value = session.Volume;
            }
        }

        _ = network;
    }

    private void ToggleMute()
    {
        try
        {
            AudioDevices.SetMuted(!_muted);
            _muted = !_muted;
            if (_mute != null)
            {
                _mute.Text = VolumeGlyph(_muted, _master?.Value ?? 0);
                _mute.Tag = _muted;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[spike] mute: " + ex.Message);
        }
    }

    private int AddCaption(string text, int y, int inner)
    {
        Controls.Add(new Label
        {
            Text = text,
            ForeColor = MutedText,
            Bounds = new Rectangle(Pad, y, inner, 18),
        });
        return y + 22;
    }

    private Label Hint(string text, int y, int inner)
    {
        return new Label
        {
            Text = text,
            ForeColor = MutedText,
            Bounds = new Rectangle(Pad, y, inner, 18),
        };
    }

    private void Place(int anchorX, int anchorTop)
    {
        var x = anchorX - (Width / 2);
        var y = anchorTop - Height - 8;
        var area = Screen.FromPoint(new Point(anchorX, Math.Max(0, anchorTop - 1))).Bounds;
        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - Width));
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - Height));
        var next = new Point(x, y);
        if (Location != next)
        {
            Location = next;
        }
    }

    private static string VolumeGlyph(bool muted, int volume)
    {
        if (muted)
        {
            return "\uE74F";
        }

        if (volume <= 0)
        {
            return "\uE992";
        }

        if (volume < 33)
        {
            return "\uE993";
        }

        return volume < 66 ? "\uE994" : "\uE995";
    }
}

internal sealed class LevelSlider : Control
{
    private int _value;
    private bool _drag;

    public LevelSlider()
    {
        Height = 22;
        Cursor = Cursors.Hand;
        Key = "";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public string Key { get; set; }

    public bool Dragging => _drag;

    public int Value
    {
        get => _value;
        set
        {
            var next = Math.Clamp(value, 0, 100);
            if (next == _value)
            {
                return;
            }

            _value = next;
            Invalidate();
        }
    }

    public event EventHandler? ValueChanged;

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var mid = Height / 2;
        var left = 8;
        var width = Math.Max(1, Width - 16);
        using var track = new SolidBrush(Theme.TrackA);
        using var fill = new SolidBrush(Theme.AccentInk);
        var knobColor = Theme.OnAccent;
        using var knob = new SolidBrush(knobColor);
        using var ring = new Pen(Theme.Same(knobColor, Color.FromArgb(22, 18, 16))
            ? Color.FromArgb(210, 255, 255, 255)
            : Color.FromArgb(170, 12, 13, 20), 1.5f);
        graphics.FillRectangle(track, left, mid - 2, width, 4);
        var filled = width * _value / 100;
        graphics.FillRectangle(fill, left, mid - 2, filled, 4);
        var knobBounds = new Rectangle(left + filled - 7, mid - 7, 14, 14);
        graphics.FillEllipse(knob, knobBounds);
        graphics.DrawEllipse(ring, knobBounds);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        _drag = true;
        Capture = true;
        SetFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag)
        {
            SetFromX(e.X);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_drag)
        {
            return;
        }

        _drag = false;
        Capture = false;
        SetFromX(e.X);
    }

    private void SetFromX(int x)
    {
        var width = Math.Max(1, Width - 16);
        var value = (int)Math.Round(Math.Clamp(x - 8, 0, width) * 100.0 / width);
        if (value == _value)
        {
            return;
        }

        _value = value;
        Invalidate();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class ChoiceButton : Button
{
    public ChoiceButton()
    {
        FlatStyle = FlatStyle.Flat;
        ForeColor = Theme.Text;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        FlatAppearance.BorderSize = 0;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(brush, ClientRectangle);
        var active = Theme.Same(BackColor, Theme.SelectedA);
        if (active)
        {
            using var mark = new SolidBrush(Theme.AccentInk);
            e.Graphics.FillRectangle(mark, 0, 8, 3, Math.Max(1, Height - 16));
        }

        var rect = new Rectangle(12, 0, Math.Max(1, Width - 22), Height);
        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            rect,
            Theme.TextOn(BackColor),
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (!Theme.Same(BackColor, Theme.SelectedA))
        {
            BackColor = Theme.HoverA;
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (Theme.Same(BackColor, Theme.HoverA))
        {
            BackColor = Theme.SurfaceA;
        }
    }
}

/// <summary>
/// Flat button that accepts translucent theme colors, so the popup acrylic
/// stays visible behind it. Plain Button rejects BackColor with alpha.
/// </summary>
internal sealed class GlassButton : Button
{
    public GlassButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
    }
}
