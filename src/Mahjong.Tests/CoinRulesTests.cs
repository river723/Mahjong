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
    /// 金币场规则测试：单价表、杠加成、零和守恒、账本破产边界、牌型识别。
    /// </summary>
    public class CoinRulesTests
    {
        private static int[] Counts(params int[] kinds)
        {
            var counts = new int[34];
            foreach (int k in kinds) counts[k]++;
            return counts;
        }

        /// <summary>万 123456789 + 中中中 + 东东 = 一条龙。</summary>
        private static int[] DragonHand()
        {
            return Counts(0, 1, 2, 3, 4, 5, 6, 7, 8, 31, 31, 31, 27, 27);
        }

        /// <summary>三个顺子分属万/条/筒 + 中中中 + 东东 = 普通胡。</summary>
        private static int[] PlainHand()
        {
            return Counts(0, 1, 2, 12, 13, 14, 24, 25, 26, 31, 31, 31, 27, 27);
        }

        /// <summary>一万到七万各一对 = 七对。</summary>
        private static int[] SevenPairsHand()
        {
            return Counts(0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6);
        }

        // 这个旧重载只给一个杠数，按「全是明杠」解释，数值与改造前一致。
        [Theory]
        [InlineData(WinPatternKind.Normal, 0, false, 1)]
        [InlineData(WinPatternKind.Normal, 0, true, 2)]
        [InlineData(WinPatternKind.PureStraight, 0, false, 3)]
        [InlineData(WinPatternKind.PureStraight, 0, true, 6)]
        [InlineData(WinPatternKind.SevenPairs, 0, false, 3)]
        [InlineData(WinPatternKind.SevenPairs, 0, true, 6)]
        [InlineData(WinPatternKind.Normal, 1, false, 2)]
        [InlineData(WinPatternKind.PureStraight, 2, true, 8)]
        public void 单价表_与方案一致(WinPatternKind kind, int kongs, bool selfDraw, int expectedUnit)
        {
            var calc = new ScoreCalculator(RuleConfig.CreateCoinMatch());
            var pattern = WinPattern.Of(kind, kongs);
            Assert.Equal(expectedUnit, calc.ComputeUnitPay(pattern, selfDraw));

            var scores = calc.ComputeScores(0, 1, selfDraw, pattern);
            Assert.Equal(expectedUnit * 3, scores[0]);
            Assert.Equal(-expectedUnit, scores[1]);
            Assert.Equal(-expectedUnit, scores[2]);
            Assert.Equal(-expectedUnit, scores[3]);
            Assert.Equal(0, scores.Sum());
        }

        [Fact]
        public void 杠加成_不吃自摸倍率()
        {
            var calc = new ScoreCalculator(RuleConfig.CreateCoinMatch());
            // 基价 3 × 2 + 2 杠 = 8；若吃倍率会变成 (3+2)×2 = 10
            Assert.Equal(8, calc.ComputeUnitPay(WinPattern.Of(WinPatternKind.PureStraight, 2), true));
        }

        [Fact]
        public void 点炮_三家各付_放铳者不多付()
        {
            var calc = new ScoreCalculator(RuleConfig.CreateCoinMatch());
            var scores = calc.ComputeScores(2, 0, false, WinPattern.Plain());
            Assert.Equal(-1, scores[0]); // 放铳者
            Assert.Equal(-1, scores[1]); // 另外两家也付
            Assert.Equal(3, scores[2]);
            Assert.Equal(-1, scores[3]);
        }

        [Fact]
        public void 流局_零转移()
        {
            var calc = new ScoreCalculator(RuleConfig.CreateCoinMatch());
            Assert.Equal(new int[4], calc.ComputeScores(-1, -1, false, WinPattern.Plain()));
        }

        [Fact]
        public void 七对关掉时_按普通基价()
        {
            var rules = RuleConfig.CreateCoinMatch();
            rules.SevenPairsAsDragon = false;
            var calc = new ScoreCalculator(rules);
            Assert.Equal(1, calc.ComputeUnitPay(WinPattern.Of(WinPatternKind.SevenPairs, 0), false));
        }

        [Fact]
        public void 牌型识别_一条龙_七对_普通()
        {
            var rules = RuleConfig.CreateCoinMatch();
            Assert.Equal(WinPatternKind.PureStraight, WinPattern.Analyze(DragonHand(), 0, rules).Kind);
            Assert.Equal(WinPatternKind.SevenPairs, WinPattern.Analyze(SevenPairsHand(), 0, rules).Kind);
            Assert.Equal(WinPatternKind.Normal, WinPattern.Analyze(PlainHand(), 0, rules).Kind);
            Assert.Equal(2, WinPattern.Analyze(DragonHand(), 2, rules).KongCount);

            Assert.True(WinChecker.HasPureStraight(DragonHand()));
            Assert.False(WinChecker.HasPureStraight(PlainHand()));
            Assert.True(WinChecker.IsSevenPairs(SevenPairsHand()));
            Assert.False(WinChecker.IsSevenPairs(PlainHand()));
        }

        [Fact]
        public void 牌能胡_且带杠时暗牌张数仍可分析()
        {
            var rules = RuleConfig.CreateCoinMatch();
            // 有副露时暗牌为 3n+2：一条龙的 9 张顺子 + 一对雀头 = 11 张
            Assert.True(WinChecker.CanWinFull(Counts(0, 1, 2, 3, 4, 5, 6, 7, 8, 27, 27), rules));
            Assert.Equal(WinPatternKind.PureStraight,
                WinPattern.Analyze(Counts(0, 1, 2, 3, 4, 5, 6, 7, 8, 27, 27), 1, rules).Kind);
        }

        [Fact]
        public void 账本_总额恒为80_恰好归零即结束()
        {
            var ledger = new CoinLedger(RuleConfig.CreateCoinMatch());
            Assert.Equal(new[] { 20, 20, 20, 20 }, ledger.Coins);

            for (int i = 0; i < 20; i++)
            {
                var outcome = ledger.Apply(new GameResult
                {
                    WinnerIndex = 0,
                    Scores = new[] { 3, -1, -1, -1 }
                });
                Assert.Equal(80, outcome.CoinsAfter.Sum());
                if (i < 19) Assert.False(outcome.IsMatchOver);
                else Assert.True(outcome.IsMatchOver);
            }

            Assert.True(ledger.IsMatchOver);
            Assert.Equal(20, ledger.RoundIndex);
            Assert.Equal(0, ledger.Coins[1]);
            Assert.Equal(new[] { 1, 2, 3 }, ledger.BankruptPlayers);
        }

        [Fact]
        public void 账本_允许负数_结算后立刻结束()
        {
            var ledger = new CoinLedger(RuleConfig.CreateCoinMatch());
            // 一条龙自摸：三家每局 -6，4 局后 20 - 24 = -4
            for (int i = 0; i < 4; i++)
            {
                ledger.Apply(new GameResult
                {
                    WinnerIndex = 0,
                    Scores = new[] { 18, -6, -6, -6 }
                });
            }
            Assert.True(ledger.IsMatchOver);
            Assert.Equal(-4, ledger.Coins[1]);
            Assert.Equal(80, ledger.Coins.Sum());
            Assert.Equal(0, ledger.Ranking()[0]); // 连赢四局者排第一
        }

        [Fact]
        public void 账本_流局不转移_结束后不再记账_可重开一场()
        {
            var ledger = new CoinLedger(RuleConfig.CreateCoinMatch());
            var draw = ledger.Apply(new GameResult { WinnerIndex = -1, Scores = new int[4] });
            Assert.Equal(new[] { 20, 20, 20, 20 }, draw.CoinsAfter);
            Assert.False(draw.IsMatchOver);
            Assert.Equal(1, ledger.RoundIndex);

            for (int i = 0; i < 20; i++)
                ledger.Apply(new GameResult { WinnerIndex = 1, Scores = new[] { -1, 3, -1, -1 } });
            Assert.True(ledger.IsMatchOver);
            var frozen = ledger.Coins;
            ledger.Apply(new GameResult { WinnerIndex = 0, Scores = new[] { 3, -1, -1, -1 } });
            Assert.Equal(frozen, ledger.Coins);

            ledger.Reset();
            Assert.Equal(new[] { 20, 20, 20, 20 }, ledger.Coins);
            Assert.False(ledger.IsMatchOver);
            Assert.Equal(0, ledger.RoundIndex);
        }

        [Fact]
        public async Task 真实对局_天胡时单价与得分向量一致()
        {
            // 随机种子 + 打第一张牌几乎胡不了（项目里既有测试也是这么处理的）：
            // 找一个庄家起手 14 张即成胡的确定性牌墙种子，验证引擎→牌型→计分的完整接线。
            var rules = RuleConfig.CreateCoinMatch();
            int seed = -1;
            for (int s = 0; s < 200000 && seed < 0; s++)
            {
                var wall = new Wall(s);
                int[] counts = new int[34];
                for (int i = 0; i < 14; i++) counts[(int)wall.AllTiles[i].Kind]++;
                if (WinChecker.CanWinFull(counts, rules)) seed = s;
            }
            Assert.True(seed >= 0, "未能找到天胡牌墙种子");

            var agents = new IAgent[4];
            for (int i = 0; i < 4; i++)
            {
                agents[i] = new ScriptedAgent(i,
                    concealedRule: ctx => ctx.ClaimOptions.FirstOrDefault(o => o.Type == ClaimType.Win));
            }
            var engine = new GameEngine(rules, agents, 0, seed);
            var result = await engine.RunAsync();

            Assert.False(result.IsDraw);
            Assert.True(result.IsSelfDraw);
            Assert.True(Enum.IsDefined(typeof(WinPatternKind), result.PatternKind));
            Assert.Equal(0, result.KongCount);
            // 天胡自摸：普通牌每家 2，一条龙/七对每家 6
            Assert.Equal(result.PatternKind == WinPatternKind.Normal ? 2 : 6, result.UnitPay);
            Assert.Equal(0, result.Scores.Sum());
            Assert.Equal(result.UnitPay * 3, result.Scores[result.WinnerIndex]);
            for (int p = 0; p < 4; p++)
            {
                if (p != result.WinnerIndex) Assert.Equal(-result.UnitPay, result.Scores[p]);
            }
        }

        [Theory]
        // u = 牌型基价 × 自摸倍率 + 明杠数 × 1 + 暗杠数 × 2
        [InlineData(WinPatternKind.Normal, 0, 0, false, 1)]
        [InlineData(WinPatternKind.Normal, 1, 0, false, 2)]
        [InlineData(WinPatternKind.Normal, 0, 1, false, 3)]
        [InlineData(WinPatternKind.Normal, 1, 1, false, 4)]
        [InlineData(WinPatternKind.Normal, 0, 1, true, 4)]
        [InlineData(WinPatternKind.Normal, 2, 2, true, 8)]
        [InlineData(WinPatternKind.PureStraight, 1, 1, false, 6)]
        [InlineData(WinPatternKind.PureStraight, 1, 2, true, 11)]
        [InlineData(WinPatternKind.SevenPairs, 0, 2, false, 7)]
        public void 单价表_明杠暗杠分档(WinPatternKind kind, int open, int concealed, bool selfDraw, int expectedUnit)
        {
            var calc = new ScoreCalculator(RuleConfig.CreateCoinMatch());
            var pattern = WinPattern.Of(kind, open, concealed);
            Assert.Equal(expectedUnit, calc.ComputeUnitPay(pattern, selfDraw));

            var scores = calc.ComputeScores(0, 1, selfDraw, pattern);
            Assert.Equal(expectedUnit * 3, scores[0]);
            Assert.Equal(-expectedUnit, scores[1]);
            Assert.Equal(-expectedUnit, scores[2]);
            Assert.Equal(-expectedUnit, scores[3]);
            Assert.Equal(0, scores.Sum());
        }

        [Fact]
        public void 暗杠加成_不吃自摸倍率()
        {
            var rules = RuleConfig.CreateCoinMatch();
            var calc = new ScoreCalculator(rules);
            var pattern = WinPattern.Of(WinPatternKind.PureStraight, 0, 2);
            // 3 × 2 + 2 × 2 = 10；若杠加成吃倍率会变成 (3 + 4) × 2 = 14
            Assert.Equal(10, calc.ComputeUnitPay(pattern, true));
            // 改倍率只影响牌型部分，杠的 4 枚保持不动
            rules.SelfDrawMultiplier = 3;
            Assert.Equal(13, calc.ComputeUnitPay(pattern, true));
            Assert.Equal(7, calc.ComputeUnitPay(pattern, false));
        }

        [Fact]
        public void WinPattern_杠计数与中文明细()
        {
            var p = WinPattern.Of(WinPatternKind.Normal, 2, 3);
            Assert.Equal(2, p.OpenKongCount);
            Assert.Equal(3, p.ConcealedKongCount);
            Assert.Equal(5, p.KongCount);
            Assert.Equal(" + 2 明杠 + 3 暗杠", p.KongDescription);
            Assert.Equal(2 * 1 + 3 * 2, p.KongBonus(RuleConfig.CreateCoinMatch()));
            Assert.Equal("", WinPattern.Plain().KongDescription);
        }

        [Fact]
        public void GameResult_杠字段与中文明细()
        {
            var result = new GameResult { OpenKongCount = 1, ConcealedKongCount = 2 };
            Assert.Equal(3, result.KongCount);
            Assert.Equal(" + 1 明杠 + 2 暗杠", result.KongDescription);
        }

        [Fact]
        public async Task 真实对局_暗杠成立并计入结算()
        {
            var rules = RuleConfig.CreateCoinMatch();
            // 找一个庄家起手 14 张里就有四张同款的确定性牌墙种子
            int seed = -1;
            for (int s = 0; s < 200000 && seed < 0; s++)
            {
                var wall = new Wall(s);
                int[] counts = new int[34];
                for (int i = 0; i < 14; i++) counts[(int)wall.AllTiles[i].Kind]++;
                if (counts.Any(c => c == 4)) seed = s;
            }
            Assert.True(seed >= 0, "未找到庄家起手四张同款的牌墙种子");

            int kongEvents = 0;
            var agents = new IAgent[4];
            for (int i = 0; i < 4; i++)
            {
                // 能暗杠就暗杠；不胡、不碰、不明杠，让引擎把整局跑完
                agents[i] = new ScriptedAgent(i,
                    concealedRule: ctx => ctx.ClaimOptions.FirstOrDefault(o => o.Type == ClaimType.ConcealedKong),
                    claimRule: ctx => null);
            }
            var engine = new GameEngine(rules, agents, 0, seed);
            engine.EventRaised += e => { if (e.Message == "暗杠") kongEvents++; };
            var result = await engine.RunAsync();

            Assert.True(kongEvents >= 1, "引擎未广播暗杠结算事件");
            Assert.True(engine.Players[0].Melds.Any(m => m.Type == ClaimType.ConcealedKong),
                "庄家的副露里没有暗杠");
            Assert.NotNull(result);
            Assert.Equal(0, result.Scores.Sum());

            // 暗杠者若最终胡牌，结算里的暗杠数必须等于其副露数
            if (!result.IsDraw && result.WinnerIndex == 0)
            {
                Assert.Equal(
                    engine.Players[0].Melds.Count(m => m.Type == ClaimType.ConcealedKong),
                    result.ConcealedKongCount);
            }

            // 引擎把「副露类型 → 明/暗杠数」分开统计，是本次改动的关键接线。
            // 真实对局里「暗杠后立刻自摸胡」的牌墙几乎凑不出来（50 万种子内无样本），
            // 所以这里用反射直接验证该转换，避免测试靠概率赌运气。
            var analyze = typeof(GameEngine).GetMethod("AnalyzeWinPattern",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(analyze);
            WinPattern counted = (WinPattern)analyze.Invoke(engine, new object[] { 0, null, true });
            int actualConcealed = engine.Players[0].Melds.Count(m => m.Type == ClaimType.ConcealedKong);
            Assert.Equal(actualConcealed, counted.ConcealedKongCount);
            Assert.Equal(0, counted.OpenKongCount);
            Assert.Equal(actualConcealed * rules.ConcealedKongBonusPerPlayer, counted.KongBonus(rules));

            // 再补一组明杠，验证两档分开计、加成按 1 和 2 分别累加
            engine.Players[0].Melds.Add(new Meld(ClaimType.Kong, new List<Tile>(), -1));
            WinPattern mixed = (WinPattern)analyze.Invoke(engine, new object[] { 0, null, true });
            Assert.Equal(1, mixed.OpenKongCount);
            Assert.Equal(actualConcealed, mixed.ConcealedKongCount);
            Assert.Equal(rules.KongBonusPerPlayer + actualConcealed * rules.ConcealedKongBonusPerPlayer,
                mixed.KongBonus(rules));
        }

        // ==================== 补杠 / 抢杠胡 ====================
        // 用 GameStarted 事件注入确定性牌局：发牌刚结束、主循环尚未开始，此时替换手牌与副露，
        // 就能稳定造出「有碰可补杠」「某家正好听杠牌」等场景，不必再搜随机种子。

        private static void SetHand(PlayerState player, params int[] kinds)
        {
            player.ConcealedTiles.Clear();
            for (int i = 0; i < kinds.Length; i++)
                player.ConcealedTiles.Add(new Tile(9000 + player.Index * 100 + i, (TileKind)kinds[i]));
        }

        private static Meld PongOf(int kind)
        {
            return new Meld(ClaimType.Pong, new List<Tile>
            {
                new Tile(8001, (TileKind)kind), new Tile(8002, (TileKind)kind), new Tile(8003, (TileKind)kind)
            }, 1);
        }

        /// <summary>0 号手里 14 张，含一组「碰」副露 + 一张同款，可补杠。</summary>
        private static void InjectAddedKongReady(GameEngine engine)
        {
            // 一万 1 张 + 四组字牌刻子 + 一张单牌：14 张，本身不能胡
            SetHand(engine.Players[0], 0, 27, 27, 27, 28, 28, 28, 29, 29, 29, 30, 30, 30, 31);
            engine.Players[0].Melds.Add(PongOf(0));
        }

        /// <summary>1 号听一万：补上一万就是「一条龙 123+456+789 万 + 中中中 + 东东」。</summary>
        private static void InjectRobber(GameEngine engine)
        {
            SetHand(engine.Players[1], 1, 2, 3, 4, 5, 6, 7, 8, 31, 31, 31, 27, 27);
        }

        /// <summary>把三家手牌设成明显不能胡的散牌，避免它们意外抢杠。</summary>
        private static void InjectDeadHands(GameEngine engine)
        {
            for (int p = 1; p < 4; p++)
                SetHand(engine.Players[p], 0, 3, 6, 9, 12, 15, 18, 21, 24, 27, 30, 31, 32);
        }

        [Fact]
        public async Task 抢杠胡_杠不成立_按点炮结算()
        {
            var rules = RuleConfig.CreateCoinMatch();
            int robOffers = 0;
            var agents = new IAgent[4];
            agents[0] = new ScriptedAgent(0,
                concealedRule: ctx => ctx.ClaimOptions.FirstOrDefault(o => o.Type == ClaimType.AddedKong),
                claimRule: ctx => null);
            agents[1] = new ScriptedAgent(1,
                claimRule: ctx => ctx.ClaimOptions.FirstOrDefault(o => o.Type == ClaimType.Win));
            agents[2] = new ScriptedAgent(2, neverHu: true);
            agents[3] = new ScriptedAgent(3, neverHu: true);

            var engine = new GameEngine(rules, agents, 0, 7);
            engine.EventRaised += e =>
            {
                if (e.Message == "可抢杠胡") robOffers++;
                if (e.Type != GameEventType.GameStarted) return;
                InjectAddedKongReady(engine);
                InjectRobber(engine);
            };
            var result = await engine.RunAsync();

            Assert.True(robOffers >= 1, "补杠亮牌时未向其余三家征询抢杠胡");
            Assert.False(result.IsDraw);
            Assert.Equal(1, result.WinnerIndex);
            Assert.False(result.IsSelfDraw);
            Assert.True(result.IsRobbedKong);
            Assert.Equal(0, result.LoserIndex);
            Assert.Equal(WinPatternKind.PureStraight, result.PatternKind);
            Assert.Equal(3, result.UnitPay);                          // 一条龙点炮，胡家无杠
            Assert.Equal(new[] { -3, 9, -3, -3 }, result.Scores);    // 三家各付，放铳者不加倍

            // 杠完全不成立：副露仍是三张碰，手里那张也没被拿走，不计任何杠加成
            Assert.Equal(ClaimType.Pong, engine.Players[0].Melds[0].Type);
            Assert.Equal(3, engine.Players[0].Melds[0].Tiles.Count);
            Assert.DoesNotContain(engine.Players[0].Melds, m => m.Type == ClaimType.AddedKong);
            Assert.Equal(0, result.OpenKongCount);
            Assert.Equal(0, result.ConcealedKongCount);
        }

        [Fact]
        public async Task 补杠_无人抢时成立_归入明杠档()
        {
            var rules = RuleConfig.CreateCoinMatch();
            int addedEvents = 0;
            var agents = new IAgent[4];
            agents[0] = new ScriptedAgent(0,
                concealedRule: ctx => ctx.ClaimOptions.FirstOrDefault(o => o.Type == ClaimType.AddedKong),
                claimRule: ctx => null);
            for (int i = 1; i < 4; i++) agents[i] = new ScriptedAgent(i, neverHu: true);

            var engine = new GameEngine(rules, agents, 0, 7);
            engine.EventRaised += e =>
            {
                if (e.Message == "补杠") addedEvents++;
                if (e.Type != GameEventType.GameStarted) return;
                InjectAddedKongReady(engine);
                InjectDeadHands(engine);
            };
            var result = await engine.RunAsync();

            Assert.True(addedEvents >= 1, "补杠成立时未广播结算事件");
            var added = engine.Players[0].Melds.FirstOrDefault(m => m.Type == ClaimType.AddedKong);
            Assert.NotNull(added);
            Assert.Equal(4, added.Tiles.Count);
            Assert.All(added.Tiles, t => Assert.Equal((TileKind)0, t.Kind));
            Assert.DoesNotContain(engine.Players[0].Melds, m => m.Type == ClaimType.Pong);
            Assert.NotNull(result);
            Assert.Equal(0, result.Scores.Sum());

            // 补杠算明杠 +1/家，不是暗杠 +2/家
            var analyze = typeof(GameEngine).GetMethod("AnalyzeWinPattern",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var pattern = (WinPattern)analyze.Invoke(engine, new object[] { 0, null, true });
            Assert.Equal(1, pattern.OpenKongCount);
            Assert.Equal(0, pattern.ConcealedKongCount);
            Assert.Equal(rules.KongBonusPerPlayer, pattern.KongBonus(rules));
        }

        [Fact]
        public async Task 抢杠胡关闭时_补杠不被抢()
        {
            var rules = RuleConfig.CreateCoinMatch();
            rules.AllowRobbingKong = false;
            int robOffers = 0;
            var agents = new IAgent[4];
            agents[0] = new ScriptedAgent(0,
                concealedRule: ctx => ctx.ClaimOptions.FirstOrDefault(o => o.Type == ClaimType.AddedKong),
                claimRule: ctx => null);
            for (int i = 1; i < 4; i++) agents[i] = new ScriptedAgent(i, neverHu: true);

            var engine = new GameEngine(rules, agents, 0, 7);
            engine.EventRaised += e =>
            {
                if (e.Message == "可抢杠胡") robOffers++;
                if (e.Type != GameEventType.GameStarted) return;
                InjectAddedKongReady(engine);
                InjectRobber(engine);
            };
            await engine.RunAsync();

            Assert.Equal(0, robOffers);
            Assert.Contains(engine.Players[0].Melds, m => m.Type == ClaimType.AddedKong);
        }

        [Fact]
        public async Task 暗杠_不可被抢杠胡()
        {
            var rules = RuleConfig.CreateCoinMatch();
            int robOffers = 0;
            var agents = new IAgent[4];
            agents[0] = new ScriptedAgent(0,
                concealedRule: ctx => ctx.ClaimOptions.FirstOrDefault(o => o.Type == ClaimType.ConcealedKong),
                claimRule: ctx => null);
            for (int i = 1; i < 4; i++) agents[i] = new ScriptedAgent(i, neverHu: true);

            var engine = new GameEngine(rules, agents, 0, 7);
            engine.EventRaised += e =>
            {
                if (e.Message == "可抢杠胡") robOffers++;
                if (e.Type != GameEventType.GameStarted) return;
                // 0 号手里四张一万 → 可暗杠；14 张手牌
                SetHand(engine.Players[0], 0, 0, 0, 0, 27, 27, 27, 28, 28, 28, 29, 29, 29, 31);
                // 1 号同样听一万：若暗杠可被抢，这里就会被征询
                InjectRobber(engine);
            };
            var result = await engine.RunAsync();

            Assert.True(robOffers == 0, "暗杠不应触发抢杠征询");
            Assert.Contains(engine.Players[0].Melds, m => m.Type == ClaimType.ConcealedKong);
            Assert.NotNull(result);
        }

        // ==================== 一炮多响：单赢家（已确认口径） ====================
        // 三家同时能胡时按 SeatOrder 座次取离放铳者/杠家最近的下家，只结算一个赢家。
        // 0 号放铳时最近下家是 2 号（右/东；逆时针座次 0→2→1→3），不是索引 +1 的 1 号。

        /// <summary>三家都听一万：补上一万即一条龙（123+456+789 万 + 中中中 + 东东）。</summary>
        private static void InjectThreeRobbers(GameEngine engine)
        {
            for (int p = 1; p < 4; p++)
                SetHand(engine.Players[p], 1, 2, 3, 4, 5, 6, 7, 8, 31, 31, 31, 27, 27);
        }

        [Fact]
        public async Task 一炮多响_三家同胡_只算离放铳者最近的一家()
        {
            var rules = RuleConfig.CreateCoinMatch();
            int winOffers = 0;
            var agents = new IAgent[4];
            agents[0] = new ScriptedAgent(0,
                discardRule: ctx => ctx.Hand.FirstOrDefault(t => t.Kind == TileKind.Man1) ?? ctx.Hand.First(),
                concealedRule: ctx => null,
                claimRule: ctx => null);
            for (int i = 1; i < 4; i++)
                agents[i] = new ScriptedAgent(i,
                    claimRule: ctx => ctx.ClaimOptions.FirstOrDefault(o => o.Type == ClaimType.Win),
                    concealedRule: ctx => null,
                    discardRule: ctx => ctx.Hand.First());

            var engine = new GameEngine(rules, agents, 0, 7);
            engine.EventRaised += e =>
            {
                if (e.Type == GameEventType.ClaimOffered && e.ClaimOptions != null &&
                    e.ClaimOptions.Any(o => o.Type == ClaimType.Win)) winOffers++;
                if (e.Type != GameEventType.GameStarted) return;
                // 0 号 14 张散牌，含一张一万可打出；自身不能胡、无四张同款
                SetHand(engine.Players[0], 0, 3, 6, 9, 12, 15, 18, 21, 24, 27, 30, 31, 32, 33);
                InjectThreeRobbers(engine);
            };
            var result = await engine.RunAsync();

            Assert.True(winOffers >= 3, "三家都应被征询到胡牌选项");
            Assert.False(result.IsDraw);
            Assert.Equal(SeatOrder.NextSeat(0), result.WinnerIndex);   // 0 号沿逆时针座次的下家 = 2 号（右/东）
            Assert.Equal(0, result.LoserIndex);
            Assert.False(result.IsSelfDraw);
            Assert.False(result.IsRobbedKong);
            Assert.Equal(WinPatternKind.PureStraight, result.PatternKind);
            Assert.Equal(3, result.UnitPay);
            Assert.Equal(new[] { -3, -3, 9, -3 }, result.Scores);
            Assert.Equal(0, result.Scores.Sum());
        }

        [Fact]
        public async Task 抢杠胡_三家可抢_只算离杠家最近的一家()
        {
            var rules = RuleConfig.CreateCoinMatch();
            int robOffers = 0;
            var agents = new IAgent[4];
            agents[0] = new ScriptedAgent(0,
                concealedRule: ctx => ctx.ClaimOptions.FirstOrDefault(o => o.Type == ClaimType.AddedKong),
                claimRule: ctx => null);
            for (int i = 1; i < 4; i++)
                agents[i] = new ScriptedAgent(i,
                    claimRule: ctx => ctx.ClaimOptions.FirstOrDefault(o => o.Type == ClaimType.Win),
                    concealedRule: ctx => null);

            var engine = new GameEngine(rules, agents, 0, 7);
            engine.EventRaised += e =>
            {
                if (e.Message == "可抢杠胡") robOffers++;
                if (e.Type != GameEventType.GameStarted) return;
                InjectAddedKongReady(engine);
                InjectThreeRobbers(engine);
            };
            var result = await engine.RunAsync();

            Assert.True(robOffers >= 3, "三家都应被征询抢杠胡");
            Assert.False(result.IsDraw);
            Assert.Equal(SeatOrder.NextSeat(0), result.WinnerIndex);   // 杠家 0 号沿逆时针座次的下家 = 2 号（右/东）
            Assert.Equal(0, result.LoserIndex);
            Assert.True(result.IsRobbedKong);
            Assert.Equal(new[] { -3, -3, 9, -3 }, result.Scores);
            Assert.Equal(ClaimType.Pong, engine.Players[0].Melds[0].Type);   // 杠未成立
        }

        /// <summary>把墙尾第一张替换牌改成指定牌面，稳定构造「杠后补牌」。</summary>
        private static void SetNextReplacementTile(GameEngine engine, int kind)
        {
            var wallField = typeof(GameEngine).GetField("_wall",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var tilesField = typeof(Wall).GetField("_tiles",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var tiles = (Tile[])tilesField.GetValue(wallField.GetValue(engine));
            tiles[Wall.TotalCount - 1] = new Tile(9999, (TileKind)kind);
        }

        [Fact]
        public async Task 明杠补牌后_开杠机会不被吞掉_且副露事件早于补牌()
        {
            var rules = RuleConfig.CreateCoinMatch();
            var events = new List<string>();
            bool sawConcealedKongOption = false;
            var agents = new IAgent[4];
            agents[0] = new ScriptedAgent(0,
                discardRule: ctx => ctx.Hand.FirstOrDefault(t => t.Kind == TileKind.Man1) ?? ctx.Hand.First(),
                concealedRule: ctx => null,
                claimRule: ctx => null);
            agents[1] = new ScriptedAgent(1,
                claimRule: ctx => ctx.ClaimOptions.FirstOrDefault(o => o.Type == ClaimType.Kong),
                concealedRule: ctx => null);   // 只验证选项被给出，不真的继续开杠
            agents[2] = new ScriptedAgent(2, neverHu: true);
            agents[3] = new ScriptedAgent(3, neverHu: true);

            var engine = new GameEngine(rules, agents, 0, 7);
            engine.EventRaised += e =>
            {
                if (e.Message == "杠" || e.Message == "杠后补牌" || e.Message == "杠上开花")
                    events.Add(e.Message);
                if (e.Type == GameEventType.ConcealedActionOffered && e.ClaimOptions != null &&
                    e.ClaimOptions.Any(o => o.Type == ClaimType.ConcealedKong))
                    sawConcealedKongOption = true;
                if (e.Type != GameEventType.GameStarted) return;
                SetHand(engine.Players[0], 0, 3, 6, 9, 12, 15, 18, 21, 24, 27, 30, 31, 32, 33);
                // 1 号：三张一万供明杠 + 三个九万，补到第四张九万即可暗杠
                SetHand(engine.Players[1], 0, 0, 0, 8, 8, 8, 2, 5, 12, 17, 22, 27, 31);
                for (int p = 2; p < 4; p++)
                    SetHand(engine.Players[p], 3, 6, 9, 12, 15, 18, 21, 24, 27, 30, 31, 32);
                SetNextReplacementTile(engine, 8);
            };
            await engine.RunAsync();

            int iMeld = events.IndexOf("杠");
            int iDraw = events.IndexOf("杠后补牌");
            int iOffer = events.IndexOf("杠上开花");
            Assert.True(iMeld >= 0 && iDraw > iMeld, "副露成立事件必须早于补牌，否则同款补牌会被 UI 误扣");
            Assert.True(iOffer > iDraw, "明杠补牌后应重新进入 concealed 征询");
            Assert.True(sawConcealedKongOption, "补牌后出现的暗杠机会不应被吞掉");
        }
    }
}
