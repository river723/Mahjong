using System;

namespace Mahjong.Core.Tiles
{
    /// <summary>
    /// 一张具体的牌。判定算法（WinChecker 等）不使用本类，只操作 TileKind/int；
    /// Tile 仅用于引擎发牌、副露分组与 UI 展示，保留唯一 Id 便于动画跟踪。
    /// </summary>
    public sealed class Tile
    {
        /// <summary>全局唯一 Id（同一局内 0-135）。</summary>
        public int Id { get; private set; }

        /// <summary>牌面。</summary>
        public TileKind Kind { get; private set; }

        public Tile(int id, TileKind kind)
        {
            Id = id;
            Kind = kind;
        }

        public override string ToString()
        {
            return Kind.GetName() + "#" + Id;
        }
    }
}
