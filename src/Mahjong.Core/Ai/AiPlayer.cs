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

        /// <param name="claimPercent">碰/杠的接受概率（0-100），由难度档位给定。</param>
        /// <param name="preferMoreWaits">较真档之上再优先选进张最多的牌（最高难度）。</param>
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

        public Task<Tile> ChooseDiscard(DecisionContext context)
        {
            var hand = context.Hand;
            if (hand == null || hand.Count == 0) return Task.FromResult<Tile>(null);

            // 每次决策用公开信息重建记牌器：自己手牌 + 牌河 + 四家副露。
            // 之前这里只有注释没有代码，导致较真档的「安全牌」判断恒为 4 张、形同虚设。
            if (context != null) _memory.Rebuild(hand, context.Discards, context.AllMelds);

            Tile chosen;
            switch (_profile.Name)
            {
                case "简单":
                    chosen = hand[0];
                    break;
                case "普通":
                    chosen = ChooseNormalDiscard(hand);
                    break;
                case "较真":
                    chosen = ChooseHardDiscard(hand);
                    break;
                default:
                    chosen = hand[0];
                    break;
            }
            return Task.FromResult(chosen);
        }

        /// <summary>普通档：优先打不破坏听牌的牌，否则打第一张。</summary>
        private Tile ChooseNormalDiscard(IList<Tile> hand)
        {
            // 构造 13 张手牌计数（含当前手牌中的 13 张，不含摸牌——摸牌后选出打哪张，剩下的 13 张就是新手牌）
            int[] counts = new int[34];
            foreach (var t in hand) counts[(int)t.Kind]++;

            foreach (var t in hand)
            {
                int[] temp = (int[])counts.Clone();
                temp[(int)t.Kind]--; // 打掉 t
                var waits = _eval.GetWaits(temp);
                if (waits.Count > 0) return t; // 打掉 t 后仍能听牌
            }
            return hand[0]; // 兜底
        }

        /// <summary>
        /// 较真档：在所有「打掉后仍能听牌」的候选里挑一张。
        /// 默认优先打外面剩余最少的牌（越接近绝张越不容易点炮）；
        /// 最高难度（preferMoreWaits）反过来先比进张种类数，再比安全度。
        /// 说明：旧实现写的是「剩余最多最安全」，方向反了，且记牌器没接线恒为 4，实际等于普通档。
        /// </summary>
        private Tile ChooseHardDiscard(IList<Tile> hand)
        {
            int[] counts = new int[34];
            foreach (var t in hand) counts[(int)t.Kind]++;

            Tile best = null;
            int bestWaits = -1;
            int bestRemaining = int.MaxValue;

            foreach (var t in hand)
            {
                int[] temp = (int[])counts.Clone();
                temp[(int)t.Kind]--;
                int waitCount = _eval.GetWaits(temp).Count;
                if (waitCount == 0) continue;

                int rem = _memory.Remaining(t.Kind);
                bool better = _preferMoreWaits
                    ? (waitCount > bestWaits) || (waitCount == bestWaits && rem < bestRemaining)
                    : (rem < bestRemaining) || (rem == bestRemaining && waitCount > bestWaits);
                if (!better) continue;

                best = t;
                bestWaits = waitCount;
                bestRemaining = rem;
            }
            return best ?? ChooseNormalDiscard(hand);
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