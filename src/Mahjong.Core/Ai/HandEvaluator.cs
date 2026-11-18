using System.Collections.Generic;
using Mahjong.Core.Rules;

namespace Mahjong.Core.Ai
{
    /// <summary>
    /// 手牌评估器：基于暗牌计数计算向听数与有效进张，供 AI 出牌决策。
    /// 与 WinChecker 同源，但不重复判定逻辑。
    /// </summary>
    public sealed class HandEvaluator
    {
        private readonly RuleConfig _rules;

        public HandEvaluator(RuleConfig rules)
        {
            _rules = rules;
        }

        /// <summary>
        /// 对任意暗牌计数返回向听数（-1 和牌，0 听牌，1 一向听…）。
        /// fixedMelds 是已副露的组数，按已完成的面子计入。
        /// </summary>
        public int GetShanten(int[] counts, int fixedMelds)
        {
            return ShantenCalculator.Calculate(counts, fixedMelds, _rules != null && _rules.AllowSevenPairs);
        }

        /// <summary>
        /// 返回 13 张（或带副露的 3n+1 张）手牌的进张状态：
        /// usefulKinds = 摸到后能降低向听数的牌种数；usefulTiles = 这些牌在外面剩余张数之和。
        /// 注意 outsideCount 必须是「打掉当前候选牌之后」的 visible 剩余（调用方负责把那张加回去）。
        /// </summary>
        public void MeasureUsefulTiles(int[] hand, int fixedMelds, int[] outsideCount,
            out int usefulKinds, out int usefulTiles)
        {
            usefulKinds = 0;
            usefulTiles = 0;
            int current = GetShanten(hand, fixedMelds);
            int[] work = (int[])hand.Clone();
            for (int k = 0; k < 34; k++)
            {
                if (outsideCount[k] <= 0) continue;
                work[k]++;
                bool useful = GetShanten(work, fixedMelds) < current;
                work[k]--;
                if (!useful) continue;
                usefulKinds++;
                usefulTiles += outsideCount[k];
            }
        }

        /// <summary>兼容旧调用：13 张手牌的听牌集合（可进张）。</summary>
        public List<int> GetWaits(int[] hand13)
        {
            return TenpaiDetector.GetWaits(hand13, _rules);
        }
    }
}
