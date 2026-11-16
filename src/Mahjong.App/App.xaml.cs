using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using Mahjong.App.Services;
using Mahjong.Core.Game;
using Mahjong.Core.Tiles;

namespace Mahjong.App
{
    /// <summary>App.xaml 的代码逻辑。</summary>
    public partial class App : Application
    {
        /// <summary>全场唯一的会话状态：金币账本活过每一局新建的 MainWindow。</summary>
        public static MatchSession Session { get; } = new MatchSession();

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 开发者开关：离线生成语音包，不启动游戏窗口。
            //   Mahjong.App.exe --genvoice --voice-dir=E:\cc_study\mj\src\Mahjong.App\Assets\Voice
            if (e.Args.Any(a => string.Equals(a, "--genvoice", StringComparison.OrdinalIgnoreCase)))
            {
                RunVoiceGenerator(e.Args);
                Shutdown();
                return;
            }
            // 开发者开关：不打开窗口，依次播一遍各类配音/音效并写自检日志
            if (e.Args.Any(a => string.Equals(a, "--audiotest", StringComparison.OrdinalIgnoreCase)))
            {
                RunAudioSelfTest();
                Shutdown();
                return;
            }

            var window = new MainWindow();
            window.Show();
        }

        private static void RunVoiceGenerator(string[] args)
        {
            string dir = GetArg(args, "--voice-dir") ??
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Voice");
            string voice = GetArg(args, "--voice");

            try
            {
                string log;
                int count = VoicePackGenerator.Generate(dir, voice, out log);
                File.WriteAllText(Path.Combine(dir, "_genvoice.log"),
                    "OK count=" + count + Environment.NewLine + log);
            }
            catch (Exception ex)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "_genvoice.log"), "FAILED" + Environment.NewLine + ex);
                }
                catch { }
            }
        }

        /// <summary>挨个播一遍开局、出牌、碰、提示、胡牌，验证素材与播放链路。</summary>
        private static void RunAudioSelfTest()
        {
            var sb = new StringBuilder();
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_audiotest.log");
            try
            {
                using (var audio = new AudioService())
                {
                    sb.AppendLine("voice=" + audio.VoiceEnabled + " sound=" + audio.SoundEnabled);
                    audio.Deal();                              // 开局
                    Thread.Sleep(900);
                    audio.Discard(1, TileKind.Man5);           // 北家 五万
                    Thread.Sleep(900);
                    audio.Discard(3, TileKind.Zhong);          // 西家 红中
                    Thread.Sleep(900);
                    audio.Claim("碰", 2);                      // 东家碰
                    Thread.Sleep(900);
                    audio.Prompt();                            // 提示音
                    Thread.Sleep(600);
                    var result = new GameResult
                    {
                        WinnerIndex = 0,
                        LoserIndex = -1,
                        IsSelfDraw = true,
                        WinningTile = new Tile(0, TileKind.Tong3),
                        Scores = new int[4]
                    };
                    audio.GameEnd(result, false);              // 南家 自摸 三筒
                    Thread.Sleep(3500);
                    sb.AppendLine("played=" + audio.PlayedClips);
                    sb.AppendLine("failed=" + audio.FailedClips);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("EX=" + ex);
            }
            File.WriteAllText(logPath, sb.ToString());
        }

        private static string GetArg(string[] args, string name)
        {
            foreach (string a in args)
            {
                bool match = string.Equals(a, name, StringComparison.OrdinalIgnoreCase) ||
                    a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase);
                if (!match) continue;
                int eq = a.IndexOf('=');
                return eq > 0 ? a.Substring(eq + 1).Trim('"') : string.Empty;
            }
            return null;
        }
    }
}
