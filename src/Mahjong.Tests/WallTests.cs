using System;
using Mahjong.Core.Tiles;
using Xunit;

namespace Mahjong.Tests
{
    /// <summary>Wall / Tile 的单元测试（M0）。</summary>
    public class WallTests
    {
        [Fact]
        public void 建墙_共136张_34种各4张()
        {
            var wall = new Wall(seed: 42);
            int[] count = new int[34];
            foreach (var tile in wall.AllTiles)
            {
                Assert.InRange((int)tile.Kind, 0, 33);
                count[(int)tile.Kind]++;
            }
            for (int k = 0; k < 34; k++)
            {
                Assert.Equal(4, count[k]);
            }
        }

        [Fact]
        public void 同种子重放_发牌序列完全一致()
        {
            var wall1 = new Wall(7);
            var wall2 = new Wall(7);
            for (int i = 0; i < Wall.TotalCount; i++)
            {
                var a = wall1.Draw();
                var b = wall2.Draw();
                Assert.Equal(a.Id, b.Id);
                Assert.Equal(a.Kind, b.Kind);
            }
        }

        [Fact]
        public void 异种子_发牌序列不同()
        {
            var wall1 = new Wall(1);
            var wall2 = new Wall(2);
            bool anyDifferent = false;
            for (int i = 0; i < Wall.TotalCount; i++)
            {
                if (wall1.Draw().Id != wall2.Draw().Id) { anyDifferent = true; }
            }
            Assert.True(anyDifferent, "不同种子的发牌序列竟然完全相同");
        }

        [Fact]
        public void 摸牌与替换牌_来自不重叠位置()
        {
            var wall = new Wall(3);
            wall.Draw(); wall.Draw(); wall.Draw();
            wall.DrawReplacement();
            wall.DrawReplacement();
            // 已摸 3 + 替换 2 = 剩 131
            Assert.Equal(136 - 3 - 2, wall.RemainingCount);
        }

        [Fact]
        public void 摸完牌墙_再摸抛异常()
        {
            var wall = new Wall(5);
            while (wall.RemainingCount > 0) wall.Draw();
            Assert.Throws<InvalidOperationException>(() => wall.Draw());
        }

        [Fact]
        public void 剩余张数随摸牌递减()
        {
            var wall = new Wall(9);
            int before = wall.RemainingCount;
            wall.Draw();
            Assert.Equal(before - 1, wall.RemainingCount);
        }
    }
}
