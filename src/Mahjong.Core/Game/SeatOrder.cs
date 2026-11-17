using System;

namespace Mahjong.Core.Game
{
    /// <summary>
    /// 屏幕座次与行牌顺序的唯一映射。
    ///
    /// 屏幕方位（俯视）：0=南（下 / 玩家）、1=北（上 / 对家）、2=东（右）、3=西（左）。
    /// 注意：这里的「东南西北」只是屏幕方位标签，不是麻将的「门风」编号。
    ///
    /// 规则依据：《中国麻将竞赛规则》「行牌依座次的逆时针方向进行」，
    /// 庄家的下家在其右手侧、上家在左手侧。玩家坐在屏幕下方、面朝桌心时，
    /// 右手边即屏幕右侧，所以俯视逆时针 = 下→右→上→左 = 0→2→1→3→0。
    /// 从庄家起门风依次 东→南→西→北 对应 0→2→1→3；
    /// 切勿按屏幕方位字面去读「东→南→西→北」（右→下→左→上是顺时针，会刚好搞反）。
    ///
    /// 引擎轮转、发牌/抓牌的家次、声明征询的先后、一炮多响「最近下家」的距离
    /// 都走这里，避免多处各写一份 (p + 1) % 4 导致口径分叉。
    /// </summary>
    public static class SeatOrder
    {
        /// <summary>按行牌方向（俯视逆时针）绕桌一圈的座位索引。</summary>
        private static readonly int[] Ring = { 0, 2, 1, 3 };

        /// <summary>座位索引 → 在 Ring 中的位置，由 Ring 反推以免两处写错。</summary>
        private static readonly int[] Position = BuildPosition();

        private static int[] BuildPosition()
        {
            var position = new int[4];
            for (int i = 0; i < Ring.Length; i++) position[Ring[i]] = i;
            return position;
        }

        /// <summary>当前出牌者沿逆时针座次的下一家（右手侧下家）。</summary>
        public static int NextSeat(int playerIndex)
        {
            if (playerIndex < 0 || playerIndex > 3)
                throw new ArgumentOutOfRangeException("playerIndex");
            return Ring[(Position[playerIndex] + 1) % 4];
        }

        /// <summary>从 from 沿行牌方向走到 to 需要几步（0=同一家，1=最近下家，最大 3）。</summary>
        public static int Distance(int from, int to)
        {
            if (from < 0 || from > 3 || to < 0 || to > 3)
                throw new ArgumentOutOfRangeException("seat");
            return (Position[to] - Position[from] + 4) % 4;
        }
    }
}
