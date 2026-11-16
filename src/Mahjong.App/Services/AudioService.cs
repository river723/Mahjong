using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Text;
using System.Threading;
using Mahjong.Core.Game;
using Mahjong.Core.Tiles;

namespace Mahjong.App.Services
{
    /// <summary>
    /// 配音 / 音效服务：预生成语音包优先，实时 TTS 兜底，全部在后台线程串行播放。
    ///
    /// 为什么这么设计：
    /// 1) 出牌/碰杠很密集，实时 TTS 每次要现合成，会拖慢节奏且语音互相打架；
    ///    棋牌应用的通行方案是「离线语音包 + 运行时兜底」，这里照此实现。
    /// 2) 播放走单条后台队列，绝不占用 UI 线程；一条播报内的多个片段连续播放。
    /// 3) 新的出牌播报会顶掉队列里还没轮到的旧出牌播报，避免牌局快进时
    ///    语音越积越多、越念越滞后；胡牌/流局则先清空队列再独占播放。
    /// 4) 用 winmm PlaySound + SND_NODEFAULT：素材缺失时保持静默，
    ///    不会像 SoundPlayer 那样在路径无效时冒出系统默认提示音。
    /// </summary>
    public sealed class AudioService : IDisposable
    {
        [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
        private static extern bool PlaySound(string pszSound, IntPtr hmod, uint flags);

        private const uint SND_SYNC = 0x0000;
        private const uint SND_NODEFAULT = 0x0002;
        private const uint SND_FILENAME = 0x00020000;

        private sealed class Clip
        {
            public string File;      // wav 绝对路径
            public string Text;      // 语音包缺失时的 TTS 文本
            public bool IsVoice;     // true=语音（受 VoiceEnabled 控制），false=音效
        }

        /// <summary>一次完整播报（音效 + 若干语音片段），抢占时整组一起取舍。</summary>
        private sealed class Announcement
        {
            public readonly List<Clip> Clips;
            public readonly bool Interruptible;

            public Announcement(List<Clip> clips, bool interruptible)
            {
                Clips = clips;
                Interruptible = interruptible;
            }
        }

        private readonly Queue<Announcement> _queue = new Queue<Announcement>();
        private readonly object _gate = new object();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly Thread _worker;
        private readonly string _voiceDir;
        private readonly string _soundDir;

        private SpeechSynthesizer _tts;   // 只在 worker 线程创建/使用，避免跨线程 COM 问题
        private volatile bool _stopped;
        private int _generation;
        private int _playedClips;
        private int _failedClips;

        /// <summary>诊断计数：已成功播放的片段数（配合 audiotest 自检）。</summary>
        public int PlayedClips { get { return _playedClips; } }
        /// <summary>诊断计数：播放失败的片段数。</summary>
        public int FailedClips { get { return _failedClips; } }
        private bool _voiceEnabled = true;
        private bool _soundEnabled = true;
        private bool _announceSeat;
        private bool _announceOwnDiscard = true;

        // === 可在界面上切换的设置 ===
        /// <summary>是否念牌面 / 动作词。</summary>
        public bool VoiceEnabled { get { return _voiceEnabled; } set { _voiceEnabled = value; Save(); } }
        /// <summary>是否播放拍牌、碰杠、胡牌等音效。</summary>
        public bool SoundEnabled { get { return _soundEnabled; } set { _soundEnabled = value; Save(); } }
        /// <summary>播报时是否加「北家」这类席位前缀（默认关，保持节奏）。</summary>
        public bool AnnounceSeat { get { return _announceSeat; } set { _announceSeat = value; Save(); } }
        /// <summary>自己出牌时是否也念牌面（默认念）。</summary>
        public bool AnnounceOwnDiscard { get { return _announceOwnDiscard; } set { _announceOwnDiscard = value; Save(); } }

        public AudioService()
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            _voiceDir = Path.Combine(root, "Assets", "Voice");
            _soundDir = Path.Combine(root, "Assets", "Sounds");
            Load();

            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Name = "MahjongAudio";
            _worker.Start();
        }

        // ==================== 对局语义播报 ====================

        /// <summary>有人打出一张牌：拍牌声 + 念牌面（其它三家必念，自己可配置）。</summary>
        public void Discard(int playerIndex, TileKind kind)
        {
            bool speak = playerIndex != 0 || _announceOwnDiscard;
            var clips = new List<Clip>();
            AddSfx(clips, "discard");
            if (speak)
            {
                if (_announceSeat) AddVoice(clips, "seat_" + playerIndex);
                AddVoice(clips, VoicePackGenerator.TileKey(kind));
            }
            Enqueue(clips, interruptible: true);
        }

        /// <summary>碰 / 杠成立：重击音效 + 动作词。</summary>
        public void Claim(string action, int playerIndex)
        {
            bool isKong = action != null && action.IndexOf("杠") >= 0;
            var clips = new List<Clip>();
            AddSfx(clips, isKong ? "kong" : "pong");
            if (_announceSeat) AddVoice(clips, "seat_" + playerIndex);
            // 语音包里有专门的「暗杠」词条；补杠暂用通用的「杠」。
            string voiceKey = action == "暗杠" ? "act_ckong" : (isKong ? "act_kong" : "act_pong");
            AddVoice(clips, voiceKey);
            Enqueue(clips, interruptible: true);
        }

        /// <summary>轮到玩家决策（可碰杠胡 / 可自摸胡）时的轻提示音。</summary>
        public void Prompt()
        {
            var clips = new List<Clip>();
            AddSfx(clips, "prompt");
            Enqueue(clips, interruptible: false);
        }

        /// <summary>开局播报。</summary>
        public void Deal()
        {
            var clips = new List<Clip>();
            AddVoice(clips, "act_deal");
            Enqueue(clips, interruptible: false);
        }

        /// <summary>
        /// 终局播报：先清空积压的出牌语音，再播「谁 + 胡了/自摸/杠上开花 + 胡哪张」或「流局」。
        /// 不念结算里的分数串，那串数字用字幕和结算面板看更清楚。
        /// </summary>
        public void GameEnd(GameResult result, bool kongWin)
        {
            Clear();
            var clips = new List<Clip>();
            if (result == null || result.IsDraw)
            {
                AddSfx(clips, "drawgame");
                AddVoice(clips, "act_draw");
            }
            else
            {
                AddSfx(clips, "win");
                AddVoice(clips, "seat_" + result.WinnerIndex);
                if (kongWin) AddVoice(clips, "act_kongwin");
                else if (result.IsSelfDraw) AddVoice(clips, "act_selfdraw");
                else AddVoice(clips, "act_win");
                if (result.WinningTile != null)
                    AddVoice(clips, VoicePackGenerator.TileKey(result.WinningTile.Kind));
            }
            Enqueue(clips, interruptible: false);
        }

        /// <summary>动态文本兜底播报（语音包里没有的内容，如结算描述）。</summary>
        public void SpeakText(string text)
        {
            if (!_voiceEnabled || string.IsNullOrWhiteSpace(text)) return;
            var clips = new List<Clip> { new Clip { Text = text, IsVoice = true } };
            Enqueue(clips, interruptible: false);
        }

        /// <summary>清空排队中的播报（结算、重开局时用）。</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _queue.Clear();
                _generation++;
            }
        }

        // ==================== 内部实现 ====================

        private string VoicePath(string key) { return Path.Combine(_voiceDir, key + ".wav"); }
        private string SoundPath(string key) { return Path.Combine(_soundDir, key + ".wav"); }

        private void AddVoice(List<Clip> clips, string key)
        {
            if (!_voiceEnabled) return;
            clips.Add(new Clip
            {
                File = VoicePath(key),
                Text = VoicePackGenerator.TextOf(key),
                IsVoice = true
            });
        }

        private void AddSfx(List<Clip> clips, string key)
        {
            if (!_soundEnabled) return;
            clips.Add(new Clip { File = SoundPath(key), IsVoice = false });
        }

        private void Enqueue(List<Clip> clips, bool interruptible)
        {
            if (clips == null || clips.Count == 0) return;
            lock (_gate)
            {
                if (interruptible)
                {
                    // 只丢弃尚未播到的、可被抢占的播报；胡牌等重要播报保留
                    int n = _queue.Count;
                    for (int i = 0; i < n; i++)
                    {
                        var a = _queue.Dequeue();
                        if (!a.Interruptible) _queue.Enqueue(a);
                    }
                }
                _queue.Enqueue(new Announcement(clips, interruptible));
                while (_queue.Count > 24) _queue.Dequeue(); // 极端情况下的兜底
            }
            _signal.Set();
        }

        private void WorkerLoop()
        {
            while (!_stopped)
            {
                Announcement announcement = null;
                int generation;
                lock (_gate)
                {
                    if (_queue.Count > 0) announcement = _queue.Dequeue();
                    generation = _generation;
                }
                if (announcement == null)
                {
                    _signal.WaitOne(250);
                    continue;
                }

                foreach (Clip clip in announcement.Clips)
                {
                    lock (_gate) { if (_generation != generation || _stopped) break; }
                    try { PlayClip(clip); }
                    catch { /* 单个素材失败不影响后续播报 */ }
                }
            }
        }

        private void PlayClip(Clip clip)
        {
            // 开关在播放时再判一次，用户中途关掉配音能立刻生效
            if (clip.IsVoice && !_voiceEnabled) return;
            if (!clip.IsVoice && !_soundEnabled) return;

            if (!string.IsNullOrEmpty(clip.File) && File.Exists(clip.File))
            {
                bool ok = PlaySound(clip.File, IntPtr.Zero, SND_FILENAME | SND_SYNC | SND_NODEFAULT);
                if (ok) Interlocked.Increment(ref _playedClips);
                else Interlocked.Increment(ref _failedClips);
                return;
            }
            // 语音包缺失：用本机中文 TTS 兜底念文本；音效缺失则直接静默
            if (clip.IsVoice && !string.IsNullOrEmpty(clip.Text))
            {
                EnsureTts();
                if (_tts != null)
                {
                    _tts.Speak(clip.Text);
                    Interlocked.Increment(ref _playedClips);
                }
                else Interlocked.Increment(ref _failedClips);
            }
        }

        private void EnsureTts()
        {
            if (_tts != null) return;
            try
            {
                var synth = new SpeechSynthesizer();
                foreach (InstalledVoice v in synth.GetInstalledVoices())
                {
                    if (!v.Enabled) continue;
                    string name = v.VoiceInfo.Name;
                    if (name.IndexOf("Huihui", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Yaoyao", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Kangkang", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("zh-CN", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (v.VoiceInfo.Culture != null && v.VoiceInfo.Culture.Name.StartsWith("zh")))
                    {
                        synth.SelectVoice(name);
                        break;
                    }
                }
                synth.Rate = 1;
                synth.Volume = 100;
                _tts = synth;
            }
            catch { _tts = null; }
        }

        // ==================== 设置持久化 ====================

        private static string SettingsPath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mahjong");
                return Path.Combine(dir, "audio.ini");
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                foreach (string line in File.ReadAllLines(SettingsPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    bool v = line.Substring(eq + 1).Trim() == "1";
                    if (k == "voice") _voiceEnabled = v;
                    else if (k == "sound") _soundEnabled = v;
                    else if (k == "seat") _announceSeat = v;
                    else if (k == "own") _announceOwnDiscard = v;
                }
            }
            catch { /* 配置损坏时用默认值 */ }
        }

        private void Save()
        {
            try
            {
                string path = SettingsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var sb = new StringBuilder();
                sb.AppendLine("voice=" + (_voiceEnabled ? "1" : "0"));
                sb.AppendLine("sound=" + (_soundEnabled ? "1" : "0"));
                sb.AppendLine("seat=" + (_announceSeat ? "1" : "0"));
                sb.AppendLine("own=" + (_announceOwnDiscard ? "1" : "0"));
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch { /* 无写权限时保持本次会话设置即可 */ }
        }

        public void Dispose()
        {
            _stopped = true;
            Clear();
            _signal.Set();
            try { _worker.Join(600); } catch { }
            try { if (_tts != null) { _tts.Dispose(); _tts = null; } } catch { }
            try { _signal.Dispose(); } catch { }
        }
    }
}
