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
            Ledger = new CoinLedger(Rules);
        }

        /// <summary>本场结束（有人金币 &lt;= 0）后，重开一场：账本归零。</summary>
        public void StartNewMatch()
        {
            Ledger.Reset();
            LastOutcome = null;
        }
    }
}
