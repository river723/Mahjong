using Mahjong.Core.Game;
using Mahjong.Core.Rules;

namespace Mahjong.App.Services
{
    /// <summary>
    /// 一场牌局的会话状态：规则 + 金币账本 + 最近一次结算快照。
    /// 本项目每局新建 MainWindow，金币必须挂在窗口之外，否则点「再来一局」就被清零。
    /// 由 App.Session 持有，全场唯一。
    /// </summary>
    public sealed class MatchSession
    {
        public RuleConfig Rules { get; }

        public CoinLedger Ledger { get; }

        public SettlementOutcome LastOutcome { get; set; }

        public MatchSession()
        {
            Rules = RuleConfig.CreateCoinMatch();
            // 设置里的「每人初始金币」在开新的一场时生效：CoinLedger 构造/Reset 会读这份规则。
            Rules.InitialCoins = GameSettings.Current.InitialCoins;
            Ledger = new CoinLedger(Rules);
        }

        /// <summary>设置保存后同步规则值（进行中的账本余额不动，等下一场 Reset 才用）。</summary>
        public void ApplySettings()
        {
            Rules.InitialCoins = GameSettings.Current.InitialCoins;
        }

        /// <summary>本场结束（有人金币 &lt;= 0）后，重开一场：按最新设置重置账本。</summary>
        public void StartNewMatch()
        {
            ApplySettings();
            Ledger.Reset();
            LastOutcome = null;
        }
    }
}
