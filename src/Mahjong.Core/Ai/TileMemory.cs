using System;
using System.Collections.Generic;
using Mahjong.Core.Game;
using Mahjong.Core.Tiles;

namespace Mahjong.Core.Ai
{
    /// <summary>
    /// 记牌器：基于牌河 + 副露 + 自己手牌，计算每种牌外面还剩几张。
    /// 只用公开信息，不偷看暗牌。
    /// </summary>
    public sealed class TileMemory
    {
        private readonly int[] _visible = new int[34];

        /// <summary>每种牌的可见剩余张数（0-4）。</summary>
        public int[] Visible { get { return _visible; } }

        /// <summary>初始化：全部 4 张可见。</summary>
        public TileMemory()
        {
            for (int k = 0; k < 34; k++) _visible[k] = 4;
        }

        /// <summary>记录一张已可见的牌（牌河 / 自己手牌 / 副露），外面剩余减 1，不低于 0。</summary>
        public void RecordDiscard(Tile tile)
        {
            int k = (int)tile.Kind;
            if (_visible[k] > 0) _visible[k]--;
        }

        /// <summary>记录一组副露（碰/杠）。</summary>
        public void RecordMeld(Meld meld)
        {
            foreach (var t in meld.Tiles)
            {
                int k = (int)t.Kind;
                if (_visible[k] > 0) _visible[k]--;
            }
        }

        /// <summary>
        /// 每次决策前用公开信息重建可见牌：自己手牌 + 牌河 + 四家副露。
        /// 外面剩余 = 4 - 上述可见张数。只用引擎给出的公开信息，不偷看别家暗牌。
        /// </summary>
        public void Rebuild(IList<Tile> hand, IList<Tile> discards, IList<Meld>[] allMelds)
        {
            for (int k = 0; k < 34; k++) _visible[k] = 4;
            if (hand != null) foreach (var t in hand) RecordDiscard(t);
            if (discards != null) foreach (var t in discards) RecordDiscard(t);
            if (allMelds != null)
            {
                foreach (var melds in allMelds)
                {
                    if (melds == null) continue;
                    foreach (var m in melds) RecordMeld(m);
                }
            }
        }

        /// <summary>某张牌外面还剩几张。</summary>
        public int Remaining(TileKind kind)
        {
            return _visible[(int)kind];
        }

        /// <summary>某张牌外面是否还有（至少 1 张）。</summary>
        public bool HasRemaining(TileKind kind)
        {
            return _visible[(int)kind] > 0;
        }
    }
}