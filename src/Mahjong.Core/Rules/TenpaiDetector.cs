using System;
using System.Collections.Generic;

namespace Mahjong.Core.Rules
{
    /// <summary>
    ///听牌检测：枚举 34 种牌，「可见剩余 > 0 且 CanWin(手牌+该牌)」即为进张。
    ///服务三处：玩家胡牌提示、AI 数进张、较真 AI 向听判断。
    /// </summary>
    public static class TenpaiDetector
    {
        /// <summary>
        ///返回全部进张（-1 表示七对等特殊情形见调用方；本方法只做标准型）。
        ///参数 hand 为 13 张（3n+1）计数；返回进张列表（可能为空）。
        /// </summary>
        public static List<int> GetWaits(int[] hand13, RuleConfig rules)
        {
            var result = new List<int>();
            int[] work = (int[])hand13.Clone();
            for (int k = 0; k < 34; k++)
            {
                work[k]++;
                if (WinChecker.CanWinFull(work, rules)) result.Add(k);
                work[k]--;
            }
            return result;
        }

        /// <summary>快捷方式：13 张是否已听牌。</summary>
        public static bool IsTenpai(int[] hand13, RuleConfig rules)
        {
            return GetWaits(hand13, rules).Count > 0;
        }
    }
}
