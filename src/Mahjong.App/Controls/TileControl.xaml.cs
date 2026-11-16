using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Mahjong.App.Services;
using Mahjong.Core.Tiles;

namespace Mahjong.App.Controls
{
    /// <summary>
    /// 牌的朝向（顺时针角度，对应牌面图案旋转量）。
    ///
    /// 麻将桌是四方对坐：只有己方的牌是正立的，对家看到的牌是倒的，
    /// 左右两家的牌是横躺的。原先四家牌河全部按正立摆放，是最明显的穿帮点。
    /// </summary>
    public enum TileFacing
    {
        /// <summary>己方（南）：正立。</summary>
        South = 0,
        /// <summary>右家（东）：牌顶朝右，顺时针 90°。</summary>
        East = 90,
        /// <summary>对家（北）：牌顶朝己，倒置 180°。</summary>
        North = 180,
        /// <summary>左家（西）：牌顶朝左，逆时针 90°。</summary>
        West = 270,
    }

    /// <summary>
    /// 麻将牌控件。
    ///
    /// 牌体改由离线预渲染的高清贴图承担（象牙白倒角牌身、白面/墨绿背壳分层、
    /// 材质噪点、阴刻填漆花色），运行时只做一次 DrawImage，不再逐帧矢量自绘。
    /// 本控件负责：接地阴影、状态光（悬停/选中/弃牌/胡牌）、抬起动画与降级绘制。
    /// </summary>
    public class TileControl : ContentControl
    {
        // 接地阴影笔刷与控件尺寸无关，静态共享并冻结，避免每张牌每帧重建
        private static readonly RadialGradientBrush AmbientShadow = MakeShadow(
            new GradientStop(Color.FromArgb(0x3C, 0, 0, 0), 0.0),
            new GradientStop(Color.FromArgb(0x18, 0, 0, 0), 0.55),
            new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1.0));
        private static readonly RadialGradientBrush ContactShadow = MakeShadow(
            new GradientStop(Color.FromArgb(0x78, 0, 0, 0), 0.0),
            new GradientStop(Color.FromArgb(0x18, 0, 0, 0), 0.55),
            new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1.0));

        private static RadialGradientBrush MakeShadow(params GradientStop[] stops)
        {
            var b = new RadialGradientBrush();
            foreach (var s in stops) b.GradientStops.Add(s);
            b.Freeze();
            return b;
        }

        // ===== 牌面种类 =====
        public TileKind Kind
        {
            get { return (TileKind)GetValue(KindProperty); }
            set { SetValue(KindProperty, value); }
        }
        public static readonly DependencyProperty KindProperty =
            DependencyProperty.Register(nameof(Kind), typeof(TileKind), typeof(TileControl),
                new PropertyMetadata(TileKind.Man1, OnKindChanged));
        private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((TileControl)d).InvalidateVisual();
        }

        // ===== 朝向（四家牌河方向不同） =====
        public TileFacing Facing
        {
            get { return (TileFacing)GetValue(FacingProperty); }
            set { SetValue(FacingProperty, value); }
        }
        public static readonly DependencyProperty FacingProperty =
            DependencyProperty.Register(nameof(Facing), typeof(TileFacing), typeof(TileControl),
                new PropertyMetadata(TileFacing.South, (d, e) => ((TileControl)d).InvalidateVisual()));

        // ===== 是否正面朝上（false = 显示墨绿牌背，用于对家立牌） =====
        public bool IsFaceUp
        {
            get { return (bool)GetValue(IsFaceUpProperty); }
            set { SetValue(IsFaceUpProperty, value); }
        }
        public static readonly DependencyProperty IsFaceUpProperty =
            DependencyProperty.Register(nameof(IsFaceUp), typeof(bool), typeof(TileControl),
                new PropertyMetadata(true, (d, e) => ((TileControl)d).InvalidateVisual()));

        // ===== 选中（点击/高亮） =====
        public bool IsSelected
        {
            get { return (bool)GetValue(IsSelectedProperty); }
            set { SetValue(IsSelectedProperty, value); }
        }
        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(TileControl),
                new PropertyMetadata(false, OnIsSelectedChanged));
        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var tc = (TileControl)d;
            tc.UpdateLift();
            tc.InvalidateVisual();
        }

        // 牌河「刚打出」的牌不再由本控件画红色描边 / 光晕：
        // 改成由 MainWindow.xaml 在牌河模板里给那张牌加一点摆放角度（略微歪着），
        // 下一轮状态复位就自动摆正。控件只负责下面「刚摸上手牌」的金色标记。

        // ===== 刚摸上来（己方手牌最右侧那张单独摆的新牌） =====
        public bool IsJustDrawn
        {
            get { return (bool)GetValue(IsJustDrawnProperty); }
            set { SetValue(IsJustDrawnProperty, value); }
        }
        public static readonly DependencyProperty IsJustDrawnProperty =
            DependencyProperty.Register(nameof(IsJustDrawn), typeof(bool), typeof(TileControl),
                new PropertyMetadata(false, (d, e) => ((TileControl)d).InvalidateVisual()));

        // ===== 胡牌（金色呼吸光脉冲） =====
        public bool WinFlash
        {
            get { return (bool)GetValue(WinFlashProperty); }
            set { SetValue(WinFlashProperty, value); }
        }
        public static readonly DependencyProperty WinFlashProperty =
            DependencyProperty.Register(nameof(WinFlash), typeof(bool), typeof(TileControl),
                new PropertyMetadata(false, OnWinFlashChanged));
        private static void OnWinFlashChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var tc = (TileControl)d;
            if ((bool)e.NewValue) tc.StartWinPulse();
            else tc.StopWinPulse();
        }

        // ===== 悬停（鼠标移入手牌） =====
        private bool _isHover;

        // ===== 动画用光强（0~1），由 Storyboard 驱动 =====
        public double GlowIntensity
        {
            get { return (double)GetValue(GlowIntensityProperty); }
            set { SetValue(GlowIntensityProperty, value); }
        }
        public static readonly DependencyProperty GlowIntensityProperty =
            DependencyProperty.Register(nameof(GlowIntensity), typeof(double), typeof(TileControl),
                new PropertyMetadata(0.0, (d, e) => ((TileControl)d).InvalidateVisual()));

        public TileControl()
        {
            DefaultStyleKey = typeof(TileControl);
            // 贴图普遍是缩小显示（源 256x352，控件 34~66 宽），高质量缩放避免锯齿
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        }

        protected override void OnMouseEnter(System.Windows.Input.MouseEventArgs e)
        {
            base.OnMouseEnter(e);
            _isHover = true;
            UpdateLift();
            InvalidateVisual();
        }

        protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            _isHover = false;
            UpdateLift();
            InvalidateVisual();
        }

        // 抬起动画：选中 -14，悬停 -8，否则 0
        private TranslateTransform _lift;
        private void UpdateLift()
        {
            double toY = IsSelected ? -14 : (_isHover ? -8 : 0);
            if (_lift == null) { _lift = new TranslateTransform(); RenderTransform = _lift; }
            var da = new DoubleAnimation(toY, TimeSpan.FromMilliseconds(130))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            Storyboard.SetTarget(da, _lift);
            Storyboard.SetTargetProperty(da, new PropertyPath(TranslateTransform.YProperty));
            new Storyboard { Children = { da } }.Begin();
        }

        // 胡牌持续呼吸光（金）
        private Storyboard _winSb;
        private void StartWinPulse()
        {
            StopWinPulse();
            var da = new DoubleAnimation(0.35, 1.0, TimeSpan.FromMilliseconds(650))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(da, this);
            Storyboard.SetTargetProperty(da, new PropertyPath(GlowIntensityProperty));
            _winSb = new Storyboard { Children = { da } };
            _winSb.Begin();
        }
        private void StopWinPulse()
        {
            _winSb?.Stop();
            _winSb = null;
            SetValue(GlowIntensityProperty, 0.0);
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double w = ActualWidth > 0 ? ActualWidth : (Width > 0 ? Width : 100);
            double h = ActualHeight > 0 ? ActualHeight : (Height > 0 ? Height : 138);
            if (w <= 0 || h <= 0) return;

            // 侧向（东/西家）时牌的横竖关系互换：控件是横的，贴图是竖的
            bool sideways = Facing == TileFacing.East || Facing == TileFacing.West;
            double dw = sideways ? h : w;
            double dh = sideways ? w : h;

            double radius = Math.Max(5, Math.Min(dw, dh) * 0.16);

            dc.PushTransform(BuildOrientation(w, h, dw, dh));
            try
            {
                // 1) 状态外发光（悬停 / 选中 / 弃牌 / 胡牌）
                DrawGlow(dc, dw, dh, radius);

                // 2) 接地阴影：大而淡的环境阴影 + 紧贴牌底的接触阴影
                DrawContactShadow(dc, dw, dh);

                // 3) 牌体贴图（正面 / 牌背）
                var bitmap = TileArtwork.Get(Kind, IsFaceUp);
                if (bitmap != null)
                    dc.DrawImage(bitmap, new Rect(0, 0, dw, dh));
                else
                    DrawFallback(dc, dw, dh);

                // 4) 刚摸上来的手牌：金色描边 + 极淡色晕 + 常亮柔光，直到下一轮归位。
                //    牌河里「刚打出」的牌不在这里上色，靠模板给它一个摆放角度来区分。
                if (IsJustDrawn)
                {
                    Color edge = GetResourceColor("TileHighlightColor", Colors.Gold);
                    var tint = new SolidColorBrush(Color.FromArgb(0x20, edge.R, edge.G, edge.B));
                    var frame = new Pen(new SolidColorBrush(edge), 3.2);
                    var box = new Rect(2, 2, dw - 4, dh * 0.94 - 4);
                    dc.DrawRoundedRectangle(tint, frame, box, radius, radius);
                }

                // 5) 选中金环
                if (IsSelected)
                {
                    var sel = new Pen(new SolidColorBrush(GetResourceColor("TileHighlightColor", Colors.Gold)), 2.5);
                    dc.DrawRoundedRectangle(null, sel,
                        new Rect(2.5, 2.5, dw - 5, dh * 0.94 - 2.5), radius, radius);
                }
            }
            finally
            {
                dc.Pop();
            }
        }

        /// <summary>
        /// 把 dw×dh 的绘制内容转成控件 w×h 里的朝向。
        /// 顺序必须呈「平移到原点 → 旋转 → 移回控件中心」，
        /// 否则 90° 旋转后画面会跑到控件外面（角点落在负坐标）。
        /// </summary>
        private Transform BuildOrientation(double w, double h, double dw, double dh)
        {
            var tg = new TransformGroup();
            tg.Children.Add(new TranslateTransform(-dw / 2, -dh / 2));
            if (Facing != TileFacing.South)
                tg.Children.Add(new RotateTransform((double)Facing));
            tg.Children.Add(new TranslateTransform(w / 2, h / 2));
            return tg;
        }

        /// <summary>
        /// 接地阴影：牌必须"压"在桌面上才有重量感。
        /// 两层径向渐变椭圆，中心放在牌底略下方，上半被牌体自然遮住，
        /// 只露出边缘一圈柔化暗晕，模拟接触阴影 + 环境遮蔽。
        /// </summary>
        private static void DrawContactShadow(DrawingContext dc, double w, double h)
        {
            dc.DrawEllipse(AmbientShadow, null, new Point(w * 0.5, h * 0.99), w * 0.62, h * 0.13);
            dc.DrawEllipse(ContactShadow, null, new Point(w * 0.5, h * 0.995), w * 0.45, h * 0.055);
        }

        private void DrawGlow(DrawingContext dc, double w, double h, double radius)
        {
            double a = 0;
            Color c = GetResourceColor("TileHighlightColor", Colors.Gold);
            if (WinFlash) { c = GetResourceColor("TileHighlightColor", Colors.Gold); a = Math.Max(a, GlowIntensity); }
            // 新摸的牌：常亮金色柔光。胡牌呼吸光优先，别把 0.35→1.0 的呼吸垫平。
            if (IsJustDrawn && !WinFlash) { c = GetResourceColor("TileHighlightColor", Colors.Gold); a = Math.Max(a, 0.55); }
            if (IsSelected) { c = GetResourceColor("TileHighlightColor", Colors.Gold); a = Math.Max(a, 0.85); }
            if (_isHover) { c = Color.FromRgb(150, 205, 255); a = Math.Max(a, 0.5); }
            if (a <= 0.01) return;

            for (int i = 3; i >= 1; i--)
            {
                byte alpha = (byte)Math.Min(255, a * 80 / i);
                var col = Color.FromArgb(alpha, c.R, c.G, c.B);
                dc.DrawRoundedRectangle(new SolidColorBrush(col), null,
                    new Rect(-i * 3, -i * 3, w + i * 6, h + i * 6), radius + i * 3, radius + i * 3);
            }
        }

        /// <summary>贴图资源缺失时的降级绘制，保证界面不会变成一片空白。</summary>
        private void DrawFallback(DrawingContext dc, double w, double h)
        {
            double depth = h * 0.08;
            double radius = Math.Max(5, Math.Min(w, h) * 0.16);
            var body = new LinearGradientBrush(
                IsFaceUp ? Colors.White : Color.FromRgb(0x20, 0x64, 0x50),
                IsFaceUp ? Colors.WhiteSmoke : Color.FromRgb(0x12, 0x3E, 0x32),
                new Point(0, 0), new Point(0, 1));
            dc.DrawRoundedRectangle(body,
                new Pen(new SolidColorBrush(Color.FromRgb(0xD2, 0xD2, 0xC8)), 1.2),
                new Rect(0, 0, w, h - depth), radius, radius);

            if (!IsFaceUp) return;
            var face = new Typeface(new FontFamily("SimSun"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var ft = new FormattedText(Kind.GetName(), CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, face, Math.Min(w, h) * 0.34,
                new SolidColorBrush(Color.FromRgb(0xA3, 0x2D, 0x2D)), 1.0);
            dc.DrawText(ft, new Point((w - ft.Width) / 2, (h - depth - ft.Height) / 2));
        }

        private Color GetResourceColor(string key, Color fallback)
        {
            var value = this.TryFindResource(key);
            if (value is SolidColorBrush brush) return brush.Color;
            if (value is Color color) return color;
            return fallback;
        }
    }
}
