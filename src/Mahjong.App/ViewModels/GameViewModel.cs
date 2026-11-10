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
        }

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

        // 牌河
        public ObservableCollection<Tile> Discard { get; } = new ObservableCollection<Tile>();

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

        // 点击手牌请求出牌（由 MainWindow 订阅，转给 GameHost.HumanChooseDiscard）
        public Action<Tile> OnTileDiscardRequested;

        public PlayerViewModel(int index)
        {
            Index = index;
        }

        public void AddHandTile(Tile tile)
        {
            Hand.Add(new TileViewModel(tile));
        }

        public void RemoveHandTile(Tile tile)
        {
            var vm = Hand.FirstOrDefault(h => h.Kind == tile.Kind);
            if (vm != null) Hand.Remove(vm);
        }

        public void ClearHand()
        {
            Hand.Clear();
        }

        public void AddDiscard(Tile tile)
        {
            Discard.Add(tile);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// 一组副露的视图（3 张碰 / 4 张杠）。
    /// </summary>
    public class MeldViewModel : INotifyPropertyChanged
    {
        public ClaimType Type { get; set; }
        public ObservableCollection<Tile> Tiles { get; } = new ObservableCollection<Tile>();

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

        // 当前回合
        private int _currentPlayer;
        public int CurrentPlayer
        {
            get { return _currentPlayer; }
            private set { SetProperty(ref _currentPlayer, value); }
        }

        // 字幕
        private string _subtitleText;
        public string SubtitleText
        {
            get { return _subtitleText; }
            set { SetProperty(ref _subtitleText, value); }
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