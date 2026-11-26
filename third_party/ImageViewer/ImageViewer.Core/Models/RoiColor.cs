#pragma warning disable CS1591
using System.Globalization;

namespace ImageViewer.Models
{
    /// <summary>
    /// 模型自有的 ARGB 颜色值类型。
    /// Chinese: 让 Models 命名空间不再引用 System.Windows.Media；与 WPF Color 的转换由渲染层的
    /// RoiColorWpfExtensions 提供，边界清晰。
    /// English: Model-owned ARGB color value type so the Models namespace does not reference
    /// System.Windows.Media. Conversion to and from WPF Color lives in RoiColorWpfExtensions at the
    /// rendering boundary.
    /// </summary>
    public readonly record struct RoiColor(byte A, byte R, byte G, byte B)
    {
        public static RoiColor FromArgb(byte a, byte r, byte g, byte b) => new(a, r, g, b);

        /// <summary>
        /// 解析颜色字符串，接受 "#AARRGGBB"、"#RRGGBB"、"#RGB" 与命名色（不区分大小写）。
        /// Chinese: 解析失败返回 null 而不抛异常，避免损坏的会话文件导致加载崩溃。
        /// English: Parses "#AARRGGBB", "#RRGGBB", "#RGB" and named colors (case-insensitive). Returns
        /// null instead of throwing so a corrupted session file cannot break loading.
        /// </summary>
        public static RoiColor? Parse(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string text = value.Trim();

            return text[0] == '#'
                ? FromHex(text[1..])
                : RoiColors.TryParseNamed(text, out RoiColor named) ? named : null;
        }

        /// <summary>
        /// 输出 "#AARRGGBB"。
        /// Chinese: 与既有持久化格式（WPF Color.ToString 的结果）保持一致。
        /// English: Emits "#AARRGGBB", matching the existing persisted format.
        /// </summary>
        public override string ToString()
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"#{A:X2}{R:X2}{G:X2}{B:X2}");
        }

        private static RoiColor? FromHex(string hex)
        {
            return hex.Length switch
            {
                8 => FromHexDigits(hex, 0, 2, 4, 6),
                6 => FromHexDigits("FF" + hex, 0, 2, 4, 6),
                3 => FromHexDigits("FF" + Expand(hex), 0, 2, 4, 6),
                _ => null
            };
        }

        private static RoiColor? FromHexDigits(string hex, int aIndex, int rIndex, int gIndex, int bIndex)
        {
            if (!TryParseByte(hex, aIndex, out byte a)
                || !TryParseByte(hex, rIndex, out byte r)
                || !TryParseByte(hex, gIndex, out byte g)
                || !TryParseByte(hex, bIndex, out byte b))
            {
                return null;
            }

            return new RoiColor(a, r, g, b);
        }

        private static bool TryParseByte(string hex, int index, out byte value)
        {
            return byte.TryParse(hex.AsSpan(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        private static string Expand(string rgb)
        {
            return new string([rgb[0], rgb[0], rgb[1], rgb[1], rgb[2], rgb[2]]);
        }
    }
}
#pragma warning restore CS1591
