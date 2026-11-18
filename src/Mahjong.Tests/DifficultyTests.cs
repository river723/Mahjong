using System.Collections.Generic;
using System.Threading.Tasks;
using Mahjong.Core.Ai;
using Mahjong.Core.Game;
using Mahjong.Core.Rules;
using Mahjong.Core.Tiles;
using Xunit;

namespace Mahjong.Tests
{
    /// <summary>动态难度与记牌器的单元测试。</summary>
    public class DifficultyTests
    {
        [Fact]
        public void 连胜两局_升到Lv1()
        {
            DifficultyState.Reset();
            Assert.Null(DifficultyState.RecordResult(true));
            Assert.NotNull(DifficultyState.RecordResult(true));
            Assert.Equal(1, DifficultyState.Level);
            Assert.Equal(2, DifficultyState.WinStreak);
            Assert.Equal(2, DifficultyState.RoundsPlayed);
        }

        [Fact]
        public void 累计四局不胜_也升到Lv1()
        {
            DifficultyState.Reset();
            for (int i = 0; i < DifficultyState.RoundsForLevel1 - 1; i++)
                Assert.Null(DifficultyState.RecordResult(false));
            Assert.NotNull(DifficultyState.RecordResult(false));
            Assert.Equal(1, DifficultyState.Level);
            Assert.Equal(0, DifficultyState.WinStreak);
        }

        [Fact]
        public void 连胜中断_连胜清零但档位不降()
        {
            DifficultyState.Reset();
            DifficultyState.RecordResult(true);
            DifficultyState.RecordResult(true);
            Assert.Equal(1, DifficultyState.Level);

            DifficultyState.RecordResult(false);
            DifficultyState.RecordResult(false);
            Assert.Equal(0, DifficultyState.WinStreak);
            Assert.Equal(1, DifficultyState.Level);
        }

        [Fact]
        public void 连胜四局_升到Lv2()
        {
            DifficultyState.Reset();
            for (int i = 0; i < DifficultyState.StreakForLevel2; i++)
                DifficultyState.RecordResult(true);
            Assert.Equal(2, DifficultyState.Level);
        }

        [Fact]
        public void 累计八局_升到Lv2()
        {
            DifficultyState.Reset();
            for (int i = 0; i < DifficultyState.RoundsForLevel2; i++)
                DifficultyState.RecordResult(false);
            Assert.Equal(2, DifficultyState.Level);
        }

        [Fact]
        public void 固定档位_不再自动升档()
        {
            DifficultyState.Reset();
            DifficultyState.SetFixedLevel(1);
            for (int i = 0; i < 10; i++)
                Assert.Null(DifficultyState.RecordResult(true));
            Assert.Equal(1, DifficultyState.Level);
            Assert.True(DifficultyState.IsFixed);
        }

        [Fact]
        public void 档位映射_普通保持原行为_较真与大师逐档增强()
        {
            Assert.Equal(AiProfile.Normal, DifficultyState.ProfileFor(0));
            Assert.Equal(AiProfile.Hard, DifficultyState.ProfileFor(1));
            Assert.Equal(AiProfile.Hard, DifficultyState.ProfileFor(2));
            Assert.Equal(50, DifficultyState.ClaimPercentFor(0));
            Assert.Equal(65, DifficultyState.ClaimPercentFor(1));
            Assert.Equal(85, DifficultyState.ClaimPercentFor(2));
            Assert.False(DifficultyState.PreferMoreWaitsFor(1));
            Assert.True(DifficultyState.PreferMoreWaitsFor(2));
        }

        [Fact]
        public void 记牌器_用公开信息重建剩余张数()
        {
            var mem = new TileMemory();
            Assert.Equal(4, mem.Remaining(TileKind.Man1));

            var hand = new List<Tile> { new Tile(1, TileKind.Man1) };
            var discards = new List<Tile> { new Tile(2, TileKind.Man1) };
            var pong = new Meld(ClaimType.Pong, new List<Tile>
            {
                new Tile(3, TileKind.Man2), new Tile(4, TileKind.Man2), new Tile(5, TileKind.Man2)
            }, -1);
            var allMelds = new IList<Meld>[] { new List<Meld> { pong }, null, null, null };

            mem.Rebuild(hand, discards, allMelds);
            Assert.Equal(2, mem.Remaining(TileKind.Man1));   // 4 - 手里 1 - 牌河 1
            Assert.Equal(1, mem.Remaining(TileKind.Man2));   // 4 - 碰掉 3
            Assert.Equal(4, mem.Remaining(TileKind.Man3));

            // 再重建一次不应累积扣减
            mem.Rebuild(hand, discards, allMelds);
            Assert.Equal(2, mem.Remaining(TileKind.Man1));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task 较真档_接上记牌器后仍能选出手里的合法牌(bool preferMoreWaits)
        {
            var ai = new AiPlayer(1, AiProfile.Hard, 7, new RuleConfig(), 65, preferMoreWaits);
            var wall = new Wall(42);
            var hand = new List<Tile>();
            for (int i = 0; i < 14; i++) hand.Add(wall.Draw());
            var discards = new List<Tile> { new Tile(200, TileKind.Man1), new Tile(201, TileKind.Tong5) };
            var allMelds = new IList<Meld>[] { new List<Meld>(), new List<Meld>(), new List<Meld>(), new List<Meld>() };
            var ctx = DecisionContext.Create(hand, new List<Meld>(), hand[13],
                new List<ClaimOption>(), -1, discards, allMelds, 100);

            Tile chosen = await ai.ChooseDiscard(ctx);
            Assert.NotNull(chosen);
            Assert.Contains(chosen, hand);
        }

        [Fact]
        public async Task 高难度碰杠概率_不会选出未提供的选项()
        {
            var ai = new AiPlayer(1, AiProfile.Hard, 7, new RuleConfig(), 85, true);
            var options = new List<ClaimOption> { ClaimOption.Of(ClaimType.Pong, TileKind.Man1) };
            var ctx = DecisionContext.Create(new List<Tile>(), new List<Meld>(), null,
                options, 0, new List<Tile>(), null, 100);

            ClaimOption got = await ai.ChooseClaim(ctx);
            Assert.True(got == null || got.Type == ClaimType.Pong);
        }

        // ===== 「设置」里的起始难度 =====

        [Fact]
        public void 起始档位可直接设为大师_且不会被自动升档拉低()
        {
            DifficultyState.Reset();
            DifficultyState.ApplyStartLevel(2, autoLevelUp: true);
            Assert.Equal(2, DifficultyState.Level);
            Assert.False(DifficultyState.IsFixed);

            // ComputeLevel 在头几局只会算出 0/1，旧逻辑不能把起始档位降回去
            Assert.Null(DifficultyState.RecordResult(false));
            Assert.Equal(2, DifficultyState.Level);
        }

        [Fact]
        public void 关闭自动升档后_固定起始档位且清空旧进度()
        {
            DifficultyState.Reset();
            DifficultyState.RecordResult(true);
            DifficultyState.ApplyStartLevel(1, autoLevelUp: false);

            Assert.Equal(1, DifficultyState.Level);
            Assert.True(DifficultyState.IsFixed);
            Assert.Equal(0, DifficultyState.RoundsPlayed);
            Assert.Equal(0, DifficultyState.WinStreak);

            for (int i = 0; i < 8; i++) Assert.Null(DifficultyState.RecordResult(true));
            Assert.Equal(1, DifficultyState.Level);
            Assert.Equal(8, DifficultyState.RoundsPlayed);   // 局数照记，只是不再升档
        }

        [Fact]
        public void 启动时播种档位_不改固定标志与会话进度()
        {
            DifficultyState.Reset();
            DifficultyState.RecordResult(true);
            DifficultyState.SetStartLevel(1);
            Assert.Equal(1, DifficultyState.Level);
            Assert.Equal(1, DifficultyState.WinStreak);
            Assert.Equal(1, DifficultyState.RoundsPlayed);
            Assert.False(DifficultyState.IsFixed);
        }

        [Theory]
        [InlineData(-5, 0)]
        [InlineData(99, 2)]
        public void 起始档位越界会收敛(int input, int expected)
        {
            DifficultyState.Reset();
            DifficultyState.ApplyStartLevel(input, autoLevelUp: true);
            Assert.Equal(expected, DifficultyState.Level);
        }
    }
}
