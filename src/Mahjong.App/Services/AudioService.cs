using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
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
    /// 2) 播放走单条后台队列，绝不占用 UI 线程。
    /// 3) 【关键】一条播报里的多个 wav 在内存里拼成一整段 PCM，用 SoundPlayer 一次性
    ///    提交播放。原来 winmm PlaySound 每次调用都重开波形设备（每段 110~200ms 空档），
    ///    而且它是进程级单一播放槽、内部按小块缓冲馈送，UI 繁忙时短词会被从中间掐断
    ///    （「开局」只念出「开」）。SoundPlayer 用自己的 waveOut 句柄整段提交，不会欠载，
    ///    也不会被进程里别的 PlaySound 打断；片段之间只留 25~35ms 气口。
    /// 4) 队列只限制积压量，不再一有新出牌就把排队中的播报全清空；Clear() 也只清
    ///    队列，不打断已经开播的那一组，避免整句话念到一半被掐断。检测到积压时
    ///    会自动省掉可选的席位前缀，把播报压回出牌节奏内（开了席位前缀时一条 1.3s，
    ///    而出牌间隔只有 0.8s）。
    /// 5) 播放加进程级锁：保证同一时刻只有一组在播，重开局时新旧窗口的 worker
    ///    不会把声音叠在一起或互相切碎。
    /// 6) 只在 SoundPlayer 不可用时退回 winmm PlaySound（SND_NODEFAULT 保证素材缺失时静默）。
    /// 7) 【蓝牙】A2DP 有 150~300ms 传输延迟，一句播完就关掉播放流的话，还在链路里的
    ///    尾巴会被驱动直接丢掉 —— 552ms 的「开局」在蓝牙耳机上只剩「开」（扬声器正常）。
    ///    所以常驻一条静音保活流，让设备上始终有流在跑，链路不拆、句尾才送得出去；
    ///    另外每条播报前垫 80ms 静音，冷启动被编码器预热吞掉的是静音而不是字头。
    ///    不想要这份开销可在 audio.ini 写 keepalive=0。
    /// </summary>
    public sealed class AudioService : IDisposable
    {
        [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
        private static extern bool PlaySound(string pszSound, IntPtr hmod, uint flags);

        [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
        private static extern bool PlaySoundMem(IntPtr pszSound, IntPtr hmod, uint flags);

        private const uint SND_SYNC = 0x0000;
        private const uint SND_NODEFAULT = 0x0002;
        private const uint SND_MEMORY = 0x0004;
        private const uint SND_FILENAME = 0x00020000;

        /// <summary>排队等待的可抢占播报上限；超出时丢最旧一条，避免越念越滞后。</summary>
        private const int MaxQueuedInterruptible = 2;
        /// <summary>语音片段之间的气口（毫秒）；音效后面留得稍长，免得拍牌声咬住字头。</summary>
        private const int GapMsVoice = 25;
        private const int GapMsAfterSfx = 35;

        /// <summary>
        /// 组尾补白（毫秒）。蓝牙耳机（A2DP）会在「这条播放流结束」时丢掉最后一段数据，
        /// 实测 552ms 的「开局」只剩「开」（扬声器上正常）；只让链路保活也不够，必须让
        /// 语音后面再接一段静音，让被丢的落在静音上。300ms 是用户实测有效的最小值附近，
        /// 扬声器上听不到差别（就是句尾多了 0.3 秒静音）。audio.ini 写 tailpad=0 可关。
        /// </summary>
        private int _tailPadMs = DefaultTailPadMs;

        private const int DefaultTailPadMs = 300;

        /// <summary>
        /// 播报音头补齐（毫秒）：冷启动的蓝牙流前 100ms 左右会被编码器预热吞掉，
        /// 留一小段静音垫在前面，被吞的就是静音而不是字头。
        /// </summary>
        private const int LeadPadMs = 80;

        /// <summary>保活静音片长度（毫秒），循环播放，用来定住蓝牙链路。</summary>
        private const int KeepAliveMs = 500;

        // 保活流：常驻一条静音播放，让输出设备（尤其是蓝牙耳机）的音频链路一直处于活动状态。
        private readonly object _keepAliveGate = new object();
        private SoundPlayer _keepAlive;
        private MemoryStream _keepAliveStream;
        private bool _keepAliveEnabled = true;

        // 常驻输出流：整场会话只打开一次，没播报时喂静音，设备上永远有流在跑
        private WaveOutSink _sink;

        /// <summary>进程级播放锁：PlaySound 只有一个播放槽，跨实例也必须串行。</summary>
        private static readonly object s_playGate = new object();

        private sealed class Clip
        {
            public string File;      // wav 绝对路径
            public string Text;      // 语音包缺失时的 TTS 文本
            public bool IsVoice;     // true=语音（受 VoiceEnabled 控制），false=音效
            public bool Optional;    // 快节奏积压时可省略（席位前缀），省下的 0.5s 让播报跟得上出牌
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
        private volatile bool _playing;   // 供自检 WaitForIdle 判断「当前组是否已播完」
        // 检测到积压后保持一段时间省略席位前缀，避免「念一次席位→积压→省席位→追上→又念席位」来回抖动
        private DateTime _degradeUntilUtc = DateTime.MinValue;
        private const int DegradeHoldSeconds = 5;
        private int _disposed;
        private int _playedClips;
        private int _failedClips;
        private int _coalescedGroups;
        private int _fallbackClips;
        private int _truncatedPlays;    // 实际播放耗时明显短于音频时长 = 被截断
        private int _lastExpectedMs;
        private int _lastElapsedMs;

        /// <summary>诊断计数：已成功播放的片段数（配合 audiotest 自检）。</summary>
        public int PlayedClips { get { return _playedClips; } }
        /// <summary>诊断计数：播放失败的片段数。</summary>
        public int FailedClips { get { return _failedClips; } }
        /// <summary>诊断计数：成功拼成一段 PCM 一次播放的播报组数（&gt;0 说明拼接生效）。</summary>
        public int CoalescedGroups { get { return _coalescedGroups; } }
        /// <summary>诊断计数：因格式不符/内存播放失败而退回逐片段播放的片段数。</summary>
        public int FallbackClips { get { return _fallbackClips; } }
        /// <summary>诊断计数：播放耗时不足音频时长 70% 的次数（&gt;0 说明有播报被截断）。</summary>
        public int TruncatedPlays { get { return _truncatedPlays; } }
        /// <summary>最后一次播放的预期/实际时长（毫秒），自检用来判断是否播完整。</summary>
        public int LastExpectedMs { get { return _lastExpectedMs; } }
        public int LastElapsedMs { get { return _lastElapsedMs; } }

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

            // 首选常驻输出流：不关流就不会在句尾丢数据，也不用补白拖节奏
            try
            {
                var sink = new WaveOutSink();
                if (sink.TryOpen()) _sink = sink;
                else sink.Dispose();
            }
            catch { _sink = null; }

            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Name = "MahjongAudio";
            _worker.Start();

            // 没拿到常驻流时才需要保活流兜底；两者同时开会抢设备，第一句反而被截断
            if (_sink == null) StartKeepAlive();
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
                if (_announceSeat) AddVoice(clips, "seat_" + playerIndex, optional: true);
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
            if (_announceSeat) AddVoice(clips, "seat_" + playerIndex, optional: true);
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

        /// <summary>
        /// 开局码牌：连着几声轻「嗒」，配合牌墙逐墩落位。
        /// 整组一次性排队，不逐墩 Enqueue——后者会在后台 worker 里积压，动画播完了还在响。
        /// 总时长约 1 秒，正好盖住码牌阶段；玩家中途跳过动画时 GameStarted 会 Clear 掉剩余组。
        /// </summary>
        public void OpeningStack()
        {
            var clips = new List<Clip>();
            for (int i = 0; i < 8; i++) AddSfx(clips, i % 3 == 0 ? "discard" : "draw");
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

        /// <summary>
        /// 清空排队中的播报（结算、重开局时用）。只影响还没开播的组，
        /// 已经开播的那组会完整念完，不再从片段中间掐断。
        /// </summary>
        public void Clear()
        {
            lock (_gate) { _queue.Clear(); }
        }

        /// <summary>
        /// 开/关保活流。开着时设备上始终有一条静音流在跑，蓝牙这类有传输延迟的
        /// 输出设备不会因为「这一句播完就没人播了」而拆掉链路，句尾数据才有机会送出去。
        /// </summary>
        public void SetKeepAlive(bool on)
        {
            _keepAliveEnabled = on;
            if (on) StartKeepAlive();
            else StopKeepAlive();
        }

        /// <summary>是否启用了保活流（自检/诊断用）。</summary>
        public bool KeepAliveActive
        {
            get { lock (_keepAliveGate) { return _keepAlive != null; } }
        }

        /// <summary>常驻输出流是否可用（自检/诊断用）；为 false 说明退回了 SoundPlayer 路径。</summary>
        public bool SinkActive { get { WaveOutSink s = _sink; return s != null && s.Active; } }

        /// <summary>诊断用：开/关常驻流，用来对比「常驻流」与「SoundPlayer + 补白」两条路。</summary>
        public void SetSink(bool on)
        {
            if (on)
            {
                if (_sink != null) return;
                try
                {
                    var sink = new WaveOutSink();
                    if (sink.TryOpen()) _sink = sink;
                    else sink.Dispose();
                }
                catch { }
                return;
            }

            WaveOutSink old = _sink;
            _sink = null;
            if (old != null)
            {
                try { old.Drain(400); } catch { }
                try { old.Dispose(); } catch { }
            }
        }

        private void StartKeepAlive()
        {
            if (!_keepAliveEnabled) return;
            lock (_keepAliveGate)
            {
                if (_keepAlive != null) return;
                try
                {
                    byte[] silent = MakeWave(new byte[16000 * 2 * KeepAliveMs / 1000], 16000, 1, 16, 0, 0);
                    if (silent == null) return;
                    _keepAliveStream = new MemoryStream(silent, false);
                    _keepAlive = new SoundPlayer(_keepAliveStream);
                    _keepAlive.Load();
                    _keepAlive.PlayLooping();
                }
                catch
                {
                    // 设备不支持就一直静默下去，不影响正常播报
                    try { if (_keepAlive != null) _keepAlive.Dispose(); } catch { }
                    _keepAlive = null;
                    try { if (_keepAliveStream != null) _keepAliveStream.Dispose(); } catch { }
                    _keepAliveStream = null;
                }
            }
        }

        private void StopKeepAlive()
        {
            lock (_keepAliveGate)
            {
                try { if (_keepAlive != null) { _keepAlive.Stop(); _keepAlive.Dispose(); } } catch { }
                _keepAlive = null;
                try { if (_keepAliveStream != null) _keepAliveStream.Dispose(); } catch { }
                _keepAliveStream = null;
            }
        }

        // ==================== 内部实现 ====================

        private string VoicePath(string key) { return Path.Combine(_voiceDir, key + ".wav"); }
        private string SoundPath(string key) { return Path.Combine(_soundDir, key + ".wav"); }

        private void AddVoice(List<Clip> clips, string key, bool optional = false)
        {
            if (!_voiceEnabled) return;
            clips.Add(new Clip
            {
                File = VoicePath(key),
                Text = VoicePackGenerator.TextOf(key),
                IsVoice = true,
                Optional = optional
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
                    // 只淘汰最旧的可抢占播报，把它们控制在很小的积压量内。
                    // 旧实现每次新出牌都清空整条队列，遇到「播报 860ms / 出牌间隔 800ms」
                    // 时排队那条永远被挤掉，听感就是隔一张漏一张。
                    int pending = 0;
                    foreach (Announcement a in _queue) if (a.Interruptible) pending++;
                    while (pending >= MaxQueuedInterruptible)
                    {
                        int n = _queue.Count;
                        bool removed = false;
                        for (int i = 0; i < n; i++)
                        {
                            Announcement a = _queue.Dequeue();
                            if (a.Interruptible && !removed) { removed = true; pending--; }
                            else _queue.Enqueue(a);
                        }
                        if (!removed) break;
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
                lock (_gate)
                {
                    if (_queue.Count > 0) announcement = _queue.Dequeue();
                }
                if (announcement == null)
                {
                    _signal.WaitOne(250);
                    continue;
                }
                _playing = true;
                try { PlayAnnouncement(announcement); }
                catch { /* 单组播报失败不影响后续播报 */ }
                finally { _playing = false; }
            }
        }

        /// <summary>
        /// 等队列排空且当前组播完（自检用，避免用固定 Sleep 猜播放时长导致日志不准）。
        /// 返回 false 表示超时。
        /// </summary>
        public bool WaitForIdle(int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                lock (_gate)
                {
                    if (_queue.Count == 0 && !_playing) return true;
                }
                Thread.Sleep(20);
            }
            return false;
        }

        /// <summary>
        /// 播一组播报：连续的 wav 片段拼成一段 PCM 一次播放；遇到缺文件要走 TTS 兜底时，
        /// 先把已攒下的片段冲刷播放，再单独合成文本，之后再继续拼后面的片段。
        /// </summary>
        private void PlayAnnouncement(Announcement announcement)
        {
            // 队列里还有没播的组 = 出牌比播报快（开了席位前缀时一条播报约 1.3s，
            // 而出牌间隔 0.8s）。此时省略可选的席位前缀，把播报压回节奏以内，
            // 免得语音越念越滞后、听起来跟画面脱节。
            bool crowded;
            lock (_gate) { crowded = _queue.Count > 0; }
            if (crowded) _degradeUntilUtc = DateTime.UtcNow.AddSeconds(DegradeHoldSeconds);
            bool degrade = DateTime.UtcNow < _degradeUntilUtc;

            var pending = new List<Clip>();
            foreach (Clip clip in announcement.Clips)
            {
                if (_stopped) break;
                // 开关在播放时再判一次，用户中途关掉配音能立刻生效
                if (clip.IsVoice && !_voiceEnabled) continue;
                if (!clip.IsVoice && !_soundEnabled) continue;
                if (clip.Optional && degrade) continue;

                if (!string.IsNullOrEmpty(clip.File) && File.Exists(clip.File))
                {
                    pending.Add(clip);
                    continue;
                }

                FlushClips(pending);
                pending.Clear();
                // 语音包缺失：用本机中文 TTS 兜底念文本；音效缺失则直接静默
                if (clip.IsVoice && !string.IsNullOrEmpty(clip.Text))
                    SpeakFallback(clip.Text);
            }
            FlushClips(pending);
        }

        /// <summary>把一组连续 wav 拼成内存 WAV 一次播放；失败时退回逐片段播放。</summary>
        private void FlushClips(List<Clip> clips)
        {
            if (clips == null || clips.Count == 0 || _stopped) return;

            byte[] pcm;
            int rate, channels;
            if (TryJoinClips(clips, out pcm, out rate, out channels))
            {
                int expectedMs = pcm.Length * 1000 / Math.Max(1, rate * channels * 2);

                // 首选常驻流：整句 PCM 排进同一条从不关闭的流里，蓝牙设备不会在
                // 「流结束」时丢尾巴，也不需要靠尾部补白拖时间。
                if (_sink != null && _sink.Active && rate == WaveOutSink.Rate && channels == WaveOutSink.Channels)
                {
                    long mark = _sink.Play(pcm);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    bool played = _sink.WaitPlayed(mark, expectedMs + 3000);
                    sw.Stop();
                    _lastExpectedMs = expectedMs;
                    _lastElapsedMs = (int)sw.ElapsedMilliseconds;
                    if (expectedMs > 100 && _lastElapsedMs < expectedMs * 70 / 100)
                        Interlocked.Increment(ref _truncatedPlays);
                    if (played)
                    {
                        Interlocked.Add(ref _playedClips, clips.Count);
                        Interlocked.Increment(ref _coalescedGroups);
                        return;
                    }
                }
                else
                {
                    // 常驻流不可用：退回 SoundPlayer 一次性提交，并靠尾部补白扛住蓝牙丢尾
                    byte[] image = MakeWave(pcm, rate, channels, 16, LeadPadMs, _tailPadMs);
                    if (image != null)
                    {
                        int imageMs = ExpectedMs(image);
                        bool ok;
                        int elapsedMs;
                        lock (s_playGate)
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            ok = PlayMemory(image);
                            sw.Stop();
                            elapsedMs = (int)sw.ElapsedMilliseconds;
                        }
                        _lastExpectedMs = imageMs;
                        _lastElapsedMs = elapsedMs;
                        // 播得比音频本身还短 = 后半段被吃掉（「开局」只念出「开」就是这种）
                        if (imageMs > 100 && elapsedMs < imageMs * 70 / 100)
                            Interlocked.Increment(ref _truncatedPlays);
                        if (ok)
                        {
                            Interlocked.Add(ref _playedClips, clips.Count);
                            Interlocked.Increment(ref _coalescedGroups);
                            return;
                        }
                    }
                }
            }

            // 格式不一致或播放不被支持：保持旧行为，逐片段播放
            foreach (Clip clip in clips)
            {
                if (_stopped) break;
                PlayFileFallback(clip);
            }
        }

        private void PlayFileFallback(Clip clip)
        {
            bool ok;
            lock (s_playGate) { ok = PlayFileWithSoundPlayer(clip.File); }
            Interlocked.Increment(ref _fallbackClips);
            if (ok) Interlocked.Increment(ref _playedClips);
            else Interlocked.Increment(ref _failedClips);
        }

        /// <summary>逐片段回退路径也走 SoundPlayer，避免回退时又被 PlaySound 截断。</summary>
        private static bool PlayFileWithSoundPlayer(string path)
        {
            try
            {
                using (var player = new SoundPlayer(path))
                {
                    player.Load();
                    player.PlaySync();
                    return true;
                }
            }
            catch
            {
                return PlaySound(path, IntPtr.Zero, SND_FILENAME | SND_SYNC | SND_NODEFAULT);
            }
        }

        private void SpeakFallback(string text)
        {
            lock (s_playGate)
            {
                EnsureTts();
                if (_tts != null)
                {
                    _tts.Speak(text);
                    Interlocked.Increment(ref _playedClips);
                }
                else Interlocked.Increment(ref _failedClips);
            }
        }

        /// <summary>
        /// 播放内存中的完整 WAV 映像。这里不用 winmm PlaySound：
        /// PlaySound 是进程级单一播放槽，内部按小块缓冲馈送，UI 繁忙时容易欠载，
        /// 短词会只播前半截（「开局」只听到「开」）。SoundPlayer 走自己的 waveOut
        /// 句柄，把整段 PCM 一次性提交给驱动，播放期间不依赖 CPU 继续喂数据，
        /// 也不会被进程里任何别的 PlaySound 调用打断。
        /// </summary>
        private static bool PlayMemory(byte[] image)
        {
            try
            {
                using (var ms = new MemoryStream(image, false))
                using (var player = new SoundPlayer(ms))
                {
                    player.Load();
                    player.PlaySync();
                    return true;
                }
            }
            catch
            {
                // 极端环境（waveOut 不可用）下退回 winmm，至少还能出声
                GCHandle handle = GCHandle.Alloc(image, GCHandleType.Pinned);
                try
                {
                    return PlaySoundMem(handle.AddrOfPinnedObject(), IntPtr.Zero,
                        SND_MEMORY | SND_SYNC | SND_NODEFAULT);
                }
                finally { handle.Free(); }
            }
        }

        /// <summary>从拼接好的 WAV 头算出这段音频应有的播放时长（毫秒）。</summary>
        private static int ExpectedMs(byte[] image)
        {
            try
            {
                int byteRate = BitConverter.ToInt32(image, 28);
                int dataLength = BitConverter.ToInt32(image, 40);
                return byteRate > 0 ? dataLength * 1000 / byteRate : 0;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 把多个 16bit PCM wav 的 data 区按顺序拼成一整段连续 PCM。
        /// 所有片段的采样率/位深/声道必须一致；片段之间填 25~35ms 的全零静音作气口。
        /// 补白不在这里做：常驻流不需要，退回 SoundPlayer 时由调用方套 MakeWave 补。
        /// </summary>
        private bool TryJoinClips(List<Clip> clips, out byte[] pcm, out int rate, out int channels)
        {
            pcm = null;
            rate = 0;
            channels = 0;
            var parts = new List<byte[]>();
            var gaps = new List<int>();
            int bits = 0, dataLength = 0;

            for (int i = 0; i < clips.Count; i++)
            {
                byte[] part;
                int r, b, c;
                if (!TryReadPcm(clips[i].File, out part, out r, out b, out c)) return false;
                if (b != 16 || c < 1 || r <= 0 || part.Length == 0) return false;
                if (rate == 0) { rate = r; bits = b; channels = c; }
                else if (r != rate || b != bits || c != channels) return false;

                parts.Add(part);
                dataLength += part.Length;
                if (i < clips.Count - 1)
                {
                    int ms = clips[i].IsVoice ? GapMsVoice : GapMsAfterSfx;
                    int bytes = rate * channels * (bits / 8) * ms / 1000;
                    gaps.Add(bytes);
                    dataLength += bytes;
                }
            }

            byte[] joined = new byte[dataLength];
            int offset = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                Buffer.BlockCopy(parts[i], 0, joined, offset, parts[i].Length);
                offset += parts[i].Length;
                if (i < gaps.Count) offset += gaps[i];   // 全零静音，byte[] 初值即 0
            }

            pcm = joined;
            return true;
        }

        /// <summary>给 PCM 数据套上标准 44 字节 RIFF 头，并在前后各补一段全零静音。</summary>
        private static byte[] MakeWave(byte[] pcm, int rate, int channels, int bits, int leadMs, int tailMs)
        {
            int blockAlign = channels * bits / 8;
            if (pcm == null || blockAlign <= 0 || rate <= 0) return null;

            int leadBytes = rate * blockAlign * Math.Max(0, leadMs) / 1000;
            int tailBytes = rate * blockAlign * Math.Max(0, tailMs) / 1000;
            int dataLength = leadBytes + pcm.Length + tailBytes;

            byte[] img = new byte[44 + dataLength];
            PutAscii(img, 0, "RIFF");
            Put32(img, 4, 36 + dataLength);
            PutAscii(img, 8, "WAVE");
            PutAscii(img, 12, "fmt ");
            Put32(img, 16, 16);
            Put16(img, 20, 1);                       // PCM
            Put16(img, 22, channels);
            Put32(img, 24, rate);
            Put32(img, 28, rate * blockAlign);      // byte rate
            Put16(img, 32, blockAlign);
            Put16(img, 34, bits);
            PutAscii(img, 36, "data");
            Put32(img, 40, dataLength);
            Buffer.BlockCopy(pcm, 0, img, 44 + leadBytes, pcm.Length);
            return img;
        }

        /// <summary>
        /// 诊断用：把单个词条按指定条件播一遍（不排队），返回预期/实际耗时。
        /// resample48 = 先重采样到 48kHz 立体声，验证是不是 16kHz 素材在蓝牙设备上
        /// 重采样出的问题；tailPadMs = 尾部补白，验证是不是播完关句柄时被丢了尾巴。
        /// </summary>
        public bool PlayProbe(string key, int tailPadMs, bool resample48, bool viaSink,
            out int expectedMs, out int elapsedMs)
        {
            expectedMs = elapsedMs = 0;
            byte[] pcm;
            int rate, bits, channels;
            if (!TryReadPcm(VoicePath(key), out pcm, out rate, out bits, out channels)) return false;

            if (resample48 && rate != 48000)
            {
                pcm = Resample(pcm, channels, rate, 48000);
                rate = 48000;
            }
            if (resample48 && channels == 1)
            {
                pcm = ToStereo(pcm);
                channels = 2;
            }

            if (viaSink && _sink != null && _sink.Active
                && rate == WaveOutSink.Rate && channels == WaveOutSink.Channels)
            {
                expectedMs = pcm.Length * 1000 / Math.Max(1, rate * channels * 2);
                var swSink = System.Diagnostics.Stopwatch.StartNew();
                long mark = _sink.Play(pcm);
                bool done = _sink.WaitPlayed(mark, expectedMs + 3000);
                swSink.Stop();
                elapsedMs = (int)swSink.ElapsedMilliseconds;
                return done;
            }

            byte[] image = MakeWave(pcm, rate, channels, 16, 40, tailPadMs);
            if (image == null) return false;
            expectedMs = ExpectedMs(image);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok;
            lock (s_playGate) { ok = PlayMemory(image); }
            sw.Stop();
            elapsedMs = (int)sw.ElapsedMilliseconds;
            return ok;
        }

        /// <summary>线性插值重采样（16bit PCM，按声道交错）。只用于诊断路径。</summary>
        private static byte[] Resample(byte[] pcm, int channels, int fromRate, int toRate)
        {
            if (fromRate <= 0 || toRate <= 0 || fromRate == toRate) return pcm;
            int frameBytes = 2 * channels;
            int frames = pcm.Length / frameBytes;
            if (frames <= 0) return pcm;

            int outFrames = (int)((long)frames * toRate / fromRate);
            byte[] output = new byte[outFrames * frameBytes];
            for (int i = 0; i < outFrames; i++)
            {
                double srcPos = (double)i * fromRate / toRate;
                int i0 = (int)srcPos;
                int i1 = Math.Min(i0 + 1, frames - 1);
                double frac = srcPos - i0;
                for (int c = 0; c < channels; c++)
                {
                    short s0 = (short)(pcm[i0 * frameBytes + c * 2] | (pcm[i0 * frameBytes + c * 2 + 1] << 8));
                    short s1 = (short)(pcm[i1 * frameBytes + c * 2] | (pcm[i1 * frameBytes + c * 2 + 1] << 8));
                    int v = (int)Math.Round(s0 + (s1 - s0) * frac);
                    if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
                    output[i * frameBytes + c * 2] = (byte)v;
                    output[i * frameBytes + c * 2 + 1] = (byte)(v >> 8);
                }
            }
            return output;
        }

        /// <summary>单声道 16bit PCM 复制成立体声（左=右），对齐蓝牙设备常见的 48k/立体声混音格式。</summary>
        private static byte[] ToStereo(byte[] mono)
        {
            int frames = mono.Length / 2;
            byte[] output = new byte[frames * 4];
            for (int i = 0; i < frames; i++)
            {
                output[i * 4] = mono[i * 2];
                output[i * 4 + 1] = mono[i * 2 + 1];
                output[i * 4 + 2] = mono[i * 2];
                output[i * 4 + 3] = mono[i * 2 + 1];
            }
            return output;
        }

        /// <summary>读取标准 RIFF/PCM wav 的 data 区与格式；任何异常都返回 false 交给上层回退。</summary>
        private static bool TryReadPcm(string path, out byte[] pcm, out int rate, out int bits, out int channels)
        {
            pcm = null;
            rate = bits = channels = 0;
            try
            {
                byte[] raw = File.ReadAllBytes(path);
                if (raw.Length < 44 || Ascii(raw, 0, 4) != "RIFF" || Ascii(raw, 8, 4) != "WAVE") return false;
                int pos = 12, fmtAt = -1, dataAt = -1, dataLength = 0;
                while (pos + 8 <= raw.Length)
                {
                    string id = Ascii(raw, pos, 4);
                    int size = BitConverter.ToInt32(raw, pos + 4);
                    if (size < 0 || pos + 8 + size > raw.Length) return false;
                    if (id == "fmt ") fmtAt = pos + 8;
                    else if (id == "data") { dataAt = pos + 8; dataLength = size; break; }
                    pos += 8 + size + (size & 1);   // chunk 按偶数字节对齐
                }
                if (fmtAt < 0 || dataAt < 0 || fmtAt + 16 > raw.Length) return false;
                if (BitConverter.ToInt16(raw, fmtAt) != 1) return false;   // 只接受未压缩 PCM
                channels = BitConverter.ToInt16(raw, fmtAt + 2);
                rate = BitConverter.ToInt32(raw, fmtAt + 4);
                bits = BitConverter.ToInt16(raw, fmtAt + 14);
                if (bits != 16 || channels < 1 || rate <= 0) return false;
                pcm = new byte[dataLength];
                Buffer.BlockCopy(raw, dataAt, pcm, 0, dataLength);
                return true;
            }
            catch { return false; }
        }

        private static string Ascii(byte[] b, int offset, int length)
        {
            if (offset < 0 || offset + length > b.Length) return "";
            return Encoding.ASCII.GetString(b, offset, length);
        }

        private static void PutAscii(byte[] b, int offset, string s)
        {
            for (int i = 0; i < s.Length; i++) b[offset + i] = (byte)s[i];
        }

        private static void Put32(byte[] b, int offset, int value)
        {
            b[offset] = (byte)value;
            b[offset + 1] = (byte)(value >> 8);
            b[offset + 2] = (byte)(value >> 16);
            b[offset + 3] = (byte)(value >> 24);
        }

        private static void Put16(byte[] b, int offset, int value)
        {
            b[offset] = (byte)value;
            b[offset + 1] = (byte)(value >> 8);
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
                    string raw = line.Substring(eq + 1).Trim();
                    if (k == "tailpad")
                    {
                        int ms;
                        if (int.TryParse(raw, out ms) && ms >= 0 && ms <= 2000) _tailPadMs = ms;
                        continue;
                    }
                    bool v = raw == "1";
                    if (k == "voice") _voiceEnabled = v;
                    else if (k == "sound") _soundEnabled = v;
                    else if (k == "seat") _announceSeat = v;
                    else if (k == "own") _announceOwnDiscard = v;
                    else if (k == "keepalive") _keepAliveEnabled = v;
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
                // 保留用户手写的额外配置（如 tailpad=300），别被界面上的开关覆盖掉
                var extra = new List<string>();
                try
                {
                    if (File.Exists(path))
                    {
                        foreach (string line in File.ReadAllLines(path))
                        {
                            int eq = line.IndexOf('=');
                            if (eq <= 0) continue;
                            string k = line.Substring(0, eq).Trim();
                            if (k == "voice" || k == "sound" || k == "seat" || k == "own") continue;
                            extra.Add(line.Trim());
                        }
                    }
                }
                catch { }
                foreach (string line in extra) sb.AppendLine(line);
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch { /* 无写权限时保持本次会话设置即可 */ }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;   // 幂等：窗口 Closed 可能重复调用
            _stopped = true;
            Clear();
            _signal.Set();
            try { _worker.Join(600); } catch { }
            if (_sink != null)
            {
                // 收尾：等排队的语音真正送出去再关流，否则最后一句又会被蓝牙削掉尾巴
                try { _sink.Drain(600); } catch { }
                try { _sink.Dispose(); } catch { }
                _sink = null;
            }
            StopKeepAlive();
            try { if (_tts != null) { _tts.Dispose(); _tts = null; } } catch { }
            try { _signal.Dispose(); } catch { }
        }
    }
}
