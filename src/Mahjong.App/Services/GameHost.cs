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
        private readonly HumanAgent _human;
        private readonly AiPlayer[] _ais = new AiPlayer[3];
        private readonly GameViewModel _viewModel;
        private readonly Dispatcher _ui;
        private readonly SoundService _sound;
        private readonly SpeechService _speech;
        private bool _running;

        public GameViewModel ViewModel { get { return _viewModel; } }

        // === UI 回调委托（由 MainWindow 赋值） ===
        public Action<ClaimOption[]> OnShowClaimButtons { get; set; }
        public Action OnHideClaimButtons { get; set; }
        public Action<string> OnShowResultOverlay { get; set; }
        public Action<string> OnSubtitleChanged { get; set; }

        // === 引擎事件（GameViewModel 订阅） ===
        public event Action<int, string> GameStarted;
        public event Action<int, Tile, string> TileDrawn;
        public event Action<int, Tile, string> TileDiscarded;
        public event Action<int, Tile, IList<ClaimOption>, string> ClaimOffered;
        public event Action<int, ClaimType, Tile, string> ClaimResolved;
        public event Action<int, string> PlayerTurnStarted;
        public event Action<int, string> GameEnded;

        public GameHost(RuleConfig rules, int seed, GameViewModel viewModel)
        {
            _rules = rules;
            _viewModel = viewModel;
            _ui = Dispatcher.CurrentDispatcher;
            _sound = new SoundService();
            _speech = new SpeechService();

            // 1. 创建 4 个 Agent
            _human = new HumanAgent(0, this);
            for (int i = 0; i < 3; i++)
            {
                _ais[i] = new AiPlayer(i + 1, AiProfile.Normal, seed + i, _rules);
            }

            var agents = new IAgent[4];
            agents[0] = _human;
            for (int i = 0; i < 3; i++) agents[i + 1] = _ais[i];

            // 2. 创建引擎
            _engine = new GameEngine(_rules, agents, dealerIndex: 0, wallSeed: seed);

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
                try { System.IO.File.AppendAllText(@"e:\cc_study\mj\engine_err.txt",
                    DateTime.Now.ToString() + " RunAsync exception: " + ex + "\n"); } catch { }
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
                    // 需要把 ClaimOption[] 传给 UI 显示大按钮
                    if (OnShowClaimButtons != null)
                    {
                        var ctx = GetDecisionContext(e.PlayerIndex, e.Tile);
                        if (ctx != null) OnShowClaimButtons(ctx.ClaimOptions.ToArray());
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
                    _speech.Speak(e.Message);
                    break;
            }
            // 同步剩余牌墙数量
            _viewModel.WallRemaining = _engine.WallRemaining;

            // 音效/字幕同步（M6）
            switch (e.Type)
            {
                case GameEventType.TileDiscarded:
                    _sound.Play("Sounds/碰.wav"); // 路径为实际资源，先占位
                    break;
                case GameEventType.ClaimResolved:
                    if (e.Message != null) _speech.Speak(e.Message);
                    break;
                case GameEventType.GameEnded:
                    break;
            }
            if (OnSubtitleChanged != null)
                OnSubtitleChanged?.Invoke(e.Message ?? "");
        }

        private DecisionContext GetDecisionContext(int playerIndex, Tile discard)
        {
            // 仅暴露该玩家应得信息
            return null;
        }

        public void HumanChooseDiscard(Tile tile)
        {
            _human.CompleteDiscard(tile);
        }

        public void HumanChooseClaim(ClaimOption option)
        {
            _human.CompleteClaim(option);
        }

        /// <summary>获取某玩家当前的暗牌（手牌）。</summary>
        public IEnumerable<Tile> GetPlayerTiles(int playerIndex)
        {
            return _engine.Players[playerIndex].ConcealedTiles;
        }
    }
}