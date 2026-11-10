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

        public AiPlayer(int index, AiProfile profile, int seed, RuleConfig rules)
        {
            _index = index;
            _profile = profile;
            _rng = new Random(seed);
            _eval = new HandEvaluator(rules);
            _memory = new TileMemory();
        }

        public Task<Tile> ChooseDiscard(DecisionContext context)
        {
            var hand = context.Hand;
            if (hand == null || hand.Count == 0) return Task.FromResult<Tile>(null);

            // 更新记牌器：本玩家手牌已知，其他牌未知（外面剩余由记牌器维护）

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

        /// <summary>较真档：普通之上 + 打剩余张数最多的听牌安全牌。</summary>
        private Tile ChooseHardDiscard(IList<Tile> hand)
        {
            var normal = ChooseNormalDiscard(hand);
            // 如果不止一种“听牌安全牌”，选外面剩余张数最多（最安全）的
            int[] counts = new int[34];
            foreach (var t in hand) counts[(int)t.Kind]++;

            int bestKind = (int)normal.Kind;
            int bestRemaining = _memory.Remaining(normal.Kind);

            foreach (var t in hand)
            {
                int[] temp = (int[])counts.Clone();
                temp[(int)t.Kind]--;
                var waits = _eval.GetWaits(temp);
                if (waits.Count > 0)
                {
                    int rem = _memory.Remaining(t.Kind);
                    if (rem > bestRemaining) { bestRemaining = rem; bestKind = (int)t.Kind; }
                }
            }
            foreach (var t in hand) if ((int)t.Kind == bestKind) return t;
            return normal;
        }

        public Task<ClaimOption> ChooseClaim(DecisionContext context)
        {
            // 胡优先
            var win = context.ClaimOptions?.FirstOrDefault(o => o.Type == ClaimType.Win);
            if (win != null) return Task.FromResult(win);

            // 普通及较真：碰/杠需判断是否升向听数；这里简化：随机 50% 决策
            var kong = context.ClaimOptions?.FirstOrDefault(o => o.Type == ClaimType.Kong);
            if (kong != null && _rng.Next(2) == 0) return Task.FromResult(kong);
            var pong = context.ClaimOptions?.FirstOrDefault(o => o.Type == ClaimType.Pong);
            if (pong != null && _rng.Next(2) == 0) return Task.FromResult(pong);
            return Task.FromResult<ClaimOption>(null);
        }

        public Task<ClaimOption> ChooseConcealedAction(DecisionContext context)
        {
            return Task.FromResult<ClaimOption>(null);
        }
    }
}