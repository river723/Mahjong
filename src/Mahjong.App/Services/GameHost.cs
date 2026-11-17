using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Mahjong.App.Services;
using Mahjong.App.ViewModels;
using Mahjong.Core.Ai;
using Mahjong.Core.Game;
using Mahjong.Core.Rules;
using Mahjong.Core.Tiles;

namespace Mahjong.App.Services
{
    /// <summary>
    /// 游戏主机：创建引擎、4 个 Agent（1 人类 + 3 AI），订阅引擎事件并转发到 UI 线程。
    /// HumanAgent 使用 TaskCompletionSource 把按钮点击传回引擎决策流程。
    /// </summary>
    public sealed class GameHost
    {
        private readonly RuleConfig _rules;
        private readonly GameEngine _engine;
        private HumanAgent _human;   // demo 模式下为 null（南家也由 AI 接管）
        private readonly AiPlayer[] _ais = new AiPlayer[3];
        private readonly GameViewModel _viewModel;
        private readonly Dispatcher _ui;
        private readonly AudioService _audio;
        private bool _running;
        // 杠上开花询问后若立即成胡，终局播报要说「杠上开花」而不是普通「自摸」
        private bool _pendingKongWin;

        public GameViewModel ViewModel { get { return _viewModel; } }

        /// <summary>配音 / 音效设置（界面开关直接改这里）。</summary>
        public AudioService Audio { get { return _audio; } }

        /// <summary>本局结果（GameEnded 之后有效），供结算界面统计连胜。</summary>
        public GameResult Result { get { return _engine.Result; } }

        // === UI 回调委托（由 MainWindow 赋值） ===
        public Action<ClaimOption[], string> OnShowClaimButtons { get; set; }
        /// <summary>自摸/杠上开花成胡时，显示「胡 / 过」按钮。</summary>
        public Action<ClaimOption[]> OnShowWinButtons { get; set; }
        public Action OnHideClaimButtons { get; set; }
        public Action<string> OnShowResultOverlay { get; set; }
        public Action<string> OnSubtitleChanged { get; set; }
        /// <summary>引擎异常终止时通知 UI（避免"点哪张都没反应"却毫无提示）。</summary>
        public Action<string> OnEngineError { get; set; }

        /// <summary>当前是否正等待人类出牌。</summary>
        public bool IsAwaitingDiscard { get { return _human != null && _human.HasPendingDiscard; } }

        // === 引擎事件（GameViewModel 订阅） ===
        public event Action<int, string> GameStarted;
        public event Action<int, Tile, string> TileDrawn;
        public event Action<int, Tile, string> TileDiscarded;
        public event Action<int, Tile, IList<ClaimOption>, string> ClaimOffered;
        public event Action<int, ClaimType, Tile, string> ClaimResolved;
        public event Action<int, string> PlayerTurnStarted;
        public event Action<int, string> GameEnded;

        public GameHost(RuleConfig rules, int seed, GameViewModel viewModel, bool demo = false,
            int difficultyLevel = 0)
        {
            _rules = rules;
            _viewModel = viewModel;
            _ui = Dispatcher.CurrentDispatcher;
            _audio = new AudioService();

            // 1. 创建 4 个 Agent。AI 档位由会话难度决定（开局时快照，一局内不变）。
            AiProfile profile = DifficultyState.ProfileFor(difficultyLevel);
            int claimPercent = DifficultyState.ClaimPercentFor(difficultyLevel);
            bool preferMoreWaits = DifficultyState.PreferMoreWaitsFor(difficultyLevel);
            // 演示模式（--demo）：南家也交给 AI，四家自动对局。用于录屏/截图核对，
            // 免去手动点牌，也让四家牌河能快速填满。
            var agents = new IAgent[4];
            if (demo)
            {
                for (int i = 0; i < 4; i++)
                    agents[i] = new AiPlayer(i, profile, seed + i, _rules, claimPercent, preferMoreWaits);
            }
            else
            {
                _human = new HumanAgent(0, this);
                agents[0] = _human;
                for (int i = 0; i < 3; i++)
                {
                    _ais[i] = new AiPlayer(i + 1, profile, seed + i, _rules, claimPercent, preferMoreWaits);
                    agents[i + 1] = _ais[i];
                }
            }

            // 2. 创建引擎
            _engine = new GameEngine(_rules, agents, dealerIndex: 0, wallSeed: seed);
            // UI 场景：一轮结算完成后、下一家摸牌前留停顿给播报，不影响碰/杠/胡的即时征询。
            // AI 打牌留 1300ms：带席位前缀的一条播报（「北家 五万」）约 1.16s，
            // 停顿短于播报长度会让配音积压、靠省略前缀才勉强跟上，最终周期性丢整句。
            // 真人自己打出的牌只停 800ms，不必让自己干等自己。
            _engine.ThinkDelayMs = 1300;
            _engine.HumanThinkDelayMs = 800;

            // 3. 连接 UI → 人类 Agent 的出牌通道（点击手牌出牌）
            viewModel.Players[0].OnTileDiscardRequested += t => HumanChooseDiscard(t);
        }

        /// <summary>在 UI 线程启动一局。</summary>
        public async Task StartAsync()
        {
            if (_running) return;
            _running = true;

            // 订阅引擎事件（引擎在后台线程发事件，需 Dispatcher 调度到 UI）
            _engine.EventRaised += e => _ui.BeginInvoke(new Action(() => HandleEvent(e)));

            try
            {
                await _engine.RunAsync();
            }
            catch (Exception ex)
            {
                GameLog.AppendEngineError(DateTime.Now.ToString() + " RunAsync exception: " + ex + "\n");
                // 引擎一旦异常终止就不再响应任何点击；必须让 UI 明确提示，而不是静默"点哪张都没反应"
                var handler = OnEngineError;
                if (handler != null)
                {
                    string detail = ex.Message;
                    try { _ui.BeginInvoke(new Action(() => handler(detail))); } catch { }
                }
            }
            finally
            {
                _running = false;
            }
        }

        private void HandleEvent(GameEvent e)
        {
            switch (e.Type)
            {
                case GameEventType.GameStarted:
                    GameStarted?.Invoke(e.PlayerIndex, e.Message);
                    break;
                case GameEventType.TileDrawn:
                    TileDrawn?.Invoke(e.PlayerIndex, e.Tile, e.Message);
                    break;
                case GameEventType.TileDiscarded:
                    TileDiscarded?.Invoke(e.PlayerIndex, e.Tile, e.Message);
                    break;
                case GameEventType.ClaimOffered:
                    // 把人类被征询时的可碰/杠/胡选项传给 UI 显示按钮
                    if (e.PlayerIndex == 0 && e.ClaimOptions != null && e.ClaimOptions.Count > 0)
                    {
                        OnShowClaimButtons?.Invoke(e.ClaimOptions.ToArray(), e.Message);
                    }
                    break;
                case GameEventType.ConcealedActionOffered:
                    // 自摸/杠上开花：只问「胡 / 过」，走独立的 concealed 决策通道
                    if (e.PlayerIndex == 0 && e.ClaimOptions != null && e.ClaimOptions.Count > 0)
                    {
                        OnShowWinButtons?.Invoke(e.ClaimOptions.ToArray());
                    }
                    break;
                case GameEventType.ClaimResolved:
                    ClaimResolved?.Invoke(e.PlayerIndex, ClaimType.None, e.Tile, e.Message);
                    if (OnHideClaimButtons != null) OnHideClaimButtons();
                    break;
                case GameEventType.PlayerTurnStarted:
                    PlayerTurnStarted?.Invoke(e.PlayerIndex, e.Message);
                    break;
                case GameEventType.GameEnded:
                    GameEnded?.Invoke(e.PlayerIndex, e.Message);
                    if (OnHideClaimButtons != null) OnHideClaimButtons();
                    if (OnShowResultOverlay != null) OnShowResultOverlay(e.Message);
                    break;
            }
            // 同步剩余牌墙数量
            _viewModel.WallRemaining = _engine.WallRemaining;

            // 配音 / 音效：与字幕同步，统一由 AudioService 在后台线程排队播放
            switch (e.Type)
            {
                case GameEventType.GameStarted:
                    _audio.Clear();
                    _pendingKongWin = false;
                    _audio.Deal();
                    break;
                case GameEventType.TileDiscarded:
                    _pendingKongWin = false;
                    // 其它三家出牌必念牌面，自己出牌可在设置里关掉；拍牌音效每次都有
                    if (e.Tile != null) _audio.Discard(e.PlayerIndex, e.Tile.Kind);
                    break;
                case GameEventType.ClaimOffered:
                    // 只有玩家自己被征询时给提示音，AI 的三家不响，免得一直叮叮叮
                    if (e.PlayerIndex == 0 && e.ClaimOptions != null && e.ClaimOptions.Count > 0)
                        _audio.Prompt();
                    break;
                case GameEventType.ConcealedActionOffered:
                    if (e.Message != null && e.Message.IndexOf("杠上开花") >= 0)
                        _pendingKongWin = true;
                    if (e.PlayerIndex == 0) _audio.Prompt();
                    break;
                case GameEventType.ClaimResolved:
                    _audio.Claim(e.Message, e.PlayerIndex);
                    break;
                case GameEventType.GameEnded:
                    // 结算只念「谁 + 胡了/自摸/杠上开花 + 胡哪张」，分数串留给字幕和结算面板
                    _audio.GameEnd(_engine.Result, _pendingKongWin);
                    break;
            }
            if (OnSubtitleChanged != null)
            {
                string subtitle = e.Message ?? "";
                // 出牌事件本身不带文本：补一条「谁打出了什么」的播报。
                // 字幕用词与语音包保持一致（东风/红中/发财/白板）。
                if (e.Type == GameEventType.TileDiscarded && string.IsNullOrEmpty(subtitle) && e.Tile != null)
                    subtitle = SeatName(e.PlayerIndex) + " 打出 " + VoicePackGenerator.TileText(e.Tile.Kind);
                if (e.Type == GameEventType.ClaimResolved && !string.IsNullOrEmpty(e.Message))
                    subtitle = SeatName(e.PlayerIndex) + " " + e.Message;
                OnSubtitleChanged?.Invoke(subtitle);
            }
        }

        private static string SeatName(int playerIndex)
        {
            if (playerIndex == 0) return "南(你)";
            if (playerIndex == 1) return "北";
            if (playerIndex == 2) return "东";
            return "西";
        }

        public void HumanChooseDiscard(Tile tile)
        {
            if (_human != null) _human.CompleteDiscard(tile);
        }

        public void HumanChooseClaim(ClaimOption option)
        {
            if (_human != null) _human.CompleteClaim(option);
        }

        /// <summary>人类在自摸/杠上开花时选择「胡」或「过」。</summary>
        public void HumanChooseConcealedAction(ClaimOption option)
        {
            if (_human != null) _human.CompleteConcealedAction(option);
        }

        /// <summary>当前是否正等待人类决定是否自摸胡。</summary>
        public bool IsAwaitingConcealedAction { get { return _human != null && _human.HasPendingConcealedAction; } }

        /// <summary>获取某玩家当前的暗牌（手牌）。</summary>
        public IEnumerable<Tile> GetPlayerTiles(int playerIndex)
        {
            return _engine.Players[playerIndex].ConcealedTiles;
        }
    }
}