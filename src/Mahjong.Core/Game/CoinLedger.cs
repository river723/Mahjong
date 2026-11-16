using System;
using System.Collections.Generic;
using Mahjong.Core.Rules;

namespace Mahjong.Core.Game
{
    /// <summary>一局结算后的账本快照（供结算面板与终局排名使用）。</summary>
    public sealed class SettlementOutcome
    {
        /// <summary>本局各家金币变动。</summary>
        public int[] ScoreDelta { get; set; }

        /// <summary>结算后各家余额。</summary>
        public int[] CoinsAfter { get; set; }

        /// <summary>本场是否已结束。</summary>
        public bool IsMatchOver { get; set; }

        /// <summary>触发结束的破产玩家索引（0-3，可能多家）。</summary>
        public int[] BankruptPlayers { get; set; }

        /// <summary>已进行的局数。</summary>
        public int RoundIndex { get; set; }
    }

    /// <summary>
    /// 跨局金币账本：会话级对象，必须活过单个窗口（本项目每局新建 MainWindow）。
    /// 每局把 GameResult.Scores 全额累加（不截断到 0，允许出现负数），
    /// 之后若任一家 &lt;= 0 则整场结束；金币总额恒为 InitialCoins × 4。
    /// </summary>
    public sealed class CoinLedger
    {
        private readonly RuleConfig _rules;
        private readonly int[] _coins = new int[4];

        public CoinLedger(RuleConfig rules)
        {
            _rules = rules ?? new RuleConfig();
            Reset();
        }

        public int[] Coins { get { return (int[])_coins.Clone(); } }

        public int RoundIndex { get; private set; }

        public bool IsMatchOver { get; private set; }

        public int[] BankruptPlayers { get; private set; }

        /// <summary>按余额降序返回玩家索引，用于终局排名。</summary>
        public int[] Ranking()
        {
            var order = new[] { 0, 1, 2, 3 };
            Array.Sort(order, (a, b) => _coins[b].CompareTo(_coins[a]));
            return order;
        }

        /// <summary>把一局结果记入账本；流局 Scores 全 0，不产生转移。</summary>
        public SettlementOutcome Apply(GameResult result)
        {
            var delta = new int[4];
            if (result != null && result.Scores != null && result.Scores.Length == 4)
                Array.Copy(result.Scores, delta, 4);

            if (!IsMatchOver)
            {
                for (int p = 0; p < 4; p++) _coins[p] += delta[p];
                RoundIndex++;

                if (_rules.EndOnNonPositiveCoin)
                {
                    var bust = new List<int>();
                    for (int p = 0; p < 4; p++)
                    {
                        if (_coins[p] <= 0) bust.Add(p);
                    }
                    if (bust.Count > 0)
                    {
                        IsMatchOver = true;
                        BankruptPlayers = bust.ToArray();
                    }
                }
            }

            return new SettlementOutcome
            {
                ScoreDelta = delta,
                CoinsAfter = Coins,
                IsMatchOver = IsMatchOver,
                BankruptPlayers = BankruptPlayers ?? new int[0],
                RoundIndex = RoundIndex
            };
        }

        /// <summary>开一场新的：四家恢复初始金币，局数与终局标志清零。</summary>
        public void Reset()
        {
            for (int p = 0; p < 4; p++) _coins[p] = _rules.InitialCoins;
            RoundIndex = 0;
            IsMatchOver = false;
            BankruptPlayers = new int[0];
        }
    }
}
