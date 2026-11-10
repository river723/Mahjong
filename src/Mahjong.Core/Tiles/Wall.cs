using System;
using System.Collections.Generic;

namespace Mahjong.Core.Tiles
{
    /// <summary>
    /// 牌墙：136 张标准牌，洗牌用可播种随机（同种子可重放，测试与回放用）。
    /// 摸牌从头部（liveWallStart）进行，杠的替换牌从尾部（replacementEnd）摸取。
    /// </summary>
    public sealed class Wall
    {
        private readonly Tile[] _tiles;
        private int _liveStart;
        private int _replacementEnd;

        /// <summary>起手每位玩家摸 13 张。</summary>
        public const int InitialHandSize = 13;

        /// <summary>牌墙总数（136 = 34 种 × 4 张）。</summary>
        public const int TotalCount = TileKindExtensions.KindCount * TileKindExtensions.CopiesPerKind;

        /// <summary>剩余可正常摸取的张数。</summary>
        public int RemainingCount
        {
            get { return _replacementEnd - _liveStart; }
        }

        /// <summary>所用的随机种子（诊断/重放用）。</summary>
        public int Seed { get; private set; }

        public Wall(int seed)
        {
            Seed = seed;
            _tiles = new Tile[TotalCount];
            int id = 0;
            for (int kind = 0; kind < TileKindExtensions.KindCount; kind++)
            {
                for (int copy = 0; copy < TileKindExtensions.CopiesPerKind; copy++)
                {
                    _tiles[id] = new Tile(id, (TileKind)kind);
                    id++;
                }
            }
            Shuffle(new Random(seed));
            _liveStart = 0;
            _replacementEnd = _tiles.Length;
        }

        /// <summary>普通摸牌（逆时针轮转的各家正常摸牌）。</summary>
        public Tile Draw()
        {
            if (RemainingCount <= 0) throw new InvalidOperationException("牌墙已摸完");
            return _tiles[_liveStart++];
        }

        /// <summary>杠后从墙尾摸的替换牌。</summary>
        public Tile DrawReplacement()
        {
            if (RemainingCount <= 0) throw new InvalidOperationException("牌墙已摸完");
            return _tiles[--_replacementEnd];
        }

        /// <summary>Fisher-Yates 洗牌。</summary>
        private void Shuffle(Random random)
        {
            for (int i = _tiles.Length - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                Tile temp = _tiles[i];
                _tiles[i] = _tiles[j];
                _tiles[j] = temp;
            }
        }

        /// <summary>全部牌（测试用，供牌数守恒校验）。</summary>
        public IList<Tile> AllTiles
        {
            get { return Array.AsReadOnly(_tiles); }
        }
    }
}
