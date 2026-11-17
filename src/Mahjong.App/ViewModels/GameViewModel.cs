using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using Mahjong.Core.Game;
using Mahjong.Core.Tiles;

namespace Mahjong.App.ViewModels
{
    /// <summary>
    /// 单张牌的绑定项（供 TileControl 使用）。
    /// </summary>
    public sealed class TileViewModel : INotifyPropertyChanged
    {
        private readonly Tile _tile;
        private bool _isSelected;

        public TileViewModel(Tile tile)
        {
            _tile = tile;
            // 手牌微不齐：用牌的唯一 Id 做确定性伪随机，避免每帧变化造成闪烁。
            // 真实手牌不会像素级贴齐；±1.6° 倾斜 + ±1.3px 高低差即可打破「图标感」。
            int id = tile.Id;
            double a = ((id * 37) % 101) / 100.0;
            double b = ((id * 61 + 17) % 101) / 100.0;
            Tilt = (a - 0.5) * 3.2;
            JitterY = (b - 0.5) * 2.6;
        }

        /// <summary>手牌倾斜角（度），绑定到 DataTemplate 外层的 RotateTransform。</summary>
        public double Tilt { get; }

        /// <summary>手牌垂直微偏移（像素）。</summary>
        public double JitterY { get; }

        /// <summary>牌面种类（绑定到 TileControl.Kind 显示）。</summary>
        public TileKind Kind { get { return _tile.Kind; } }

        /// <summary>底层 Tile 实例（供点击出牌时传回引擎）。</summary>
        public Tile DiscardTile { get { return _tile; } }
        public string Name { get { return _tile.Kind.GetName(); } }

        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged("IsSelected");
                }
            }
        }

        // 胡牌呼吸光（绑定到 TileControl.WinFlash）
        private bool _winFlash;
        public bool WinFlash
        {
            get { return _winFlash; }
            set { if (_winFlash != value) { _winFlash = value; OnPropertyChanged("WinFlash"); } }
        }

        /// <summary>
        /// 刚摸上来、尚未归位的一张牌。
        /// 它不参加手牌整理，固定单独摆在手牌最右侧，并与前面的牌留出空隙，
        /// 一眼就能认出「这张是新摸的」；到自己下一轮再重新归位排序。
        /// </summary>
        private bool _isJustDrawn;
        public bool IsJustDrawn
        {
            get { return _isJustDrawn; }
            set { if (_isJustDrawn != value) { _isJustDrawn = value; OnPropertyChanged("IsJustDrawn"); } }
        }

        /// <summary>
        /// 最近刚打出的一张牌（牌河用）。持久状态：它在牌河里被歪着摆放，
        /// 直到本家下一次打牌才「归位」摆正。不用红色描边、也不用闪光。
        /// </summary>
        private bool _justDiscarded;
        public bool JustDiscarded
        {
            get { return _justDiscarded; }
            set
            {
                if (_justDiscarded != value)
                {
                    _justDiscarded = value;
                    OnPropertyChanged("JustDiscarded");
                    OnPropertyChanged("DiscardAngle");
                    OnPropertyChanged("DiscardOffsetX");
                    OnPropertyChanged("DiscardOffsetY");
                }
            }
        }

        /// <summary>
        /// 刚打出那张牌在牌河里的摆放角度（度）。
        /// 用牌 Id 做确定性伪随机，每张歪 9~14°、左右方向不定 —— 像真人随手丢下去
        /// 还没推正，比给牌上色/描边自然得多；归位后变成 0，牌自动摆正。
        /// 做成可绑定属性是因为 RenderTransform 里的 RotateTransform 要绑它。
        /// </summary>
        public double DiscardAngle
        {
            get
            {
                if (!_justDiscarded) return 0;
                long id = _tile.Id;
                double deg = 9 + ((id * 73 + 29) % 11) * 0.5;   // 9° ~ 14°
                return ((id * 37) % 2 == 0) ? deg : -deg;
            }
        }

        /// <summary>
        /// 刚打出的牌与牌河主体拉开的距离（像素）：让它整张「掉」到牌河外侧一点，
        /// 而不是沿着队伍的延长线继续往右挤。
        /// 之前往右推 13px 会撞上侧栏牌河的右边界（一行恰好排满 3 张）被裁掉一角，
        /// 往上抬 5px 又会贴住上一行的牌，两下合起来看着反而更近 —— 这次反过来：
        /// 水平只留很小的错开，主要往没有牌的那一侧（局部 +Y）挪开，明显脱离那一片。
        /// 归位后两个值都变 0，牌自动贴回队伍。
        /// 仍用 RenderTransform 平移而非布局 Margin：改 Margin 会把新牌挤到下一行，
        /// 造成整片牌位跳动。X 之所以设 0：南北牌河 Border 的宽度恰好等于一行牌总宽，
        /// 歪牌若正好落在行末，再往右平移就会被 ClipToBounds 削掉一个角；
        /// 拉开距离完全交给 Y（往下掉）+ 角度，水平不再冒险。
        /// XAML 里南北容器高 160（两行 132 + 28px 余量）、内容左右各留 16px：
        /// 14px 下移 + 旋转约 5px 包围盒增量 = 19px < 28px，旋转横向增量约 7px < 16px，
        /// 四个方向都装得下，且滚动量仍是整行倍数，不会把上一行牌削掉。
        /// </summary>
        public double DiscardOffsetX { get { return 0; } }
        public double DiscardOffsetY { get { return _justDiscarded ? 14 : 0; } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// 单个玩家的视图模型：手牌（可绑定）、牌河、副露。
    /// </summary>
    public class PlayerViewModel : INotifyPropertyChanged
    {
        public int Index { get; }

        // 手牌：可直接绑定到 ItemsControl
        public ObservableCollection<TileViewModel> Hand { get; } = new ObservableCollection<TileViewModel>();

        // 牌河（带状态的对象，便于让「刚打出」那张歪着摆放、下一轮摆正）
        public ObservableCollection<TileViewModel> Discard { get; } = new ObservableCollection<TileViewModel>();

        // 副露
        public ObservableCollection<MeldViewModel> Melds { get; } = new ObservableCollection<MeldViewModel>();

        // 对手不显示具体牌，只显示背面计数
        public ObservableCollection<Tile> BackHand { get; } = new ObservableCollection<Tile>();

        private bool _isCurrentTurn;
        public bool IsCurrentTurn
        {
            get { return _isCurrentTurn; }
            set { if (_isCurrentTurn != value) { _isCurrentTurn = value; OnPropertyChanged("IsCurrentTurn"); } }
        }

        // 金币余额（跨局累计，由 App.Session.Ledger 刷新）；桌面上不直接显示，只在 ToolTip 里给出
        private int _coins;
        public int Coins
        {
            get { return _coins; }
            set { if (_coins != value) { _coins = value; OnPropertyChanged("Coins"); } }
        }

        // 座位名（金币徽章里的身份文字）
        private string _seatName;
        public string SeatName
        {
            get { return _seatName; }
            set { if (_seatName != value) { _seatName = value; OnPropertyChanged("SeatName"); } }
        }

        // 一摞金币的图片：按余额档位换 1~5 层的摞，钱越多摞越高（不要求精确）。
        // 只是字符串属性，RefreshCoins 即使来自引擎回调线程也没有集合跨线程问题。
        private string _coinStackSource;
        public string CoinStackSource
        {
            get { return _coinStackSource; }
            private set { if (_coinStackSource != value) { _coinStackSource = value; OnPropertyChanged("CoinStackSource"); } }
        }

        // 摞旁边显示的精确数量
        private string _coinAmount;
        public string CoinAmount
        {
            get { return _coinAmount; }
            private set { if (_coinAmount != value) { _coinAmount = value; OnPropertyChanged("CoinAmount"); } }
        }

        // 悬停时补一句完整说明
        private string _coinHint;
        public string CoinHint
        {
            get { return _coinHint; }
            private set { if (_coinHint != value) { _coinHint = value; OnPropertyChanged("CoinHint"); } }
        }

        /// <summary>刷新这一家的余额、座位名、金币摞、数字与悬停提示。</summary>
        public void SetCoins(int coins, string seatName)
        {
            Coins = coins;
            SeatName = seatName;
            CoinStackSource = CoinPile.StackSource(coins);
            CoinAmount = CoinPile.AmountText(coins);
            CoinHint = seatName + " · 余额 " + coins + " 币"
                       + (CoinPile.IsBust(coins) ? "（金币耗尽）" : "");
        }

        // 点击手牌请求出牌（由 MainWindow 订阅，转给 GameHost.HumanChooseDiscard）
        public Action<Tile> OnTileDiscardRequested;

        public PlayerViewModel(int index)
        {
            Index = index;
        }

        /// <summary>
        /// 追加一张手牌。justDrawn=true 表示这是「刚摸上来的牌」：
        /// 先把上一巡那张归位，再把这张单独摆到最右侧，不参加排序。
        /// 开局发牌用默认值（13/14 张全部整理好，不标新牌）。
        /// </summary>
        public void AddHandTile(Tile tile, bool justDrawn = false)
        {
            if (justDrawn) CommitDrawnTile();
            var vm = new TileViewModel(tile);
            Hand.Add(vm);
            if (justDrawn) vm.IsJustDrawn = true;
            SortHand();
        }

        /// <summary>开局庄家起手 14 张里多出来的那张，也按「刚摸的牌」单独摆。</summary>
        public void MarkOpeningDrawnTile(Tile tile)
        {
            var vm = Hand.FirstOrDefault(h => ReferenceEquals(h.DiscardTile, tile));
            if (vm == null) return;
            foreach (var h in Hand) h.IsJustDrawn = false;
            vm.IsJustDrawn = true;
            SortHand();
        }

        /// <summary>
        /// 下一轮归位：清掉「刚摸」标记，让这张牌重新参加正常排序。
        /// 由「本家出牌」与「本家下一次摸牌」两处触发，保证标记最多只留一轮。
        /// </summary>
        public void CommitDrawnTile()
        {
            bool had = false;
            foreach (var t in Hand)
            {
                if (t.IsJustDrawn) { t.IsJustDrawn = false; had = true; }
            }
            if (had) SortHand();
        }

        public void RemoveHandTile(Tile tile)
        {
            if (tile == null) return;
            // 优先按实例引用精确移除：必须与引擎移除的是同一张 Tile，
            // 否则手里有对子时会「引擎删 B、UI 删 A」造成实例漂移，
            // 后续点击那张漂移的牌会让引擎 Remove 失败并抛「非法出牌」导致整局卡死。
            var exact = Hand.FirstOrDefault(h => ReferenceEquals(h.DiscardTile, tile));
            if (exact != null)
            {
                Hand.Remove(exact);
                SortHand();
                return;
            }
            // 回退：按牌面移除第一张同款（碰/杠扣牌等 UI 侧无对应实例的场景）
            var vm = Hand.FirstOrDefault(h => h.Kind == tile.Kind);
            if (vm != null) Hand.Remove(vm);
            SortHand();
        }

        /// <summary>
        /// 整理手牌：按花色聚拢、同花色内点数升序。
        /// TileKind 枚举值天然为 万(0-8)→条(9-17)→筒(18-26)→字(27-33)，
        /// 因此按 (int)Kind 排序即可得到「同花色成块、点数从小到大」的排列。
        /// 刚摸的那一摞外的单张（IsJustDrawn）不参与排序，固定留在最右侧 ——
        /// 这正是真实牌桌上把自摸牌单独搁一边的摆法。
        /// 复用原有 TileViewModel 实例（不清空重建），以保留选中态等状态。
        /// </summary>
        public void SortHand()
        {
            var drawn = Hand.FirstOrDefault(t => t.IsJustDrawn);
            var sorted = Hand.Where(t => !t.IsJustDrawn).OrderBy(t => (int)t.Kind).ToList();
            Hand.Clear();
            foreach (var t in sorted) Hand.Add(t);
            if (drawn != null) Hand.Add(drawn);
        }

        public void ClearHand()
        {
            Hand.Clear();
        }

        /// <summary>
        /// 往牌河追加一张。新牌标为「刚打出」→ 歪着摆放，本家上一张的标记同时清除
        /// （角度归 0、自动摆正）。于是每家牌河永远只有最后一张是歪的，下一轮才归位。
        /// </summary>
        public TileViewModel AddDiscard(Tile tile)
        {
            foreach (var old in Discard) old.JustDiscarded = false;
            var vm = new TileViewModel(tile) { JustDiscarded = true };
            Discard.Add(vm);
            return vm;
        }

        /// <summary>
        /// 碰/杠把牌河最后一张收走后，重新把该家现存的最新一张标为「刚打出」，
        /// 避免那张歪着的牌被收走后整片牌河都摆得笔直、分不出最近一张。
        /// </summary>
        public void RefreshLatestDiscard()
        {
            foreach (var t in Discard) t.JustDiscarded = false;
            if (Discard.Count > 0) Discard[Discard.Count - 1].JustDiscarded = true;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// 副露里的一张牌。除了牌面还要带「正面 / 背面」：
    /// 暗杠按真实牌桌的摆法只翻两端两张，中间两张扣着。
    /// </summary>
    public class MeldTileViewModel
    {
        public TileKind Kind { get; set; }
        public bool IsFaceUp { get; set; } = true;

        public MeldTileViewModel() { }

        public MeldTileViewModel(TileKind kind, bool isFaceUp = true)
        {
            Kind = kind;
            IsFaceUp = isFaceUp;
        }
    }

    /// <summary>
    /// 一组副露的视图（3 张碰 / 4 张杠）。
    /// 不依赖 Tile 实例，只存牌面 + 是否正面，够 XAML 直接绑定。
    /// </summary>
    public class MeldViewModel : INotifyPropertyChanged
    {
        public ClaimType Type { get; set; }
        public ObservableCollection<MeldTileViewModel> Tiles { get; } =
            new ObservableCollection<MeldTileViewModel>();

        /// <summary>追加一张（补杠升级已有碰时用，默认正面）。</summary>
        public void AddTile(TileKind kind, bool faceUp = true)
        {
            Tiles.Add(new MeldTileViewModel(kind, faceUp));
        }

        /// <summary>
        /// 按本组牌型补齐整组牌：碰 3 张、杠 4 张。
        /// 暗杠 = 第 1 张和第 4 张正面、中间两张背面；碰 / 明杠 / 补杠全部正面。
        /// </summary>
        public void AddTiles(TileKind kind)
        {
            int count = Type == ClaimType.Pong ? 3 : 4;
            for (int i = 0; i < count; i++)
            {
                bool faceUp = Type != ClaimType.ConcealedKong || i == 0 || i == count - 1;
                Tiles.Add(new MeldTileViewModel(kind, faceUp));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// 游戏状态视图（MVVM 绑定入口）。
    /// </summary>
    public class GameViewModel : INotifyPropertyChanged
    {
        // 四位玩家
        public ObservableCollection<PlayerViewModel> Players { get; } = new ObservableCollection<PlayerViewModel>();

        // 剩余牌墙数量
        private int _wallRemaining;
        public int WallRemaining
        {
            get { return _wallRemaining; }
            set { SetProperty(ref _wallRemaining, value); }
        }

        // 当前回合（由 PlayerTurnStarted 事件驱动刷新，中央「轮到…」绑定它）
        private int _currentPlayer;
        public int CurrentPlayer
        {
            get { return _currentPlayer; }
            set
            {
                if (_currentPlayer == value) return;
                _currentPlayer = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("CurrentPlayer"));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("CurrentPlayerName"));
            }
        }

        /// <summary>
        /// 当前回合者的人话名称（0=自己、1=北、2=东、3=西），
        /// 中央信息区直接绑这个名字，避免把枚举/索引直接显示成「当前 0 家」。
        /// </summary>
        public string CurrentPlayerName
        {
            get
            {
                switch (_currentPlayer)
                {
                    case 0: return "你";
                    case 1: return "北家";
                    case 2: return "东家";
                    default: return "西家";
                }
            }
        }

        // 字幕
        private string _subtitleText;
        public string SubtitleText
        {
            get { return _subtitleText; }
            set { SetProperty(ref _subtitleText, value); }
        }

        /// <summary>
        /// 用账本余额刷新四家。三家座位标签、牌桌中部自己的余额、结算面板四家
        /// 总览都绑同一个 CoinBadge 模板，因此只维护一份「座位名 + 金币摞 +
        /// 精确数字」数据即可，不再拼文字串。
        /// </summary>
        public void RefreshCoins(int[] coins)
        {
            if (coins == null || coins.Length < 4) return;
            string[] names = { "你", "北", "东", "西" };
            for (int p = 0; p < 4; p++)
                Players[p].SetCoins(coins[p], names[p]);
        }

        // 游戏日志
        public ObservableCollection<string> Log { get; } = new ObservableCollection<string>();

        // 正在等待人类操作（碰/杠/胡/过）
        private bool _waitingForHuman;
        public bool WaitingForHuman
        {
            get { return _waitingForHuman; }
            set { SetProperty(ref _waitingForHuman, value); }
        }

        public GameViewModel()
        {
            for (int i = 0; i < 4; i++)
                Players.Add(new PlayerViewModel(i));
        }

        public void AddLog(string text)
        {
            Log.Add(text);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void SetProperty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            if (!System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value))
            {
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}