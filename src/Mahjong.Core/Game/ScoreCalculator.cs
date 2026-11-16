using System;
using Mahjong.Core.Rules;

namespace Mahjong.Core.Game
{
    /// <summary>
    /// 金币场计分器：
    ///   每家支付单价 u = 牌型基价 × 自摸倍率 + 明杠数 × 1 + 暗杠数 × 2
    /// 三家各付 u、胡家收 3u，得分向量长度 4 且总和恒为 0。
    ///
    /// 与旧番分制的区别（口径已确认）：
    ///   1) 点炮也是三家各付，放铳者不多付，因此 loserIndex 只作记录、不参与结算；
    ///   2) 自摸是「牌型基价 × 2」的倍率，不是在底分上加分；
    ///   3) 杠加成在倍率之后（不吃自摸倍率），明杠 +1/家、暗杠 +2/家，
    ///      且只统计胡牌者已成立的杠；
    ///   4) 没胡牌的人杠了不产生任何转移（白杠），流局零转移。
    /// </summary>
    public sealed class ScoreCalculator
    {
        private readonly RuleConfig _rules;

        public ScoreCalculator(RuleConfig rules)
        {
            _rules = rules ?? new RuleConfig();
        }

        /// <summary>兼容旧调用的重载：普通胡、无杠。</summary>
        public int[] ComputeScores(int winnerIndex, int loserIndex, bool isSelfDraw)
        {
            return ComputeScores(winnerIndex, loserIndex, isSelfDraw, WinPattern.Plain());
        }

        /// <summary>计算一局的金币变动向量（长度 4，和为 0）。winnerIndex &lt; 0 为流局。</summary>
        public int[] ComputeScores(int winnerIndex, int loserIndex, bool isSelfDraw, WinPattern pattern)
        {
            var scores = new int[4];
            if (winnerIndex < 0 || winnerIndex > 3) return scores;

            int unit = ComputeUnitPay(pattern, isSelfDraw);
            for (int p = 0; p < 4; p++)
            {
                if (p == winnerIndex) continue;
                scores[p] = -unit;
            }
            scores[winnerIndex] = unit * 3;
            return scores;
        }

        /// <summary>本局每家应付的金币数（杠加成不吃自摸倍率）。</summary>
        public int ComputeUnitPay(WinPattern pattern, bool isSelfDraw)
        {
            WinPattern p = pattern ?? WinPattern.Plain();
            int basePay = p.BasePay(_rules);
            int multiplier = isSelfDraw ? Math.Max(1, _rules.SelfDrawMultiplier) : 1;
            return basePay * multiplier + p.KongBonus(_rules);
        }
    }
}
