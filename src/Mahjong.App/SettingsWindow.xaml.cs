using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Mahjong.App.Services;
using Mahjong.Core.Tiles;

namespace Mahjong.App
{
    /// <summary>
    /// 「设置」窗口：声音 / 每人初始金币 / 开始难度。
    ///
    /// 生效规则：
    /// 1) 声音四个开关直接写 AudioService 的属性，setter 即时生效并落盘（audio.ini）；
    ///    因为「试听」会临时应用，打开时留一份快照，点取消或右上角 X 时还原。
    /// 2) 初始金币写 GameSettings.Current → App.Session.ApplySettings() 同步到 RuleConfig，
    ///    进行中的账本余额不动，下一次 CoinLedger.Reset()（开新的一场）才用新值。
    /// 3) 难度只保存配置；是否立刻重置难度进度由 MainWindow 决定（命令行固定档位优先）。
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly AudioService _audio;
        private readonly bool _difficultyLocked;

        // 打开时的声音快照：取消时回滚（声音是即时生效的，没有「暂存」这一层）
        private readonly bool _snapVoice;
        private readonly bool _snapSound;
        private readonly bool _snapSeat;
        private readonly bool _snapOwn;

        /// <summary>用户点了「保存并开新的一场」。</summary>
        public bool RequestRestart { get; private set; }

        /// <summary>难度相关设置是否改动：改动后要重置连胜/局数，从新档位重新起步。</summary>
        public bool DifficultyChanged { get; private set; }

        /// <param name="audio">当前窗口的音频服务（每局新建，不能缓存）。</param>
        /// <param name="difficultyLocked">启动参数固定了难度时置 true：难度控件只读并给出说明。</param>
        public SettingsWindow(AudioService audio, bool difficultyLocked = false)
        {
            InitializeComponent();
            _audio = audio;
            _difficultyLocked = difficultyLocked;

            _snapVoice = audio != null && audio.VoiceEnabled;
            _snapSound = audio != null && audio.SoundEnabled;
            _snapSeat = audio != null && audio.AnnounceSeat;
            _snapOwn = audio != null && audio.AnnounceOwnDiscard;

            VoiceBox.IsChecked = _snapVoice;
            SfxBox.IsChecked = _snapSound;
            SeatBox.IsChecked = _snapSeat;
            OwnBox.IsChecked = _snapOwn;

            var s = GameSettings.Current;
            CoinsBox.Text = s.InitialCoins.ToString(CultureInfo.InvariantCulture);
            Diff0.IsChecked = s.StartDifficulty <= 0;
            Diff1.IsChecked = s.StartDifficulty == 1;
            Diff2.IsChecked = s.StartDifficulty >= 2;
            AutoLevelBox.IsChecked = s.AutoLevelUp;
            CoinsRange.Text = string.Format("枚（{0} ~ {1}）",
                GameSettings.MinInitialCoins, GameSettings.MaxInitialCoins);
            UpdateDiffHint();

            if (_difficultyLocked)
            {
                Diff0.IsEnabled = Diff1.IsEnabled = Diff2.IsEnabled = false;
                AutoLevelBox.IsEnabled = false;
                DifficultyLockHint.Visibility = Visibility.Visible;
            }
        }

        private int SelectedDifficulty()
        {
            if (Diff2.IsChecked == true) return 2;
            if (Diff1.IsChecked == true) return 1;
            return 0;
        }

        private void UpdateDiffHint()
        {
            if (DiffHint == null) return;
            switch (SelectedDifficulty())
            {
                case 2:
                    DiffHint.Text = "大师：AI 更积极碰杠，并在能听牌的候选里优先选进张最多的牌。";
                    break;
                case 1:
                    DiffHint.Text = "较真：AI 出牌更较真、更愿意碰杠，适合有点基础的玩家。";
                    break;
                default:
                    DiffHint.Text = "普通：AI 常规出牌，适合熟悉规则、轻松玩两局。";
                    break;
            }
        }

        private void Difficulty_Changed(object sender, RoutedEventArgs e) { UpdateDiffHint(); }

        private void DigitsOnly(object sender, TextCompositionEventArgs e)
        {
            foreach (char c in e.Text)
            {
                if (c < '0' || c > '9') { e.Handled = true; return; }
            }
        }

        private void Preset_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn != null) CoinsBox.Text = Convert.ToString(btn.Tag, CultureInfo.InvariantCulture);
        }

        /// <summary>恢复默认只改界面，仍要点「保存」才落盘。</summary>
        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            VoiceBox.IsChecked = true;
            SfxBox.IsChecked = true;
            SeatBox.IsChecked = false;
            OwnBox.IsChecked = true;
            CoinsBox.Text = GameSettings.DefaultInitialCoins.ToString(CultureInfo.InvariantCulture);
            Diff0.IsChecked = true;
            AutoLevelBox.IsChecked = true;
            UpdateDiffHint();
        }

        private void Preview_Click(object sender, RoutedEventArgs e)
        {
            ApplyAudioFromUi();
            if (_audio == null) return;
            _audio.Prompt();
            _audio.Discard(1, TileKind.Man5);
        }

        private void ApplyAudioFromUi()
        {
            if (_audio == null) return;
            _audio.VoiceEnabled = VoiceBox.IsChecked == true;
            _audio.SoundEnabled = SfxBox.IsChecked == true;
            _audio.AnnounceSeat = SeatBox.IsChecked == true;
            _audio.AnnounceOwnDiscard = OwnBox.IsChecked == true;
        }

        private void RestoreAudioSnapshot()
        {
            if (_audio == null) return;
            _audio.VoiceEnabled = _snapVoice;
            _audio.SoundEnabled = _snapSound;
            _audio.AnnounceSeat = _snapSeat;
            _audio.AnnounceOwnDiscard = _snapOwn;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; }

        private void Save_Click(object sender, RoutedEventArgs e) { SaveAndClose(false); }

        private void SaveAndRestart_Click(object sender, RoutedEventArgs e) { SaveAndClose(true); }

        private void SaveAndClose(bool restart)
        {
            int? coins = GameSettings.TryParseInitialCoins(CoinsBox.Text);
            if (!coins.HasValue)
            {
                MessageBox.Show("初始金币请填 " + GameSettings.MinInitialCoins + " ~ "
                    + GameSettings.MaxInitialCoins + " 之间的整数。", "设置未保存",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                CoinsBox.Focus();
                CoinsBox.SelectAll();
                return;
            }

            ApplyAudioFromUi();

            var s = GameSettings.Current;
            int difficulty = SelectedDifficulty();
            bool autoLevel = AutoLevelBox.IsChecked == true;
            DifficultyChanged = s.StartDifficulty != difficulty || s.AutoLevelUp != autoLevel;

            s.InitialCoins = coins.Value;
            s.StartDifficulty = difficulty;
            s.AutoLevelUp = autoLevel;
            s.Save();
            // 规则对象与账本是同一个引用：下一场 Reset 时就会按新金币发牌。
            App.Session.ApplySettings();

            RequestRestart = restart;
            DialogResult = true;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // 点 X / 取消 / Esc：把试听时临时应用的声音开关还原。
            if (DialogResult != true) RestoreAudioSnapshot();
            base.OnClosing(e);
        }
    }
}
