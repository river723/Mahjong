using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mahjong.Core.Game;
using Mahjong.Core.Rules;
using Mahjong.Core.Tiles;
using Xunit;

namespace Mahjong.Tests
{
    /// <summary>
    /// 脚本 Agent：可配置决策，支持 neverHu 标志避免自摸/胡导致的状态混乱。
    /// </summary>
    public sealed class ScriptedAgent : IAgent
    {
        private readonly Func<DecisionContext, Tile> _discardRule;
        private readonly Func<DecisionContext, ClaimOption> _claimRule;
        private readonly Func<DecisionContext, ClaimOption> _concealedRule;
        private readonly bool _neverHu;

        public int PlayerIndex;

        public ScriptedAgent(int playerIndex,
            Func<DecisionContext, Tile> discardRule = null,
            Func<DecisionContext, ClaimOption> claimRule = null,
            Func<DecisionContext, ClaimOption> concealedRule = null,
            bool neverHu = false)
        {
            PlayerIndex = playerIndex;
            _discardRule = discardRule;
            _claimRule = claimRule;
            _concealedRule = concealedRule;
            _neverHu = neverHu;
        }

        public Task<Tile> ChooseDiscard(DecisionContext context)
        {
            if (_discardRule != null) return Task.FromResult(_discardRule(context));
            // 默认：打手牌第一张
            if (context.Hand == null || context.Hand.Count == 0) return Task.FromResult<Tile>(null);
            return Task.FromResult(context.Hand[0]);
        }

        public Task<ClaimOption> ChooseClaim(DecisionContext context)
        {
            if (_neverHu) return Task.FromResult<ClaimOption>(null);
            if (_claimRule != null) return Task.FromResult(_claimRule(context));
            return Task.FromResult<ClaimOption>(null);
        }

        public Task<ClaimOption> ChooseConcealedAction(DecisionContext context)
        {
            if (_neverHu) return Task.FromResult<ClaimOption>(null);
            if (_concealedRule != null) return Task.FromResult(_concealedRule(context));
            return Task.FromResult<ClaimOption>(null);
        }
    }

    /// <summary>
    ///M2：GameEngine + ClaimResolver + ScoreCalculator 核心流程测试。
    ///所有测试均使用四个相同的 SimpleAgent（打第一张、从不声明），
    ///以确保状态机本身正确；必要时再额外验证 ClaimResolver 纯逻辑。
    /// </summary>
    public class GameEngineTests
    {
        private static RuleConfig Rules() { return new RuleConfig(); }

        [Fact]
        public async Task 四简单代理_须流局_基础()
        {
            // 验证最基础的游戏流程：四家打第一张，从不声明 → 必然流局
            for (int seed = 0; seed < 20; seed++)
            {
                var agents = new IAgent[4];
                for (int i = 0; i < 4; i++) agents[i] = new ScriptedAgent(i, neverHu: true);
                var engine = new GameEngine(Rules(), agents, 0, wallSeed: seed);
                var result = await engine.RunAsync();
                Assert.True(result.IsDraw, $"seed={seed} 应该流局");
                Assert.Equal(0, result.Scores.Sum());
            }
        }

        [Fact]
        public async Task 声明纯逻辑_胡优先于碰()
        {
            // 纯 ClaimResolver 逻辑测试，不需要完整引擎运行
            var resolver = new ClaimResolver(Rules());
            var claims = new List<ClaimOption>[4];
            // 玩家 2 胡牌，玩家 1 只能碰：胡应优先，胡者为最近家（逆时针）
            claims[0] = null;
            claims[1] = new List<ClaimOption> { ClaimOption.Of(ClaimType.Pong, TileKind.Man1) };
            claims[2] = new List<ClaimOption> { ClaimOption.Of(ClaimType.Win, TileKind.Man1) };
            claims[3] = null;
            var res = resolver.Resolve(claims, discarder: 0);
            Assert.Equal(2, res.PlayerIndex);
            Assert.Equal(ClaimType.Win, res.Type);
        }

        [Fact]
        public void 胡优先于碰_纯逻辑验证_次之()
        {
            var resolver = new ClaimResolver(Rules());
            var claims = new List<ClaimOption>[4];
            claims[1] = new List<ClaimOption> { ClaimOption.Of(ClaimType.Pong, TileKind.Man1) };
            claims[2] = new List<ClaimOption> { ClaimOption.Of(ClaimType.Win, TileKind.Man2) };
            var res = resolver.Resolve(claims, discarder: 0);
            Assert.Equal(2, res.PlayerIndex);
            Assert.Equal(ClaimType.Win, res.Type);
        }

        // ==================== 一炮多响：单赢家 + 座位序优先级 ====================
        // 口径（已确认）：多家同时能胡只算一家胡，按出牌顺序取离放铳者最近的下家；
        // 与谁先点按钮无关，征询是三家并行收集完再统一结算的。端到端验证见 CoinRulesTests。

        [Theory]
        [InlineData(0, 1)]
        [InlineData(1, 2)]
        [InlineData(2, 3)]
        [InlineData(3, 0)]
        public void 一炮多响_三家同胡_只取离放铳者最近的下家(int discarder, int expectedWinner)
        {
            var resolver = new ClaimResolver(Rules());
            var claims = new List<ClaimOption>[4];
            for (int p = 0; p < 4; p++)
            {
                claims[p] = p == discarder ? null :
                    new List<ClaimOption> { ClaimOption.Of(ClaimType.Win, TileKind.Man1) };
            }
            var res = resolver.Resolve(claims, discarder);
            Assert.NotNull(res);
            Assert.Equal(expectedWinner, res.PlayerIndex);
            Assert.Equal(ClaimType.Win, res.Type);
        }

        [Fact]
        public void 一炮多响_近家放弃时_远家才递补()
        {
            var resolver = new ClaimResolver(Rules());
            var claims = new List<ClaimOption>[4];
            claims[1] = null;   // 最近的下家放弃，不应由它占位
            claims[2] = new List<ClaimOption> { ClaimOption.Of(ClaimType.Win, TileKind.Man1) };
            claims[3] = new List<ClaimOption> { ClaimOption.Of(ClaimType.Win, TileKind.Man1) };
            var res = resolver.Resolve(claims, discarder: 0);
            Assert.Equal(2, res.PlayerIndex);
            Assert.Equal(ClaimType.Win, res.Type);
        }

        [Fact]
        public void 一炮多响_胡优先于其他家的碰杠()
        {
            var resolver = new ClaimResolver(Rules());
            var claims = new List<ClaimOption>[4];
            claims[1] = new List<ClaimOption> { ClaimOption.Of(ClaimType.Kong, TileKind.Man1) };
            claims[2] = new List<ClaimOption> { ClaimOption.Of(ClaimType.Win, TileKind.Man1) };
            claims[3] = new List<ClaimOption> { ClaimOption.Of(ClaimType.Pong, TileKind.Man1) };
            var res = resolver.Resolve(claims, discarder: 0);
            Assert.Equal(2, res.PlayerIndex);
            Assert.Equal(ClaimType.Win, res.Type);
        }

        [Fact]
        public void 无吃_永不产生吃选项()
        {
            var player = new PlayerState(1);
            int[] handCounts = new int[34];
            handCounts[0] = 2; handCounts[1] = 1; handCounts[2] = 1;
            var resolver = new ClaimResolver(Rules());
            var options = resolver.GetOptions(player, new Tile(999, TileKind.Man3), handCounts);
            Assert.Empty(options);
        }

        [Fact]
        public async Task 随机对局_牌数守恒()
        {
            // 使用四 SimpleAgent 验证 136 张牌在任意局中守恒
            for (int seed = 0; seed < 50; seed++)
            {
                var agents = new IAgent[4];
                for (int i = 0; i < 4; i++) agents[i] = new ScriptedAgent(i, neverHu: true);
                var engine = new GameEngine(Rules(), agents, 0, wallSeed: seed);
                var result = await engine.RunAsync();
                int handSum = engine.Players.Sum(p => p.ConcealedTiles.Count);
                int meldSum = engine.Players.Sum(p => p.Melds.Sum(m => m.Tiles.Count));
                // 手牌+副露+牌河=136
                Assert.Equal(136, handSum + meldSum + engine.Discards.Count);
            }
        }

        [Fact]
        public async Task 同种子重放_流程一致()
        {
            async Task<List<string>> RunOnce(int wallSeed)
            {
                var log = new List<string>();
                var agents = new IAgent[4];
                for (int i = 0; i < 4; i++) agents[i] = new ScriptedAgent(i, neverHu: true);
                var engine = new GameEngine(Rules(), agents, 0, wallSeed: wallSeed);
                engine.EventRaised += e => log.Add(e.Type + ":" + e.PlayerIndex + ":" + (e.Tile != null ? e.Tile.Id.ToString() : "-"));
                await engine.RunAsync();
                return log;
            }
            var log1 = await RunOnce(77);
            var log2 = await RunOnce(77);
            Assert.Equal(log1.Count, log2.Count);
            for (int i = 0; i < log1.Count; i++)
                Assert.Equal(log1[i], log2[i]);
        }

        [Fact]
        public async Task 碰后_副露正确()
        {
            bool sawPong = false;
            for (int seed = 0; seed < 100 && !sawPong; seed++)
            {
                var agents = new IAgent[4];
                for (int i = 0; i < 4; i++)
                {
                    agents[i] = new ScriptedAgent(i,
                        claimRule: ctx => ctx.ClaimOptions.Any(o => o.Type == ClaimType.Pong)
                            ? ctx.ClaimOptions.First(o => o.Type == ClaimType.Pong) : null);
                }
                var engine = new GameEngine(Rules(), agents, 0, wallSeed: seed);
                engine.EventRaised += e =>
                {
                    if (e.Type == GameEventType.ClaimResolved && e.Message == "碰") sawPong = true;
                };
                await engine.RunAsync();
            }
            Assert.True(sawPong, "100 个 seed 内未出现碰——检查 claimRule 逻辑");
        }

        [Fact]
        public async Task 流局_分数全零()
        {
            for (int seed = 0; seed < 50; seed++)
            {
                var agents = new IAgent[4];
                for (int i = 0; i < 4; i++) agents[i] = new ScriptedAgent(i, neverHu: true);
                var engine = new GameEngine(Rules(), agents, 0, wallSeed: seed);
                var result = await engine.RunAsync();
                if (result.IsDraw)
                {
                    Assert.Equal(0, result.Scores.Sum());
                    Assert.True(result.Scores.All(s => s == 0));
                    return;
                }
            }
            // 50 层内应有流局
            Assert.Fail("50 个 seed 内未出现流局");
        }

        /// <summary>
        /// 回归：自摸成胡时必须广播「带 Win 选项」的 ConcealedActionOffered，并能真正胡牌结束。
        /// 旧代码用不带选项的 ClaimOffered 重载（UI 收不到胡按钮）、且 HumanAgent 一律放弃胡。
        /// 用「庄家起手 14 张即成胡」的确定性牌墙种子构造场景，避免依赖随机对局概率。
        /// </summary>
        [Fact]
        public async Task 自摸胡_事件携带Win选项_且能胡牌结束()
        {
            // 找一个天胡种子：牌墙前 14 张正好是庄家起手牌且成胡
            int seed = -1;
            for (int s = 0; s < 200000 && seed < 0; s++)
            {
                var wall = new Wall(s);
                int[] counts = new int[34];
                for (int i = 0; i < 14; i++) counts[(int)wall.AllTiles[i].Kind]++;
                if (WinChecker.CanWinFull(counts, Rules())) seed = s;
            }
            Assert.True(seed >= 0, "未能找到天胡牌墙种子");

            bool sawOffer = false;
            bool hasWinOption = false;
            var agents = new IAgent[4];
            for (int i = 0; i < 4; i++)
            {
                // 能胡就胡；别人打牌时不声明
                agents[i] = new ScriptedAgent(i,
                    concealedRule: ctx => ctx.ClaimOptions.FirstOrDefault(o => o.Type == ClaimType.Win));
            }
            var engine = new GameEngine(Rules(), agents, 0, wallSeed: seed);
            engine.EventRaised += e =>
            {
                if (e.Type != GameEventType.ConcealedActionOffered) return;
                sawOffer = true;
                if (e.ClaimOptions != null && e.ClaimOptions.Any(o => o.Type == ClaimType.Win))
                    hasWinOption = true;
            };
            var result = await engine.RunAsync();

            Assert.True(sawOffer, "自摸成胡未广播 ConcealedActionOffered，UI 不会弹出胡按钮");
            Assert.True(hasWinOption, "自摸征询事件未携带 Win 选项");
            Assert.False(result.IsDraw, "自摸应胡牌结束，而不是继续出牌直至流局");
            Assert.True(result.IsSelfDraw, "应为自摸胡");
        }

        /// <summary>
        /// 回归：碰/杠后主循环不得再摸一张牌。
        /// 出牌阶段暗牌数应恒为 14 - 3×副露组数（即 3n+2）；若重复摸牌会变成 3n，
        /// 导致「碰过牌就再也判不出胡」。
        /// </summary>
        [Fact]
        public async Task 碰杠后不重复摸牌_出牌阶段手牌恒为3n加2()
        {
            bool sawPong = false;
            bool badShape = false;

            for (int seed = 0; seed < 200 && !sawPong; seed++)
            {
                var agents = new IAgent[4];
                for (int i = 0; i < 4; i++)
                {
                    agents[i] = new ScriptedAgent(i,
                        discardRule: ctx =>
                        {
                            int expected = 14 - 3 * ctx.Melds.Count;
                            if (ctx.Hand.Count != expected) badShape = true;
                            return ctx.Hand[0];
                        },
                        claimRule: ctx => ctx.ClaimOptions.FirstOrDefault(o => o.Type == ClaimType.Pong),
                        concealedRule: ctx => null);  // 不胡，让对局继续
                }
                var engine = new GameEngine(Rules(), agents, 0, wallSeed: seed);
                engine.EventRaised += e =>
                {
                    if (e.Type == GameEventType.ClaimResolved && e.Message == "碰") sawPong = true;
                };
                await engine.RunAsync();
            }

            Assert.True(sawPong, "200 个 seed 内未出现碰");
            Assert.False(badShape, "出牌阶段暗牌数不是 3n+2：碰/杠后多摸了一张牌");
        }
    }
}