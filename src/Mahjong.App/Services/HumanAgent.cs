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
    public sealed class HumanAgent : IAgent, IHumanControlled
    {
        private readonly int _playerIndex;
        private readonly GameHost _host;
        // 可重复的出牌请求通道：每一回合由引擎发起，UI 回调用 CompleteDiscard 响应
        private TaskCompletionSource<Tile> _pendingDiscard;
        private readonly object _discardLock = new object();
        // 可重复的声明请求通道：每次引擎征询都新建等待点，避免已完成的 TCS 被复用导致异常/串局
        private TaskCompletionSource<ClaimOption> _pendingClaim;
        private readonly object _claimLock = new object();
        // 自摸/杠上开花的决策通道（与「他打牌后声明」分开，避免两个等待点串台）
        private TaskCompletionSource<ClaimOption> _pendingConcealed;
        private readonly object _concealedLock = new object();

        public HumanAgent(int playerIndex, GameHost host)
        {
            _playerIndex = playerIndex;
            _host = host;
        }

        /// <summary>当前是否正在等待人类出牌（供 UI 判断点击是否有效，避免"点了没反应"的困惑）。</summary>
        public bool HasPendingDiscard
        {
            get { lock (_discardLock) { return _pendingDiscard != null; } }
        }

        public Task<Tile> ChooseDiscard(DecisionContext context)
        {
            // 每次引擎征询都创建一个全新的等待点，避免上一回合已完成的 TCS 被复用导致死锁。
            // 用局部变量持有并返回，避免在 lock 外读取字段时的竞态（读到 null 或已完成的 TCS）。
            var tcs = new TaskCompletionSource<Tile>();
            lock (_discardLock)
            {
                _pendingDiscard = tcs;
            }
            return tcs.Task;
        }

        public Task<ClaimOption> ChooseClaim(DecisionContext context)
        {
            // 每次引擎征询都创建一个全新的等待点（与出牌通道同构）
            var tcs = new TaskCompletionSource<ClaimOption>();
            lock (_claimLock)
            {
                _pendingClaim = tcs;
            }
            return tcs.Task;
        }

        /// <summary>当前是否正在等待人类决定是否自摸胡。</summary>
        public bool HasPendingConcealedAction
        {
            get { lock (_concealedLock) { return _pendingConcealed != null; } }
        }

        public Task<ClaimOption> ChooseConcealedAction(DecisionContext context)
        {
            // 自摸成胡时必须真正等待人类点「胡」或「过」，不能一律返回 null（等于强制放弃胡）。
            var tcs = new TaskCompletionSource<ClaimOption>();
            lock (_concealedLock)
            {
                _pendingConcealed = tcs;
            }
            return tcs.Task;
        }

        /// <summary>UI 回调用：人类选择「胡」或「过」。</summary>
        public void CompleteConcealedAction(ClaimOption option)
        {
            TaskCompletionSource<ClaimOption> tcs;
            lock (_concealedLock)
            {
                tcs = _pendingConcealed;
                _pendingConcealed = null;
            }
            if (tcs != null) tcs.TrySetResult(option);
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
            // 没有挂起的出牌请求（例如误操作）：直接忽略，不污染下一次征询
            if (tcs != null)
            {
                tcs.TrySetResult(tile);
            }
        }

        public void CompleteClaim(ClaimOption option)
        {
            TaskCompletionSource<ClaimOption> tcs;
            lock (_claimLock)
            {
                tcs = _pendingClaim;
                _pendingClaim = null;
            }
            // 没有挂起的声明征询：直接忽略误点
            if (tcs != null)
            {
                tcs.TrySetResult(option);
            }
        }
    }
}