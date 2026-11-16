using System;
using System.Windows;
using System.Windows.Media.Imaging;
using Mahjong.Core.Tiles;

namespace Mahjong.App.Services
{
    /// <summary>
    /// 牌面贴图缓存。
    ///
    /// 贴图由 tools/gen_tiles.py 离线预渲染（34 种正面 + 1 张牌背，带 alpha 的 PNG），
    /// 作为 Resource 打包进程序集。这里只做一次解码并 Freeze，
    /// 之后所有 TileControl 共享同一份 BitmapSource，运行时开销接近零。
    ///
    /// 文件名顺序与 TileKind 枚举（0..33）严格对应，新增牌种时同步维护。
    /// </summary>
    internal static class TileArtwork
    {
        private static readonly string[] Files =
        {
            "man1", "man2", "man3", "man4", "man5", "man6", "man7", "man8", "man9",
            "pin1", "pin2", "pin3", "pin4", "pin5", "pin6", "pin7", "pin8", "pin9",
            "tong1", "tong2", "tong3", "tong4", "tong5", "tong6", "tong7", "tong8", "tong9",
            "east", "south", "west", "north", "zhong", "fa", "bai"
        };

        private static readonly BitmapSource[] Faces = new BitmapSource[TileKindExtensions.KindCount];
        private static BitmapSource _back;
        private static bool _backLoaded;

        /// <summary>取牌面/牌背贴图；资源缺失时返回 null，由 TileControl 走降级绘制。</summary>
        public static BitmapSource Get(TileKind kind, bool faceUp)
        {
            if (!faceUp)
            {
                if (!_backLoaded)
                {
                    _back = Load("back");
                    _backLoaded = true;
                }
                return _back;
            }

            int i = (int)kind;
            if (i < 0 || i >= Faces.Length) i = 0;
            if (Faces[i] == null) Faces[i] = Load(Files[i]);
            return Faces[i];
        }

        private static BitmapSource Load(string name)
        {
            try
            {
                var uri = new Uri("pack://application:,,,/Assets/Tiles/" + name + ".png", UriKind.Absolute);
                var info = Application.GetResourceStream(uri);
                if (info == null || info.Stream == null) return null;
                using (var stream = info.Stream)
                {
                    var decoder = BitmapDecoder.Create(
                        stream,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);
                    var frame = decoder.Frames[0];
                    frame.Freeze();
                    return frame;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
