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

            // 诊断日志目录写死为 E:\cc_study\mj：启动时预先建好，目录缺失的程序不再秒崩。
            // （GameLog 每次写入前还会再自愈一次，双保险。）
            GameLog.EnsureDir();

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
            // 开发者开关：把同一个词条按 4 种播法依次播一遍，用于定位「一个词只说一半」
            if (e.Args.Any(a => string.Equals(a, "--voiceab", StringComparison.OrdinalIgnoreCase)))
            {
                RunVoiceProbeTest();
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

        /// <summary>
        /// 诊断：把「开局」按 4 种播法依次播一遍，定位「一个词只说一半」到底出在哪一层。
        /// 应用层已经证实是完整提交的（--audiotest 的 play_ms 实测 ≥ 音频时长），
        /// 所以只剩两种可能：播完就关句柄，蓝牙链路里还没送出去的那截被丢了（补白能救）；
        /// 或者 16kHz 素材在设备的 48kHz 混音格式上重采样出问题（重采样能救）。
        /// </summary>
        private static void RunVoiceProbeTest()
        {
            var sb = new StringBuilder();
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_voiceab.log");
            var cases = new[]
            {
                new { Index = 1, Pad = 0,   Rs = false, Sink = true  },
                new { Index = 2, Pad = 0,   Rs = false, Sink = false },
                new { Index = 3, Pad = 300, Rs = false, Sink = false }
            };
            try
            {
                using (var audio = new AudioService())
                {
                    sb.AppendLine("# 依次播放 3 遍「开局」，每遍之间停 1.6 秒");
                    sb.AppendLine("# 1 = 常驻流（新方案，不补白，应该完整）");
                    sb.AppendLine("# 2 = SoundPlayer 直接播（旧行为，蓝牙上应只剩「开」）");
                    sb.AppendLine("# 3 = SoundPlayer + 组尾补白 300ms（上一轮验证过的兜底，应该完整）");
                    sb.AppendLine("# 听完回一句：哪几遍是完整的「开局」");
                    foreach (var c in cases)
                    {
                        sb.AppendLine("--- 第 " + c.Index + " 遍即将播放（sink=" + c.Sink + " pad=" + c.Pad + "） ---");
                        File.WriteAllText(logPath, sb.ToString());   // 先落盘，边听边能看
                        audio.SetSink(c.Sink);
                        // 关掉常驻流后要等设备真的空闲下来，否则测的还是「热」链路
                        if (!c.Sink) Thread.Sleep(1500);
                        int expected, elapsed;
                        bool ok = audio.PlayProbe("act_deal", c.Pad, c.Rs, c.Sink, out expected, out elapsed);
                        sb.AppendLine("variant=" + c.Index + " sink=" + c.Sink + " pad=" + c.Pad
                            + " ok=" + ok + " expected=" + expected + "ms elapsed=" + elapsed + "ms");
                        Thread.Sleep(1600);
                    }
                    sb.AppendLine("done");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("EX=" + ex);
            }
            File.WriteAllText(logPath, sb.ToString());
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
                    sb.AppendLine("voice=" + audio.VoiceEnabled + " sound=" + audio.SoundEnabled
                        + " seat=" + audio.AnnounceSeat + " sink=" + audio.SinkActive
                        + " keepalive=" + audio.KeepAliveActive);
                    audio.Deal();                              // 开局
                    Drain(audio, sb, "deal");
                    audio.Discard(1, TileKind.Man5);           // 北家 五万
                    Drain(audio, sb, "disc1");
                    audio.Discard(3, TileKind.Zhong);          // 西家 红中
                    Drain(audio, sb, "disc2");
                    audio.Claim("碰", 2);                      // 东家碰
                    Drain(audio, sb, "claim");
                    audio.Prompt();                            // 提示音
                    Drain(audio, sb, "prompt");
                    // 快节奏压力：连续 5 次出牌不等播完，验证不无限积压、不掐断整句，
                    // 且积压时会自动省掉席位前缀把播报压回节奏内。
                    int beforeBurst = audio.PlayedClips;
                    for (int i = 0; i < 5; i++) audio.Discard(1, TileKind.Man5);
                    Drain(audio, sb, "burst");
                    sb.AppendLine("burst_clips=" + (audio.PlayedClips - beforeBurst));
                    var result = new GameResult
                    {
                        WinnerIndex = 0,
                        LoserIndex = -1,
                        IsSelfDraw = true,
                        WinningTile = new Tile(0, TileKind.Tong3),
                        Scores = new int[4]
                    };
                    audio.GameEnd(result, false);              // 南家 自摸 三筒
                    Drain(audio, sb, "gameend");
                    sb.AppendLine("played=" + audio.PlayedClips);
                    sb.AppendLine("failed=" + audio.FailedClips);
                    sb.AppendLine("coalesced=" + audio.CoalescedGroups);
                    sb.AppendLine("fallback=" + audio.FallbackClips);
                    sb.AppendLine("truncated=" + audio.TruncatedPlays);
                    sb.AppendLine("last_play_ms=" + audio.LastExpectedMs + "/" + audio.LastElapsedMs);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("EX=" + ex);
            }
            File.WriteAllText(logPath, sb.ToString());
        }

        /// <summary>自检：等这组真正播完，把实测耗时和累计计数写进日志。</summary>
        private static void Drain(AudioService audio, StringBuilder sb, string name)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool idle = audio.WaitForIdle(5000);
            sw.Stop();
            sb.AppendLine(name + " waited=" + sw.ElapsedMilliseconds + "ms idle=" + idle
                + " played=" + audio.PlayedClips + " groups=" + audio.CoalescedGroups
                + " play_ms=" + audio.LastExpectedMs + "/" + audio.LastElapsedMs);
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
