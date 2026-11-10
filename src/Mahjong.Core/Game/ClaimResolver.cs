using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Core.Rules;
using Mahjong.Core.Tiles;

namespace Mahjong.Core.Game
{
    /// <summary>
    ///声明结算器：每次有人出牌后广播，收集碰/杠/胡声明并按优先级结算。
    ///胡 > 碰/杠；多家可胡时取离点炮者最近（逆时针）一家。
    ///无吃（推倒胡）。
    /// </summary>
    public sealed class ClaimResolver
    {
        private readonly RuleConfig _rules;

        public ClaimResolver(RuleConfig rules)
        {
            _rules = rules;
        }

        /// <summary>
        ///计算某玩家对刚打出的牌有哪些声明选项（不含 Win 时的 CanWin 判定——那需要完整暗牌计数，
        ///由引擎组装 counts 后调用 WinChecker；这里只做碰/杠的手牌张数判定与胡的入口判定）。
        /// </summary>
        public List<ClaimOption> GetOptions(PlayerState player, Tile discarded, int[] handCounts)
        {
            var options = new List<ClaimOption>();
            int kind = (int)discarded.Kind;

            // 胡：手牌 + 打出的这张 = 14 张能胡
            int[] winCounts = (int[])handCounts.Clone();
            winCounts[kind]++;
            if (WinChecker.CanWinFull(winCounts, _rules))
            {
                options.Add(ClaimOption.Of(ClaimType.Win, discarded.Kind));
            }

            // 碰：手里有 2 张同款
            if (_rules.AllowPong && handCounts[kind] >= 2)
            {
                options.Add(ClaimOption.Of(ClaimType.Pong, discarded.Kind));
            }

            // 明杠：手里有 3 张同款
            if (_rules.AllowKong && handCounts[kind] >= 3)
            {
                options.Add(ClaimOption.Of(ClaimType.Kong, discarded.Kind));
            }

            return options;
        }

        /// <summary>
        ///多家声明结算。返回赢的声明与声明者；全部放弃返回 null。
        ///优先级：胡 > 杠 > 碰（同为胡时取离点炮者最近，逆时针）。
        /// </summary>
        public ClaimResolution Resolve(IList<ClaimOption>[] claimsByPlayer, int discarder)
        {
            // 找胡
            int winner = -1;
            int bestDistance = int.MaxValue;
            for (int p = 0; p < claimsByPlayer.Length; p++)
            {
                if (p == discarder) continue;
                var winOpt = claimsByPlayer[p] == null ? null :
                    claimsByPlayer[p].FirstOrDefault(o => o.Type == ClaimType.Win);
                if (winOpt != null)
                {
                    // 逆时针距离：从点炮者出发走几步到 p
                    int distance = (p - discarder + 4) % 4;
                    if (distance > 0 && distance < bestDistance)
                    {
                        bestDistance = distance;
                        winner = p;
                    }
                }
            }
            if (winner >= 0)
            {
                return new ClaimResolution(winner, ClaimType.Win, claimsByPlayer[winner]
                    .First(o => o.Type == ClaimType.Win), discarder);
            }

            // 找杠/碰（有杠优先于碰——同一个人时；不同人之间碰/杠都是该人获得，无竞争）
            for (int p = 0; p < claimsByPlayer.Length; p++)
            {
                if (p == discarder) continue;
                if (claimsByPlayer[p] == null) continue;
                var kong = claimsByPlayer[p].FirstOrDefault(o => o.Type == ClaimType.Kong);
                if (kong != null) return new ClaimResolution(p, ClaimType.Kong, kong, discarder);
                var pong = claimsByPlayer[p].FirstOrDefault(o => o.Type == ClaimType.Pong);
                if (pong != null) return new ClaimResolution(p, ClaimType.Pong, pong, discarder);
            }

            return null;
        }
    }

    /// <summary>结算出的声明结果。</summary>
    public sealed class ClaimResolution
    {
        public int PlayerIndex { get; private set; }
        public ClaimType Type { get; private set; }
        public ClaimOption Option { get; private set; }

        /// <summary>被声明那张牌的打出者（用于副露 FromPlayer 与点炮计分）。</summary>
        public int FromDiscarder { get; private set; }

        public ClaimResolution(int playerIndex, ClaimType type, ClaimOption option, int fromDiscarder)
        {
            PlayerIndex = playerIndex;
            Type = type;
            Option = option;
            FromDiscarder = fromDiscarder;
        }
    }
}
