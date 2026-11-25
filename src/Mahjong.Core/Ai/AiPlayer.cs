using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mahjong.Core.Game;
using Mahjong.Core.Rules;
using Mahjong.Core.Tiles;

namespace Mahjong.Core.Ai
{
    /// <summary>AI 档位配置。</summary>
    public sealed class AiProfile
    {
        public string Name { get; set; }
        public AiProfile(string name) { Name = name; }
        public static readonly AiProfile Easy = new AiProfile("简单");
        public static readonly AiProfile Normal = new AiProfile("普通");
        public static readonly AiProfile Hard = new AiProfile("较真");
        public static readonly AiProfile Master = new AiProfile("大师");
    }

    /// <summary>AI 玩家（非作弊，只用公开信息）。</summary>
    public sealed class AiPlayer : IAgent
    {
        private readonly int _index;
        private readonly AiProfile _profile;
        private readonly Random _rng;
        private readonly HandEvaluator _eval;
        private readonly TileMemory _memory;
        private readonly int _claimPercent;
        private readonly int _skill;

        private sealed class Candidate
        {
            public Tile Tile;
            public int Kind;
            public int Shanten;
            public int UsefulKinds;
            public int UsefulTiles;
            public int Remaining;
        }

        /// <param name="claimPercent">碰/杠的接受概率（0-100），由难度档位给定。</param>
        /// <param name="preferMoreWaits">旧调用方式：true 表示最高档（大师）。</param>
        /// <param name="skill">强度档位 0=普通 1=较真 2=大师；-1 表示按 profile 推断，兼容旧调用。</param>
        public AiPlayer(int index, AiProfile profile, int seed, RuleConfig rules,
            int claimPercent = 50, bool preferMoreWaits = false, int skill = -1)
        {
            _index = index;
            _profile = profile;
            _rng = new Random(seed);
            _eval = new HandEvaluator(rules);
            _memory = new TileMemory();
            _claimPercent = claimPercent;
            _skill = skill >= 0 ? skill
                : (profile == AiProfile.Master || preferMoreWaits) ? 2
                : profile == AiProfile.Hard ? 1 : 0;
        }

        /// <summary>
        /// 出牌：先按向听数最小筛出候选，再在同级候选里比较进张余量。
        /// 旧实现只在「已经听牌」时才挑牌，没听就打手牌第一张——等于把起手牌
        /// 按发牌顺序打光，永远不会向听，实测四家胡牌率不到 10%。
        /// </summary>
        public Task<Tile> ChooseDiscard(DecisionContext context)
        {
            var hand = context.Hand;
            if (hand == null || hand.Count == 0) return Task.FromResult<Tile>(null);

            // 每次决策用公开信息重建记牌器：自己手牌 + 牌河 + 四家副露。
            if (context != null) _memory.Rebuild(hand, context.Discards, context.AllMelds);

            // 简单档保留旧行为（打第一张），供对照测试用。
            if (_profile.Name == "简单") return Task.FromResult(hand[0]);

            int fixedMelds = context.Melds != null ? context.Melds.Count : 0;
            int[] counts = new int[34];
            foreach (var t in hand) counts[(int)t.Kind]++;

            int[] outside = new int[34];
            for (int k = 0; k < 34; k++) outside[k] = _memory.Remaining((TileKind)k);

            // 同款牌只评估一次，打哪张都一样。
            var candidates = new List<Candidate>();
            var seen = new bool[34];
            foreach (var t in hand)
            {
                int k = (int)t.Kind;
                if (seen[k]) continue;
                seen[k] = true;

                counts[k]--;
                int shanten = _eval.GetShanten(counts, fixedMelds);
                counts[k]++;
                candidates.Add(new Candidate { Tile = t, Kind = k, Shanten = shanten });
            }

            if (candidates.Count == 0) return Task.FromResult(hand[0]);
            int bestShanten = candidates.Min(c => c.Shanten);

            // 「看漏搭子」概率：普通 12%、较真 4%、大师 0%。
            // 偶尔退一步选向听更差的打法，是三档强弱的主要来源；
            // 幅度控制住，普通档仍以胡牌为主，不能退回以前 90% 流局的状态。
            int slipPercent = _skill == 0 ? 12 : (_skill == 1 ? 4 : 0);
            if (slipPercent > 0 && _rng.Next(100) < slipPercent)
            {
                var worse = candidates.Where(c => c.Shanten == bestShanten + 1).ToList();
                if (worse.Count > 0)
                    return Task.FromResult(worse[_rng.Next(worse.Count)].Tile);
            }

            // 只对最低向听的候选算进张，避免 14×34 次无谓搜索。
            foreach (var c in candidates)
            {
                if (c.Shanten != bestShanten) continue;
                counts[c.Kind]--;
                outside[c.Kind]++; // 这张马上打出去，回到「外面」
                _eval.MeasureUsefulTiles(counts, fixedMelds, outside,
                    out c.UsefulKinds, out c.UsefulTiles);
                c.Remaining = outside[c.Kind];
                outside[c.Kind]--;
                counts[c.Kind]++;
            }

            Candidate best = null;
            long bestScore = long.MinValue;
            foreach (var c in candidates)
            {
                if (c.Shanten != bestShanten) continue;

                // 三档梯度（都按向听数最小筛过，差别在同级候选怎么挑）：
                // 普通：只看进张张数，再叠一点判断噪声（偶尔看漏），碰杠也保守；
                // 较真：记牌后按进张张数取最优，同分偏向外面剩得少的安全牌；
                // 大师：进张种类优先（听面广更容易先摸到），再比张数和安全，碰杠最积极。
                long score;
                if (_skill >= 2)
                    score = (long)c.UsefulKinds * 15 + c.UsefulTiles * 10 + (4 - c.Remaining);
                else if (_skill == 1)
                    score = (long)c.UsefulTiles * 10 + (4 - c.Remaining) * 2;
                else
                    score = (long)c.UsefulTiles * 10 + _rng.Next(0, 120);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = c;
                }
            }

            return Task.FromResult(best != null ? best.Tile : hand[0]);
        }

        public Task<ClaimOption> ChooseClaim(DecisionContext context)
        {
            // 胡优先
            var win = context.ClaimOptions?.FirstOrDefault(o => o.Type == ClaimType.Win);
            if (win != null) return Task.FromResult(win);

            var kong = context.ClaimOptions?.FirstOrDefault(o => o.Type == ClaimType.Kong);
            var pong = context.ClaimOptions?.FirstOrDefault(o => o.Type == ClaimType.Pong);

            // 大师档：杠几乎总有利，直接接；碰会跳过一次摸牌，只有真能降低向听才碰。
            // 旧版大师无脑高概率碰，反而把成牌拖慢了——这里改成会算账。
            if (_skill >= 2)
            {
                if (kong != null) return Task.FromResult(kong);
                if (pong != null && PongImprovesShanten(context, pong)) return Task.FromResult(pong);
                return Task.FromResult<ClaimOption>(null);
            }

            // 普通/较真按难度给定的概率接受。
            if (kong != null && RollClaim()) return Task.FromResult(kong);
            if (pong != null && RollClaim()) return Task.FromResult(pong);
            return Task.FromResult<ClaimOption>(null);
        }

        /// <summary>碰之后不摸牌直接出一张：枚举打哪张最好，只有向听真降了才值得碰。</summary>
        private bool PongImprovesShanten(DecisionContext context, ClaimOption option)
        {
            var hand = context.Hand;
            if (hand == null) return false;
            int fixedMelds = context.Melds != null ? context.Melds.Count : 0;
            var counts = new int[34];
            foreach (var t in hand) counts[(int)t.Kind]++;
            int before = _eval.GetShanten(counts, fixedMelds);

            int k = (int)option.Kind;
            if (counts[k] < 2) return false;
            counts[k] -= 2;   // 自己手里的两张拿去凑碰，第三张是别人打出的

            int after = int.MaxValue;
            for (int j = 0; j < 34; j++)
            {
                if (counts[j] == 0) continue;
                counts[j]--;
                int s = _eval.GetShanten(counts, fixedMelds + 1);
                counts[j]++;
                if (s < after) after = s;
            }
            return after < before;
        }

        private bool RollClaim()
        {
            if (_claimPercent == 50) return _rng.Next(2) == 0;
            return _rng.Next(100) < _claimPercent;
        }

        public Task<ClaimOption> ChooseConcealedAction(DecisionContext context)
        {
            // 自摸/杠上开花优先；没有胡时按「暗杠 > 补杠」开杠。
            // 这里刻意不掷 _rng，避免改变既有种子下的随机序列。
            var win = context.ClaimOptions?.FirstOrDefault(o => o.Type == ClaimType.Win);
            if (win != null) return Task.FromResult(win);
            var concealedKong = context.ClaimOptions?.FirstOrDefault(o => o.Type == ClaimType.ConcealedKong);
            if (concealedKong != null) return Task.FromResult(concealedKong);
            var addedKong = context.ClaimOptions?.FirstOrDefault(o => o.Type == ClaimType.AddedKong);
            return Task.FromResult(addedKong);
        }
    }
}
