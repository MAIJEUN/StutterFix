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
    // 처리하는 형식: 8비트 RGB / RGBA / 흑백 / 흑백+알파, 1~8비트 팔레트, 투명색(tRNS), 인터레이스(Adam7).
    // 16비트, 1~4비트 흑백, 손상된 파일은 false 를 돌려주고 원래 LoadImage 가 처리하게 한다.
    // PNG 는 무손실이라 올바르게 풀면 어떤 해독기든 같은 픽셀이 나온다(JPG 는 손실 압축이라 해독기마다 달라서 원래 방식에 맡긴다).
    // 흑백은 RGB 세 성분에 같은 값을 넣는다(Hello (BPM) 2026: 흑백+알파 PNG 13장, 인터레이스 1장이 원래 방식으로 메인 스레드에서 풀렸다).
    // 결과는 유니티 텍스처 순서(아래 줄부터)로 뒤집어서 관리 힙 밖(AllocHGlobal)에 담는다.
    // 수백 MB 짜리 배열을 GC 힙에 만들지 않기 위해서다.
    // libdeflate (MIT, Eric Biggers) 로 zlib 압축을 푼다. 게임(Mono)의 DeflateStream(zlib) 보다 훨씬 빠르다.
    // Hello (BPM) 2026 불러오기 측정: 해독 시간(작업 스레드 합계)의 70%가 압축 풀기(45.7초)였다.
    // 압축 풀기는 무손실이라 어느 엔진으로 풀어도 같은 바이트가 나온다(PNG 1,047장을 PIL 과 비교해 확인).
    // DLL 을 못 불러오면 Ready 가 false 로 남고 원래 DeflateStream 길을 쓴다.
    internal static unsafe class NativeInflate
    {
        internal static bool Ready;
        internal static string Status = "안 불러옴";

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibraryW(string path);
        [DllImport("libdeflate", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr libdeflate_alloc_decompressor();
        [DllImport("libdeflate", CallingConvention = CallingConvention.Cdecl)] private static extern void libdeflate_free_decompressor(IntPtr d);
        [DllImport("libdeflate", CallingConvention = CallingConvention.Cdecl)] private static extern int libdeflate_zlib_decompress(IntPtr d, byte* input, UIntPtr inLen, byte* output, UIntPtr outAvail, UIntPtr* actualOut);

        // 전체 경로의 DLL 을 먼저 올려 두면, 이름으로 찾는 DllImport("libdeflate") 가 이미 올라온 것을 쓴다.
        internal static void Init(string dllPath)
        {
            if (Ready) return;
            try
            {
                if (!File.Exists(dllPath)) { Status = "DLL 없음"; return; }
                if (LoadLibraryW(dllPath) == IntPtr.Zero) { Status = "DLL 불러오기 실패 (" + Marshal.GetLastWin32Error() + ")"; return; }
                var d = libdeflate_alloc_decompressor();
                if (d == IntPtr.Zero) { Status = "해독기 만들기 실패"; return; }
                libdeflate_free_decompressor(d);
                Ready = true; Status = "사용 중";
            }
            catch (Exception ex) { Ready = false; Status = "실패: " + ex.Message; }
        }

        // 스레드마다 해독기 하나와 풀 자리(네이티브 메모리) 하나를 둔다. 작업 스레드가 끝날 때 FreeThread 로 푼다.
        [ThreadStatic] private static IntPtr dec;
        [ThreadStatic] private static IntPtr buf;
        [ThreadStatic] private static long cap;

        // 성공하면 풀린 바이트(정확히 expected 바이트)의 주소. 크기가 다르거나 손상이면 null.
        internal static byte* Inflate(byte[] zlibData, int len, long expected)
        {
            if (!Ready || expected <= 0 || expected > int.MaxValue) return null;
            try
            {
                if (dec == IntPtr.Zero) { dec = libdeflate_alloc_decompressor(); if (dec == IntPtr.Zero) return null; }
                if (cap < expected)
                {
                    if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                    long want = Math.Max(expected, cap * 3 / 2);
                    buf = Marshal.AllocHGlobal((IntPtr)want); cap = want;
                }
                UIntPtr actual;
                int r;
                fixed (byte* src = zlibData)
                    r = libdeflate_zlib_decompress(dec, src, (UIntPtr)len, (byte*)buf, (UIntPtr)expected, &actual);
                if (r != 0 || (long)actual.ToUInt64() != expected) return null;   // 0 = 성공, 크기도 정확히 같아야 함
                return (byte*)buf;
            }
            catch { return null; }
        }

        internal static void FreeThread()
        {
            try
            {
                if (dec != IntPtr.Zero) { libdeflate_free_decompressor(dec); dec = IntPtr.Zero; }
                if (buf != IntPtr.Zero) { Marshal.FreeHGlobal(buf); buf = IntPtr.Zero; cap = 0; }
            }
            catch { }
        }
    }

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
            if (!sawEnd || width <= 0 || height <= 0 || (interlace != 0 && interlace != 1) || idatLen < 3) return false;

            int channels;
            if (colorType == 6 && bitDepth == 8) channels = 4;
            else if (colorType == 2 && bitDepth == 8) channels = 3;
            else if (colorType == 3 && (bitDepth == 1 || bitDepth == 2 || bitDepth == 4 || bitDepth == 8) && plte != null) channels = 1;
            else if (colorType == 4 && bitDepth == 8) channels = 2;
            else if (colorType == 0 && bitDepth == 8) channels = 1;
            else return false;

            // RGB·흑백에 투명색이 지정돼 있으면 알파가 필요하다
            bool rgbKey = colorType == 2 && trns != null && trns.Length >= 6;
            bool grayKey = colorType == 0 && trns != null && trns.Length >= 2;
            format = ((colorType == 2 && !rgbKey) || (colorType == 0 && !grayKey)) ? FormatRGB24 : FormatRGBA32;
            int outBpp = format == FormatRGB24 ? 3 : 4;
            var cv = new Conv { ColorType = colorType, BitDepth = bitDepth, Plte = plte, Trns = trns, RgbKey = rgbKey, GrayKey = grayKey, OutBpp = outBpp };

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

            pixels = Marshal.AllocHGlobal((IntPtr)size);
            bool ok = false;
            long tInflate = 0, tFilter = 0;
            try
            {
                // libdeflate 가 있으면: 압축된 줄 전체를 한 번에 풀고(필터 바이트 포함 "원본 줄" 묶음), 거기서 결과 메모리로 필터를 되돌린다.
                // 풀린 크기가 PNG 머리 정보로 계산한 크기와 정확히 같아야만 쓴다. 아니면(손상, 여분 데이터) 아래 원래 길로 다시 푼다.
                if (NativeInflate.Ready)
                {
                    long rawLen = 0;
                    int np = interlace == 1 ? 7 : 1;
                    for (int ps = 0; ps < np; ps++)
                    {
                        int xs = interlace == 1 ? A7x[ps] : 0, ys = interlace == 1 ? A7y[ps] : 0, dx = interlace == 1 ? A7dx[ps] : 1, dy = interlace == 1 ? A7dy[ps] : 1;
                        long pw = (width - xs + dx - 1) / dx, ph = (height - ys + dy - 1) / dy;
                        if (pw > 0 && ph > 0) rawLen += ph * (1 + (pw * channels * bitDepth + 7) / 8);
                    }
                    long n0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    byte* raw = NativeInflate.Inflate(idat, (int)idatLen, rawLen);
                    long n1 = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (raw != null && FromRaw(raw, width, height, channels, bitDepth, bpp, interlace, colorType, rgbKey, outBpp, outRow, cv, (byte*)pixels, prev, cur))
                    {
                        System.Threading.Interlocked.Add(ref InflateTicks, n1 - n0);
                        System.Threading.Interlocked.Add(ref FilterTicks, System.Diagnostics.Stopwatch.GetTimestamp() - n1);
                        System.Threading.Interlocked.Increment(ref NativeImages);
                        if (interlace == 1 || colorType == 0 || colorType == 4) System.Threading.Interlocked.Increment(ref NewKinds);
                        ok = true;
                        return true;
                    }
                    System.Threading.Interlocked.Increment(ref NativeFallbacks);
                }

                using (var z = new DeflateStream(new MemoryStream(idat, 2, (int)idatLen - 2), CompressionMode.Decompress))
                {
                    var one = oneBuf ?? (oneBuf = new byte[1]);
                    byte* dst0 = (byte*)pixels;
                    // 인터레이스가 아니면 한 번(전체), Adam7 이면 일곱 번. 각 패스는 (시작 x, 시작 y, x 간격, y 간격) 의 부분 이미지다.
                    int passes = interlace == 1 ? 7 : 1;
                    for (int ps = 0; ps < passes; ps++)
                    {
                        int xs = interlace == 1 ? A7x[ps] : 0, ys = interlace == 1 ? A7y[ps] : 0;
                        int dx = interlace == 1 ? A7dx[ps] : 1, dy = interlace == 1 ? A7dy[ps] : 1;
                        int pw = (width - xs + dx - 1) / dx, ph = (height - ys + dy - 1) / dy;
                        if (pw <= 0 || ph <= 0) continue;   // 빈 패스는 데이터가 없다
                        int pRow = (int)(((long)pw * channels * bitDepth + 7) / 8);
                        Array.Clear(prev, 0, pRow);   // 패스마다 첫 줄의 "윗줄"은 0

                        // 빠른 길: 인터레이스 아닌 RGBA / RGB(투명색 없음)는 출력 모양이 PNG 줄과 똑같다.
                        // 압축을 결과 메모리에 바로 풀고 그 자리에서 필터를 되돌린다 ("윗줄" = 결과 메모리의 바로 위 줄).
                        // 예전에는 줄마다 임시 배열에 풀고 필터를 되돌린 뒤 결과로 한 번 더 복사했다(2026 이면 10GB 복사).
                        if (passes == 1 && (colorType == 6 || (colorType == 2 && !rgbKey)) && pRow == outRow)
                        {
                            fixed (byte* zero = prev)
                            {
                                for (int y = 0; y < height; y++)
                                {
                                    long a0 = System.Diagnostics.Stopwatch.GetTimestamp();
                                    if (!ReadFull(z, one, 1)) return false;
                                    int filter = one[0];
                                    byte* row = dst0 + (height - 1 - y) * outRow;
                                    if (!ReadFullPtr(z, row, pRow)) return false;
                                    long a1 = System.Diagnostics.Stopwatch.GetTimestamp();
                                    byte* up = y == 0 ? zero : row + outRow;   // 이미지의 윗줄 = 메모리에서는 다음 줄 (아래 줄부터 담으므로)
                                    if (!UnfilterPtr(row, up, pRow, bpp, filter)) return false;
                                    tFilter += System.Diagnostics.Stopwatch.GetTimestamp() - a1;
                                    tInflate += a1 - a0;
                                }
                            }
                            continue;
                        }

                        for (int py = 0; py < ph; py++)
                        {
                            long a0 = System.Diagnostics.Stopwatch.GetTimestamp();
                            if (!ReadFull(z, one, 1)) return false;
                            int filter = one[0];
                            if (!ReadFull(z, cur, pRow)) return false;
                            long a1 = System.Diagnostics.Stopwatch.GetTimestamp();
                            if (!Unfilter(cur, prev, pRow, bpp, filter)) return false;

                            int y = ys + py * dy;
                            byte* dst = dst0 + (height - 1 - y) * outRow + (long)xs * outBpp;   // 유니티 텍스처는 아래 줄이 먼저
                            if (dx == 1 && (colorType == 6 || (colorType == 2 && !rgbKey)))
                                Marshal.Copy(cur, 0, (IntPtr)dst, pRow);
                            else
                                Convert(cur, dst, pw, dx * outBpp, cv);
                            tFilter += System.Diagnostics.Stopwatch.GetTimestamp() - a1;
                            tInflate += a1 - a0;

                            var t = prev; prev = cur; cur = t;
                        }
                    }
                }
                ok = true;
                System.Threading.Interlocked.Add(ref InflateTicks, tInflate);
                System.Threading.Interlocked.Add(ref FilterTicks, tFilter);
                if (interlace == 1 || colorType == 0 || colorType == 4) System.Threading.Interlocked.Increment(ref NewKinds);
                return true;
            }
            catch { return false; }
            finally
            {
                if (!ok) { Marshal.FreeHGlobal(pixels); pixels = IntPtr.Zero; size = 0; }
            }
        }

        // 해독 시간 나눠 보기 (모든 작업 스레드 합계): 압축 풀기 / 필터 되돌리기+픽셀 옮기기. 맵 불러오기 끝에 로그로 남긴다.
        internal static long InflateTicks, FilterTicks, NewKinds, NativeImages, NativeFallbacks;
        internal static void ResetStats() { InflateTicks = FilterTicks = NewKinds = NativeImages = NativeFallbacks = 0; }

        // libdeflate 로 한 번에 푼 "원본 줄" 묶음(줄마다 필터 바이트 1 + 데이터)에서 결과를 만든다. 원래 길과 같은 규칙.
        private static bool FromRaw(byte* raw, int width, int height, int channels, int bitDepth, int bpp, int interlace, int colorType, bool rgbKey,
                                    int outBpp, long outRow, Conv cv, byte* dst0, byte[] zeroBuf, byte[] rowBuf)
        {
            byte* src = raw;
            int passes = interlace == 1 ? 7 : 1;
            fixed (byte* zero0 = zeroBuf)
            {
                for (int ps = 0; ps < passes; ps++)
                {
                    int xs = interlace == 1 ? A7x[ps] : 0, ys = interlace == 1 ? A7y[ps] : 0;
                    int dx = interlace == 1 ? A7dx[ps] : 1, dy = interlace == 1 ? A7dy[ps] : 1;
                    int pw = (width - xs + dx - 1) / dx, ph = (height - ys + dy - 1) / dy;
                    if (pw <= 0 || ph <= 0) continue;
                    int pRow = (int)(((long)pw * channels * bitDepth + 7) / 8);
                    for (int i = 0; i < pRow; i++) zero0[i] = 0;
                    bool direct = passes == 1 && (colorType == 6 || (colorType == 2 && !rgbKey)) && pRow == outRow;
                    byte* up = zero0;   // 원본 줄 기준 윗줄(되돌린 값). 빠른 길에서는 결과 메모리의 윗줄
                    for (int py = 0; py < ph; py++, src += 1 + pRow)
                    {
                        int filter = src[0];
                        int y = ys + py * dy;
                        if (direct)
                        {
                            byte* row = dst0 + (height - 1 - y) * outRow;
                            if (!UnfilterTo(row, src + 1, up, pRow, bpp, filter)) return false;
                            up = row;
                        }
                        else
                        {
                            // 드문 형식: 원본 줄 자리에서 되돌리고(윗줄 = 바로 앞 원본 줄) 픽셀 모양을 바꿔 옮긴다
                            byte* c = src + 1;
                            if (!UnfilterPtr(c, up, pRow, bpp, filter)) return false;
                            up = c;
                            byte* dst = dst0 + (height - 1 - y) * outRow + (long)xs * outBpp;
                            Marshal.Copy((IntPtr)c, rowBuf, 0, pRow);
                            Convert(rowBuf, dst, pw, dx * outBpp, cv);
                        }
                    }
                }
            }
            return true;
        }

        // 필터를 되돌리며 결과 메모리에 바로 쓴다. s = 원본 줄(필터 적용된 값), d = 결과 줄, p = 결과의 윗줄.
        private static bool UnfilterTo(byte* d, byte* s, byte* p, int n, int bpp, int filter)
        {
            switch (filter)
            {
                case 0: Buffer.MemoryCopy(s, d, n, n); return true;
                case 1:
                    for (int i = 0; i < bpp && i < n; i++) d[i] = s[i];
                    for (int i = bpp; i < n; i++) d[i] = (byte)(s[i] + d[i - bpp]);
                    return true;
                case 2:
                    {
                        int i = 0;
                        const ulong H = 0x8080808080808080UL, L = 0x7F7F7F7F7F7F7F7FUL;
                        for (; i + 8 <= n; i += 8)
                        {
                            ulong x = *(ulong*)(s + i), y = *(ulong*)(p + i);
                            *(ulong*)(d + i) = ((x & L) + (y & L)) ^ ((x ^ y) & H);
                        }
                        for (; i < n; i++) d[i] = (byte)(s[i] + p[i]);
                        return true;
                    }
                case 3:
                    for (int i = 0; i < bpp && i < n; i++) d[i] = (byte)(s[i] + (p[i] >> 1));
                    for (int i = bpp; i < n; i++) d[i] = (byte)(s[i] + ((d[i - bpp] + p[i]) >> 1));
                    return true;
                case 4:
                    for (int i = 0; i < bpp && i < n; i++) d[i] = (byte)(s[i] + p[i]);
                    {
                        byte* di = d + bpp, si = s + bpp, ai = d, bi = p + bpp, ci = p, end = d + n;
                        for (; di < end; di++, si++, ai++, bi++, ci++)
                        {
                            int a = *ai, b = *bi, cc = *ci;
                            int pa = b - cc, pb = a - cc, pc = pa + pb;
                            pa = pa < 0 ? -pa : pa; pb = pb < 0 ? -pb : pb; pc = pc < 0 ? -pc : pc;
                            *di = (byte)(*si + ((pa <= pb && pa <= pc) ? a : (pb <= pc ? b : cc)));
                        }
                    }
                    return true;
                default: return false;
            }
        }

        // Adam7 패스: 시작 x, 시작 y, x 간격, y 간격 (PNG 명세)
        private static readonly int[] A7x = { 0, 4, 0, 2, 0, 1, 0 }, A7y = { 0, 0, 4, 0, 2, 0, 1 }, A7dx = { 8, 8, 4, 4, 2, 2, 1 }, A7dy = { 8, 8, 8, 4, 4, 2, 2 };

        private sealed class Conv { public int ColorType, BitDepth, OutBpp; public byte[] Plte, Trns; public bool RgbKey, GrayKey; }

        // 필터를 되돌린 한 줄(count 픽셀)을 출력 픽셀로 옮긴다. 픽셀 하나마다 step 바이트씩 건너뛴다(인터레이스 패스).
        private static void Convert(byte[] cur, byte* dst, int count, int step, Conv cv)
        {
            switch (cv.ColorType)
            {
                case 6:
                    fixed (byte* s0 = cur) { byte* s = s0; for (int x = 0; x < count; x++, s += 4, dst += step) { dst[0] = s[0]; dst[1] = s[1]; dst[2] = s[2]; dst[3] = s[3]; } }
                    return;
                case 2:
                    if (cv.RgbKey) { RgbKey(cur, dst, count, cv.Trns, step); return; }
                    fixed (byte* s0 = cur) { byte* s = s0; for (int x = 0; x < count; x++, s += 3, dst += step) { dst[0] = s[0]; dst[1] = s[1]; dst[2] = s[2]; } }
                    return;
                case 4:
                    fixed (byte* s0 = cur) { byte* s = s0; for (int x = 0; x < count; x++, s += 2, dst += step) { byte g = s[0]; dst[0] = g; dst[1] = g; dst[2] = g; dst[3] = s[1]; } }
                    return;
                case 0:
                    {
                        byte key = cv.GrayKey ? cv.Trns[1] : (byte)0;   // 8비트면 16비트 값의 아래 바이트
                        fixed (byte* s0 = cur)
                        {
                            byte* s = s0;
                            for (int x = 0; x < count; x++, s++, dst += step)
                            {
                                byte g = s[0]; dst[0] = g; dst[1] = g; dst[2] = g;
                                if (cv.OutBpp == 4) dst[3] = (byte)(cv.GrayKey && g == key ? 0 : 255);
                            }
                        }
                        return;
                    }
                default:
                    Palette(cur, dst, count, cv.BitDepth, cv.Plte, cv.Trns, step);
                    return;
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
            fixed (byte* c = cur, p = prev) return UnfilterPtr(c, p, n, bpp, filter);
        }

        // 필터 되돌리기 (PNG 명세 그대로). c = 이번 줄(제자리에서 바뀜), p = 윗줄(이미 되돌린 값).
        private static bool UnfilterPtr(byte* c, byte* p, int n, int bpp, int filter)
        {
            switch (filter)
            {
                case 0: return true;
                case 1: for (int i = bpp; i < n; i++) c[i] = (byte)(c[i] + c[i - bpp]); return true;
                case 2:
                    {
                        // 8바이트씩: 바이트끼리 더하되 자리올림이 옆 바이트로 넘어가지 않게 (SWAR)
                        int i = 0;
                        const ulong H = 0x8080808080808080UL, L = 0x7F7F7F7F7F7F7F7FUL;
                        for (; i + 8 <= n; i += 8)
                        {
                            ulong x = *(ulong*)(c + i), y = *(ulong*)(p + i);
                            *(ulong*)(c + i) = ((x & L) + (y & L)) ^ ((x ^ y) & H);
                        }
                        for (; i < n; i++) c[i] = (byte)(c[i] + p[i]);
                        return true;
                    }
                case 3:
                    for (int i = 0; i < bpp && i < n; i++) c[i] = (byte)(c[i] + (p[i] >> 1));
                    for (int i = bpp; i < n; i++) c[i] = (byte)(c[i] + ((c[i - bpp] + p[i]) >> 1));
                    return true;
                case 4:
                    for (int i = 0; i < bpp && i < n; i++) c[i] = (byte)(c[i] + p[i]);
                    {
                        byte* ci = c + bpp, ai = c, bi = p + bpp, cci = p, end = c + n;
                        for (; ci < end; ci++, ai++, bi++, cci++)
                        {
                            int a = *ai, b = *bi, cc = *cci;
                            int pa = b - cc, pb = a - cc, pc = pa + pb;
                            pa = pa < 0 ? -pa : pa; pb = pb < 0 ? -pb : pb; pc = pc < 0 ? -pc : pc;
                            *ci = (byte)(*ci + ((pa <= pb && pa <= pc) ? a : (pb <= pc ? b : cc)));
                        }
                    }
                    return true;
                default: return false;
            }
        }

        // 압축을 풀어 네이티브 메모리에 바로 쓴다. 게임(Mono)의 DeflateStream 은 Read(Span) 을 덮어쓰지 않아서 기본 구현이
        // 빌린 배열에 풀고 한 번 더 복사한다. 안쪽의 ReadCore(Span) 이 있으면 그것을 불러 복사를 없앤다(없으면 Read(Span)).
        private delegate int SpanRead(DeflateStream s, Span<byte> dst);
        private static SpanRead spanRead;
        private static bool spanLooked;
        private static bool ReadFullPtr(DeflateStream z, byte* dst, int count)
        {
            if (!spanLooked)
            {
                spanLooked = true;
                try
                {
                    var m = typeof(DeflateStream).GetMethod("ReadCore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(Span<byte>) }, null);
                    if (m != null && m.ReturnType == typeof(int)) spanRead = (SpanRead)Delegate.CreateDelegate(typeof(SpanRead), m);
                }
                catch { spanRead = null; }
            }
            int got = 0;
            while (got < count)
            {
                var span = new Span<byte>(dst + got, count - got);
                int n = spanRead != null ? spanRead(z, span) : z.Read(span);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        private static void RgbKey(byte[] cur, byte* dst, int width, byte[] trns, int step)
        {
            byte kr = trns[1], kg = trns[3], kb = trns[5];   // 8비트면 16비트 값의 아래 바이트
            fixed (byte* s0 = cur)
            {
                byte* s = s0;
                for (int x = 0; x < width; x++, s += 3, dst += step)
                {
                    dst[0] = s[0]; dst[1] = s[1]; dst[2] = s[2];
                    dst[3] = (byte)(s[0] == kr && s[1] == kg && s[2] == kb ? 0 : 255);
                }
            }
        }

        private static void Palette(byte[] cur, byte* dst, int width, int bitDepth, byte[] plte, byte[] trns, int step)
        {
            int entries = plte.Length / 3;
            int mask = (1 << bitDepth) - 1;
            int perByte = 8 / bitDepth;
            for (int x = 0; x < width; x++, dst += step)
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
