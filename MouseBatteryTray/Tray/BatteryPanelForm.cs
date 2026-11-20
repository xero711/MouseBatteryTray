using System.Drawing.Drawing2D;
using System.Drawing.Text;
using MouseBatteryTray.Hardware;

namespace MouseBatteryTray.Tray;

/// <summary>
/// トレイアイコンの左クリックで開閉する大きなバッテリー画面。
/// 表示する値は TrayApplicationContext から渡される BatteryReading だけで、値の補完や推測はしない。
/// </summary>
internal sealed class BatteryPanelForm : Form
{
    // 96 DPI 基準のレイアウト値 (px)
    private const int BaseWidth = 600;
    private const int OuterMargin = 16;
    private const int HeaderHeight = 40;
    private const int CardGap = 12;
    private const int CardHeight = 214;
    private const int CardPadding = 18;
    private const long ReopenGuardMilliseconds = 400;

    private static readonly Color BackgroundColor = Color.FromArgb(22, 25, 32);
    private static readonly Color BorderColor = Color.FromArgb(58, 64, 78);
    private static readonly Color CardColor = Color.FromArgb(34, 38, 48);
    private static readonly Color TrackColor = Color.FromArgb(58, 63, 76);
    private static readonly Color PrimaryTextColor = Color.FromArgb(242, 244, 248);
    private static readonly Color SecondaryTextColor = Color.FromArgb(160, 168, 184);
    private static readonly Color DimTextColor = Color.FromArgb(120, 127, 142);
    private static readonly Color LowColor = Color.FromArgb(255, 92, 100);
    private static readonly Color OkColor = Color.FromArgb(82, 211, 143);
    private static readonly Color ChargingColor = Color.FromArgb(255, 210, 90);
    private static readonly Color SleepColor = Color.FromArgb(150, 160, 200);

    private readonly List<string> _order = [];
    private readonly Dictionary<string, BatteryReading> _readings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Color> _accents = new(StringComparer.Ordinal);
    private AppText _text;

    private readonly Font _titleFont = new("Yu Gothic UI", 12f, FontStyle.Bold);
    private readonly Font _hintFont = new("Yu Gothic UI", 9f, FontStyle.Regular);
    private readonly Font _nameFont = new("Yu Gothic UI", 13f, FontStyle.Bold);
    private readonly Font _percentFont = new("Segoe UI", 32f, FontStyle.Bold);
    private readonly Font _statusFont = new("Yu Gothic UI", 11f, FontStyle.Bold);
    private readonly Font _detailFont = new("Yu Gothic UI", 9f, FontStyle.Regular);

    private bool _isRefreshing;
    private long _lastAutoHideTick;

    public BatteryPanelForm(IEnumerable<(BatteryReading Reading, Color Accent)> devices, ContextMenuStrip menu, AppText text)
    {
        _text = text;
        SetDevices(devices);

        Text = "Mouse Battery";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        BackColor = BackgroundColor;
        DoubleBuffered = true;
        ContextMenuStrip = menu;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        ApplyScaledSize();
    }

    public void SetDevices(IEnumerable<(BatteryReading Reading, Color Accent)> devices)
    {
        _order.Clear();
        _readings.Clear();
        _accents.Clear();
        foreach (var (reading, accent) in devices)
        {
            _order.Add(reading.DeviceId);
            _readings[reading.DeviceId] = reading;
            _accents[reading.DeviceId] = accent;
        }

        ApplyScaledSize();
        Invalidate();
    }

    public void SetLanguage(AppText text)
    {
        _text = text;
        Invalidate();
    }

    /// <summary>トレイアイコンの左クリックで呼ぶ。表示中なら閉じ、非表示なら開いて前面に出す。</summary>
    public void Toggle()
    {
        if (Visible)
        {
            Hide();
            return;
        }

        // トレイアイコンを押した瞬間にフォーカスが外れて自動で閉じた直後は、再表示しない (閉じる操作として扱う)
        if (Environment.TickCount64 - _lastAutoHideTick < ReopenGuardMilliseconds)
        {
            return;
        }

        ShowNearCursor();
    }

    public void UpdateReading(BatteryReading reading)
    {
        if (IsDisposed || !_readings.ContainsKey(reading.DeviceId))
        {
            return;
        }

        _readings[reading.DeviceId] = reading;
        Invalidate();
    }

    public void SetRefreshing(bool isRefreshing)
    {
        if (IsDisposed || _isRefreshing == isRefreshing)
        {
            return;
        }

        _isRefreshing = isRefreshing;
        Invalidate();
    }

    private void ShowNearCursor()
    {
        ApplyScaledSize();

        var cursor = Cursor.Position;
        var workingArea = Screen.FromPoint(cursor).WorkingArea;
        var offset = Px(12);

        // 通知領域は通常画面下端なのでカーソルの上に出す。上に入らなければ下に出す (タスクバーが上にある場合)。
        var x = cursor.X - Width / 2;
        var y = cursor.Y - Height - offset;
        if (y < workingArea.Top)
        {
            y = cursor.Y + offset;
        }

        // 作業領域 (タスクバーを除く画面) からはみ出さないように収める
        x = Math.Clamp(x, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - Width));
        y = Math.Clamp(y, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - Height));
        Location = new Point(x, y);

        Show();
        TopMost = true;
        BringToFront();
        Activate();
    }

    private void ApplyScaledSize()
    {
        var columns = _order.Count <= 1 ? 1 : 2;
        var rows = Math.Max(1, (int)Math.Ceiling(_order.Count / (double)columns));
        var height = OuterMargin + HeaderHeight + rows * CardHeight + Math.Max(0, rows - 1) * CardGap + OuterMargin;
        ClientSize = new Size(Px(BaseWidth), Px(height));
    }

    private float ScaleFactor => DeviceDpi / 96f;

    private int Px(int value) => (int)Math.Round(value * ScaleFactor);

    private float ScaleF(float value) => value * ScaleFactor;

    protected override CreateParams CreateParams
    {
        get
        {
            const int CS_DROPSHADOW = 0x00020000;
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (Visible)
        {
            _lastAutoHideTick = Environment.TickCount64;
            Hide();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            Hide();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Alt+F4 などでは破棄せず隠すだけにする (アプリ終了時の Dispose は別経路)
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyScaledSize();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        using (var borderPen = new Pen(BorderColor, 1f))
        {
            g.DrawRectangle(borderPen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        DrawHeader(g);

        var margin = ScaleF(OuterMargin);
        var cardTop = margin + ScaleF(HeaderHeight);
        var columns = _order.Count <= 1 ? 1 : 2;
        var gap = ScaleF(CardGap);
        var cardWidth = (ClientSize.Width - margin * 2 - gap * (columns - 1)) / columns;

        if (_order.Count == 0)
        {
            using var emptyBrush = new SolidBrush(SecondaryTextColor);
            using var emptyFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(_text.NoMouse, _statusFont, emptyBrush,
                new RectangleF(margin, cardTop, ClientSize.Width - margin * 2, ScaleF(CardHeight)), emptyFormat);
        }

        for (var i = 0; i < _order.Count; i++)
        {
            var id = _order[i];
            var column = i % columns;
            var row = i / columns;
            var rect = new RectangleF(
                margin + column * (cardWidth + gap),
                cardTop + row * (ScaleF(CardHeight) + gap),
                cardWidth,
                ScaleF(CardHeight));
            DrawCard(g, rect, _readings[id], _accents[id]);
        }
    }

    private void DrawHeader(Graphics g)
    {
        var margin = ScaleF(OuterMargin);
        var rect = new RectangleF(margin, margin, ClientSize.Width - margin * 2, ScaleF(HeaderHeight) - ScaleF(8));

        using var left = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        using var right = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        using var titleBrush = new SolidBrush(PrimaryTextColor);
        using var hintBrush = new SolidBrush(_isRefreshing ? ChargingColor : SecondaryTextColor);

        g.DrawString(_text.PanelTitle, _titleFont, titleBrush, rect, left);
        var hint = _isRefreshing ? _text.PanelRefreshing : _text.PanelHint;
        g.DrawString(hint, _hintFont, hintBrush, rect, right);
    }

    private void DrawCard(Graphics g, RectangleF card, BatteryReading reading, Color accent)
    {
        var radius = ScaleF(10);
        using (var cardPath = RoundedRect(card, radius))
        using (var cardBrush = new SolidBrush(CardColor))
        {
            g.FillPath(cardBrush, cardPath);

            // 上端のアクセント帯 (G502 X は青、LAMZU Maya は紫)
            var state = g.Save();
            g.SetClip(cardPath);
            using (var accentBrush = new SolidBrush(accent))
            {
                g.FillRectangle(accentBrush, card.X, card.Y, card.Width, ScaleF(5));
            }
            g.Restore(state);
        }

        var pad = ScaleF(CardPadding);
        var x = card.X + pad;
        var width = card.Width - pad * 2;
        var isLive = reading.Availability == BatteryAvailability.Available;

        using var nearNoWrap = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter
        };

        // 製品名
        using (var nameBrush = new SolidBrush(accent))
        {
            g.DrawString(reading.DisplayName, _nameFont, nameBrush, new RectangleF(x, card.Y + ScaleF(18), width, ScaleF(28)), nearNoWrap);
        }

        // 残量 % (未取得なら "--"。値を作らない)
        var percentText = reading.Percent is int p ? $"{p}%" : "--";
        var percentRect = new RectangleF(x, card.Y + ScaleF(50), width, ScaleF(62));
        var percentColor = isLive && reading.Percent is int currentPercent
            ? currentPercent <= 15 ? LowColor : PrimaryTextColor
            : DimTextColor;
        using (var percentBrush = new SolidBrush(percentColor))
        {
            g.DrawString(percentText, _percentFont, percentBrush, percentRect, nearNoWrap);
        }

        if (reading.IsCharging == true && isLive)
        {
            var textWidth = g.MeasureString(percentText, _percentFont).Width;
            DrawBolt(g, new RectangleF(x + textWidth + ScaleF(2), percentRect.Y + ScaleF(14), ScaleF(20), ScaleF(34)));
        }

        // ゲージ
        var gauge = new RectangleF(x, card.Y + ScaleF(120), width, ScaleF(16));
        using (var trackPath = RoundedRect(gauge, gauge.Height / 2))
        using (var trackBrush = new SolidBrush(TrackColor))
        {
            g.FillPath(trackBrush, trackPath);
        }

        if (reading.Percent is int percent && percent > 0)
        {
            var fillWidth = Math.Max(gauge.Height, gauge.Width * Math.Clamp(percent, 0, 100) / 100f);
            var fill = new RectangleF(gauge.X, gauge.Y, fillWidth, gauge.Height);
            var fillColor = !isLive
                ? Color.FromArgb(110, accent)
                : percent <= 15 ? LowColor : accent;
            using var fillPath = RoundedRect(fill, gauge.Height / 2);
            using var fillBrush = new SolidBrush(fillColor);
            g.FillPath(fillBrush, fillPath);
        }
        else if (isLive && reading.Percent == 0)
        {
            // Confirmed empty is distinct from an unknown or sleeping battery.
            using var emptyPath = RoundedRect(gauge, gauge.Height / 2);
            using var warningPen = new Pen(LowColor, ScaleF(1.5f));
            g.DrawPath(warningPen, emptyPath);
        }

        // 状態
        var (statusText, statusColor) = DescribeStatus(reading);
        var statusRect = new RectangleF(x, card.Y + ScaleF(148), width, ScaleF(26));
        var dot = ScaleF(10);
        using (var dotBrush = new SolidBrush(statusColor))
        {
            g.FillEllipse(dotBrush, statusRect.X, statusRect.Y + (statusRect.Height - dot) / 2, dot, dot);
        }
        using (var statusBrush = new SolidBrush(statusColor))
        {
            var textRect = new RectangleF(statusRect.X + dot + ScaleF(8), statusRect.Y, statusRect.Width - dot - ScaleF(8), statusRect.Height);
            g.DrawString(statusText, _statusFont, statusBrush, textRect, nearNoWrap);
        }

        // 取得時刻と補足
        var detail = reading.LastUpdated is DateTimeOffset updated
            ? _text.Updated(updated.ToLocalTime())
            : _text.NoReading;
        if (!isLive && !string.IsNullOrWhiteSpace(reading.Detail))
        {
            detail += $"・{reading.Detail}";
        }

        using (var detailBrush = new SolidBrush(SecondaryTextColor))
        {
            g.DrawString(detail, _detailFont, detailBrush, new RectangleF(x, card.Y + ScaleF(180), width, ScaleF(22)), nearNoWrap);
        }
    }

    private (string Text, Color Color) DescribeStatus(BatteryReading reading) => _text.PanelStatus(reading);

    private static void DrawBolt(Graphics g, RectangleF r)
    {
        PointF[] bolt =
        [
            new(r.X + r.Width * 0.62f, r.Y),
            new(r.X, r.Y + r.Height * 0.56f),
            new(r.X + r.Width * 0.45f, r.Y + r.Height * 0.56f),
            new(r.X + r.Width * 0.32f, r.Bottom),
            new(r.Right, r.Y + r.Height * 0.40f),
            new(r.X + r.Width * 0.55f, r.Y + r.Height * 0.40f)
        ];
        using var brush = new SolidBrush(ChargingColor);
        g.FillPolygon(brush, bolt);
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // メニューは TrayApplicationContext が所有しているのでここでは破棄しない
            ContextMenuStrip = null;
            _titleFont.Dispose();
            _hintFont.Dispose();
            _nameFont.Dispose();
            _percentFont.Dispose();
            _statusFont.Dispose();
            _detailFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
