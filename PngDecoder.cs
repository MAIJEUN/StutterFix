using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace StutterFix
{
    // 작업 스레드에서 쓰는 PNG 해독기. 유니티 API 를 전혀 쓰지 않는다.
    //
    // 유니티의 ImageConversion.LoadImage 는 메인 스레드에서만 돌아서, 이미지 719장(원본 합계 13GB 픽셀)을
    // 한 장씩 푸는 데 65초가 걸렸다. 같은 일을 여러 코어에서 미리 해 두려고 직접 푼다.
    //
    // 흔한 형식만 처리한다: 8비트 RGB / RGBA, 1~8비트 팔레트, 투명색(tRNS).
    // 16비트, 흑백, 인터레이스, 손상된 파일은 false 를 돌려주고 원래 LoadImage 가 처리하게 한다.
    // 결과는 유니티 텍스처 순서(아래 줄부터)로 뒤집어서 관리 힙 밖(AllocHGlobal)에 담는다.
    // 수백 MB 짜리 배열을 GC 힙에 만들지 않기 위해서다.
    internal static unsafe class PngDecoder
    {
        internal const int FormatRGB24 = 3, FormatRGBA32 = 4;   // UnityEngine.TextureFormat 값

        // 작업 스레드마다 버퍼를 재사용한다. 이미지마다 새로 만들면 로딩 중 GC가 13번 돌아 메인 스레드를 세웠다.
        [ThreadStatic] private static byte[] idatBuf, curBuf, prevBuf, oneBuf;

        private static byte[] Grow(ref byte[] b, long n) { if (b == null || b.Length < n) b = new byte[Math.Max(n, b == null ? 0 : b.Length * 3 / 2)]; return b; }

        internal static bool TryDecode(byte[] d, int dLen, out int width, out int height, out int format, out IntPtr pixels, out long size)
        {
            width = height = format = 0; pixels = IntPtr.Zero; size = 0;
            if (d == null || dLen < 45 || dLen > d.Length) return false;
            if (d[0] != 0x89 || d[1] != 0x50 || d[2] != 0x4E || d[3] != 0x47 || d[4] != 0x0D || d[5] != 0x0A || d[6] != 0x1A || d[7] != 0x0A) return false;

            int bitDepth = 0, colorType = 0, interlace = 0;
            byte[] plte = null, trns = null;
            long idatLen = 0;
            int pos = 8;
            bool sawEnd = false;

            // 1차: 머리 정보와 IDAT 전체 길이
            while (pos + 8 <= dLen)
            {
                int len = BE(d, pos); int type = BE(d, pos + 4);
                int data = pos + 8;
                if (len < 0 || data + (long)len > dLen) return false;
                if (type == 0x49484452) // IHDR
                {
                    width = BE(d, data); height = BE(d, data + 4);
                    bitDepth = d[data + 8]; colorType = d[data + 9]; interlace = d[data + 12];
                }
                else if (type == 0x504C5445) { plte = new byte[len]; Buffer.BlockCopy(d, data, plte, 0, len); }
                else if (type == 0x74524E53) { trns = new byte[len]; Buffer.BlockCopy(d, data, trns, 0, len); }
                else if (type == 0x49444154) idatLen += len;
                else if (type == 0x49454E44) { sawEnd = true; break; }
                pos = data + len + 4;
            }
            if (!sawEnd || width <= 0 || height <= 0 || interlace != 0 || idatLen < 3) return false;

            int channels;
            if (colorType == 6 && bitDepth == 8) channels = 4;
            else if (colorType == 2 && bitDepth == 8) channels = 3;
            else if (colorType == 3 && (bitDepth == 1 || bitDepth == 2 || bitDepth == 4 || bitDepth == 8) && plte != null) channels = 1;
            else return false;

            // RGB 에 투명색이 지정돼 있으면 알파가 필요하다
            bool rgbKey = colorType == 2 && trns != null && trns.Length >= 6;
            format = (colorType == 2 && !rgbKey) ? FormatRGB24 : FormatRGBA32;
            int outBpp = format == FormatRGB24 ? 3 : 4;

            long rowBytes = ((long)width * channels * bitDepth + 7) / 8;
            long outRow = (long)width * outBpp;
            size = outRow * height;
            if (size > int.MaxValue || rowBytes > int.MaxValue / 2) return false;   // LoadRawTextureData 는 int 크기만 받는다

            // 2차: IDAT 를 하나로 모은다(압축된 크기라 작다)
            if (idatLen > int.MaxValue) return false;
            var idat = Grow(ref idatBuf, idatLen);
            long at = 0;
            pos = 8;
            while (pos + 8 <= dLen)
            {
                int len = BE(d, pos); int type = BE(d, pos + 4);
                if (type == 0x49444154) { Buffer.BlockCopy(d, pos + 8, idat, (int)at, len); at += len; }
                else if (type == 0x49454E44) break;
                pos += 12 + len;
            }
            if ((idat[0] & 0x0F) != 8) return false;   // zlib deflate 가 아니면 포기

            int bpp = Math.Max(1, channels * bitDepth / 8);   // 필터가 쓰는 "왼쪽 픽셀" 거리
            var cur = Grow(ref curBuf, rowBytes);
            var prev = Grow(ref prevBuf, rowBytes);
            Array.Clear(prev, 0, (int)rowBytes);   // 첫 줄의 "윗줄"은 0

            pixels = Marshal.AllocHGlobal((IntPtr)size);
            bool ok = false;
            try
            {
                using (var z = new DeflateStream(new MemoryStream(idat, 2, (int)idatLen - 2), CompressionMode.Decompress))
                {
                    var one = oneBuf ?? (oneBuf = new byte[1]);
                    byte* dst0 = (byte*)pixels;
                    for (int y = 0; y < height; y++)
                    {
                        if (!ReadFull(z, one, 1)) return false;
                        int filter = one[0];
                        if (!ReadFull(z, cur, (int)rowBytes)) return false;
                        if (!Unfilter(cur, prev, (int)rowBytes, bpp, filter)) return false;

                        byte* dst = dst0 + (height - 1 - y) * outRow;   // 유니티 텍스처는 아래 줄이 먼저
                        if (colorType == 6 || (colorType == 2 && !rgbKey))
                            Marshal.Copy(cur, 0, (IntPtr)dst, (int)rowBytes);
                        else if (colorType == 2)
                            RgbKey(cur, dst, width, trns);
                        else
                            Palette(cur, dst, width, bitDepth, plte, trns);

                        var t = prev; prev = cur; cur = t;
                    }
                }
                ok = true;
                return true;
            }
            catch { return false; }
            finally
            {
                if (!ok) { Marshal.FreeHGlobal(pixels); pixels = IntPtr.Zero; size = 0; }
            }
        }

        private static int BE(byte[] d, int p) { return (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3]; }

        private static bool ReadFull(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        private static bool Unfilter(byte[] cur, byte[] prev, int n, int bpp, int filter)
        {
            fixed (byte* c = cur, p = prev)
            {
                switch (filter)
                {
                    case 0: return true;
                    case 1: for (int i = bpp; i < n; i++) c[i] = (byte)(c[i] + c[i - bpp]); return true;
                    case 2: for (int i = 0; i < n; i++) c[i] = (byte)(c[i] + p[i]); return true;
                    case 3:
                        for (int i = 0; i < bpp && i < n; i++) c[i] = (byte)(c[i] + (p[i] >> 1));
                        for (int i = bpp; i < n; i++) c[i] = (byte)(c[i] + ((c[i - bpp] + p[i]) >> 1));
                        return true;
                    case 4:
                        for (int i = 0; i < bpp && i < n; i++) c[i] = (byte)(c[i] + p[i]);
                        for (int i = bpp; i < n; i++)
                        {
                            int a = c[i - bpp], b = p[i], cc = p[i - bpp];
                            int pa = b - cc, pb = a - cc, pc = pa + pb;
                            if (pa < 0) pa = -pa; if (pb < 0) pb = -pb; if (pc < 0) pc = -pc;
                            int pred = (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : cc);
                            c[i] = (byte)(c[i] + pred);
                        }
                        return true;
                    default: return false;
                }
            }
        }

        private static void RgbKey(byte[] cur, byte* dst, int width, byte[] trns)
        {
            byte kr = trns[1], kg = trns[3], kb = trns[5];   // 8비트면 16비트 값의 아래 바이트
            fixed (byte* s0 = cur)
            {
                byte* s = s0;
                for (int x = 0; x < width; x++, s += 3, dst += 4)
                {
                    dst[0] = s[0]; dst[1] = s[1]; dst[2] = s[2];
                    dst[3] = (byte)(s[0] == kr && s[1] == kg && s[2] == kb ? 0 : 255);
                }
            }
        }

        private static void Palette(byte[] cur, byte* dst, int width, int bitDepth, byte[] plte, byte[] trns)
        {
            int entries = plte.Length / 3;
            int mask = (1 << bitDepth) - 1;
            int perByte = 8 / bitDepth;
            for (int x = 0; x < width; x++, dst += 4)
            {
                int idx;
                if (bitDepth == 8) idx = cur[x];
                else
                {
                    int b = cur[x / perByte];
                    int shift = 8 - bitDepth * (x % perByte + 1);
                    idx = (b >> shift) & mask;
                }
                if (idx < entries) { dst[0] = plte[idx * 3]; dst[1] = plte[idx * 3 + 1]; dst[2] = plte[idx * 3 + 2]; }
                else { dst[0] = dst[1] = dst[2] = 0; }
                dst[3] = trns != null && idx < trns.Length ? trns[idx] : (byte)255;
            }
        }
    }
}
