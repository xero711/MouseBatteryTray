using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MouseBatteryTray.Hardware;

namespace MouseBatteryTray.Tray;

internal static class TrayIconFactory
{
    private const int LowThreshold = 15;
    private const int MediumThreshold = 35;

    // 高解像度で描いてから実寸へ高品質縮小し、小さな丸みを滑らかにする。
    private const int Supersample = 4;

    // フィル色 (現在値)。暗いトラック上で遠目にも判別できる鮮やかさにする。
    private static readonly Color NormalFill = Color.FromArgb(62, 224, 118);
    private static readonly Color MediumFill = Color.FromArgb(255, 160, 36);
    private static readonly Color LowFill = Color.FromArgb(255, 66, 66);

    // 前回値 (スリープ等) のフィルは灰色で現在値と区別する。
    private static readonly Color StaleFill = Color.FromArgb(150, 155, 165);

    // シェル内側の暗い溝と、フィルが走るトラック。
    private static readonly Color TrackRim = Color.FromArgb(16, 18, 22);
    private static readonly Color TrackColor = Color.FromArgb(42, 46, 54);

    // 未取得時のシェルの灰色 (機種色を少し残して混ぜる)。
    private static readonly Color UnknownGray = Color.FromArgb(120, 124, 130);

    // 充電中は端子と稲妻をこの色にする。
    private static readonly Color ChargingColor = Color.FromArgb(255, 214, 40);
    private static readonly Color SleepIndicator = Color.FromArgb(125, 195, 255);
    private static readonly Color MissingIndicator = Color.FromArgb(202, 208, 218);

    private enum StateMark
    {
        None,
        Sleeping,
        Empty,
        NotDetected,
        Unknown
    }

    // glyph は互換のため引数に残すが描画しない (機種は accent のシェル/端子の色で識別)。
    public static Icon Create(BatteryReading reading, string glyph, Color accent)
    {
        // 通知領域が実際に使う小アイコン寸法で描画し、Windows による拡大縮小 (ぼやけ) を避ける。
        var small = SystemInformation.SmallIconSize;
        var size = Math.Clamp(Math.Max(small.Width, small.Height), 16, 64);

        var isCurrent = reading.Availability == BatteryAvailability.Available;
        var percent = reading.Percent is int raw ? Math.Clamp(raw, 0, 100) : (int?)null;
        var charging = isCurrent && reading.IsCharging == true;
        var stateMark = reading.Availability switch
        {
            BatteryAvailability.Sleeping => StateMark.Sleeping,
            BatteryAvailability.NotDetected => StateMark.NotDetected,
            BatteryAvailability.Unknown => StateMark.Unknown,
            BatteryAvailability.Available when percent == 0 => StateMark.Empty,
            _ => StateMark.None
        };

        Color shellColor;
        Color terminalColor;
        Color? fillColor = null;
        var emptyWarning = false;
        if (percent is int value)
        {
            shellColor = accent;
            if (value > 0)
            {
                fillColor = isCurrent ? FillColorFor(value) : StaleFill;
            }

            // 0% は塗らず、赤い端子とトラックの赤枠で警告する (残量を偽って塗らない)。
            emptyWarning = isCurrent && value == 0;
            terminalColor = charging ? ChargingColor : emptyWarning ? LowFill : accent;
        }
        else
        {
            // 未取得: 値を作らず、空の暗いトラックと灰色寄りのシェルにする。
            shellColor = Blend(accent, UnknownGray, 0.6f);
            terminalColor = charging ? ChargingColor : shellColor;
        }

        var hiSize = size * Supersample;
        using var canvas = new Bitmap(hiSize, hiSize, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;

            DrawBattery(graphics, size, shellColor, terminalColor, fillColor, percent ?? 0, emptyWarning, charging);
            DrawStateMark(graphics, size, stateMark);
        }

        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY); // 縁で外側の黒が混ざるのを防ぐ。
            graphics.DrawImage(canvas, new Rectangle(0, 0, size, size), 0, 0, hiSize, hiSize, GraphicsUnit.Pixel, attributes);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void DrawStateMark(Graphics graphics, int size, StateMark mark)
    {
        if (mark == StateMark.None)
        {
            return;
        }

        var unit = size * Supersample / 16f;
        var center = new PointF(size * Supersample - unit * 3.4f, unit * 3.55f);
        var radius = unit * 3.3f;

        if (mark == StateMark.NotDetected)
        {
            // A clear slash across the grey battery means the mouse itself was not detected.
            var start = new PointF(unit * 3.4f, unit * 11.2f);
            var end = new PointF(unit * 10.5f, unit * 4.1f);
            using (var edge = new Pen(TrackRim, unit * 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                graphics.DrawLine(edge, start, end);
            }

            using var slash = new Pen(MissingIndicator, unit * 0.95f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawLine(slash, start, end);
            return;
        }

        var badgeColor = mark switch
        {
            StateMark.Sleeping => SleepIndicator,
            StateMark.Empty => LowFill,
            _ => UnknownGray
        };
        var bounds = new RectangleF(center.X - radius, center.Y - radius, radius * 2, radius * 2);
        using (var badgeBrush = new SolidBrush(badgeColor))
        using (var badgeEdge = new Pen(TrackRim, unit * 0.8f))
        {
            graphics.FillEllipse(badgeBrush, bounds);
            graphics.DrawEllipse(badgeEdge, bounds);
        }

        if (mark == StateMark.Sleeping)
        {
            // A compact Z remains legible in the 16px shell and identifies the stale reading as sleep.
            using var zPen = new Pen(Color.White, unit * 1.15f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            var left = center.X - unit * 1.4f;
            var right = center.X + unit * 1.4f;
            var top = center.Y - unit * 1.3f;
            var bottom = center.Y + unit * 1.4f;
            graphics.DrawLine(zPen, left, top, right, top);
            graphics.DrawLine(zPen, right, top, left, bottom);
            graphics.DrawLine(zPen, left, bottom, right, bottom);
        }
        else if (mark == StateMark.Empty)
        {
            // A crisp exclamation is the warning mark for a confirmed 0%, separate from low-but-nonzero fill.
            using var markPen = new Pen(Color.White, unit * 1.05f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawLine(markPen, center.X, center.Y - unit * 1.45f, center.X, center.Y + unit * 0.25f);
            using var dotBrush = new SolidBrush(Color.White);
            graphics.FillEllipse(dotBrush, center.X - unit * 0.5f, center.Y + unit * 0.75f, unit, unit);
        }
        else
        {
            // Question mark means the first reading is pending or unavailable; it never implies an empty battery.
            using var questionPen = new Pen(Color.White, unit * 1.05f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var start = new PointF(center.X - unit * 0.9f, center.Y - unit * 0.95f);
            var c1 = new PointF(center.X - unit * 0.1f, center.Y - unit * 1.7f);
            var c2 = new PointF(center.X + unit * 1.15f, center.Y - unit * 0.75f);
            var end = new PointF(center.X + unit * 0.15f, center.Y + unit * 0.05f);
            graphics.DrawBezier(questionPen, start, c1, c2, end);
            graphics.DrawLine(questionPen, end.X, end.Y, end.X, center.Y + unit * 0.35f);
            using var dotBrush = new SolidBrush(Color.White);
            graphics.FillEllipse(dotBrush, center.X - unit * 0.4f, center.Y + unit * 0.8f, unit * 0.8f, unit * 0.8f);
        }
    }

    // ---- レイアウト (16 グリッド基準。16px では外形とフィル領域が整数ピクセルに揃う) ----
    //   シェル   : x 0-14, y 3-13 (14x10, 角丸 2.5)
    //   端子     : x 14-16, y 6-10 (右側の角を丸める)
    //   トラック : x 2-12, y 5-11 (10x6。16px では 1px = 10%)
    //   シェル縁は 1.5、縁とトラックの間に 0.5 の暗い溝。
    private static void DrawBattery(
        Graphics graphics,
        int size,
        Color shellColor,
        Color terminalColor,
        Color? fillColor,
        int percent,
        bool emptyWarning,
        bool charging)
    {
        var unit = size * Supersample / 16f;
        float Snap(double grid) => RoundInt(size * grid / 16.0) * Supersample;

        var hiSize = size * Supersample;
        var shell = RectangleF.FromLTRB(0, Snap(3), Snap(14), Snap(13));
        var terminal = RectangleF.FromLTRB(Snap(14) - unit, Snap(6), hiSize, Snap(10));
        var trackArea = RectangleF.FromLTRB(Snap(2), Snap(5), Snap(12), Snap(11));
        var rim = RectangleF.Inflate(trackArea, unit * 0.5f, unit * 0.5f);

        // 端子 (左端はシェルの下に潜らせて継ぎ目を出さない)。
        using (var terminalPath = RightRoundedRect(terminal, unit * 0.9f))
        using (var terminalBrush = VerticalGradient(terminal, Lighten(terminalColor, 0.2f), Darken(terminalColor, 0.1f)))
        {
            graphics.FillPath(terminalBrush, terminalPath);
        }

        // シェル: 機種色。上がわずかに明るい控えめなグラデーションで立体感だけ出す。
        using (var shellPath = RoundedRect(shell, unit * 2.5f))
        using (var shellBrush = VerticalGradient(shell, Lighten(shellColor, 0.22f), Darken(shellColor, 0.1f)))
        {
            graphics.FillPath(shellBrush, shellPath);
        }

        // 縁とトラックを分ける暗い溝。
        using (var rimPath = RoundedRect(rim, unit * 1.5f))
        using (var rimBrush = new SolidBrush(TrackRim))
        {
            graphics.FillPath(rimBrush, rimPath);
        }

        using var trackPath = RoundedRect(trackArea, unit * 1f);
        using (var trackBrush = new SolidBrush(TrackColor))
        {
            graphics.FillPath(trackBrush, trackPath);
        }

        if (fillColor is Color fill)
        {
            // 残量に比例して左から伸びる 1 本のフィル。右端は直線にして水位として読ませる。
            // 1% 以上は最低 1 グリッド (16px で 1px) を見せ、0% と区別する。
            var width = Math.Max(unit, trackArea.Width * percent / 100f);
            var fillRect = new RectangleF(trackArea.X, trackArea.Y, width, trackArea.Height);

            var state = graphics.Save();
            graphics.SetClip(trackPath);
            using (var fillBrush = VerticalGradient(fillRect, Lighten(fill, 0.18f), Darken(fill, 0.06f)))
            {
                graphics.FillRectangle(fillBrush, fillRect);
            }

            // 上端 1 グリッドだけの淡い艶。
            using (var glossBrush = new SolidBrush(Color.FromArgb(56, 255, 255, 255)))
            {
                graphics.FillRectangle(glossBrush, new RectangleF(fillRect.X, fillRect.Y, fillRect.Width, unit));
            }

            graphics.Restore(state);
        }
        else if (emptyWarning)
        {
            // 0%: 塗らずにトラックの内周を赤い細枠で囲む。
            var outline = RectangleF.Inflate(trackArea, -unit * 0.5f, -unit * 0.5f);
            using var outlinePath = RoundedRect(outline, unit * 0.6f);
            using var warningPen = new Pen(LowFill, unit);
            graphics.DrawPath(warningPen, outlinePath);
        }

        // 充電中は全サイズで稲妻を描く (黄色い端子と併用)。
        if (charging)
        {
            DrawBolt(graphics, trackArea, unit);
        }
    }

    // 稲妻はトラック右寄りに置き、左側の残量フィルをなるべく覆わない。
    // 16 グリッド基準で fill x 7.9-11.9, y 4.5-11.5 (縁取り込みで x 7.2-12.6, y 3.8-12.2)。
    private static void DrawBolt(Graphics graphics, RectangleF area, float unit)
    {
        var cx = area.Right - unit * 2.1f;
        var cy = area.Y + area.Height / 2f;

        // グリッド単位の稲妻形状。16px でも腕が約 1.2px 残る太さにする。
        (float X, float Y)[] shape =
        [
            (1.1f, -3.5f), (-2.0f, 0.6f), (-0.7f, 0.6f),
            (-1.1f, 3.5f), (2.0f, -0.6f), (0.7f, -0.6f),
        ];
        var points = shape.Select(p => new PointF(cx + p.X * unit, cy + p.Y * unit)).ToArray();

        using var path = new GraphicsPath();
        path.AddPolygon(points);

        // 外側へ約 0.7 グリッドの暗い縁取りで、緑/橙のフィル上でも輪郭を保つ。
        using (var outline = new Pen(TrackRim, unit * 1.4f) { LineJoin = LineJoin.Round })
        {
            graphics.DrawPath(outline, path);
        }

        using var brush = new SolidBrush(ChargingColor);
        graphics.FillPath(brush, path);
    }

    private static Color FillColorFor(int percent) => percent switch
    {
        <= LowThreshold => LowFill,
        <= MediumThreshold => MediumFill,
        _ => NormalFill,
    };

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (d <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }

        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static GraphicsPath RightRoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        path.AddLine(rect.Left, rect.Top, rect.Right - d / 2, rect.Top);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddLine(rect.Right - d / 2, rect.Bottom, rect.Left, rect.Bottom);
        path.CloseFigure();
        return path;
    }

    private static LinearGradientBrush VerticalGradient(RectangleF rect, Color top, Color bottom)
    {
        // 端で反対色が回り込まないよう、グラデーション範囲を少し広げる。
        var bounds = RectangleF.Inflate(rect, 0, 1);
        return new LinearGradientBrush(bounds, top, bottom, LinearGradientMode.Vertical);
    }

    private static Color Lighten(Color color, float amount) => Blend(color, Color.White, amount);

    private static Color Darken(Color color, float amount) => Blend(color, Color.Black, amount);

    private static int RoundInt(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private static Color Blend(Color from, Color to, float amount)
    {
        return Color.FromArgb(255,
            (int)(from.R + (to.R - from.R) * amount),
            (int)(from.G + (to.G - from.G) * amount),
            (int)(from.B + (to.B - from.B) * amount));
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
