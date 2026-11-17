using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Mahjong.App.Services
{
    /// <summary>
    /// 常驻 waveOut 输出流：整个会话只打开一次设备，之后一直有数据在喂（没播报时喂静音）。
    ///
    /// 为什么需要它：蓝牙耳机（A2DP）会在「这条播放流结束」时把还没送出去的尾部数据丢掉。
    /// 用 System.Media.SoundPlayer 每播一句就开关一次流，552ms 的「开局」在蓝牙耳机上
    /// 只剩「开」，而扬声器正常（实测对照：扬声器完整、蓝牙只有前半截）。
    /// 事后补静音（tailpad）能救，但每组要多 300ms，出牌间隔只有 800ms，会把播报越拖越滞后
    /// 甚至漏念。改成一条不关的流之后，句尾数据只是被延迟送出、不会被丢，也不需要补白。
    ///
    /// 使用时：<see cref="TryOpen"/> 打开 → <see cref="Play"/> 入队 PCM → <see cref="WaitPlayed"/>
    /// 等这段真正播完 → <see cref="Drain"/> 收尾。<see cref="TryOpen"/> 失败时上层退回 SoundPlayer。
    /// </summary>
    internal sealed class WaveOutSink : IDisposable
    {
        public const int Rate = 16000;
        public const int Channels = 1;
        public const int Bits = 16;

        private const int WAVE_MAPPER = -1;
        private const int CALLBACK_EVENT = 0x00050000;
        private const int WHDR_DONE = 0x00000001;
        private const int WHDR_PREPARED = 0x00000002;

        /// <summary>每个缓冲的时长（毫秒）。4 个缓冲 = 最多 320ms 的预排，够扛住线程调度抖动。</summary>
        private const int BufferMs = 80;
        private const int BufferCount = 4;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public int dwBufferLength;
            public int dwBytesRecorded;
            public IntPtr dwUser;
            public int dwFlags;
            public int dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [DllImport("winmm.dll")]
        private static extern int waveOutOpen(out IntPtr hwo, int uDeviceID, ref WAVEFORMATEX pwfx,
            IntPtr dwCallback, IntPtr dwInstance, int fdwOpen);
        [DllImport("winmm.dll")]
        private static extern int waveOutClose(IntPtr hwo);
        [DllImport("winmm.dll")]
        private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
        [DllImport("winmm.dll")]
        private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
        [DllImport("winmm.dll")]
        private static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);
        [DllImport("winmm.dll")]
        private static extern int waveOutReset(IntPtr hwo);

        private readonly object _lock = new object();
        private readonly Queue<byte[]> _audio = new Queue<byte[]>();
        private readonly ManualResetEvent _evt = new ManualResetEvent(false);

        private IntPtr _hwo = IntPtr.Zero;
        private IntPtr[] _headerPtr;
        private IntPtr[] _dataPtr;
        private int[] _audioLen;      // 每个缓冲里「真实语音」的字节数，用来算播放进度
        private bool[] _inFlight;
        private int _headerSize;
        private int _bufferBytes;
        private Thread _thread;
        private volatile bool _stop;
        private volatile bool _failed;

        private byte[] _cur;          // 正在消费的 PCM 块
        private int _curPos;
        private long _queuedBytes;
        private long _playedBytes;

        /// <summary>设备是否已经打开且在正常送数据。</summary>
        public bool Active { get { return _hwo != IntPtr.Zero && !_failed; } }

        /// <summary>打开常驻流；失败时返回 false，调用方应退回 SoundPlayer。</summary>
        public bool TryOpen()
        {
            try
            {
                var fmt = new WAVEFORMATEX
                {
                    wFormatTag = 1,
                    nChannels = Channels,
                    nSamplesPerSec = Rate,
                    nAvgBytesPerSec = (uint)(Rate * Channels * Bits / 8),
                    nBlockAlign = (ushort)(Channels * Bits / 8),
                    wBitsPerSample = Bits,
                    cbSize = 0
                };
                IntPtr evt = _evt.SafeWaitHandle.DangerousGetHandle();
                if (waveOutOpen(out _hwo, WAVE_MAPPER, ref fmt, evt, IntPtr.Zero, CALLBACK_EVENT) != 0)
                {
                    _hwo = IntPtr.Zero;
                    return false;
                }

                _headerSize = Marshal.SizeOf(typeof(WAVEHDR));
                _bufferBytes = Rate * Channels * (Bits / 8) * BufferMs / 1000;
                _headerPtr = new IntPtr[BufferCount];
                _dataPtr = new IntPtr[BufferCount];
                _audioLen = new int[BufferCount];
                _inFlight = new bool[BufferCount];

                for (int i = 0; i < BufferCount; i++)
                {
                    _dataPtr[i] = Marshal.AllocHGlobal(_bufferBytes);
                    _headerPtr[i] = Marshal.AllocHGlobal(_headerSize);
                    WriteHeader(i, _dataPtr[i], _bufferBytes, 0);
                    if (waveOutPrepareHeader(_hwo, _headerPtr[i], _headerSize) != 0)
                    {
                        Dispose();
                        return false;
                    }
                }

                _thread = new Thread(Loop);
                _thread.IsBackground = true;
                _thread.Priority = ThreadPriority.AboveNormal;
                _thread.Name = "MahjongWaveOut";
                _thread.Start();
                return true;
            }
            catch
            {
                Dispose();
                return false;
            }
        }

        /// <summary>把一段 16kHz/16bit/单声道 PCM 排进输出流，返回入队后的累计字节数（供 WaitPlayed 用）。</summary>
        public long Play(byte[] pcm)
        {
            if (pcm == null || pcm.Length == 0) return 0;
            lock (_lock)
            {
                _audio.Enqueue(pcm);
                _queuedBytes += pcm.Length;
                Monitor.PulseAll(_lock);
                return _queuedBytes;
            }
        }

        /// <summary>等到累计播放字节数达到 mark（即这段音频真的送出去了）。</summary>
        public bool WaitPlayed(long mark, int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            lock (_lock)
            {
                while (_playedBytes < mark)
                {
                    int left = timeoutMs - (int)sw.ElapsedMilliseconds;
                    if (left <= 0 || _failed) return _playedBytes >= mark;
                    Monitor.Wait(_lock, left);
                }
                return true;
            }
        }

        /// <summary>等排队的音频全部播完（退出前用，避免关流把尾巴削掉）。</summary>
        public bool Drain(int timeoutMs)
        {
            long mark;
            lock (_lock) { mark = _queuedBytes; }
            return WaitPlayed(mark, timeoutMs);
        }

        private void Loop()
        {
            try
            {
                while (!_stop)
                {
                    int free = -1, inFlight = 0;
                    for (int i = 0; i < BufferCount; i++)
                    {
                        if (_inFlight[i])
                        {
                            inFlight++;
                            WAVEHDR h = ReadHeader(i);
                            if ((h.dwFlags & WHDR_DONE) != 0)
                            {
                                _inFlight[i] = false;
                                inFlight--;
                                int done = _audioLen[i];
                                _audioLen[i] = 0;
                                if (done > 0)
                                {
                                    lock (_lock)
                                    {
                                        _playedBytes += done;
                                        Monitor.PulseAll(_lock);
                                    }
                                }
                            }
                        }
                        if (!_inFlight[i] && free < 0) free = i;
                    }

                    bool pending;
                    lock (_lock) { pending = _audio.Count > 0; }

                    // 没语音要喂时只留 2 个缓冲在飞：既不会断流，也不会让下一句在缓冲区里
                    // 多等一整排（那等于白白给每条播报加了 300ms 延迟）。
                    if (free < 0 || (!pending && _cur == null && inFlight >= 2))
                    {
                        _evt.Reset();
                        _evt.WaitOne(50);
                        continue;
                    }

                    int filled = Fill(free);
                    // 只更新长度并清掉「完成」标记，必须保留 WHDR_PREPARED，
                    // 否则 waveOutWrite 会认为这个头没准备过而直接失败。
                    WAVEHDR hdr = ReadHeader(free);
                    hdr.lpData = _dataPtr[free];
                    hdr.dwBufferLength = _bufferBytes;
                    hdr.dwBytesRecorded = 0;
                    hdr.dwFlags = hdr.dwFlags & ~WHDR_DONE;
                    Marshal.StructureToPtr(hdr, _headerPtr[free], false);
                    if (waveOutWrite(_hwo, _headerPtr[free], _headerSize) != 0)
                    {
                        _failed = true;
                        break;
                    }
                    _inFlight[free] = true;
                    _audioLen[free] = filled;
                }
            }
            catch
            {
                _failed = true;
            }
            finally
            {
                lock (_lock) { _playedBytes = _queuedBytes; Monitor.PulseAll(_lock); }
            }
        }

        /// <summary>填一个缓冲：优先取排队的语音，取不够就补静音。返回其中语音的字节数。</summary>
        private int Fill(int index)
        {
            IntPtr dst = _dataPtr[index];
            int filled = 0;
            while (filled < _bufferBytes)
            {
                if (_cur == null || _curPos >= _cur.Length)
                {
                    _cur = null;
                    lock (_lock)
                    {
                        while (_audio.Count > 0)
                        {
                            byte[] next = _audio.Dequeue();
                            if (next != null && next.Length > 0) { _cur = next; _curPos = 0; break; }
                        }
                    }
                    if (_cur == null) break;   // 没语音了，剩下补零
                }

                int n = Math.Min(_bufferBytes - filled, _cur.Length - _curPos);
                Marshal.Copy(_cur, _curPos, new IntPtr(dst.ToInt64() + filled), n);
                _curPos += n;
                filled += n;
            }
            if (filled < _bufferBytes)
                Zero(new IntPtr(dst.ToInt64() + filled), _bufferBytes - filled);
            return filled;
        }

        private static void Zero(IntPtr ptr, int bytes)
        {
            byte[] zeros = new byte[Math.Min(bytes, 4096)];
            int done = 0;
            while (done < bytes)
            {
                int n = Math.Min(zeros.Length, bytes - done);
                Marshal.Copy(zeros, 0, new IntPtr(ptr.ToInt64() + done), n);
                done += n;
            }
        }

        private WAVEHDR ReadHeader(int index)
        {
            return (WAVEHDR)Marshal.PtrToStructure(_headerPtr[index], typeof(WAVEHDR));
        }

        private void WriteHeader(int index, IntPtr data, int length, int flags)
        {
            var h = new WAVEHDR
            {
                lpData = data,
                dwBufferLength = length,
                dwBytesRecorded = 0,
                dwUser = IntPtr.Zero,
                dwFlags = flags,
                dwLoops = 0,
                lpNext = IntPtr.Zero,
                reserved = IntPtr.Zero
            };
            Marshal.StructureToPtr(h, _headerPtr[index], false);
        }

        public void Dispose()
        {
            _stop = true;
            try { _evt.Set(); } catch { }
            try { if (_thread != null) _thread.Join(800); } catch { }

            if (_hwo != IntPtr.Zero)
            {
                try { waveOutReset(_hwo); } catch { }
                if (_headerPtr != null)
                {
                    for (int i = 0; i < _headerPtr.Length; i++)
                    {
                        try { waveOutUnprepareHeader(_hwo, _headerPtr[i], _headerSize); } catch { }
                    }
                }
                try { waveOutClose(_hwo); } catch { }
                _hwo = IntPtr.Zero;
            }

            if (_dataPtr != null)
            {
                for (int i = 0; i < _dataPtr.Length; i++)
                {
                    if (_dataPtr[i] != IntPtr.Zero) { try { Marshal.FreeHGlobal(_dataPtr[i]); } catch { } }
                    if (_headerPtr[i] != IntPtr.Zero) { try { Marshal.FreeHGlobal(_headerPtr[i]); } catch { } }
                }
                _dataPtr = null;
                _headerPtr = null;
            }

            try { _evt.Dispose(); } catch { }
        }
    }
}
