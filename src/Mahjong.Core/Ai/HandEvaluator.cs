using System.Collections.Generic;
using Mahjong.Core.Rules;
using Mahjong.Core.Tiles;

namespace Mahjong.Core.Ai
{
    /// <summary>
    /// 手牌评估器：基于 13 张手牌，计算听牌数/听牌集合，用于 AI 出牌决策。
    /// 与 WinChecker 同源，但不重复判定逻辑，通过 TenpaiDetector 复用。
    /// </summary>
    public sealed class HandEvaluator
    {
        private readonly RuleConfig _rules;

        public HandEvaluator(RuleConfig rules)
        {
            _rules = rules;
        }

        /// <summary>
        /// 对 13 张手牌返回听牌集合（可进张）。
        /// </summary>
        public List<int> GetWaits(int[] hand13)
        {
            return Mahjong.Core.Rules.TenpaiDetector.GetWaits(hand13, _rules);
        }

        /// <summary>
        /// 评估打掉候选牌（hand13 中某张牌）后的留牌价值：
        /// 打掉后听牌数越高（越接近听牌）留牌越好；听牌数为 0 留牌不佳。
        /// </summary>
        public int EvaluateLeaveValue(int[] hand13, int discardKind)
        {
            var waits = GetWaits(hand13);
            foreach (int w in waits)
            {
                if (w == discardKind) return 100; // 打掉能进张，留牌价值高
            }
            // 打掉的牌本身不是听牌（听牌通常由进张构成），留牌价值按 0 处理（简单策略）
            return 0;
        }
    }
}