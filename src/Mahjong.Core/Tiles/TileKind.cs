using System;

namespace Mahjong.Core.Tiles
{
    /// <summary>
    /// 34 种牌面。索引 0-8 = 一万~九万，9-17 = 一条~九条，18-26 = 一筒~九筒，
    /// 27-33 = 东、南、西、北、中、发、白。
    /// 引擎内部一律用 int（即 TileKind 值）或 int[34] 计数数组传递，判定算法不要操作 Tile 对象。
    /// </summary>
    public enum TileKind
    {
        // 万子 1-9
        Man1 = 0, Man2, Man3, Man4, Man5, Man6, Man7, Man8, Man9,
        // 条子 1-9
        Pin1 = 9, Pin2, Pin3, Pin4, Pin5, Pin6, Pin7, Pin8, Pin9,
        // 筒子 1-9
        Tong1 = 18, Tong2, Tong3, Tong4, Tong5, Tong6, Tong7, Tong8, Tong9,
        // 字牌
        East = 27, South, West, North, Zhong, Fa, Bai
    }

    /// <summary>TileKind 的扩展方法。</summary>
    public static class TileKindExtensions
    {
        /// <summary>牌面总数（34 种）。</summary>
        public const int KindCount = 34;

        /// <summary>每种牌的张数（4 张）。</summary>
        public const int CopiesPerKind = 4;

        /// <summary>是否为字牌（27-33）。</summary>
        public static bool IsHonor(this TileKind kind)
        {
            return (int)kind >= 27;
        }

        /// <summary>是否为数牌（万/条/筒，0-26）。</summary>
        public static bool IsNumber(this TileKind kind)
        {
            return (int)kind < 27;
        }

        /// <summary>花色。</summary>
        public static Suit GetSuit(this TileKind kind)
        {
            int k = (int)kind;
            if (k < 9) return Suit.Character;
            if (k < 18) return Suit.Bamboo;
            if (k < 27) return Suit.Dot;
            return Suit.Honor;
        }

        /// <summary>数牌的点数（1-9）；字牌返回 0。</summary>
        public static int GetRank(this TileKind kind)
        {
            int k = (int)kind;
            if (k < 27) return k % 9 + 1;
            return 0;
        }

        /// <summary>中文名（「五万」「三条」「東」「發」），用于 UI 大字显示与语音。</summary>
        public static string GetName(this TileKind kind)
        {
            int k = (int)kind;
            if (k < 27)
            {
                string[] ranks = { "一", "二", "三", "四", "五", "六", "七", "八", "九" };
                string suitName;
                Suit suit = kind.GetSuit();
                if (suit == Suit.Character) suitName = "万";
                else if (suit == Suit.Bamboo) suitName = "条";
                else suitName = "筒";
                return ranks[k % 9] + suitName;
            }
            switch (k)
            {
                case 27: return "东";
                case 28: return "南";
                case 29: return "西";
                case 30: return "北";
                case 31: return "中";
                case 32: return "发";
                case 33: return "白";
                default: throw new ArgumentOutOfRangeException("kind");
            }
        }
    }
}
