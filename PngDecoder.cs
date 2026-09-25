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
    // 추가로 8비트 흑백·흑백+알파와 16비트 RGBA 를 유니티처럼 ARGB32 로 푼다(유니티 결과와 바이트까지 같음을 확인).
    // 그 밖의 16비트, 인터레이스, 손상된 파일은 false 를 돌려주고 원래 LoadImage 가 처리하게 한다.
    // 결과는 유니티 텍스처 순서(아래 줄부터)로 뒤집어서 관리 힙 밖(AllocHGlobal)에 담는다.
    // 수백 MB 짜리 배열을 GC 힙에 만들지 않기 위해서다.
    internal static unsafe class PngDecoder
    {
        internal static bool GrayVerified = Edition.Dev;   // 알파 없는 8비트 흑백: 유니티와 같음이 확인되면 모두에게
        internal const int FormatRGB24 = 3, FormatRGBA32 = 4, FormatARGB32 = 5;   // UnityEngine.TextureFormat 값 (추가 형식은 유니티처럼 ARGB32, 개발자용 비교로 확인)

        // 작업 스레드마다 버퍼를 재사용한다. 이미지마다 새로 만들면 로딩 중 GC가 13번 돌아 메인 스레드를 세웠다.
        [ThreadStatic] private static byte[] idatBuf, curBuf, prevBuf, oneBuf;

        private static byte[] Grow(ref byte[] b, long n) { if (b == null || b.Length < n) b = new byte[Math.Max(n, b == null ? 0 : b.Length * 3 / 2)]; return b; }

        internal static bool TryDecode(byte[] d, int dLen, out int width, out int height, out int format, out IntPtr pixels, out long size) { bool u; return TryDecode(d, dLen, false, out width, out height, out format, out pixels, out size, out u); }
        internal static bool TryDecode(byte[] d, int dLen, bool extra, out int width, out int height, out int format, out IntPtr pixels, out long size, out bool usedExtra)
        {
            width = height = format = 0; pixels = IntPtr.Zero; size = 0; usedExtra = false;
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
            usedExtra = false;
            if (colorType == 6 && bitDepth == 8) channels = 4;
            else if (colorType == 2 && bitDepth == 8) channels = 3;
            else if (colorType == 3 && (bitDepth == 1 || bitDepth == 2 || bitDepth == 4 || bitDepth == 8) && plte != null) channels = 1;
            // (추가 형식, 유니티 결과와 같음이 확인된 뒤에만 켠다) 8비트 흑백·흑백+알파 -> RGBA32, 16비트 RGBA -> RGBA64
            else if (extra && GrayVerified && colorType == 0 && bitDepth == 8) { channels = 1; usedExtra = true; }   // 알파 없는 흑백은 아직 실제 파일로 확인 전
            else if (extra && colorType == 4 && bitDepth == 8) { channels = 2; usedExtra = true; }
            else if (extra && colorType == 6 && bitDepth == 16) { channels = 4; usedExtra = true; }
            else return false;

            // RGB 에 투명색이 지정돼 있으면 알파가 필요하다
            bool rgbKey = colorType == 2 && trns != null && trns.Length >= 6;
            bool argb = usedExtra;   // 유니티는 16비트 RGBA 와 흑백+알파를 ARGB32 로 만든다(2026-09-26 개발자용 비교)
            bool rgba16 = colorType == 6 && bitDepth == 16;
            format = argb ? FormatARGB32 : (colorType == 2 && !rgbKey) ? FormatRGB24 : FormatRGBA32;
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
                        if (rgba16)
                            Argb16(cur, dst, width);   // 16비트 -> 8비트(윗 바이트), A R G B 순서
                        else if (colorType == 6 || (colorType == 2 && !rgbKey))
                            Marshal.Copy(cur, 0, (IntPtr)dst, (int)rowBytes);
                        else if (colorType == 0 || colorType == 4)
                            Gray(cur, dst, width, colorType == 4, trns);
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

        // 16비트 RGBA -> ARGB32: 각 값의 윗 바이트(PNG 는 큰 쪽 바이트가 먼저), A R G B 순서
        private static void Argb16(byte[] cur, byte* dst, int width)
        {
            fixed (byte* s0 = cur)
            {
                byte* s = s0;
                for (int x = 0; x < width; x++, s += 8, dst += 4) { dst[0] = s[6]; dst[1] = s[0]; dst[2] = s[2]; dst[3] = s[4]; }
            }
        }

        // 흑백(+알파) -> ARGB32: A, 밝기, 밝기, 밝기. 알파는 있으면 그 값, 없으면 255 (tRNS 의 흑백 투명색이면 0)
        private static void Gray(byte[] cur, byte* dst, int width, bool alpha, byte[] trns)
        {
            int key = !alpha && trns != null && trns.Length >= 2 ? trns[1] : -1;
            fixed (byte* s0 = cur)
            {
                byte* s = s0;
                for (int x = 0; x < width; x++, dst += 4)
                {
                    byte v = *s++;
                    dst[1] = v; dst[2] = v; dst[3] = v;
                    dst[0] = alpha ? *s++ : (byte)(v == key ? 0 : 255);
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

        // 긴 변이 maxSide 를 넘으면 그 크기로 줄인다 (선택 기능 "큰 이미지 줄이기").
        // 출력 한 픽셀이 덮는 입력 영역을 평균한다. RGBA 는 알파로 가중해서 평균해야 투명한 가장자리가 검게 번지지 않는다.
        // 줄였으면 새 버퍼를 돌려주고 원래 버퍼는 풀어 준다. factor = 새 크기 / 원래 크기.
        internal static bool Downscale(ref IntPtr pixels, ref int width, ref int height, int format, ref long size, int maxSide, out float factor)
        {
            factor = 1f;
            int big = Math.Max(width, height);
            if (maxSide <= 0 || big <= maxSide || pixels == IntPtr.Zero) return false;
            if (format != FormatRGB24 && format != FormatRGBA32) return false;   // 추가 형식(ARGB32)은 줄이지 않는다(원래 방식과 같게 둔다)
            factor = (float)maxSide / big;
            int nw = Math.Max(1, (int)Math.Round(width * (double)factor)), nh = Math.Max(1, (int)Math.Round(height * (double)factor));
            int bpp = format == FormatRGB24 ? 3 : 4;
            long nsize = (long)nw * nh * bpp;
            IntPtr dst = Marshal.AllocHGlobal((IntPtr)nsize);
            try
            {
                byte* s0 = (byte*)pixels, d0 = (byte*)dst;
                double sx = (double)width / nw, sy = (double)height / nh;
                for (int y = 0; y < nh; y++)
                {
                    int y0 = (int)(y * sy), y1 = Math.Max(y0 + 1, Math.Min(height, (int)((y + 1) * sy)));
                    for (int x = 0; x < nw; x++)
                    {
                        int x0 = (int)(x * sx), x1 = Math.Max(x0 + 1, Math.Min(width, (int)((x + 1) * sx)));
                        double r = 0, g = 0, b = 0, a = 0, ur = 0, ug = 0, ub = 0; int n = 0;
                        for (int yy = y0; yy < y1; yy++)
                        {
                            byte* p = s0 + ((long)yy * width + x0) * bpp;
                            for (int xx = x0; xx < x1; xx++, p += bpp)
                            {
                                if (bpp == 4) { double al = p[3]; r += p[0] * al; g += p[1] * al; b += p[2] * al; a += al; ur += p[0]; ug += p[1]; ub += p[2]; }
                                else { r += p[0]; g += p[1]; b += p[2]; }
                                n++;
                            }
                        }
                        byte* q = d0 + ((long)y * nw + x) * bpp;
                        if (bpp == 4)
                        {
                            if (a > 0) { q[0] = (byte)(r / a + 0.5); q[1] = (byte)(g / a + 0.5); q[2] = (byte)(b / a + 0.5); }
                            else { q[0] = (byte)(ur / n + 0.5); q[1] = (byte)(ug / n + 0.5); q[2] = (byte)(ub / n + 0.5); }   // 완전히 투명해도 원래 색을 둔다 (가장자리가 어둡게 번지지 않게)
                            q[3] = (byte)(a / n + 0.5);
                        }
                        else { q[0] = (byte)(r / n + 0.5); q[1] = (byte)(g / n + 0.5); q[2] = (byte)(b / n + 0.5); }
                    }
                }
            }
            catch { Marshal.FreeHGlobal(dst); factor = 1f; return false; }
            Marshal.FreeHGlobal(pixels);
            pixels = dst; width = nw; height = nh; size = nsize;
            return true;
        }
    }
}
