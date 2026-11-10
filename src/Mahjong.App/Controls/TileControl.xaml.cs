using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mahjong.Core.Tiles;

namespace Mahjong.App.Controls
{
    /// <summary>
    /// 麻将牌控件：矢量自绘立体牌身 + 真实花色牌面（腾讯欢乐麻将风格）。
    /// 依据 ElderTheme.xaml 中的尺寸/颜色资源。
    /// </summary>
    public class TileControl : ContentControl
    {
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
            ((TileControl)d).InvalidateVisual();
        }

        public bool JustDiscarded
        {
            get { return (bool)GetValue(JustDiscardedProperty); }
            set { SetValue(JustDiscardedProperty, value); }
        }
        public static readonly DependencyProperty JustDiscardedProperty =
            DependencyProperty.Register(nameof(JustDiscarded), typeof(bool), typeof(TileControl),
                new PropertyMetadata(false));

        public TileControl()
        {
            DefaultStyleKey = typeof(TileControl);
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double w = ActualWidth > 0 ? ActualWidth : (Width > 0 ? Width : 100);
            double h = ActualHeight > 0 ? ActualHeight : 138;
            if (w <= 0 || h <= 0) return;

            double radius = Math.Max(4, Math.Min(w, h) * 0.12);

            // 投影（向右下偏移）
            Color shadowColor = GetResourceColor("TileShadowColor", Color.FromArgb(0x3D, 0, 0, 0));
            dc.DrawRoundedRectangle(new SolidColorBrush(shadowColor), null,
                new Rect(2.5, 4, w - 2, h - 2), radius, radius);

            // 牌身：上亮下暗的立体渐变
            Color top = GetResourceColor("TileTopColor", Colors.White);
            Color bottom = GetResourceColor("TileBottomColor", Colors.LightGray);
            var bodyBrush = new LinearGradientBrush(top, bottom, new Point(0, 0), new Point(0, 1));

            Color borderColor = IsSelected ? GetResourceColor("TileSelectedBorderColor", Colors.Orange)
                                : JustDiscarded ? GetResourceColor("TileDiscardHighlightColor", Colors.Red)
                                : GetResourceColor("TileBorderColor", Colors.Gray);
            var borderPen = new Pen(new SolidColorBrush(borderColor), IsSelected || JustDiscarded ? 3 : 1.5);

            dc.DrawRoundedRectangle(bodyBrush, borderPen, new Rect(0, 0, w, h), radius, radius);

            // 顶部高光带（白到透明），增强立体感
            var hiBrush = new LinearGradientBrush(
                Color.FromArgb(200, 255, 255, 255), Color.FromArgb(0, 255, 255, 255),
                new Point(0, 0), new Point(0, 0.55));
            dc.DrawRoundedRectangle(hiBrush, null,
                new Rect(2, 2, w - 4, (h - 4) * 0.42), radius * 0.8, radius * 0.8);

            // 牌面内容
            DrawFace(dc, w, h);

            // 选中 / 刚打出 高亮框
            if (IsSelected)
            {
                var selPen = new Pen(new SolidColorBrush(GetResourceColor("TileHighlightColor", Colors.Gold)), 3);
                dc.DrawRoundedRectangle(null, selPen, new Rect(3, 3, w - 6, h - 6), radius, radius);
            }
            if (JustDiscarded)
            {
                var flashPen = new Pen(new SolidColorBrush(GetResourceColor("TileWinFlashColor", Colors.Red)), 3);
                dc.DrawRoundedRectangle(null, flashPen, new Rect(2, 2, w - 4, h - 4), radius, radius);
            }
        }

        private void DrawFace(DrawingContext dc, double w, double h)
        {
            var suit = Kind.GetSuit();
            if (suit == Suit.Character) DrawCharacter(dc, w, h);
            else if (suit == Suit.Bamboo) DrawBamboo(dc, w, h);
            else if (suit == Suit.Dot) DrawDot(dc, w, h);
            else DrawHonor(dc, w, h);
        }

        // 万子：红色「数字汉字 + 萬」
        private void DrawCharacter(DrawingContext dc, double w, double h)
        {
            int rank = Kind.GetRank();
            string[] digits = { "", "一", "二", "三", "四", "五", "六", "七", "八", "九" };
            Color red = GetSuitColor();
            var face = new Typeface(new FontFamily("SimSun"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

            double bigSize = Math.Min(w, h) * 0.5;
            var ftBig = new FormattedText(digits[rank], CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, face, bigSize, new SolidColorBrush(red));
            double x1 = (w - ftBig.Width) / 2;
            double y1 = h * 0.10;
            dc.DrawText(ftBig, new Point(x1, y1));

            double smallSize = bigSize * 0.62;
            var ftSmall = new FormattedText("萬", CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, face, smallSize, new SolidColorBrush(red));
            double x2 = (w - ftSmall.Width) / 2;
            double y2 = y1 + ftBig.Height * 0.80;
            dc.DrawText(ftSmall, new Point(x2, y2));
        }

        // 条子：绿色竹节阵列
        private void DrawBamboo(DrawingContext dc, double w, double h)
        {
            int n = Kind.GetRank();
            Color green = GetSuitColor();
            DrawSymbolGrid(dc, w, h, n, (cx, cy, r) =>
            {
                double bw = r * 0.52, bh = r * 1.7;
                dc.DrawRoundedRectangle(new SolidColorBrush(green), null,
                    new Rect(cx - bw / 2, cy - bh / 2, bw, bh), bw * 0.4, bw * 0.4);
                // 顶部小叶
                dc.DrawEllipse(new SolidColorBrush(green), null,
                    new Point(cx, cy - bh / 2 - r * 0.14), r * 0.24, r * 0.24);
            });
        }

        // 筒子：蓝色同心圆阵列
        private void DrawDot(DrawingContext dc, double w, double h)
        {
            int n = Kind.GetRank();
            Color blue = GetSuitColor();
            DrawSymbolGrid(dc, w, h, n, (cx, cy, r) =>
            {
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(blue), Math.Max(1, r * 0.16)),
                    new Point(cx, cy), r * 0.64, r * 0.64);
                dc.DrawEllipse(new SolidColorBrush(blue), null,
                    new Point(cx, cy), r * 0.26, r * 0.26);
            });
        }

        // 在牌面内按真实麻将排布把 n 个符号画成网格
        private void DrawSymbolGrid(DrawingContext dc, double w, double h, int n, Action<double, double, double> draw)
        {
            int[] rows = PatternFor(n);
            int maxCols = 0;
            foreach (var r in rows) maxCols = Math.Max(maxCols, r);

            double padX = w * 0.14, padY = h * 0.10;
            double areaW = w - padX * 2, areaH = h - padY * 2;
            double cellW = areaW / maxCols;
            double cellH = areaH / rows.Length;
            double symR = Math.Min(cellW, cellH) * 0.42;

            for (int i = 0; i < rows.Length; i++)
            {
                int cnt = rows[i];
                double rowW = cnt * cellW;
                double startX = (w - rowW) / 2 + cellW / 2;
                double cy = padY + cellH * i + cellH / 2;
                for (int j = 0; j < cnt; j++)
                {
                    draw(startX + j * cellW, cy, symR);
                }
            }
        }

        // 真实麻将 1-9 的行列排布
        private static int[] PatternFor(int n)
        {
            switch (n)
            {
                case 1: return new[] { 1 };
                case 2: return new[] { 2 };
                case 3: return new[] { 3 };
                case 4: return new[] { 2, 2 };
                case 5: return new[] { 2, 3 };
                case 6: return new[] { 3, 3 };
                case 7: return new[] { 3, 4 };
                case 8: return new[] { 4, 4 };
                case 9: return new[] { 3, 3, 3 };
                default: return new[] { n };
            }
        }

        // 字牌：风牌蓝 / 中红 / 發绿 / 白板蓝框
        private void DrawHonor(DrawingContext dc, double w, double h)
        {
            string name = Kind.GetName();
            if (name == "白")
            {
                var pen = new Pen(new SolidColorBrush(GetResourceColor("SuitColor_Tong", Colors.Blue)), 2.5);
                double inset = Math.Min(w, h) * 0.18;
                dc.DrawRoundedRectangle(null, pen, new Rect(inset, inset, w - inset * 2, h - inset * 2), 6, 6);
                return;
            }

            Color c = GetHonorColor();
            var face = new Typeface(new FontFamily("SimSun"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            double size = Math.Min(w, h) * 0.62;
            var ft = new FormattedText(name, CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, face, size, new SolidColorBrush(c));
            double x = (w - ft.Width) / 2;
            double y = (h - ft.Height) / 2 + ft.Height * 0.06;
            dc.DrawText(ft, new Point(x, y));
        }

        private Color GetHonorColor()
        {
            switch (Kind)
            {
                case TileKind.Zhong: return GetResourceColor("SuitColor_Man", Colors.Red);
                case TileKind.Fa: return GetResourceColor("SuitColor_Pin", Colors.Green);
                default: return GetResourceColor("SuitColor_Tong", Colors.Blue);
            }
        }

        private Color GetSuitColor()
        {
            var suit = Kind.GetSuit();
            if (suit == Suit.Character) return GetResourceColor("SuitColor_Man", Colors.DarkRed);
            if (suit == Suit.Bamboo) return GetResourceColor("SuitColor_Pin", Colors.DarkGreen);
            if (suit == Suit.Dot) return GetResourceColor("SuitColor_Tong", Colors.DarkBlue);
            return GetResourceColor("SuitColor_Honor", Colors.Black);
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
