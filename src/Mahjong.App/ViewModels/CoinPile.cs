namespace Mahjong.App.ViewModels
{
    /// <summary>
    /// 余额 -> 一摞金币的高度档位。
    ///
    /// 设计取舍：桌面上每人显示「一摞金币 + 精确数字」。摞的层数只表示
    /// 「穷 / 一般 / 富裕」，按区间分档、不保证与余额成精确比例（用户明确说
    /// 不用很精确）；具体数量交给旁边的数字，ToolTip 再补一句完整说明。
    /// 初始余额 20（RuleConfig.InitialCoins）落在 2 层，四家开局对称。
    ///
    /// 档位从 8 收到 5：界面图标只有 40px，8 层时每枚币仅 3px，画不出
    /// 参考插画里「下弯的椭圆顶缘 + 滚花段」，只会挤成一排直条栅栏。
    /// 5 层时每枚币有 10px，弧度和滚花都立得住，层间高度差反而更明显。
    /// </summary>
    public static class CoinPile
    {
        private const string BaseUri = "pack://application:,,,/Assets/Coins/";
        public const int MaxLayers = 5;

        public static bool IsBust(int coins) { return coins <= 0; }

        /// <summary>摞的层数（1~5）。余额耗尽时给 1 层灰色矮摞。</summary>
        public static int LayerCount(int coins)
        {
            if (coins <= 0) return 1;
            if (coins <= 10) return 1;
            if (coins <= 20) return 2;
            if (coins <= 35) return 3;
            if (coins <= 55) return 4;
            return 5;
        }

        /// <summary>按档位返回金币摞图片的绝对 pack URI（WPF 会把字符串转成 BitmapImage）。</summary>
        public static string StackSource(int coins)
        {
            return BaseUri + (IsBust(coins) ? "stack_bust.png" : "stack_" + LayerCount(coins) + ".png");
        }

        /// <summary>徽章旁边的精确数量（负数照实显示）。</summary>
        public static string AmountText(int coins)
        {
            return coins.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
