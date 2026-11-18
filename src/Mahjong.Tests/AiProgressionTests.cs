using System.Collections.Generic;
using System.Threading.Tasks;
using Mahjong.Core.Ai;
using Mahjong.Core.Game;
using Mahjong.Core.Rules;
using Mahjong.Core.Tiles;
using Xunit;

namespace Mahjong.Tests
{
    /// <summary>
    /// AI 做牌能力回归：旧版只在已听牌时挑牌，没听就打手牌第一张，
    /// 实测四家胡牌率不足 10%。这里锁住「会向听」这条底线。
    /// </summary>
    public class AiProgressionTests
    {
        private static int[] H(params int[] kinds)
        {
            var counts = new int[34];
            foreach (int k in kinds) counts[k]++;
            return counts;
        }

        [Fact]
        public void 向听_标准型与七对型判定正确()
        {
            // 11m 234m 567m 123p 45s：听 3s/6s
            Assert.Equal(0, ShantenCalculator.Calculate(
                H(0, 0, 1, 2, 3, 4, 5, 6, 9, 10, 11, 12, 13), 0, true));
            Assert.Equal(-1, ShantenCalculator.Calculate(
                H(0, 0, 1, 2, 3, 4, 5, 6, 9, 10, 11, 12, 13, 14), 0, true));
            // 11m 234m 567m 12p 45s 9m：一向听
            Assert.Equal(1, ShantenCalculator.Calculate(
                H(0, 0, 1, 2, 3, 4, 5, 6, 9, 10, 12, 13, 8), 0, true));
            // 六对单骑与七对和牌
            Assert.Equal(0, ShantenCalculator.Calculate(
                H(0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 15), 0, true));
            Assert.Equal(-1, ShantenCalculator.Calculate(
                H(0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 15, 15), 0, true));
            // 间隔对子（拆不出顺子）：开七对时听牌，关掉七对后不再是听牌
            int[] spacedPairs = H(0, 0, 2, 2, 4, 4, 6, 6, 8, 8, 9, 9, 20);
            Assert.Equal(0, ShantenCalculator.Calculate(spacedPairs, 0, true));
            Assert.True(ShantenCalculator.Calculate(spacedPairs, 0, false) > 0);
        }

        [Fact]
        public void 向听_副露按已完成面子计入()
        {
            // 一副露 + 11m 234m 567m 12p：听 3p
            Assert.Equal(0, ShantenCalculator.Calculate(
                H(0, 0, 1, 2, 3, 4, 5, 6, 9, 10), 1, true));
        }

        [Theory]
        [InlineData("普通")]
        [InlineData("较真")]
        [InlineData("大师")]
        public async Task AI会拆孤张做牌_而不是打手牌第一张(string level)
        {
            var profile = level == "普通" ? AiProfile.Normal : AiProfile.Hard;
            bool prefer = level == "大师";
            var ai = new AiPlayer(1, profile, 7, new RuleConfig(), 50, prefer);

            // 11m 234m 567m 123p 45s + 孤张 9m：只有打 9m 才听 3s/6s。
            int[] kinds = { 0, 0, 1, 2, 3, 4, 5, 6, 9, 10, 11, 12, 13, 8 };
            var hand = new List<Tile>();
            for (int i = 0; i < kinds.Length; i++) hand.Add(new Tile(i, (TileKind)kinds[i]));
            var melds = new IList<Meld>[] { new List<Meld>(), new List<Meld>(), new List<Meld>(), new List<Meld>() };
            var ctx = DecisionContext.Create(hand, new List<Meld>(), hand[13],
                new List<ClaimOption>(), -1, new List<Tile>(), melds, 100);

            Tile chosen = await ai.ChooseDiscard(ctx);
            Assert.NotNull(chosen);
            Assert.Equal(TileKind.Man9, chosen.Kind);
        }

        [Fact]
        public async Task 四个普通AI_绝大多数局能胡牌()
        {
            var rules = RuleConfig.CreateCoinMatch();
            int draws = 0;
            for (int seed = 0; seed < 30; seed++)
            {
                var agents = new IAgent[4];
                for (int i = 0; i < 4; i++)
                    agents[i] = new AiPlayer(i, AiProfile.Normal, seed * 7 + i, rules);
                var engine = new GameEngine(rules, agents, 0, seed);
                var result = await engine.RunAsync();
                if (result.IsDraw) draws++;
            }
            Assert.True(draws <= 3, "30 局里流局 " + draws + " 局，AI 做牌能力退化");
        }
    }
}
