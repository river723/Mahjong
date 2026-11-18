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
        private readonly bool _preferMoreWaits;

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
        /// <param name="preferMoreWaits">较真档之上再优先选进张种类最多的牌（最高难度）。</param>
        public AiPlayer(int index, AiProfile profile, int seed, RuleConfig rules,
            int claimPercent = 50, bool preferMoreWaits = false)
        {
            _index = index;
            _profile = profile;
            _rng = new Random(seed);
            _eval = new HandEvaluator(rules);
            _memory = new TileMemory();
            _claimPercent = claimPercent;
            _preferMoreWaits = preferMoreWaits;
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

                // 三档差异：普通纯效率；较真在同效率时偏向安全牌（外面剩得少的）；
                // 大师先比进张种类数，再比进张张数与安全。
                long score;
                if (_preferMoreWaits)
                    score = (long)c.UsefulKinds * 1000 + c.UsefulTiles * 4 + (4 - c.Remaining);
                else if (_profile.Name == "较真")
                    score = (long)c.UsefulTiles * 4 + (4 - c.Remaining);
                else
                    score = (long)c.UsefulTiles * 10;

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

            // 碰/杠按难度给定的概率接受（普通档保持旧的 50% 掷法，随机序列不变）
            var kong = context.ClaimOptions?.FirstOrDefault(o => o.Type == ClaimType.Kong);
            if (kong != null && RollClaim()) return Task.FromResult(kong);
            var pong = context.ClaimOptions?.FirstOrDefault(o => o.Type == ClaimType.Pong);
            if (pong != null && RollClaim()) return Task.FromResult(pong);
            return Task.FromResult<ClaimOption>(null);
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
