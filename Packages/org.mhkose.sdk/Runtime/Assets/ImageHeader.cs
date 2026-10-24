namespace MHK.Assets
{
    /// <summary>Size and alpha of a PNG or JPG read from its header, without decoding the pixels.</summary>
    static class ImageHeader
    {
        const int PngIhdrEnd = 26;
        const int PngColorTypeOffset = 25;
        const int PngAlphaFlag = 4;
        const int PngPalette = 3;

        /// <summary>Whether <paramref name="bytes"/> start like a PNG or JPG whose size could be read.</summary>
        internal static bool TryRead(byte[] bytes, out int width, out int height, out bool alpha)
        {
            width = height = 0;
            alpha = false;
            if (bytes == null || bytes.Length < PngIhdrEnd) return false;
            if (bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G')
            {
                width = BigEndian(bytes, 16, 4);
                height = BigEndian(bytes, 20, 4);
                var colorType = bytes[PngColorTypeOffset];
                alpha = (colorType & PngAlphaFlag) != 0 || colorType == PngPalette;
                return width > 0 && height > 0;
            }
            return bytes[0] == 0xFF && bytes[1] == 0xD8 && TryReadJpg(bytes, out width, out height);
        }

        // Walks the JPG segments to the first start-of-frame marker, which holds the size.
        static bool TryReadJpg(byte[] bytes, out int width, out int height)
        {
            width = height = 0;
            var i = 2;
            while (i + 9 < bytes.Length)
            {
                if (bytes[i] != 0xFF)
                {
                    i++;
                    continue;
                }
                var marker = bytes[i + 1];
                if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                {
                    height = BigEndian(bytes, i + 5, 2);
                    width = BigEndian(bytes, i + 7, 2);
                    return width > 0 && height > 0;
                }
                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0xFF)
                {
                    i += marker == 0xFF ? 1 : 2;
                    continue;
                }
                i += 2 + BigEndian(bytes, i + 2, 2);
            }
            return false;
        }

        static int BigEndian(byte[] bytes, int offset, int count)
        {
            var v = 0;
            for (var k = 0; k < count; k++) v = (v << 8) | bytes[offset + k];
            return v;
        }
    }
}
