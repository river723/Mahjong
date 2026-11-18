using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Mahjong.App.Controls;
using Mahjong.App.Services;
using Mahjong.App.ViewModels;
using Mahjong.Core.Ai;
using Mahjong.Core.Game;
using Mahjong.Core.Rules;
using Mahjong.Core.Tiles;

namespace Mahjong.App
{
    /// <summary>
    /// 主窗口：MVVM 架构，完全解耦。
    /// </summary>
    public partial class MainWindow : Window
    {
        private GameHost _host;
        private GameViewModel _viewModelRef;

        // 人类出牌时，被点手牌的屏幕中心点（作为飞行起点）
        private Point? _pendingDiscardFrom;

        // 当前被征询时，人类可做的碰/杠/胡选项（按钮点击时据此提交正确选项）
        private ClaimOption[] _pendingClaimOptions;

        // 当前声明面板是否是「自摸胡询问」（决定点击提交到 concealed 通道还是 claim 通道）
        private bool _isWinPrompt;

        // 最近一次出牌的玩家索引（碰/杠时据此把那张牌从牌河移走）
        private int _lastDiscarder = -1;

        // 本局结算是否已计入难度统计（防止事件重复触发把连胜多算）
        private bool _resultRecorded;

        // 本局金币是否已记入账本（防止 GameEnded 重复触发导致重复扣币）
        private bool _settlementApplied;

        // 牌桌整体缩放系数（由 UpdateTableScale 维护）。出牌飞行层、弹字特效在窗口坐标里作画，
        // 必须乘上同一个系数，否则牌落定的瞬间会和牌河里的牌大小对不上，看着"跳"一下。
        private double _tableScale = 1.0;

        // 结算面板的"砸入"缩放（单独一层，和外层的窗口缩放叠在一起）
        private ScaleTransform _winSlam;

        // 结算期间的彩带补发计时器：面板关掉后必须停，否则会在下一局的窗口上继续撒纸
        private System.Windows.Threading.DispatcherTimer _confettiTrickle;

        // 彩带回收器（按寿命清理落出画面的纸片，全窗共用一个）
        private System.Windows.Threading.DispatcherTimer _confettiSweep;

        // 排版基准：默认 1000×800 窗口下牌桌内容区（扣掉窗口边框、外边距 14 与牌桌边框 7）的实测尺寸。
        // XAML 里所有行高/列宽/牌尺寸（30×40、46×62、32×24、42×31…）都是按这个基准手调出来的，
        // 所以缩放系数以它为 1.0：默认窗口尺寸下画面与改动前完全一致。
        private const double TableBaseWidth = 943.6;
        private const double TableBaseHeight = 720.4;

        // ===== 开局码牌动画 =====
        // 引擎要等动画播完（或点一下跳过）才启动：它跑起来约 1 秒就会出第一张牌，
        // 并行的话牌墙还没码完，牌河里已经冒出一张牌了。
        private const int StacksPerSide = 17;   // 真实麻将每人面前码 17 墩；竖边 16 墩与横边端墩压叠成角，实际绘制 66 墩
        private const int StackStartMs = 120;
        private const int StackStepMs = 11;
        private const int StackFlyMs = 240;
        private bool _gameLaunched;
        private bool _introFinished;
        private bool _introHold;
        private System.Windows.Threading.DispatcherTimer _introTimer;

        /// <summary>一墩牌的落点与所属边，供「开门搬牌」时把对应两墩推出去。</summary>
        private sealed class IntroStack
        {
            public Canvas Element;
            public int Side;      // 0=北 1=东 2=南 3=西
            public int Index;
        }

        public MainWindow()
        {
            InitializeComponent();

            // 创建 MVVM 和 GameHost。规则取自会话（金币场：七对开启），与账本用的是同一份配置。
            var rules = App.Session.Rules;
            var viewModel = new GameViewModel();
            _viewModelRef = viewModel;
            // 每局新建窗口：开局先把上一局结算后的余额显示出来，金币不会被重开清零。
            viewModel.RefreshCoins(App.Session.Ledger.Coins);
            // --demo：四家全自动，用于录屏与截图核对（南家不等待点击）
            bool demo = Environment.GetCommandLineArgs()
                .Any(a => string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase));
            // --difficulty N / MJ_DIFFICULTY=N：固定档位，用于调试与截图复现；不指定则按连胜/局数自动升档
            int? fixedLevel = ResolveFixedLevel();
            if (fixedLevel.HasValue) DifficultyState.SetFixedLevel(fixedLevel.Value);

            bool seedFixed;
            int seed = ResolveSeed(demo, out seedFixed);
            _host = new GameHost(rules, seed, viewModel, demo, DifficultyState.Level);

            // 标题展示种子与难度：种子用来复现牌局（--seed <数字> / MJ_SEED=<数字>），难度是本局 AI 档位
            Title = string.Format("家人麻将 — 种子 {0} · 难度 {1} Lv{2}{3}",
                seed, DifficultyState.LevelName(DifficultyState.Level), DifficultyState.Level,
                (seedFixed || DifficultyState.IsFixed) ? "（固定）" : "");

            // 将 viewModel 设置为 DataContext
            this.DataContext = viewModel;

            // 关窗时停掉音频后台线程；声音设置统一由「设置」窗口管理
            Closed += (s, args) => { if (_introTimer != null) _introTimer.Stop(); _host.Audio.Dispose(); };
            // F2：键盘等价入口，鼠标不方便时也能随时打开设置
            KeyDown += (s, args) =>
            {
                if (args.Key != Key.F2) return;
                args.Handled = true;
                SettingsButton_Click(this, new RoutedEventArgs());
            };

            // 调试用视觉校验钩子（正常启动不生效）：设环境变量 MJ_FAKE_SOUTH_MELD，
            // 「pong」= 只塞一组碰（常见情形），「1」= 碰 + 明杠（最宽情形 13+7），
            // 「ckong」= 碰 + 暗杠（核对暗杠只露两端、中间两张扣牌）。
            // 注意：假副露会在 GameStarted 里按张数从手牌扣掉，保持「手牌 + 副露 = 13/14」，
            // 否则截图里会凭空多出 7 张牌，看着像手牌变多了。
            string fakeMeld = Environment.GetEnvironmentVariable("MJ_FAKE_SOUTH_MELD");
            if (fakeMeld == "1" || fakeMeld == "pong" || fakeMeld == "ckong")
            {
                var pong = new MeldViewModel { Type = ClaimType.Pong };
                pong.AddTiles(TileKind.Tong2);
                viewModel.Players[0].Melds.Add(pong);
            }
            if (fakeMeld == "1")
            {
                var kong = new MeldViewModel { Type = ClaimType.Kong };
                kong.AddTiles(TileKind.Bai);
                viewModel.Players[0].Melds.Add(kong);
            }
            if (fakeMeld == "ckong")
            {
                var ckong = new MeldViewModel { Type = ClaimType.ConcealedKong };
                ckong.AddTiles(TileKind.Bai);
                viewModel.Players[0].Melds.Add(ckong);
            }

            // 连接 GameHost 的回调
            _host.OnShowClaimButtons = (options, message) =>
            {
                _pendingClaimOptions = options;
                _isWinPrompt = false;
                LogClaim("ShowClaimButtons: " + string.Join(", ", options.Select(o => o.Type + "(" + o.Kind + ")")));
                // 只显示人类实际能做的声明按钮；「过」始终显示
                PongButton.Visibility = options.Any(o => o.Type == ClaimType.Pong) ? Visibility.Visible : Visibility.Collapsed;
                PongButton.Content = "碰";   // concealed 面板可能把它改成「补杠」，这里复位
                KongButton.Visibility = options.Any(o => o.Type == ClaimType.Kong) ? Visibility.Visible : Visibility.Collapsed;
                KongButton.Content = "杠";   // concealed 面板可能把它改成「暗杠」，这里复位
                WinButton.Visibility  = options.Any(o => o.Type == ClaimType.Win)  ? Visibility.Visible : Visibility.Collapsed;
                PassButton.Visibility = Visibility.Visible;

                // 抢杠胡只允许「胡 / 过」，提示要说明这张牌来自别人的补杠
                var parts = new System.Collections.Generic.List<string>();
                if (options.Any(o => o.Type == ClaimType.Win))  parts.Add("胡");
                if (options.Any(o => o.Type == ClaimType.Pong)) parts.Add("碰");
                if (options.Any(o => o.Type == ClaimType.Kong)) parts.Add("杠");
                ClaimHint.Text = message == "可抢杠胡"
                    ? "可抢杠胡！是否胡？"
                    : "是否 " + string.Join(" / ", parts) + " ？";
                ClaimPanel.Visibility = Visibility.Visible;
                TurnHint.Visibility = Visibility.Collapsed;
            };
            // 自己回合内的暗动作：自摸胡 / 杠上开花 / 暗杠，都走 concealed 决策通道
            _host.OnShowWinButtons = options =>
            {
                _pendingClaimOptions = options;
                _isWinPrompt = true;
                LogClaim("ShowWinButtons: " + string.Join(", ", options.Select(o => o.Type + "(" + o.Kind + ")")));
                bool canWin = options.Any(o => o.Type == ClaimType.Win);
                bool canConcealedKong = options.Any(o => o.Type == ClaimType.ConcealedKong);
                bool canAddedKong = options.Any(o => o.Type == ClaimType.AddedKong);
                PongButton.Visibility = canAddedKong ? Visibility.Visible : Visibility.Collapsed;
                PongButton.Content = "补杠";
                KongButton.Visibility = canConcealedKong ? Visibility.Visible : Visibility.Collapsed;
                KongButton.Content = "暗杠";
                WinButton.Visibility = canWin ? Visibility.Visible : Visibility.Collapsed;
                PassButton.Visibility = Visibility.Visible;
                var actions = new System.Collections.Generic.List<string>();
                if (canWin) actions.Add("胡");
                if (canConcealedKong) actions.Add("暗杠");
                if (canAddedKong) actions.Add("补杠");
                ClaimHint.Text = (canConcealedKong || canAddedKong)
                    ? "可 " + string.Join(" / ", actions) + "，是否要做？"
                    : "自摸胡牌！是否胡？";
                ClaimPanel.Visibility = Visibility.Visible;
                TurnHint.Visibility = Visibility.Collapsed;
            };
            _host.OnHideClaimButtons = () => HideClaimPanel();
            _host.OnShowResultOverlay = message =>
            {
                var result = _host.Result;
                bool playerWon = result != null && result.WinnerIndex == 0;

                // 胡牌特效：只有自己胡牌才让手牌金闪。
                // （别家胡 / 荒庄流局时己方手牌发光会让人以为是自己赢了）
                if (playerWon)
                    foreach (var t in viewModel.Players[0].Hand) t.WinFlash = true;

                // 结算时记一局：玩家（0 号）赢才续连胜，AI 赢或流局则清零。
                // 升档在下一局生效（AI 是开局时按档位创建的），提示文案里说明。
                string notice = null;
                if (!_resultRecorded)
                {
                    _resultRecorded = true;
                    notice = DifficultyState.RecordResult(playerWon);
                }

                // 金币结算：全额记入账本后再判定本场是否结束。只记一次，防重复扣币。
                if (!_settlementApplied)
                {
                    _settlementApplied = true;
                    App.Session.LastOutcome = App.Session.Ledger.Apply(result);
                    viewModel.RefreshCoins(App.Session.Ledger.Coins);
                }
                ShowWinEffect(message, notice, result);
            };
            _host.OnSubtitleChanged = subtitle =>
            {
                viewModel.SubtitleText = subtitle;
            };
            _host.OnEngineError = detail =>
            {
                // 引擎已停止运转，点击不会再有响应；明确告知而不是让用户干点
                viewModel.SubtitleText = "游戏已停止：" + detail + "（请关闭窗口重新开局）";
                MessageBox.Show("游戏引擎异常终止，已停止响应出牌。\n\n" + detail +
                                "\n\n请关闭窗口重新开局。", "游戏异常", MessageBoxButton.OK, MessageBoxImage.Warning);
            };

            // 订阅 GameHost 引擎事件，把牌数据同步到 ViewModel（供 UI 绑定显示）
            _host.GameStarted += (playerIndex, message) =>
            {
                try
                {
                    GameLog.AppendEvent(DateTime.Now.ToString("HH:mm:ss.fff") + " [UI] GameStarted handler entered\n");
                    // 初始发牌：从引擎状态读出各家起手 13/14 张（仅读一次，后续增量靠 TileDrawn）
                    for (int p = 0; p < 4; p++)
                    {
                        var vmPlayer = viewModel.Players[p];
                        int n = 0;
                        Tile lastTile = null;
                        foreach (var tile in _host.GetPlayerTiles(p))
                        {
                            vmPlayer.AddHandTile(tile);
                            lastTile = tile;
                            n++;
                        }
                        // 庄家起手 14 张里多出来的那张就是「刚摸上来的」，照样单独摆在最右侧。
                        // 引擎对庄家首轮不再补发 TileDrawn，所以要在发牌这一步补上标记。
                        if (n == 14 && lastTile != null) vmPlayer.MarkOpeningDrawnTile(lastTile);
                        GameLog.AppendEvent(DateTime.Now.ToString("HH:mm:ss.fff") + " [UI] p=" + p + " hand=" + n + "\n");
                    }
                    // 调试钩子塞的假副露是「凭空多出来」的牌：真实发牌不会替它扣手牌。
                    // 这里按假副露张数从手牌尾部扣掉，保持牌桌守恒（正常启动不进这个分支）。
                    string debugMeld = Environment.GetEnvironmentVariable("MJ_FAKE_SOUTH_MELD");
                    if (debugMeld == "1" || debugMeld == "pong" || debugMeld == "ckong")
                    {
                        var hand = viewModel.Players[0].Hand;
                        int fakeTiles = viewModel.Players[0].Melds.Sum(m => m.Tiles.Count);
                        for (int i = 0; i < fakeTiles && hand.Count > 1; i++)
                            hand.RemoveAt(hand.Count - 1);
                    }
                    GameLog.AppendEvent(DateTime.Now.ToString("HH:mm:ss.fff") + " [UI] GameStarted handler done, Players[0].Hand.Count=" +
                    viewModel.Players[0].Hand.Count + "\n");
                }
                catch (Exception ex)
                {
                    GameLog.AppendEvent(DateTime.Now.ToString("HH:mm:ss.fff") + " [UI] GameStarted handler EX: " + ex + "\n");
                }
            };
            _host.TileDrawn += (playerIndex, tile, message) =>
            {
                // 摸牌是「新」一张：上一巡那张先归位，新牌单独摆在最右侧、不参加排序
                viewModel.Players[playerIndex].AddHandTile(tile, true);
            };
            _host.TileDiscarded += (playerIndex, tile, message) =>
            {
                _lastDiscarder = playerIndex;
                // 新打出的这张歪着摆放（JustDiscarded → DiscardAngle）；本家上一张同时摆正。
                // 到下一轮（本家再打一张）它才归位，比原来闪 0.85 秒就好找得多。
                TileViewModel vm = viewModel.Players[playerIndex].AddDiscard(tile);
                // 牌河固定尺寸，出牌多了末尾的新牌会被裁掉：滚到最新一张
                ScrollDiscardToLatest(playerIndex);
                // 出牌后从手牌移除对应那张。四家都要扣：
                // 原来只扣「人类方」，三家 AI 的牌背张数就只增不减，打得越久越像垒墙。
                viewModel.Players[playerIndex].RemoveHandTile(tile);
                // 这一轮结束：上一巡「刚摸的牌」重新参加排序，归到正常位置。
                viewModel.Players[playerIndex].CommitDrawnTile();
                // 出牌飞行动画（人类方用刚才点击的手牌位置作为起点）。
                // 落点在动画开始前量：飞行牌要精确落在「牌河里那张牌真正停住的位置」，
                // 而不是牌河中心，否则落定瞬间还要再挪一下、看着像跳。量取前先同步滚到最后，
                // 保证和挂牌的位置完全一致。
                Point? from = (playerIndex == 0) ? _pendingDiscardFrom : null;
                _pendingDiscardFrom = null;
                Dispatcher.BeginInvoke(new Action(() => FlyDiscard(playerIndex, tile.Kind, from, vm)));
            };
            _host.ClaimResolved += (playerIndex, claimType, tile, message) =>
            {
                LogClaim("Resolved: p=" + playerIndex + " " + (message ?? ""));
                // 碰 / 杠：把被打出的那张牌从「打出者的牌河」移走，归入碰牌者的副露，
                // 并从碰牌者手牌扣掉对应张数（碰 2 / 杠 3）。
                bool isAddedKong = message != null && message.IndexOf("补杠") >= 0;
                bool isConcealedKong = message != null && message.IndexOf("暗杠") >= 0;
                bool isPong = message != null && message.IndexOf("碰") >= 0;
                bool isKong = message != null && message.IndexOf("杠") >= 0;
                if (isAddedKong && tile != null)
                {
                    // 补杠是把已有的一组「碰」升级成四张，不是新增副露；
                    // 那张牌来自自己手里，牌河也不动，只扣手牌一张。
                    var meld = viewModel.Players[playerIndex].Melds.FirstOrDefault(
                        m => m.Type == ClaimType.Pong && m.Tiles.Count > 0 && m.Tiles[0].Kind == tile.Kind);
                    if (meld != null)
                    {
                        meld.AddTile(tile.Kind);      // 补杠四张全亮，不扣牌
                        meld.Type = ClaimType.AddedKong;
                    }
                    viewModel.Players[playerIndex].RemoveHandTile(new Tile(tile.Id, tile.Kind));
                }
                else if ((isPong || isKong) && tile != null)
                {
                    // 暗杠的四张全在自己手里，牌河不动；明杠/碰才需要把别人打出的那张收回。
                    int from = isConcealedKong ? -1 : (_lastDiscarder >= 0 ? _lastDiscarder : -1);
                    if (from >= 0)
                    {
                        var fromP = viewModel.Players[from];
                        // 牌河里被碰/杠走的就是「本次打出的那张」：移除最后一张同款
                        var removed = fromP.Discard.LastOrDefault(x => x.Kind == tile.Kind);
                        if (removed != null)
                        {
                            fromP.Discard.Remove(removed);
                            // 被收走的正是「刚打出」那张时，把牌河现存最后一张补上标记
                            fromP.RefreshLatestDiscard();
                        }
                    }

                    int take = isConcealedKong ? 4 : (isKong ? 3 : 2);   // 手牌扣牌：暗杠 4 / 明杠 3 / 碰 2
                    var meld = new MeldViewModel
                    {
                        Type = isConcealedKong ? ClaimType.ConcealedKong
                             : (isKong ? ClaimType.Kong : ClaimType.Pong)
                    };
                    // AddTiles 会按 Type 决定张数与明暗：暗杠只翻两端，中间两张画牌背。
                    meld.AddTiles(tile.Kind);
                    viewModel.Players[playerIndex].Melds.Add(meld);

                    // 从碰牌者手牌扣掉对应张数（与引擎一致；玩家自己碰牌时手牌需正确减少）
                    for (int i = 0; i < take; i++)
                        viewModel.Players[playerIndex].RemoveHandTile(new Tile(tile.Id, tile.Kind));
                }

                // 碰/杠/胡/吃 弹字特效（按消息文本判断具体动作）
                Dispatcher.BeginInvoke(new Action(() => ShowClaimPopup(playerIndex, message)));
            };
            _host.PlayerTurnStarted += (playerIndex, message) =>
            {
                // 中央「轮到{席位}」跟着真实回合走；之前这个属性从未被赋值，
                // 整局都显示「轮到你」，与实际出牌顺序完全对不上。
                viewModel.CurrentPlayer = playerIndex;
                // 只更新「轮到谁」提示，不收起声明面板：引擎在等人类声明期间若又发本事件
                // （例如碰/杠后回到主循环开头），收起面板会让人点不到按钮而引擎仍在 await，形成死锁。
                // 面板统一由 SubmitClaim（点击后）与 OnHideClaimButtons（结算/终局）收起。
                TurnHint.Text = (playerIndex == 0) ? "轮到你了，请出牌" : "等待各家出牌…";
            };

            // 注意：UI→人类 Agent 的出牌通路由 GameHost 构造函数统一订阅（只订阅一次），
            // 这里不要重复 +=，否则一次点击会触发两次 CompleteDiscard，可能误消费下一回合的等待点。

            // 布局自检：--dump-layout 启动后把各区域实测尺寸写文件并退出（排查挤压/裁切用）
            if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--dump-layout", StringComparison.OrdinalIgnoreCase)))
                ContentRendered += (s, e) => DumpLayout();

            // 特效预览（调试/录屏用）：--preview-win=self|ai|draw
            // 启动后立刻把胡牌特效放一遍并停在结算界面，方便核对动画与截图，不影响对局逻辑。
            string preview = (ReadOption("preview-win") ?? "").Trim().ToLowerInvariant();
            if (preview == "self" || preview == "ai" || preview == "draw")
            {
                ContentRendered += (s, e) =>
                {
                    // 预览用金币场的真实结算口径，不再用旧的番分假文案；
                    // 并且要走一次账本，否则 WinCoins（收支 + 余额 + 单价拆解）会被隐藏。
                    GameResult fake;
                    string previewText;
                    if (preview == "draw")
                    {
                        fake = new GameResult { WinnerIndex = -1, LoserIndex = -1, Scores = new int[4] };
                        previewText = "荒庄流局 · 无金币转移";
                    }
                    else if (preview == "ai")
                    {
                        // 北家普通点炮胡：u=1，三家各 -1、胡家 +3
                        fake = new GameResult
                        {
                            WinnerIndex = 1, LoserIndex = 2, Scores = new[] { -1, 3, -1, -1 },
                            UnitPay = 1, PatternKind = WinPatternKind.Normal
                        };
                        previewText = "北 胡 · 普通胡 · 每家 -1 · 北 +3";
                    }
                    else
                    {
                        // 自己一条龙自摸 + 1 明杠 + 1 暗杠：u = 3×2 + 1 + 2 = 9
                        fake = new GameResult
                        {
                            WinnerIndex = 0, LoserIndex = -1, IsSelfDraw = true,
                            Scores = new[] { 27, -9, -9, -9 },
                            UnitPay = 9, PatternKind = WinPatternKind.PureStraight,
                            OpenKongCount = 1, ConcealedKongCount = 1
                        };
                        previewText = "自摸 · 一条龙 + 1 明杠 + 1 暗杠 · 每家 -9";
                    }
                    App.Session.LastOutcome = App.Session.Ledger.Apply(fake);
                    viewModel.RefreshCoins(App.Session.Ledger.Coins);
                    ShowWinEffect(previewText, null, fake);
                };
            }

            // 窗口放大（最大化 / 拖拽边角）时整桌等比放大，三家对手的牌跟着变大。
            // 挂在牌桌外框上而不是窗口上：拿到的是扣掉外边距与边框后的真实可用尺寸。
            TableFrame.SizeChanged += (s, e) => UpdateTableScale();
            Loaded += (s, e) => UpdateTableScale();

            // 启动游戏（异步）：先播「码牌 → 掷骰 → 开门搬牌」的开场动画，
            // 播完或被点一下跳过之后，才由 FinishOpening → LaunchGame 真正开局。
            StartOpeningSequence(preview);
        }

        /// <summary>
        /// 本局牌墙种子。优先级：命令行 --seed N（或 --seed=N） → 环境变量 MJ_SEED → 随机。
        /// 显式指定时固定牌墙，用于调试、录屏与复现（标题里的数字可直接拿来回放）。
        /// --demo 且未指定种子时沿用旧的固定值 1，保证截图核对可复现；
        /// 正常对局未指定则每局随机（含结算后点「继续」重开的新窗口）。
        /// 非法值一律忽略并回退，不让一个手滑的参数把游戏卡死。
        /// </summary>
        private static int ResolveSeed(bool demo, out bool seedFixed)
        {
            string raw = ReadOption("seed");

            int seed;
            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out seed))
            {
                seedFixed = true;
                return seed;
            }

            seedFixed = demo;
            return demo ? 1 : RandomSeed();
        }

        /// <summary>
        /// 生成随机种子。不用 new Random().Next()：.NET Framework 的 Random() 以时间作种，
        /// 连续重开窗口时可能撞出同一副牌；Guid + TickCount 混合则基本不会重复。
        /// 限制在 [1, 1000000000)，避免 GameHost 里 seed + i 逼近 int 上限溢出到负数。
        /// </summary>
        private static int RandomSeed()
        {
            int mixed = Guid.NewGuid().GetHashCode() ^ Environment.TickCount;
            return (int)((uint)mixed % 1000000000u) + 1;
        }

        /// <summary>
        /// 固定难度档位：--difficulty N / --difficulty=N / MJ_DIFFICULTY=N。
        /// 未指定或非法返回 null，交给 DifficultyState 按连胜/局数自动升档；越界值收敛到 0..2。
        /// </summary>
        private static int? ResolveFixedLevel()
        {
            string raw = ReadOption("difficulty");
            int level;
            if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw.Trim(), out level)) return null;
            if (level < 0) level = 0;
            if (level > DifficultyState.MaxLevel) level = DifficultyState.MaxLevel;
            return level;
        }

        /// <summary>读启动参数：--name N / --name=N 优先，其次环境变量 MJ_NAME。</summary>
        private static string ReadOption(string name)
        {
            string flag = "--" + name;
            string prefix = flag + "=";
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    return args[i + 1];
                if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return args[i].Substring(prefix.Length);
            }
            return Environment.GetEnvironmentVariable("MJ_" + name.ToUpperInvariant());
        }

        /// <summary>
        /// 按窗口可用空间重算牌桌整体缩放系数。
        ///
        /// 之前只有己方手牌套了 Viewbox，最大化时手牌会自己长大，而北/东/西三家的牌是
        /// 写死的 30×40、46×62、32×24、42×31，一直保持原样——牌桌看着就散了。
        /// 这里把"可用空间 ÷ 基准尺寸"算成一个系数，写到 TableGrid 的 LayoutTransform 上：
        /// 整桌（牌、字、按钮）同步等比放大，且因为 LayoutTransform 参与布局
        /// （父容器按 可用尺寸 ÷ 系数 来排布），放大后依旧铺满窗口，不会留白也不会溢出。
        ///
        /// 只放大不缩太多（下限 0.7）：窗口拉到很小的时候交给 MinWidth/MinHeight 挡住。
        /// </summary>
        private void UpdateTableScale()
        {
            if (TableFrame == null || TableScale == null) return;

            double w = TableFrame.ActualWidth - TableFrame.BorderThickness.Left - TableFrame.BorderThickness.Right;
            double h = TableFrame.ActualHeight - TableFrame.BorderThickness.Top - TableFrame.BorderThickness.Bottom;
            if (w < 40 || h < 40) return;   // 还没完成第一次布局

            // 取宽高两个方向里较小的倍数，保证整桌等比、不会把牌压扁或拉长
            double scale = Math.Min(w / TableBaseWidth, h / TableBaseHeight);
            if (scale < 0.7) scale = 0.7;
            if (scale > 3.0) scale = 3.0;

            // 变化太小就不动，避免窗口拖动时高频重排
            if (Math.Abs(scale - _tableScale) < 0.005) return;

            _tableScale = scale;
            TableScale.ScaleX = scale;
            TableScale.ScaleY = scale;
        }

        /// <summary>
        /// 把关键容器的实测尺寸写成文本，用于确认「谁把谁挤扁了」。
        /// 仅在 --dump-layout 下调用，写完即退出。
        /// </summary>
        private void DumpLayout()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("client " + ActualWidth + "x" + ActualHeight);
            sb.AppendLine("scale " + _tableScale.ToString("0.###") +
                          "（牌桌逻辑尺寸 " + TableGrid.ActualWidth.ToString("0.#") + "x" + TableGrid.ActualHeight.ToString("0.#") + "）");
            for (int i = 0; i < TableGrid.RowDefinitions.Count; i++)
                sb.AppendLine("row" + i + " h=" + TableGrid.RowDefinitions[i].ActualHeight);
            for (int i = 0; i < TableGrid.ColumnDefinitions.Count; i++)
                sb.AppendLine("col" + i + " w=" + TableGrid.ColumnDefinitions[i].ActualWidth);
            Action<string, FrameworkElement> line = (name, el) =>
            {
                if (el == null) { sb.AppendLine(name + " <null>"); return; }
                string pos = "";
                try
                {
                    var p = el.TransformToAncestor(RootGrid).Transform(new Point(0, 0));
                    pos = " at=" + p.X.ToString("0.0") + "," + p.Y.ToString("0.0");
                }
                catch { /* 未参与布局 */ }
                sb.AppendLine(name + " actual=" + el.ActualWidth + "x" + el.ActualHeight +
                              " desired=" + el.DesiredSize.Width + "x" + el.DesiredSize.Height + pos);
            };
            line("PanelWest", PanelWest);
            line("DiscardWest", DiscardWest);
            line("ScrollWest", DiscardScrollWest);
            line("PanelEast", PanelEast);
            line("DiscardEast", DiscardEast);
            line("DiscardNorth", DiscardNorth);
            line("DiscardSouth", DiscardSouth);
            line("CenterInfo", CenterInfo);
            line("HandRow", HandViewbox);
            try
            {
                var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "layout.txt");
                System.IO.File.WriteAllText(path, sb.ToString());
            }
            catch { /* 自检失败不影响正常对局 */ }
            Close();
        }

        /// <summary>手牌点击：把该牌交给人类 Agent 出牌（仅当轮到自己时有效）。</summary>
        private void HandTile_Click(object sender, RoutedEventArgs e)
        {
            // 从 ContentPresenter 的 DataContext 取 TileViewModel
            if (sender is System.Windows.Controls.ContentPresenter cp &&
                cp.DataContext is TileViewModel tv)
            {
                // 引擎当前没在等待出牌（例如正等别家、或正等你做碰/杠/胡选择）：
                // 只给提示、不阻断提交——引擎与 UI 分属不同线程，硬拦截会误挡合法点击。
                // CompleteDiscard 本身已有"无挂起请求则忽略"的保护，不会污染下一回合。
                if (!_host.IsAwaitingDiscard)
                    _viewModelRef.SubtitleText = "现在还不能出牌：请等轮到你，或先完成碰/杠/胡选择";

                // 记录被点手牌的屏幕中心，作为出牌飞行起点
                var tl = cp.PointToScreen(new Point(0, 0));
                _pendingDiscardFrom = new Point(tl.X + cp.ActualWidth / 2, tl.Y + cp.ActualHeight / 2);
                _viewModelRef.Players[0].OnTileDiscardRequested?.Invoke(tv.DiscardTile);
            }
        }

        private void PongButton_Click(object sender, EventArgs e)
        {
            // 同一个按钮复用：别人打牌时是「碰」，自己回合里是「补杠」。
            SubmitClaim(_isWinPrompt ? ClaimType.AddedKong : ClaimType.Pong);
        }

        private void KongButton_Click(object sender, EventArgs e)
        {
            // 同一个按钮复用：别人打牌时是「明杠」，自己回合里是「暗杠」。
            SubmitClaim(_isWinPrompt ? ClaimType.ConcealedKong : ClaimType.Kong);
        }

        private void WinButton_Click(object sender, EventArgs e)
        {
            SubmitClaim(ClaimType.Win);
        }

        private void PassButton_Click(object sender, EventArgs e)
        {
            SubmitClaim(null);
        }

        /// <summary>
        /// 声明按钮的统一提交入口。顺序不能颠倒：先快照选项与面板类型，再收起面板，最后提交。
        /// 旧代码在碰/杠里先 HideClaimPanel（当时会清空 _pendingClaimOptions）再 PickClaim，
        /// 于是永远取到 null、提交给引擎等于「过」，表现为「又不能碰了」。
        /// </summary>
        private void SubmitClaim(ClaimType? type)
        {
            // 1) 先快照（隐藏面板之后这些状态可能已失效）
            bool isWin = _isWinPrompt;
            ClaimOption option = type.HasValue ? PickClaim(type.Value) : null;

            LogClaim("click=" + (type.HasValue ? type.Value.ToString() : "Pass") +
                     " submit=" + (option == null ? "null" : option.Type + "(" + option.Kind + ")") +
                     " channel=" + (isWin ? "concealed" : "claim"));

            // 2) 收起面板（纯视觉），再清理本轮状态
            HideClaimPanel();
            _pendingClaimOptions = null;
            _isWinPrompt = false;

            // 3) 保险：点了具体动作却拿不到选项时，不要静默当成「过」而毫无提示
            if (type.HasValue && option == null)
            {
                LogClaim("ERROR: option lost for " + type.Value);
                _viewModelRef.SubtitleText = "声明选项已失效，本次按「过」处理（详见 events.txt）";
            }

            // 4) 按面板类型提交到正确通道
            if (isWin)
                _host.HumanChooseConcealedAction(option);   // 自摸 / 杠上开花
            else
                _host.HumanChooseClaim(option);             // 别人打牌后的碰/杠/胡/过
        }

        // 从当前被征询的可选项中取出指定类型（牌种类已正确含在选项里）
        private ClaimOption PickClaim(ClaimType type)
        {
            if (_pendingClaimOptions == null) return null;
            return _pendingClaimOptions.FirstOrDefault(o => o.Type == type);
        }

        /// <summary>只做视觉收起，绝不清空选项数据（防止先隐藏后取值把碰变成过）。</summary>
        private void HideClaimPanel()
        {
            ClaimPanel.Visibility = Visibility.Collapsed;
            TurnHint.Visibility = Visibility.Visible;
        }

        /// <summary>声明链路诊断日志：再出问题时可直接看 events.txt 判断断在哪一环。</summary>
        private static void LogClaim(string line)
        {
            try
            {
                GameLog.AppendEvent(DateTime.Now.ToString("HH:mm:ss.fff") + " [Claim] " + line + "\n");
            }
            catch { }
        }

        // ============================================================
        // 出牌飞行动画：在 FlyLayer 上生成一张牌克隆，从起点抛物线飞到牌河落点
        // ============================================================
        private void FlyDiscard(int playerIndex, TileKind kind, Point? fromScreen, TileViewModel restVm = null)
        {
            if (FlyLayer == null) return;

            // 各家弃牌尺寸（与牌河 ItemTemplate 保持一致，否则落定瞬间会“跳”一下）
            double tw, th;
            TileFacing facing = TileFacing.South;
            if (playerIndex == 0) { tw = 46; th = 62; }
            else if (playerIndex == 1) { tw = 46; th = 62; facing = TileFacing.North; }
            else if (playerIndex == 2) { tw = 42; th = 31; facing = TileFacing.East; }
            else { tw = 42; th = 31; facing = TileFacing.West; }

            // 牌河里的牌已被整桌缩放系数放大，飞行牌必须同比放大，否则落定时会突然缩一圈
            tw *= _tableScale;
            th *= _tableScale;

            // 落点：优先取「牌河里那张新牌真正停住的位置」（含歪放、整桌缩放、北家 180° 旋转），
            // 拿不到时才退回牌河 Border 中心。飞行牌精确落在停留位上，落定瞬间不需要再挪。
            Point target = FlyLayer.PointFromScreen(GetDiscardCenterScreen(playerIndex));
            Point? rest = GetDiscardRestPoint(playerIndex, restVm);
            if (rest.HasValue) target = rest.Value;
            else if (restVm != null && restVm.FlyPending)
                restVm.FlyPending = false;   // 量不到停留位：飞行牌不会落到那张牌上，立即把它显出来
            Point targetLocal = target;

            // 起点：人类取点击位置；AI 从座位方向飞入
            Point sourceLocal;
            if (fromScreen.HasValue)
            {
                sourceLocal = FlyLayer.PointFromScreen(fromScreen.Value);
            }
            else
            {
                double ox = 0, oy = 0;
                if (playerIndex == 0) oy = 120;
                else if (playerIndex == 1) oy = -130;
                else if (playerIndex == 2) ox = 130;
                else ox = -130;
                // 起手点相对牌河中心/停留位的距离同样跟着整桌缩放走，牌桌放大后才不会显得"从很近的地方窜出来"
                ox *= _tableScale;
                oy *= _tableScale;
                sourceLocal = new Point(targetLocal.X - ox, targetLocal.Y - oy);
            }

            double dx = targetLocal.X - sourceLocal.X;
            double dy = targetLocal.Y - sourceLocal.Y;
            double dur = 440;
            double peak = Math.Max(48, Math.Abs(dy) * 0.5);   // 更夸张的拱起
            double tilt = (playerIndex % 2 == 0) ? -22 : 22;    // 飞行中带旋转，落定回正

            // 本批飞行牌（主牌 + 拖影），统一清理，杜绝残留在手牌上。
            // 飞行牌与停留牌同位置：落定瞬间把隐藏的停留牌显出来，观感就是「牌被扔进了那一格」。
            var flyTiles = new System.Collections.Generic.List<TileControl>();
            bool cleaned = false;
            Action cleanup = () =>
            {
                if (cleaned) return;
                cleaned = true;
                foreach (var t in flyTiles)
                    if (t != null) FlyLayer.Children.Remove(t);
                flyTiles.Clear();
                if (restVm != null && restVm.FlyPending) restVm.FlyPending = false;
            };

            try
            {
                var sb = new Storyboard();

                // 主牌（带投影）
                var main = MakeFlyTile(kind, tw, th, sourceLocal, tilt, true, facing);
                FlyLayer.Children.Add(main.Tile);
                flyTiles.Add(main.Tile);
                AddFlightAnimations(sb, main, dx, dy, peak, dur, 0);

                // 拖影：3 个滞后、低透明克隆，沿同一轨迹跟随
                double[] lags = { 55, 110, 165 };
                double[] ops = { 0.30, 0.18, 0.10 };
                for (int i = 0; i < lags.Length; i++)
                {
                    var g = MakeFlyTile(kind, tw, th, sourceLocal, tilt, false, facing);
                    g.Tile.Opacity = ops[i];
                    FlyLayer.Children.Add(g.Tile);
                    flyTiles.Add(g.Tile);
                    AddFlightAnimations(sb, g, dx, dy, peak, dur, lags[i]);
                }

                sb.Completed += (s, e) => cleanup();
                sb.Begin();

                // 兜底：即使 Storyboard 未触发 Completed（WPF 偶发），也保证飞行牌被清除，
                // 避免一张小牌永远压在你的手牌上挡视线。
                var fallback = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1600)
                };
                fallback.Tick += (s2, e2) => { fallback.Stop(); cleanup(); };
                fallback.Start();
            }
            catch
            {
                // 任何异常都立即清掉，不留残牌
                cleanup();
            }
        }

        // 飞行牌（位置用 Canvas.Left/Top 驱动，最可靠；缩放/旋转用变换）
        private sealed class FlyTile
        {
            public TileControl Tile;
            public ScaleTransform Sc;
            public RotateTransform Rt;
        }

        private FlyTile MakeFlyTile(TileKind kind, double w, double h, Point src, double tilt, bool shadow, TileFacing facing)
        {
            var tile = new TileControl
            {
                Kind = kind,
                Width = w,
                Height = h,
                Facing = facing,
                IsHitTestVisible = false
            };
            // 起点直接定位到 Canvas 坐标（窗口坐标）
            Canvas.SetLeft(tile, src.X - w / 2);
            Canvas.SetTop(tile, src.Y - h / 2);
            var sc = new ScaleTransform(1.18, 1.18);
            var rt = new RotateTransform(tilt);
            tile.RenderTransformOrigin = new Point(0.5, 0.5);
            tile.RenderTransform = new TransformGroup { Children = { rt, sc } };
            if (shadow)
                tile.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 10, ShadowDepth = 4, Opacity = 0.5 };
            return new FlyTile { Tile = tile, Sc = sc, Rt = rt };
        }

        // 给一张飞行牌追加位移（Canvas.Left/Top 抛物线）/ 缩放 / 旋转动画
        private void AddFlightAnimations(Storyboard sb, FlyTile ft, double dx, double dy, double peak, double dur, double beginMs)
        {
            var begin = TimeSpan.FromMilliseconds(beginMs);
            double srcLeft = Canvas.GetLeft(ft.Tile);
            double srcTop = Canvas.GetTop(ft.Tile);
            double dstLeft = srcLeft + dx;
            double dstTop = srcTop + dy;

            // X：线性飞到落点（Canvas.Left 附加属性，绝对可靠）
            AddDouble(sb, ft.Tile, Canvas.LeftProperty, srcLeft, dstLeft, dur, EasingMode.EaseOut, begin);

            // Y：抛物线（两段链式，先上拱后落到落点），用 Canvas.Top
            var ay1 = new DoubleAnimation(srcTop, srcTop - peak, TimeSpan.FromMilliseconds(dur / 2))
            {
                BeginTime = begin,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            SetTarget(ay1, ft.Tile, Canvas.TopProperty);
            var ay2 = new DoubleAnimation(srcTop - peak, dstTop, TimeSpan.FromMilliseconds(dur / 2))
            {
                BeginTime = TimeSpan.FromMilliseconds(dur / 2 + beginMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            SetTarget(ay2, ft.Tile, Canvas.TopProperty);
            sb.Children.Add(ay1);
            sb.Children.Add(ay2);

            // 缩放回落 / 旋转回正：变换类 Freezable 走 Storyboard 会静默失效（见 Anim 的注释），
            // 必须直接 BeginAnimation，否则牌会一路保持 1.18 倍、落地瞬间才"啪"地缩一下。
            Anim(ft.Sc, ScaleTransform.ScaleXProperty, 1.18, 1.0, dur, EasingMode.EaseOut, (int)beginMs);
            Anim(ft.Sc, ScaleTransform.ScaleYProperty, 1.18, 1.0, dur, EasingMode.EaseOut, (int)beginMs);
            Anim(ft.Rt, RotateTransform.AngleProperty, ft.Rt.Angle, 0, dur, EasingMode.EaseOut, (int)beginMs);
        }

        private Point GetDiscardCenterScreen(int playerIndex)
        {
            Border border;
            if (playerIndex == 0) border = DiscardSouth;
            else if (playerIndex == 1) border = DiscardNorth;
            else if (playerIndex == 2) border = DiscardEast;
            else border = DiscardWest;
            if (border == null) return new Point();
            // 必须把「本地中心点」交给 PointToScreen 整体变换，而不是取本地原点再补半宽半高：
            // 北家牌河整块旋转 180°（RenderTransform），本地原点的屏幕位置落在盒子的右下角，
            // 原点+半宽半高 会偏到 (W/2, H/2) 之外——北家出牌动画直接飞到东家那边。
            // 中心点在旋转下不变，LayoutTransform 缩放也会由 PointToScreen 一并算好。
            return border.PointToScreen(new Point(border.ActualWidth / 2, border.ActualHeight / 2));
        }

        /// <summary>
        /// 牌河是固定尺寸容器（防止撑高布局把己方手牌挤下去），出牌多了之后末尾的新牌
        /// 会被裁剪看不见。每次出牌后滚到内容末尾，保证歪着摆放的最新那张始终可见；
        /// 历史牌仍可用鼠标滚轮翻看。
        /// </summary>
        private void ScrollDiscardToLatest(int playerIndex)
        {
            var sv = DiscardScrollViewerOf(playerIndex);
            if (sv == null) return;

            // ItemsControl 的容器是异步生成的，等布局完成后再滚，确保滚到真正的内容末尾
            Dispatcher.BeginInvoke(new Action(() =>
            {
                sv.UpdateLayout();
                // 四家统一滚到内容末尾（最新一张）。北家整块旋转 180° 只改变视觉方向，
                // 滚动坐标仍是局部坐标：用 ScrollToHome 会停在局部最前面那两行旧牌，
                // 牌河超过两行时最新打出的牌反而被滚出视口看不见，所以不能反向。
                sv.ScrollToEnd();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>玩家索引 → 对应牌河的 ScrollViewer（0 南 / 1 北 / 2 东 / 3 西）。</summary>
        private ScrollViewer DiscardScrollViewerOf(int playerIndex)
        {
            switch (playerIndex)
            {
                case 0: return DiscardScrollSouth;
                case 1: return DiscardScrollNorth;
                case 2: return DiscardScrollEast;
                default: return DiscardScrollWest;
            }
        }

        /// <summary>
        /// 量出「刚打出的这张牌」在牌河里真正停住的位置（FlyLayer 局部坐标）。
        /// 飞行牌直接落在停留位上，而不是牌河中心——否则牌被 WrapPanel 排到别处，
        /// 落定瞬间还要再挪一下、看着像跳。
        /// 返回 null 表示量不到（容器未生成、布局未就绪等），调用方退回牌河中心兜底。
        /// </summary>
        private Point? GetDiscardRestPoint(int playerIndex, TileViewModel vm)
        {
            if (vm == null) return null;
            var sv = DiscardScrollViewerOf(playerIndex);
            if (sv == null) return null;
            var ic = sv.Content as ItemsControl;
            if (ic == null) return null;

            try
            {
                // ItemsControl 的容器是异步生成的：先强制布局，确认这张牌确实排进了牌河；
                // 再滚到最新一张，量出来的才是最终显示位置（滚动偏移会一起算进变换）。
                sv.UpdateLayout();
                sv.ScrollToEnd();
                ic.UpdateLayout();

                int idx = ic.Items.IndexOf(vm);
                if (idx < 0) return null;
                var container = ic.ItemContainerGenerator.ContainerFromIndex(idx) as FrameworkElement;
                if (container == null || container.ActualWidth <= 0) return null;

                // 数据模板根节点（那张带歪放变换的 Grid）。它的中心点经完整变换链——
                // WrapPanel 排位、滚动偏移、北家 180° 旋转、整桌 LayoutTransform 缩放——
                // 落到 FlyLayer 坐标系，就是飞行牌该去的落点。
                FrameworkElement visual = container;
                if (VisualTreeHelper.GetChildrenCount(container) > 0 &&
                    VisualTreeHelper.GetChild(container, 0) is FrameworkElement root)
                {
                    if (root.RenderSize.Width > 0 && root.RenderSize.Height > 0) visual = root;
                }
                var center = new Point(visual.RenderSize.Width / 2, visual.RenderSize.Height / 2);
                return visual.TransformToVisual(FlyLayer).Transform(center);
            }
            catch
            {
                // 任何一步不稳（容器未生成、布局中）都退回牌河中心，别让动画消失
                return null;
            }
        }

        // ============================================================
        // 胡牌全屏特效
        //   自己胡牌 → 「重击」版：白闪过曝 + 屏幕震动 + 放射金光 + 四道冲击波
        //              + 金色爆裂 + 火花爆射 + 彩带雨 + 标题从 2.6 倍砸回 1 倍
        //   别家胡牌 → 同一套动作，幅度/时长/粒子量整体收一档（别人胡牌不该抢主角）
        //   荒庄流局 → 只留暗场 + 标题，不放任何庆祝特效
        // ============================================================
        private void ShowWinEffect(string message, string notice, GameResult result)
        {
            bool playerWon = result != null && result.WinnerIndex == 0;
            bool draw = result == null || result.IsDraw;
            bool mega = playerWon;

            // ---- 文案与标题 ----
            WinSubtitle.Text = message ?? "";
            WinNotice.Text = notice ?? "";
            WinNotice.Visibility = string.IsNullOrEmpty(notice) ? Visibility.Collapsed : Visibility.Visible;

            if (draw)
            {
                WinTitle.Text = "荒庄流局";
                WinTitle.FontSize = 88;
                WinTitle.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xE2, 0xA8));
            }
            else if (playerWon)
            {
                WinTitle.Text = "胡啦！";
                WinTitle.FontSize = 132;
                WinTitle.Foreground = (Brush)FindResource("TitleGoldBrush");
            }
            else
            {
                WinTitle.Text = SeatName(result.WinnerIndex) + " 胡了";
                WinTitle.FontSize = 92;
                WinTitle.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD9, 0x8A));
            }

            // ---- 金币场：本局收支（文字）+ 四家余额（金币图片），破产时切到终局态 ----
            SettlementOutcome outcome = App.Session.LastOutcome;
            if (outcome != null)
            {
                WinCoins.Text = BuildCoinSettlementText(outcome, result);
                WinCoins.Visibility = Visibility.Visible;
                // 徽章绑定的是 viewModel.Players，RefreshCoins 已在结算前把金币摞与数字刷新过
                WinCoinBadges.Visibility = Visibility.Visible;
            }
            else
            {
                WinCoins.Visibility = Visibility.Collapsed;
                WinCoinBadges.Visibility = Visibility.Collapsed;
            }

            if (outcome != null && outcome.IsMatchOver)
            {
                WinTitle.Text = "本场结束";
                WinTitle.FontSize = 80;
                WinSubtitle.Text = string.Format("第 {0} 局 · {1} 金币耗尽",
                    outcome.RoundIndex,
                    string.Join("、", outcome.BankruptPlayers.Select(SeatName).ToArray()));
                WinContinueButton.Content = "再来一场";
            }

            // 窗口越大，结算文字与粒子也要跟着长大，否则最大化时标题只占屏幕一小块。
            // 外层 uiScale 负责跟窗口走，内层 _winSlam 负责"砸入"动画，两层分开互不干扰。
            double ui = Math.Max(1.0, _tableScale);
            var uiScale = new ScaleTransform(ui, ui);
            _winSlam = new ScaleTransform(1, 1);
            WinPanel.RenderTransform = new TransformGroup { Children = { uiScale, _winSlam } };

            // ---- 先亮场并跑一次布局：下面所有特效都要用实际尺寸算中心 ----
            WinOverlay.Visibility = Visibility.Visible;
            WinOverlay.Opacity = 0;
            WinOverlay.UpdateLayout();

            RayCanvas.Children.Clear();
            RingCanvas.Children.Clear();
            SparkCanvas.Children.Clear();
            ConfettiCanvas.Children.Clear();
            WinBurst.Width = WinBurst.Height = mega ? 940 : 560;

            // ---- 暗场淡入 ----
            WinOverlay.Opacity = 0;
            Anim(WinOverlay, UIElement.OpacityProperty, 0, 1, mega ? 110 : 280);

            // ---- 白闪 + 整窗震动：打击感主要来自这两下 ----
            if (mega) { FlashScreen(0.82, 440); ShakeWindow(30, 780); }
            else if (!draw) { FlashScreen(0.45, 380); ShakeWindow(8, 300); }

            // ---- 金光爆发 ----
            double burstFrom = mega ? 0.10 : 0.20;
            double burstMs = mega ? 900 : 650;
            var burstScale = new ScaleTransform(burstFrom, burstFrom);
            WinBurst.RenderTransform = burstScale;
            WinBurst.Opacity = 0.95;
            Anim(burstScale, ScaleTransform.ScaleXProperty, burstFrom, mega ? 1.28 : 1.7, burstMs);
            Anim(burstScale, ScaleTransform.ScaleYProperty, burstFrom, mega ? 1.28 : 1.7, burstMs);
            Anim(WinBurst, UIElement.OpacityProperty, 0.95, 0, burstMs + 150);

            // ---- 放射金光 + 冲击波（沿时间轴错开，像一波接一波地炸开）----
            if (!draw)
            {
                BuildWinRays(mega);
                SpawnRing(0.28, mega ? 2.7 : 1.9, 9, mega ? 620 : 500, 0, 0xE0);
                SpawnRing(0.22, mega ? 3.2 : 2.2, 5, mega ? 720 : 580, mega ? 130 : 90, 0xA0);
                if (mega)
                {
                    SpawnRing(0.18, 3.9, 3, 800, 270, 0x78);
                    SpawnRing(0.10, 4.8, 2, 900, 430, 0x50);
                }
            }

            // ---- 整块文字砸入：先大一号"压"在白闪后面，等白闪退下去再猛砸回 1 倍 ----
            // 顺序很重要：白闪盖住的瞬间文字已经就位，闪一退正好看见它砸下来，比两者同时发生更有层次。
            // 缩放的是整块面板而不是单标题——只放标题会盖住下面的番数，砸下来时糊成一团。
            double slamFrom = mega ? 1.42 : 0.2;
            int slamMs = mega ? 480 : 440;
            IEasingFunction slamEase = mega
                ? (IEasingFunction)new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.85 }
                : new CubicEase { EasingMode = EasingMode.EaseOut };
            Action slam = () =>
            {
                var a = Anim(_winSlam, ScaleTransform.ScaleXProperty, slamFrom, 1, slamMs, EasingMode.EaseOut, 0, slamEase);
                Anim(_winSlam, ScaleTransform.ScaleYProperty, slamFrom, 1, slamMs, EasingMode.EaseOut, 0, slamEase);
                a.Completed += (s, e) => StartTitleBreathing();
            };
            _winSlam.ScaleX = _winSlam.ScaleY = slamFrom;   // 先摆到位，等白闪盖住再开始砸
            if (mega) After(150, slam);
            else slam();

            // ---- 火花爆射 + 彩带雨（比标题稍晚，先看清字再多起来）----
            if (!draw)
            {
                SpawnBurstSparks(mega ? 72 : 34, 0);
                // 纸片分两波撒：第一波立刻落下，第二波错开一点，避免"哗"一下全落完
                SpawnConfetti(mega ? 96 : 54, true);
                After(mega ? 700 : 500, () => SpawnConfetti(mega ? 64 : 34, false));
                // 之后细水长流地补：纸片落完要几秒，光靠前两波后半段就空了。
                // 频率按"画面上常驻多少片"倒推——纸片穿过整屏约 4.5 秒，
                // 每 450ms 补 7 片 ≈ 15 片/秒，画面里常年飘着四十来片，热闹但不挡字。
                StartConfettiTrickle(mega ? 7 : 4, mega ? 450 : 900);
            }
            else StopConfettiTrickle();
        }

        /// <summary>
        /// 低频补发彩带：结算面板停留期间每隔一段时间撒一小撮，维持"一直在飘"的观感。
        /// 面板一关就自己停，不会在下一局继续撒。
        /// </summary>
        private void StartConfettiTrickle(int count, int everyMs)
        {
            StopConfettiTrickle();
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(everyMs)
            };
            timer.Tick += (s, e) =>
            {
                if (WinOverlay == null || WinOverlay.Visibility != Visibility.Visible)
                {
                    timer.Stop();
                    if (ReferenceEquals(_confettiTrickle, timer)) _confettiTrickle = null;
                    return;
                }
                SpawnConfetti(count, false);
            };
            _confettiTrickle = timer;
            timer.Start();
        }

        private void StopConfettiTrickle()
        {
            if (_confettiTrickle != null)
            {
                _confettiTrickle.Stop();
                _confettiTrickle = null;
            }
        }

        /// <summary>
        /// 延后执行：用来错开特效的各波动作。
        ///
        /// 不用 Timeline.BeginTime 做延迟——那套在本地动画上延迟极不稳定（实测会被拉长好几倍），
        /// 用普通 DispatcherTimer 更可控；窗口已经关掉时直接跳过，避免对着已关闭的窗口作画。
        /// </summary>
        private void After(int ms, Action action)
        {
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(ms)
            };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                if (WinOverlay == null || WinOverlay.Visibility != Visibility.Visible) return;
                action();
            };
            timer.Start();
        }

        /// <summary>
        /// 特效层（WinOverlay）的尺寸：它用 Margin=-60 铺满窗口，所以恒等于窗口尺寸 + 120。
        ///
        /// 这里刻意不读 WinOverlay.ActualWidth/ActualHeight：刚切到 Visible 的那一瞬间它们还是 0，
        /// 粒子/纸片会按 0 算距离，落得只有几十像素（彩带"看不见"就是这个原因）。
        /// </summary>
        private Size OverlaySize()
        {
            double w = ActualWidth > 1 ? ActualWidth : 1000;
            double h = ActualHeight > 1 ? ActualHeight : 800;
            return new Size(w + 120, h + 120);
        }

        /// <summary>
        /// 直接对某个属性起一段动画。
        ///
        /// 这里刻意不用 Storyboard + SetTarget：实测这套环境下，通过 Storyboard 去驱动
        /// 「变换类 Freezable」（ScaleTransform / TranslateTransform / RotateTransform）的属性时
        /// 动画会**静默失效**——不抛异常、也不动，只有 FrameworkElement 上的属性（如 Opacity）才生效；
        /// 换成 BeginAnimation 直接作用在同一个对象上就完全正常。特效里几乎全是变换动画，
        /// 所以统一走这条路径（原来的胡牌特效大半没动，正是栽在这个坑里）。
        /// </summary>
        private static DoubleAnimation Anim(DependencyObject target, DependencyProperty prop,
            double from, double to, double ms, EasingMode ease = EasingMode.EaseOut,
            int beginMs = 0, IEasingFunction easing = null)
        {
            var da = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = easing ?? new CubicEase { EasingMode = ease }
            };
            if (beginMs > 0) da.BeginTime = TimeSpan.FromMilliseconds(beginMs);

            var animatable = target as IAnimatable;
            if (animatable != null) animatable.BeginAnimation(prop, da);
            return da;
        }

        /// <summary>座位名：与中央信息区一致（0=己方）。</summary>
        private static string SeatName(int index)
        {
            switch (index)
            {
                case 0: return "你";
                case 1: return "北家";
                case 2: return "东家";
                default: return "西家";
            }
        }

        /// <summary>结算面板上的金币说明：本局收支账目；四家余额改由金币摞 + 数字显示，终局再附破产者与排名。</summary>
        private static string BuildCoinSettlementText(SettlementOutcome outcome, GameResult result)
        {
            string head;
            if (result == null || result.IsDraw)
            {
                head = "本局：荒庄流局 · 无金币转移";
            }
            else
            {
                head = string.Format("本局：{0}{1}{2} · 每家 -{3} · {4} +{5}",
                    result.PatternName,
                    result.IsSelfDraw ? "自摸" : (result.IsRobbedKong ? "抢杠胡" : ""),
                    result.KongDescription,
                    result.UnitPay,
                    SeatName(result.WinnerIndex),
                    result.UnitPay * 3);

                // 有杠时拆开单价，方便核对「明杠 +1 / 暗杠 +2」是否算对（加成不吃自摸倍率）
                if (result.KongCount > 0)
                {
                    var rules = App.Session.Rules;
                    int openBonus = result.OpenKongCount * rules.KongBonusPerPlayer;
                    int concealedBonus = result.ConcealedKongCount * rules.ConcealedKongBonusPerPlayer;
                    int basePart = result.UnitPay - openBonus - concealedBonus;
                    head += string.Format("\n单价 {0} = 牌型 {1} + 明杠 {2} + 暗杠 {3}",
                        result.UnitPay, basePart, openBonus, concealedBonus);
                }
            }

            // 四家余额不再写成「余额：你 20 · 北 20 …」一行文字，改由 WinCoinBadges 的金币摞 + 数字呈现
            int[] coins = outcome.CoinsAfter;
            if (!outcome.IsMatchOver) return head;

            string bust = string.Join("、", outcome.BankruptPlayers.Select(SeatName).ToArray());
            string rank = string.Join(" > ", App.Session.Ledger.Ranking()
                .Select(i => SeatName(i) + " " + coins[i]).ToArray());
            return head + "\n本场结束：" + bust + " 金币耗尽 · 排名 " + rank;
        }

        /// <summary>全屏白闪：透明度先冲到峰值再褪掉，模拟"过曝一帧"。</summary>
        private void FlashScreen(double peak, int ms)
        {
            WinFlashRect.Opacity = 0;
            var anim = new DoubleAnimationUsingKeyFrames();
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(peak, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70))));
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(peak * 0.35, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds((int)(ms * 0.45))))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            });
            WinFlashRect.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        /// <summary>
        /// 整窗震动：随机位移关键帧，衰减到 0。FillBehavior.Stop 保证抖完自动归位，
        /// 不会把牌桌永久偏移（否则后面几局整个界面都是歪的）。
        /// </summary>
        private void ShakeWindow(double amplitude, int ms)
        {
            var tt = RootGrid.RenderTransform as TranslateTransform;
            if (tt == null)
            {
                tt = new TranslateTransform();
                RootGrid.RenderTransform = tt;
            }
            var rnd = new Random(Guid.NewGuid().GetHashCode());
            foreach (var axis in new[] { TranslateTransform.XProperty, TranslateTransform.YProperty })
            {
                var anim = new DoubleAnimationUsingKeyFrames
                {
                    Duration = TimeSpan.FromMilliseconds(ms),
                    FillBehavior = FillBehavior.Stop
                };
                const int steps = 18;
                for (int i = 0; i <= steps; i++)
                {
                    double p = (double)i / steps;
                    double damp = (1 - p) * (1 - p);   // 平方衰减：开头猛，结尾收干净
                    double v = (i == steps) ? 0 : (rnd.NextDouble() * 2 - 1) * amplitude * damp;
                    anim.KeyFrames.Add(new LinearDoubleKeyFrame(v, KeyTime.FromPercent(p)));
                }
                tt.BeginAnimation(axis, anim);
            }
        }

        /// <summary>
        /// 放射金光：一圈细长三角形从中心射向四角，用径向渐变（中心亮、边缘透明）当填充，
        /// 整体再挂一个极慢的旋转——静止时是把金光，转起来像光在扫。
        /// </summary>
        private void BuildWinRays(bool mega)
        {
            var size = OverlaySize();
            double w = size.Width;
            double h = size.Height;
            RayCanvas.Width = w;
            RayCanvas.Height = h;

            double cx = w / 2, cy = h / 2;
            double r = Math.Sqrt(w * w + h * h) / 2 + 40;   // 一直铺到四角外面，避免看到端点

            int n = mega ? 40 : 24;
            var geo = new GeometryGroup();
            for (int i = 0; i < n; i++)
            {
                double a = Math.PI * 2 * i / n;
                double hw = Math.PI / n * 0.62;
                var fig = new PathFigure { StartPoint = new Point(cx, cy), IsClosed = true, IsFilled = true };
                fig.Segments.Add(new LineSegment(new Point(cx + Math.Cos(a - hw) * r, cy + Math.Sin(a - hw) * r), true));
                fig.Segments.Add(new LineSegment(new Point(cx + Math.Cos(a + hw) * r, cy + Math.Sin(a + hw) * r), true));
                geo.Children.Add(new PathGeometry(new[] { fig }));
            }

            var brush = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5
            };
            // 收得比较紧：只在中心一圈亮，往外很快透明掉。
            // 拉太远会变成整屏黄色滤镜，把暗场吃掉、也把牌桌糊住（试过 0.95 才透明，太脏）。
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xC8, 0xFF, 0xE8, 0x9A), 0.00));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x66, 0xFF, 0xC1, 0x35), 0.14));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xA8, 0x18), 0.42));

            var path = new System.Windows.Shapes.Path
            {
                Data = geo,
                Fill = brush,
                Opacity = 0,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            var rot = new RotateTransform(0);
            path.RenderTransform = rot;
            RayCanvas.Children.Add(path);

            path.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, mega ? 0.62 : 0.34, TimeSpan.FromMilliseconds(mega ? 620 : 900))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            // 极慢旋转：窗口关掉时随视觉树一起销毁，不需要额外清理
            rot.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(mega ? 60 : 100)) { RepeatBehavior = RepeatBehavior.Forever });
        }

        /// <summary>冲击波：一圈扩散并淡出的金环。叠三四道错开的，才有"炸开"的层次。</summary>
        private void SpawnRing(double fromScale, double toScale, double thickness, int ms, int delayMs, byte alpha)
        {
            var box = OverlaySize();
            double w = box.Width;
            double h = box.Height;
            double size = Math.Min(w, h) * 0.62;

            var ring = new Ellipse
            {
                Width = size,
                Height = size,
                Stroke = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xD8, 0x6A)),
                StrokeThickness = thickness * Math.Max(1.0, _tableScale),
                Opacity = 0,                       // 延迟期间保持不可见，等 Storyboard 开始才亮
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            Canvas.SetLeft(ring, (w - size) / 2);
            Canvas.SetTop(ring, (h - size) / 2);
            var sc = new ScaleTransform(fromScale, fromScale);
            ring.RenderTransform = sc;
            RingCanvas.Children.Add(ring);

            Anim(sc, ScaleTransform.ScaleXProperty, fromScale, toScale, ms, EasingMode.EaseOut, delayMs);
            Anim(sc, ScaleTransform.ScaleYProperty, fromScale, toScale, ms, EasingMode.EaseOut, delayMs);
            // 淡出用同一时长收尾；淡出结束即把环从画布上摘掉，避免越积越多
            var fade = Anim(ring, UIElement.OpacityProperty, 1, 0, ms, EasingMode.EaseOut, delayMs);
            fade.Completed += (s, e) => RingCanvas.Children.Remove(ring);
        }

        /// <summary>爆裂火花：从中心向四周飞散的小光点（金/白/橙红），距离、角度、时长全随机。</summary>
        private void SpawnBurstSparks(int count, int delayMs)
        {
            var rnd = new Random(Guid.NewGuid().GetHashCode());
            var box = OverlaySize();
            double w = box.Width;
            double h = box.Height;
            double cx = w / 2, cy = h / 2;
            double reach = Math.Min(w, h) * 0.62;

            var palette = new[]
            {
                Colors.Gold, Color.FromRgb(0xFF, 0xF3, 0xC4), Colors.White,
                Color.FromRgb(0xFF, 0x8A, 0x2B), Color.FromRgb(0xFF, 0x4D, 0x2B)
            };

            for (int i = 0; i < count; i++)
            {
                double size = (3 + rnd.NextDouble() * 7) * Math.Max(1.0, _tableScale);
                Shape dot;
                if (i % 3 == 0)
                    dot = new Rectangle { Width = size * 0.6, Height = size * 1.7, RadiusX = 1, RadiusY = 1 };
                else
                    dot = new Ellipse { Width = size, Height = size };

                dot.Fill = new SolidColorBrush(palette[rnd.Next(palette.Length)]);
                dot.IsHitTestVisible = false;
                dot.RenderTransformOrigin = new Point(0.5, 0.5);
                Canvas.SetLeft(dot, cx - dot.Width / 2);
                Canvas.SetTop(dot, cy - dot.Height / 2);

                double ang = rnd.NextDouble() * Math.PI * 2;
                double dist = reach * (0.35 + rnd.NextDouble() * 0.85);
                int ms = 700 + rnd.Next(600);

                var tt = new TranslateTransform();
                var rt = new RotateTransform(rnd.Next(360));
                dot.RenderTransform = new TransformGroup { Children = { rt, tt } };
                SparkCanvas.Children.Add(dot);

                Anim(tt, TranslateTransform.XProperty, 0, Math.Cos(ang) * dist, ms, EasingMode.EaseOut, delayMs);
                // 竖直方向多给一点，让光点像被抛出去后略微下沉
                Anim(tt, TranslateTransform.YProperty, 0, Math.Sin(ang) * dist + 90, ms, EasingMode.EaseOut, delayMs);
                // 后半程淡出，淡完从画布上摘掉
                var fade = Anim(dot, UIElement.OpacityProperty, 1, 0, ms * 0.55, EasingMode.EaseOut, delayMs + (int)(ms * 0.45));
                fade.Completed += (s, e) => SparkCanvas.Children.Remove(dot);
            }
        }

        private void StartTitleBreathing()
        {
            var sc = _winSlam;
            if (sc == null) return;
            var bx = new DoubleAnimation(0.99, 1.03, TimeSpan.FromMilliseconds(900))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            var by = new DoubleAnimation(0.99, 1.03, TimeSpan.FromMilliseconds(900))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            var animatable = sc as IAnimatable;
            if (animatable == null) return;
            animatable.BeginAnimation(ScaleTransform.ScaleXProperty, bx);
            animatable.BeginAnimation(ScaleTransform.ScaleYProperty, by);
        }

        /// <summary>
        /// 彩带雨：从画面顶部落下的彩色纸片。
        ///
        /// 匀速下落（线性）而不是缓动：缓动要么前段慢到看不见，要么后段快到一闪而过，
        /// 纸片这种轻飘飘的东西反而匀速最像。preseed=true 时给每片一个随机"已经下落了一段"的
        /// 初始高度，这样胡牌瞬间屏幕上就已经有纸在飘，不会先空一两秒。
        /// </summary>
        private void SpawnConfetti(int count, bool preseed)
        {
            var rnd = new Random(Guid.NewGuid().GetHashCode());
            var colors = new[] { Colors.Gold, Colors.Red, Colors.LimeGreen, Colors.DodgerBlue, Colors.Orange, Colors.White };
            var box = OverlaySize();
            double w = box.Width;
            double h = box.Height;
            // 窗口最大化后特效层面积是默认的好几倍，纸片数量按缩放系数补上，
            // 否则同一把牌在小窗口里是"满屏彩带"、最大化后就成了稀稀拉拉几片。
            count = Math.Max(1, (int)Math.Round(count * Math.Max(1.0, _tableScale)));
            const double Speed = 280;   // px/s：纸片下落速度（慢一点，整段结算都还有纸在飘）

            for (int i = 0; i < count; i++)
            {
                double paperScale = Math.Max(1.0, _tableScale);
                double pw = (7 + rnd.Next(8)) * paperScale;
                double ph = (12 + rnd.Next(10)) * paperScale;
                var piece = new Rectangle
                {
                    Width = pw,
                    Height = ph,
                    Fill = new SolidColorBrush(colors[rnd.Next(colors.Length)]),
                    RadiusX = 2,
                    RadiusY = 2,
                    Opacity = 0.95
                };
                double startTop = -40 - rnd.Next(50);
                Canvas.SetLeft(piece, rnd.NextDouble() * w);
                Canvas.SetTop(piece, startTop);

                double dist = h + 100 - startTop;
                // preseed：一开始就把纸片铺满整个画面高度（负值 = 还在屏幕上方），
                // 这样胡牌瞬间就有"满屏在飘"的密度，而不是先空一段再从上往下灌。
                double head = preseed ? (rnd.NextDouble() * 1.05 - 0.05) * dist : 0;

                var tt = new TranslateTransform(0, head);
                // 顺序必须是「先翻转、后平移」（{ rt, tt }）：TransformGroup 是按顺序相乘的，
                // 写成 { tt, rt } 会先把纸片平移到落点、再绕自身中心转一圈，位置被拧到画面外，
                // 结果就是"撒了几十片却只看得见几片"。旋转中心也必须是纸片自己的中心。
                var rt = new RotateTransform(rnd.Next(360)) { CenterX = pw / 2, CenterY = ph / 2 };
                piece.RenderTransform = new TransformGroup { Children = { rt, tt } };
                ConfettiCanvas.Children.Add(piece);

                // 每片下落速度略有快慢：全都一个速度的话，纸片会齐刷刷地同时到底，
                // 画面底部像"排队落地"；给 ±20% 的差速就散开了，也像真的纸片。
                double speed = Speed * (0.82 + rnd.NextDouble() * 0.42);
                double ms = (dist - head) / speed * 1000.0;
                // 记下"落到画面下方之外"的时刻，交给统一的清理器回收。
                // 早先每批纸片各起一个 5 秒定时器去清，跟后来的补发批次打架（会把还在飘的一起清掉）；
                // 改成按寿命回收后，画布上的元素数 ≈ 撒放速率 × 下落时长，天然稳定不堆积。
                piece.Tag = DateTime.UtcNow.AddMilliseconds(ms + 500);
                EnsureConfettiSweep();
                var animatable = tt as IAnimatable;
                if (animatable != null)
                    animatable.BeginAnimation(TranslateTransform.YProperty,
                        new DoubleAnimation(head, dist, TimeSpan.FromMilliseconds(ms)));

                // 翻转比自转更像纸片：绕自身中心翻面。
                // 起点必须取初始角度：写成 DoubleAnimation(0, 540) 会把随机构造的初始角度覆盖成 0，
                // 结果所有纸片一开始都笔直竖着，看着像一排小方片而不是翻飞的纸屑。
                var flip = new DoubleAnimation(rt.Angle, rt.Angle + 540, TimeSpan.FromMilliseconds(ms));
                var flipable = rt as IAnimatable;
                if (flipable != null) flipable.BeginAnimation(RotateTransform.AngleProperty, flip);
            }
        }

        /// <summary>
        /// 彩带回收器：每秒扫一遍，把已经落到画面之外（Tag 里记的到期时刻已过）的纸片摘掉。
        /// 全窗只留这一个定时器，撒得再密也不会堆出一堆计时器。
        /// </summary>
        private void EnsureConfettiSweep()
        {
            if (_confettiSweep != null) return;
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(0.5)
            };
            timer.Tick += (s, e) =>
            {
                if (ConfettiCanvas == null) { timer.Stop(); _confettiSweep = null; return; }
                var now = DateTime.UtcNow;
                for (int i = ConfettiCanvas.Children.Count - 1; i >= 0; i--)
                {
                    var el = ConfettiCanvas.Children[i];
                    var stamp = el is FrameworkElement ? ((FrameworkElement)el).Tag as DateTime? : null;
                    // 没有到期标记的（理论上不会出现）一并收掉，避免漏网的纸片永远挂在画布上
                    if (stamp == null || stamp.Value <= now) ConfettiCanvas.Children.RemoveAt(i);
                }
                if (ConfettiCanvas.Children.Count == 0) { timer.Stop(); _confettiSweep = null; }
            };
            _confettiSweep = timer;
            timer.Start();
        }

        private void StopConfettiSweep()
        {
            if (_confettiSweep != null)
            {
                _confettiSweep.Stop();
                _confettiSweep = null;
            }
        }

        private void WinContinueButton_Click(object sender, RoutedEventArgs e)
        {
            // 本场已因破产结束：点「再来一场」时重置账本，按设置里的初始金币重新发牌。
            RestartRound(App.Session.Ledger.IsMatchOver);
        }

        /// <summary>
        /// 关掉当前窗口、开一局新的。结算面板的「再来一局」和设置里的
        /// 「保存并开新的一场」共用这条路径，避免两处各自维护窗口状态继承。
        /// startNewMatch=true 时账本归零（四家恢复当前设置的初始金币）。
        /// </summary>
        private void RestartRound(bool startNewMatch)
        {
            // 收掉特效层：新窗口是新实例，别让上一轮的粒子和 Forever 动画
            // （光芒旋转/标题呼吸）继续挂在已关闭的窗口上
            WinOverlay.Visibility = Visibility.Collapsed;
            StopConfettiTrickle();
            StopConfettiSweep();
            ConfettiCanvas.Children.Clear();
            RayCanvas.Children.Clear();
            RingCanvas.Children.Clear();
            SparkCanvas.Children.Clear();

            if (startNewMatch) App.Session.StartNewMatch();

            // 重开一局：先停掉本局的音频后台线程，再建新窗口。
            // 否则新窗口的 AudioService 会先启动 worker，而旧窗口要到 Close() 时才停，
            // 两个 worker 同时调 PlaySound（进程级、后一次会打断前一次）会把声音切碎。
            _host.Audio.Dispose();
            var w = new MainWindow();
            // 新窗口继承上一局的窗口状态：最大化后点「再来一局」，新窗口保持最大化，
            // 否则会退回 XAML 默认的 1000×800。手动调整过的窗口尺寸也一并继承。
            w.WindowState = WindowState;
            if (WindowState != WindowState.Maximized)
            {
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.Left = Left;
                w.Top = Top;
                w.Width = Width;
                w.Height = Height;
            }
            w.Show();
            Close();
        }

        /// <summary>
        /// 「设置」入口。声音由设置窗口直接写到 AudioService（即时生效）；
        /// 初始金币与难度在保存后同步到会话规则/难度状态，必要时立刻开新的一场。
        /// 命令行 --difficulty 固定档位时优先级更高，只保存配置、不改本会话档位。
        /// </summary>
        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_host == null || _host.Audio == null) return;

            bool locked = ResolveFixedLevel().HasValue;
            var dlg = new SettingsWindow(_host.Audio, locked) { Owner = this };
            bool? saved = dlg.ShowDialog();

            // 取消时设置窗口已自行回滚「试听」的临时改动，这里不用再处理声音。
            if (saved != true) return;

            if (dlg.DifficultyChanged && !locked)
            {
                var s = GameSettings.Current;
                // 改了起始难度 = 换一条难度曲线：连胜/局数进度清零，下一局从新档位开始。
                DifficultyState.ApplyStartLevel(s.StartDifficulty, s.AutoLevelUp);
            }
            if (dlg.RequestRestart) RestartRound(true);
        }

        // ============================================================
        // 开局动画：四面牌墙逐墩码起 → 掷骰 → 开门搬牌 → 揭幕发牌
        //
        // 每局都是新的 MainWindow（金币账本挂在 App.Session 上），所以把动画挂在
        // 构造末尾即可，程序启动和「再来一局」都会播。引擎延后到动画结束才启动，
        // 否则庄家约 1 秒就出第一张牌，牌河会和还没码完的牌墙撞在一起。
        // ============================================================

        /// <summary>构造末尾调用：决定直接开局，还是先播开场动画。</summary>
        private void StartOpeningSequence(string preview)
        {
            string[] args = Environment.GetCommandLineArgs();
            bool noIntro = args.Any(a => string.Equals(a, "--no-intro", StringComparison.OrdinalIgnoreCase))
                || Environment.GetEnvironmentVariable("MJ_NO_INTRO") == "1";
            bool dump = args.Any(a => string.Equals(a, "--dump-layout", StringComparison.OrdinalIgnoreCase));
            _introHold = args.Any(a => string.Equals(a, "--intro-hold", StringComparison.OrdinalIgnoreCase));
            // 结算预览 / 布局自检不该被开场动画挡住；--no-intro 给调试和老机器用
            if (noIntro || dump || !string.IsNullOrEmpty(preview)) { LaunchGame(); return; }

            ContentRendered += (s, e) => PlayOpeningAnimation();
            KeyDown += (s, e) => SkipOpening();
        }

        /// <summary>真正启动引擎，只调一次（跳过动画与收尾定时器可能同时到达）。</summary>
        private void LaunchGame()
        {
            if (_gameLaunched) return;
            _gameLaunched = true;
            _ = _host.StartAsync();
        }

        private void IntroLayer_MouseDown(object sender, MouseButtonEventArgs e) { SkipOpening(); }

        private void SkipOpening() { FinishOpening(); }

        /// <summary>收掉动画层并开局。点击 / 按键跳过、动画自然结束、窗口关闭都走这里。</summary>
        private void FinishOpening()
        {
            if (_introFinished) return;
            _introFinished = true;
            if (_introTimer != null) { _introTimer.Stop(); _introTimer = null; }
            if (IntroLayer != null)
            {
                IntroLayer.Children.Clear();
                IntroLayer.BeginAnimation(UIElement.OpacityProperty, null);
                IntroLayer.Opacity = 1;
                IntroLayer.Visibility = Visibility.Collapsed;
            }
            LaunchGame();
        }

        private void PlayOpeningAnimation()
        {
            if (_introFinished || _gameLaunched) return;
            double w = ActualWidth > 100 ? ActualWidth : 1000;
            double h = ActualHeight > 100 ? ActualHeight : 800;
            // 牌墙跟着整桌缩放：最大化时不会缩成中间一小圈
            double scale = Math.Max(0.75, Math.Min(_tableScale, 2.4));

            IntroLayer.Children.Clear();
            IntroLayer.BeginAnimation(UIElement.OpacityProperty, null);
            IntroLayer.Opacity = 1;
            IntroLayer.Visibility = Visibility.Visible;

            // 1) 复刻真实牌桌：直接取主题的 TableBackground / TableFrameBrush，
            //    保持同样的 Margin 14、圆角 26、金边 7，连「中心亮、四周暗」的径向光都一致。
            //    上面再叠一层暖白柔光提亮曝光，避免退回原先近黑的暗遮罩。
            Brush felt;
            try { felt = ((Brush)TryFindResource("TableBackground"))?.Clone() ?? MakeIntroTableBrush(); }
            catch { felt = MakeIntroTableBrush(); }
            Brush gold;
            try { gold = ((Brush)TryFindResource("TableFrameBrush"))?.Clone() ?? Brushes.Gold; }
            catch { gold = Brushes.Gold; }

            var stage = new Canvas { Width = w, Height = h, IsHitTestVisible = false, Opacity = 0 };
            var table = new Border
            {
                Width = w - 28, Height = h - 28,
                CornerRadius = new CornerRadius(26),
                BorderBrush = gold, BorderThickness = new Thickness(7),
                Background = felt, IsHitTestVisible = false
            };
            Canvas.SetLeft(table, 14);
            Canvas.SetTop(table, 14);
            stage.Children.Add(table);

            var glow = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.4), Center = new Point(0.5, 0.4),
                RadiusX = 0.82, RadiusY = 0.82
            };
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x2E, 0xFF, 0xF7, 0xDC), 0.0));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x1C, 0xFF, 0xF2, 0xC8), 0.62));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x0A, 0xFF, 0xEC, 0xB4), 1.0));
            var light = new Rectangle
            {
                Width = w - 28, Height = h - 28,
                RadiusX = 19, RadiusY = 19, Fill = glow, IsHitTestVisible = false
            };
            Canvas.SetLeft(light, 14);
            Canvas.SetTop(light, 14);
            stage.Children.Add(light);
            IntroLayer.Children.Add(stage);
            Anim(stage, UIElement.OpacityProperty, 0, 1, 220, EasingMode.EaseOut);

            // 2) 牌墙几何：以窗口中心画一圈方形牌墙。牌背贴图本身自带一圈浅色外框，
            //    再留几何缝就会显得牌与牌离得很远，所以让相邻墩轻微压叠 (1.08)。
            double side = Math.Min(Math.Min(w * 0.80, h * 0.82), 760 * scale);
            if (side < 380 * scale) side = 380 * scale;
            double cx = w / 2, cy = h / 2, half = side / 2;
            double step = side / StacksPerSide;
            double tileW = step * 1.08;    // 沿墙方向轻微重叠，收紧牌缝
            double tileH = step * 1.42;    // 垂直墙方向的厚度（牌长）
            var rng = new Random(unchecked((int)DateTime.Now.Ticks));
            ImageSource back = TileArtwork.Get(TileKind.Man1, false);

            var stacks = new List<IntroStack>();
            int order = 0;
            for (int sd = 0; sd < 4; sd++)
            {
                // 竖边各 16 墩，从距角 1 墩处起、到距角 1 墩处止，
                // 与横边端墩轻微压叠成闭合拐角，四边不会断开留缺口。
                int count = (sd == 1 || sd == 3) ? StacksPerSide - 1 : StacksPerSide;
                for (int i = 0; i < count; i++)
                {
                    double x, y;
                    if (sd == 0) { x = cx - half + (i + 0.5) * step; y = cy - half; }       // 北
                    else if (sd == 2) { x = cx - half + (i + 0.5) * step; y = cy + half; }  // 南
                    else if (sd == 1) { x = cx + half; y = cy - half + (i + 1.0) * step; }  // 东
                    else { x = cx - half; y = cy - half + (i + 1.0) * step; }               // 西
                    bool horizontal = (sd == 1 || sd == 3);
                    double bw = horizontal ? tileH : tileW;
                    double bh = horizontal ? tileW : tileH;

                    var el = MakeIntroStack(bw, bh, back);
                    // 起点都挤在牌桌中央（像刚洗完的一堆牌），按北→东→南→西的顺序逐墩归位
                    double sx = cx + (rng.NextDouble() - 0.5) * 96 * scale;
                    double sy = cy + (rng.NextDouble() - 0.5) * 72 * scale;
                    Canvas.SetLeft(el, sx - bw / 2);
                    Canvas.SetTop(el, sy - bh / 2);
                    IntroLayer.Children.Add(el);

                    int t = StackStartMs + order * StackStepMs;
                    var pop = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.28 };
                    Anim(el, Canvas.LeftProperty, sx - bw / 2, x - bw / 2, StackFlyMs, EasingMode.EaseOut, t);
                    Anim(el, Canvas.TopProperty, sy - bh / 2, y - bh / 2, StackFlyMs, EasingMode.EaseOut, t);
                    Anim(el, UIElement.OpacityProperty, 0, 1, 130, EasingMode.EaseOut, t);
                    // 变换类 Freezable 必须直接 BeginAnimation（见 Anim 的注释），Storyboard 会静默失效
                    var sc = new ScaleTransform(0.5, 0.5);
                    var rt = new RotateTransform((rng.NextDouble() - 0.5) * 34);
                    el.RenderTransformOrigin = new Point(0.5, 0.5);
                    el.RenderTransform = new TransformGroup { Children = { rt, sc } };
                    Anim(sc, ScaleTransform.ScaleXProperty, 0.5, 1, StackFlyMs, EasingMode.EaseOut, t, pop);
                    Anim(sc, ScaleTransform.ScaleYProperty, 0.5, 1, StackFlyMs, EasingMode.EaseOut, t, pop);
                    Anim(rt, RotateTransform.AngleProperty, rt.Angle, 0, StackFlyMs, EasingMode.EaseOut, t);

                    stacks.Add(new IntroStack { Element = el, Side = sd, Index = i });
                    order++;
                }
            }
            int stackEnd = StackStartMs + order * StackStepMs + StackFlyMs;
            int diceAt = stackEnd + 30;
            int moveAt = diceAt + 430;
            int fadeAt = moveAt + 430;
            int total = fadeAt + 320;

            // 3) 中央阶段文字（码牌 → 掷骰 → 开门）：绿绒布桌面上用亮金字 + 深色投影
            var label = new TextBlock
            {
                Text = "码 牌",
                FontSize = 36 * scale,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Microsoft YaHei"),
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xF0, 0xB8)),
                Width = side,
                TextAlignment = TextAlignment.Center,
                IsHitTestVisible = false,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 14, ShadowDepth = 2, Opacity = 0.8 }
            };
            Canvas.SetLeft(label, cx - side / 2);
            Canvas.SetTop(label, cy - 34 * scale);
            IntroLayer.Children.Add(label);
            Anim(label, UIElement.OpacityProperty, 0, 1, 260, EasingMode.EaseOut);

            // 4) 掷骰：两颗骰子落到文字下方，点数之和决定开门方位与墩号
            int d1 = rng.Next(1, 7), d2 = rng.Next(1, 7);
            double dieSize = 56 * scale;
            for (int i = 0; i < 2; i++)
            {
                var die = MakeIntroDie(i == 0 ? d1 : d2, dieSize);
                double dx = cx - dieSize * 1.15 + i * dieSize * 1.3;
                double dy = cy + 48 * scale;
                Canvas.SetLeft(die, dx);
                Canvas.SetTop(die, dy - 180 * scale);
                die.Opacity = 0;
                die.Effect = new DropShadowEffect
                {
                    Color = Color.FromRgb(0x8A, 0x76, 0x52), BlurRadius = 10,
                    ShadowDepth = 3, Opacity = 0.45
                };
                IntroLayer.Children.Add(die);
                Anim(die, Canvas.TopProperty, dy - 180 * scale, dy, 420, EasingMode.EaseOut, diceAt,
                     new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 });
                Anim(die, UIElement.OpacityProperty, 0, 1, 150, EasingMode.EaseOut, diceAt);
                var dr = new RotateTransform(i == 0 ? -22 : 18);
                die.RenderTransformOrigin = new Point(0.5, 0.5);
                die.RenderTransform = dr;
                Anim(dr, RotateTransform.AngleProperty, dr.Angle, 0, 420, EasingMode.EaseOut, diceAt);
            }

            // 5) 开门搬牌：点数所指那两墩往墙外推一小截并点亮
            int openSide = (d1 + d2) % 4;
            int openIdx = (d1 + d2 + 1) % (StacksPerSide - 2);  // 保证 openIdx+1 在竖边 16 墩范围内
            double push = 26 * scale, ox = 0, oy = 0;
            if (openSide == 0) oy = -push;
            else if (openSide == 2) oy = push;
            else if (openSide == 1) ox = push;
            else ox = -push;
            var moved = new List<FrameworkElement>();
            foreach (var s in stacks)
            {
                if (s.Side != openSide || (s.Index != openIdx && s.Index != openIdx + 1)) continue;
                // 开门的两墩额外放大 1.32 倍，让「搬牌」这个动作在亮色舞台上足够醒目
                var tg = (TransformGroup)s.Element.RenderTransform;
                var tt = new TranslateTransform(0, 0);
                var zoom = new ScaleTransform(1, 1);
                tg.Children.Add(tt);
                tg.Children.Add(zoom);
                var pop = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
                Anim(tt, TranslateTransform.XProperty, 0, ox, 300, EasingMode.EaseOut, moveAt);
                Anim(tt, TranslateTransform.YProperty, 0, oy, 300, EasingMode.EaseOut, moveAt);
                Anim(zoom, ScaleTransform.ScaleXProperty, 1, 1.32, 300, EasingMode.EaseOut, moveAt, pop);
                Anim(zoom, ScaleTransform.ScaleYProperty, 1, 1.32, 300, EasingMode.EaseOut, moveAt, pop);
                // 金橙描边：亮底上比单纯的光晕更清楚
                var hi = new Rectangle
                {
                    Width = s.Element.Width, Height = s.Element.Height,
                    RadiusX = 3, RadiusY = 3,
                    Stroke = new SolidColorBrush(Color.FromRgb(0xE0, 0x86, 0x0C)),
                    StrokeThickness = 2.5,
                    IsHitTestVisible = false, Opacity = 0
                };
                s.Element.Children.Add(hi);
                Anim(hi, UIElement.OpacityProperty, 0, 1, 220, EasingMode.EaseOut, moveAt);
                moved.Add(s.Element);
            }

            // 6) 揭幕：整层淡出后由定时器清空并启动引擎
            if (!_introHold)
                Anim(IntroLayer, UIElement.OpacityProperty, 1, 0, 300, EasingMode.EaseIn, fadeAt);
            try { _host.Audio.OpeningStack(); } catch { }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool diceShown = false, movedShown = false;
            _introTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(35)
            };
            _introTimer.Tick += (s, e) =>
            {
                double t = sw.ElapsedMilliseconds;
                if (!diceShown && t >= diceAt) { diceShown = true; label.Text = "掷骰 · 开门"; }
                if (!movedShown && t >= moveAt)
                {
                    movedShown = true;
                    label.Text = "开门 · 搬 " + (openIdx + 1) + " 墩";
                    foreach (var el in moved)
                        el.Effect = new DropShadowEffect
                        {
                            Color = Color.FromRgb(0xE8, 0x9A, 0x18), BlurRadius = 20,
                            ShadowDepth = 0, Opacity = 0.95
                        };
                }
                // --intro-hold：停在码完牌的画面上，方便截图核对；点一下 / 按任意键继续
                if (_introHold) { if (t >= fadeAt + 200) _introTimer.Stop(); return; }
                if (t >= total) FinishOpening();
            };
            _introTimer.Start();
        }

        /// <summary>主题资源缺失时，按 ElderTheme 里 TableBackground 的原色兜底。</summary>
        private static Brush MakeIntroTableBrush()
        {
            var b = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.4), Center = new Point(0.5, 0.4),
                RadiusX = 0.82, RadiusY = 0.82
            };
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0x33, 0xB5, 0x67), 0.0));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0x0E, 0x5C, 0x32), 0.62));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0x07, 0x3A, 0x20), 1.0));
            return b;
        }

        /// <summary>一墩牌 = 上下两张叠放；上层直接用牌背贴图，不再额外描边（贴图自带浅色牌框）。</summary>
        private static Canvas MakeIntroStack(double w, double h, ImageSource back)
        {
            var c = new Canvas { Width = w, Height = h, IsHitTestVisible = false, Opacity = 0 };
            // 下层牌错开 1.5px 做厚度感，用比桌面更深的绿，避免每墩外圈又多一道亮框
            var under = new Rectangle
            {
                Width = w - 1.5, Height = h - 1.5, RadiusX = 3, RadiusY = 3,
                Fill = new SolidColorBrush(Color.FromRgb(0x0B, 0x3D, 0x25))
            };
            Canvas.SetLeft(under, 1.5);
            Canvas.SetTop(under, 1.5);
            c.Children.Add(under);

            if (back != null)
            {
                var face = new Image { Source = back, Width = w - 1.5, Height = h - 1.5, Stretch = Stretch.Fill };
                Canvas.SetLeft(face, 0);
                Canvas.SetTop(face, 0);
                c.Children.Add(face);
            }
            else
            {
                // 贴图资源缺失时降级为纯色牌背，动画不至于变成一片空白
                var face = new Rectangle
                {
                    Width = w - 1.5, Height = h - 1.5, RadiusX = 3, RadiusY = 3,
                    Fill = new SolidColorBrush(Color.FromRgb(0x35, 0x86, 0x60)),
                    Stroke = new SolidColorBrush(Color.FromRgb(0xE4, 0xF2, 0xD8)), StrokeThickness = 1
                };
                c.Children.Add(face);
            }
            return c;
        }

        /// <summary>画一颗中式骰子（1、4 为红点，其余黑点）。</summary>
        private static FrameworkElement MakeIntroDie(int value, double size)
        {
            var border = new Border
            {
                Width = size, Height = size,
                CornerRadius = new CornerRadius(size * 0.18),
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF6, 0xE3)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x66, 0x44)),
                BorderThickness = new Thickness(1.5),
                IsHitTestVisible = false
            };
            var g = new Grid { Margin = new Thickness(size * 0.14) };
            for (int i = 0; i < 3; i++)
            {
                g.RowDefinitions.Add(new RowDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition());
            }
            int[] dots = DieDots(value);
            var brush = new SolidColorBrush((value == 1 || value == 4)
                ? Color.FromRgb(0xC0, 0x22, 0x22) : Color.FromRgb(0x22, 0x28, 0x22));
            for (int i = 0; i < dots.Length; i += 2)
            {
                var dot = new Ellipse
                {
                    Width = size * 0.15, Height = size * 0.15, Fill = brush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetRow(dot, dots[i]);
                Grid.SetColumn(dot, dots[i + 1]);
                g.Children.Add(dot);
            }
            border.Child = g;
            return border;
        }

        /// <summary>骰子点位，返回连续的 {行, 列}，行列均为 0..2。</summary>
        private static int[] DieDots(int v)
        {
            switch (v)
            {
                case 1: return new[] { 1, 1 };
                case 2: return new[] { 0, 0, 2, 2 };
                case 3: return new[] { 0, 0, 1, 1, 2, 2 };
                case 4: return new[] { 0, 0, 0, 2, 2, 0, 2, 2 };
                case 5: return new[] { 0, 0, 0, 2, 1, 1, 2, 0, 2, 2 };
                default: return new[] { 0, 0, 0, 2, 1, 0, 1, 2, 2, 0, 2, 2 };
            }
        }

        // ============================================================
        // 碰/杠/胡/吃 弹字特效：在对应座位中心弹出大字，弹入后淡出
        // ============================================================
        private void ShowClaimPopup(int playerIndex, string message)
        {
            if (FlyLayer == null || string.IsNullOrEmpty(message)) return;

            string text; Color col;
            if (message.IndexOf("胡") >= 0) { text = "胡啦！"; col = Colors.Gold; }
            else if (message.IndexOf("暗杠") >= 0) { text = "暗杠！"; col = Colors.LimeGreen; }
            else if (message.IndexOf("补杠") >= 0) { text = "补杠！"; col = Colors.LimeGreen; }
            else if (message.IndexOf("杠") >= 0) { text = "杠！"; col = Colors.LimeGreen; }
            else if (message.IndexOf("碰") >= 0) { text = "碰！"; col = Colors.DodgerBlue; }
            else if (message.IndexOf("吃") >= 0) { text = "吃！"; col = Colors.Cyan; }
            else return;   // 过 / 无声明 不弹

            var tb = new TextBlock
            {
                Text = text,
                FontSize = 56 * _tableScale,   // 与整桌缩放同步，最大化时大字才不会显得太小
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Microsoft YaHei"),
                Foreground = new SolidColorBrush(col),
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            tb.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 8, ShadowDepth = 2, Opacity = 0.6 };

            var anchor = FlyLayer.PointFromScreen(GetDiscardCenterScreen(playerIndex));
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tb, anchor.X - tb.DesiredSize.Width / 2);
            Canvas.SetTop(tb, anchor.Y - tb.DesiredSize.Height / 2);
            FlyLayer.Children.Add(tb);

            // 弹入：ScaleTransform 是 Freezable，走 Storyboard 会静默失效（见 Anim 的注释），
            // 之前"碰！"一直是以 0.3 倍的小字直接淡出，压根没弹起来过。
            var sc = new ScaleTransform(0.3, 0.3);
            tb.RenderTransform = sc;
            var popEase = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 };
            Anim(sc, ScaleTransform.ScaleXProperty, 0.3, 1.25, 260, EasingMode.EaseOut, 0, popEase);
            Anim(sc, ScaleTransform.ScaleYProperty, 0.3, 1.25, 260, EasingMode.EaseOut, 0, popEase);

            // 停在放大的那一刻，再整体淡出并移除
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(520))
            {
                BeginTime = TimeSpan.FromMilliseconds(520),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fade.Completed += (s, e) => FlyLayer.Children.Remove(tb);
            tb.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        // 工具：往 Storyboard 追加一条 DoubleAnimation（可带起始延迟；缓动不指定则用 CubicEase）
        private static DoubleAnimation AddDouble(Storyboard sb, DependencyObject target, object property, double from, double to, double ms, EasingMode ease, TimeSpan begin = default, IEasingFunction easing = null)
        {
            var da = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
            {
                BeginTime = begin,
                EasingFunction = easing ?? new CubicEase { EasingMode = ease }
            };
            Storyboard.SetTarget(da, target);
            Storyboard.SetTargetProperty(da, MakePath(property));
            sb.Children.Add(da);
            return da;
        }

        private static void SetTarget(Timeline tl, DependencyObject target, object property)
        {
            Storyboard.SetTarget(tl, target);
            Storyboard.SetTargetProperty(tl, MakePath(property));
        }

        // PropertyPath(object) 重载只接受 DependencyProperty/PropertyInfo/PropertyDescriptor；
        // 字符串属性名必须走字符串重载，否则抛 InvalidOperationException
        private static PropertyPath MakePath(object property)
        {
            return property is string name ? new PropertyPath(name) : new PropertyPath((DependencyProperty)property);
        }
    }
}