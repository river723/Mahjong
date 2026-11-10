using Mahjong.Core.Rules;

namespace Mahjong.Core.Game
{
    /// <summary>
    ///计分器（推倒胡简单底分制）。
    ///番 = 底番 1 +（自摸 ? 1 : 0）。
    ///点炮：点炮者付「番分」（1 番 = 1 分）；自摸：其余三家各付「自摸番分」（1 番 = 每家 2 分，共 6）。
    ///杠不计分（v1 简化）。流局无转移。
    /// </summary>
    public sealed class ScoreCalculator
    {
        private readonly RuleConfig _rules;

        /// <summary>底番。</summary>
        public const int BaseFan = 1;

        /// <summary>自摸加番。</summary>
        public const int SelfDrawFan = 1;

        /// <summary>普通点炮每分。</summary>
        public const int DealInBase = 1;

        /// <summary>自摸时每家支付。</summary>
        public const int SelfDrawPerPlayer = 2;

        public ScoreCalculator(RuleConfig rules)
        {
            _rules = rules;
        }

        /// <summary>计算一局的得分向量（长度 4，和为 0）。</summary>
        public int[] ComputeScores(int winnerIndex, int loserIndex, bool isSelfDraw)
        {
            var scores = new int[4];
            if (winnerIndex < 0) return scores; // 流局

            int fan = BaseFan + (isSelfDraw ? SelfDrawFan : 0);
            if (isSelfDraw)
            {
                for (int p = 0; p < 4; p++)
                {
                    if (p == winnerIndex) continue;
                    scores[p] -= fan * SelfDrawDrawPerPlayerAdjust;
                }
                scores[winnerIndex] = fan * 3 * SelfDrawDrawPerPlayerAdjust;
            }
            else
            {
                int amount = fan * DealInBase;
                scores[loserIndex] -= amount;
                scores[winnerIndex] += amount;
            }
            return scores;
        }

        /// <summary>自摸每家实际支付（2 分基础 × 番数）。</summary>
        private const int SelfDrawDrawPerPlayerAdjust = SelfDrawPerPlayer;
    }
}
