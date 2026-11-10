using System;
using System.Threading.Tasks;
using Mahjong.Core.Game;
using Mahjong.Core.Rules;
using Mahjong.Core.Tiles;

namespace Mahjong.App.Services
{
    /// <summary>
    /// 人类玩家 Agent：使用 TaskCompletionSource 让 MainWindow 的按钮点击接入引擎决策流程。
    /// </summary>
    public sealed class HumanAgent : IAgent
    {
        private readonly int _playerIndex;
        private readonly GameHost _host;
        private readonly TaskCompletionSource<Tile> _discardTcs = new TaskCompletionSource<Tile>();
        private readonly TaskCompletionSource<ClaimOption> _claimTcs = new TaskCompletionSource<ClaimOption>();
        // 可重复的出牌请求通道：每一回合由引擎发起，UI 回调用 CompleteDiscard 响应
        private TaskCompletionSource<Tile> _pendingDiscard;
        private readonly object _discardLock = new object();

        public HumanAgent(int playerIndex, GameHost host)
        {
            _playerIndex = playerIndex;
            _host = host;
        }

        public Task<Tile> ChooseDiscard(DecisionContext context)
        {
            // 每次引擎征询都创建一个全新的等待点，避免上一回合已完成的 TCS 被复用导致死锁
            lock (_discardLock)
            {
                _pendingDiscard = new TaskCompletionSource<Tile>();
            }
            return _pendingDiscard.Task;
        }

        public Task<ClaimOption> ChooseClaim(DecisionContext context)
        {
            return _claimTcs.Task;
        }

        public Task<ClaimOption> ChooseConcealedAction(DecisionContext context)
        {
            return Task.FromResult<ClaimOption>(null);
        }

        /// <summary>UI 回调用：人类选定某张手牌打出（由 GameHost.HumanChooseDiscard 触发）。</summary>
        public void CompleteDiscard(Tile tile)
        {
            TaskCompletionSource<Tile> tcs;
            lock (_discardLock)
            {
                tcs = _pendingDiscard;
                _pendingDiscard = null;
            }
            if (tcs != null)
            {
                tcs.TrySetResult(tile);
                return;
            }
            // 没有挂起的出牌请求（例如误操作）：降级到一次性 TCS
            try { _discardTcs.TrySetResult(tile); } catch { /* 已填充则忽略 */ }
        }

        public void CompleteClaim(ClaimOption option)
        {
            _claimTcs.SetResult(option);
        }
    }
}