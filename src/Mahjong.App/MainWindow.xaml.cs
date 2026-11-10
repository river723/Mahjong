using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Mahjong.App.Controls;
using Mahjong.App.Services;
using Mahjong.App.ViewModels;
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

        public MainWindow()
        {
            InitializeComponent();

            // 创建 MVVM 和 GameHost
            var rules = new RuleConfig();
            var viewModel = new GameViewModel();
            _viewModelRef = viewModel;
            _host = new GameHost(rules, seed: 1, viewModel);

            // 将 viewModel 设置为 DataContext
            this.DataContext = viewModel;

            // 连接 GameHost 的回调
            _host.OnShowClaimButtons = options =>
            {
                viewModel.Log.Add("可声明按钮：" + string.Join(",", options.Select(o => o.Type.ToString())));
            };
            _host.OnHideClaimButtons = () => { };
            _host.OnShowResultOverlay = message =>
            {
                MessageBox.Show(message, "游戏结果");
            };
            _host.OnSubtitleChanged = subtitle =>
            {
                viewModel.SubtitleText = subtitle;
            };

            // 订阅 GameHost 引擎事件，把牌数据同步到 ViewModel（供 UI 绑定显示）
            _host.GameStarted += (playerIndex, message) =>
            {
                try
                {
                    System.IO.File.AppendAllText(@"e:\cc_study\mj\events.txt",
                        DateTime.Now.ToString("HH:mm:ss.fff") + " [UI] GameStarted handler entered\n");
                    // 初始发牌：从引擎状态读出各家起手 13/14 张（仅读一次，后续增量靠 TileDrawn）
                    for (int p = 0; p < 4; p++)
                    {
                        var vmPlayer = viewModel.Players[p];
                        int n = 0;
                        foreach (var tile in _host.GetPlayerTiles(p))
                        {
                            vmPlayer.AddHandTile(tile);
                            n++;
                        }
                        System.IO.File.AppendAllText(@"e:\cc_study\mj\events.txt",
                            DateTime.Now.ToString("HH:mm:ss.fff") + " [UI] p=" + p + " hand=" + n + "\n");
                    }
                    System.IO.File.AppendAllText(@"e:\cc_study\mj\events.txt",
                        DateTime.Now.ToString("HH:mm:ss.fff") + " [UI] GameStarted handler done, Players[0].Hand.Count=" +
                        viewModel.Players[0].Hand.Count + "\n");
                }
                catch (Exception ex)
                {
                    System.IO.File.AppendAllText(@"e:\cc_study\mj\events.txt",
                        DateTime.Now.ToString("HH:mm:ss.fff") + " [UI] GameStarted handler EX: " + ex + "\n");
                }
            };
            _host.TileDrawn += (playerIndex, tile, message) =>
            {
                // 摸牌是「新」一张：追加到已有手牌，不清空
                viewModel.Players[playerIndex].AddHandTile(tile);
            };
            _host.TileDiscarded += (playerIndex, tile, message) =>
            {
                viewModel.Players[playerIndex].AddDiscard(tile);
                // 出牌后从手牌移除对应那张（人类方）
                if (playerIndex == 0)
                    viewModel.Players[0].RemoveHandTile(tile);
            };

            // 人类点击手牌出牌
            viewModel.Players[0].OnTileDiscardRequested += tile => _host.HumanChooseDiscard(tile);

            // 启动游戏（异步）
            _ = _host.StartAsync();
        }

        /// <summary>手牌点击：把该牌交给人类 Agent 出牌（仅当轮到自己时有效）。</summary>
        private void HandTile_Click(object sender, RoutedEventArgs e)
        {
            // 从 ContentPresenter 的 DataContext 取 TileViewModel
            if (sender is System.Windows.Controls.ContentPresenter cp &&
                cp.DataContext is TileViewModel tv)
            {
                _viewModelRef.Players[0].OnTileDiscardRequested?.Invoke(tv.DiscardTile);
            }
        }

        private void PongButton_Click(object sender, EventArgs e)
        {
            _host.HumanChooseClaim(ClaimOption.Of(ClaimType.Pong, TileKind.Man1));
        }

        private void KongButton_Click(object sender, EventArgs e)
        {
            _host.HumanChooseClaim(ClaimOption.Of(ClaimType.Kong, TileKind.Man1));
        }

        private void WinButton_Click(object sender, EventArgs e)
        {
            _host.HumanChooseClaim(ClaimOption.Of(ClaimType.Win, TileKind.Man1));
        }

        private void PassButton_Click(object sender, EventArgs e)
        {
            _host.HumanChooseClaim(null);
        }
    }
}