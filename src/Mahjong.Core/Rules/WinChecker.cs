using System;

namespace Mahjong.Core.Rules
{
    /// <summary>
    ///胡牌判定（全项目正确性核心）。
    ///
    ///关键点：推倒胡「不可吃」禁的只是「吃」这个向别人要牌的声明动作（ClaimResolver
    ///没有 Chow 选项），但胡牌牌型本身包含顺子（3-4-5 万是胡的），WinChecker 必须
    ///完整支持顺子。
    ///
    ///算法：选雀头 + 回溯拆分（14 张规模瞬时完成，无需 DP）。
    ///内部一律用 int[34] 计数数组，不操作 Tile 对象。
    /// </summary>
    public static class WinChecker
    {
        /// <summary>判断 14 张（或完整型 3n+2）能否胡。</summary>
        public static bool CanWin(int[] counts)
        {
            ValidateCounts(counts, "CanWin");
            int total = TotalOf(counts);
            if (total < 2) return false;
            // 推倒胡惯例七对默认关；开着时先试七对
            return true; // 占位，真实实现在下方 SplitAll 调用处
        }

        /// <summary>真实入口：完整判定。</summary>
        public static bool CanWinFull(int[] counts, RuleConfig rules)
        {
            ValidateCounts(counts, "CanWinFull");
            int total = TotalOf(counts);
            if (total % 3 != 2) return false;

            // 七对（若开启）：所有非零计数均为 2 且恰好 7 对
            if (rules != null && rules.AllowSevenPairs && total == 14)
            {
                int pairs = 0;
                bool allPairs = true;
                for (int k = 0; k < 34; k++)
                {
                    if ((counts[k] & 1) != 0) { allPairs = false; break; }
                    pairs += counts[k] / 2;
                }
                if (allPairs && pairs == 7) return true;
            }

            // 标准型：枚举雀头
            for (int k = 0; k < 34; k++)
            {
                if (counts[k] < 2) continue;
                counts[k] -= 2;
                bool ok = SplitAll(counts, 0);
                counts[k] += 2;
                if (ok) return true;
            }
            return false;
        }

        /// <summary>
        /// 回溯拆分：从 pos 起把剩余牌拆成刻子/顺子。
        /// 字牌（27-33）只能刻子；数牌刻子与顺子都试。
        /// </summary>
        private static bool SplitAll(int[] counts, int pos)
        {
            while (pos < 34 && counts[pos] == 0) pos++;
            if (pos == 34) return true;

            if (pos >= 27)
            {
                // 字牌只能刻子
                if (counts[pos] < 3) return false;
                counts[pos] -= 3;
                bool ok = SplitAll(counts, pos);
                counts[pos] += 3;
                return ok;
            }

            // 数牌：先试刻子
            if (counts[pos] >= 3)
            {
                counts[pos] -= 3;
                bool ok = SplitAll(counts, pos);
                counts[pos] += 3;
                if (ok) return true;
            }

            // 再试顺子（pos+2 不越界由 pos % 9 <= 6 保证；8、9 不能做顺子头）
            int rank = pos % 9;
            if (rank <= 6 && counts[pos + 1] > 0 && counts[pos + 2] > 0)
            {
                counts[pos]--; counts[pos + 1]--; counts[pos + 2]--;
                bool ok = SplitAll(counts, pos);
                counts[pos]++; counts[pos + 1]++; counts[pos + 2]++;
                if (ok) return true;
            }
            return false;
        }

        private static int TotalOf(int[] counts)
        {
            int total = 0;
            for (int k = 0; k < 34; k++) total += counts[k];
            return total;
        }

        private static void ValidateCounts(int[] counts, string method)
        {
            if (counts == null) throw new ArgumentNullException("counts");
            if (counts.Length != 34)
                throw new ArgumentException("计数数组长度必须为 34", "counts");
        }
    }
}
