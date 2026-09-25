using System;

namespace StutterFix
{
    // 작업 스레드용 DXT1 / DXT5 압축기 (유니티 API 를 쓰지 않는다).
    //
    // PACL2 의 "이미지 손실 압축 허용" 은 이미지를 그래픽카드에 올리기 직전(Texture2D.Apply 앞)마다 메인 스레드에서 유니티 압축
    // (Texture2D.Compress(false))을 부른다. Arche 에서 247장 11.5초. 같은 압축을 이미지 미리 풀기의 작업 스레드에서 해 두고,
    // 압축을 부르는 순간 결과를 넣는다(TexCompress).
    //
    // 방식: 실시간 DXT 압축(J.M.P. van Waveren, "Real-Time DXT Compression", 2006)과 같은 흐름.
    //   색: 16픽셀의 채널별 최소·최대(상자)를 범위의 1/16 만큼 안쪽으로 당겨 두 끝색으로 쓰고, 네 색(끝색 둘과 1/3·2/3 지점) 중
    //       가장 가까운 것을 고른다. 알파(DXT5): 최소·최대를 1/32 만큼 당기고 8단계 중 가장 가까운 것.
    // 픽셀 순서는 유니티 텍스처 저장 순서 그대로(아래 줄이 먼저) 4x4 블록으로 자른다. 유니티 압축도 저장된 픽셀에서 블록을 만든다
    // (개발자용 비교에서 유니티 압축 결과와 원본 대비 오차를 함께 재어 확인).
    internal static unsafe class DxtEncoder
    {
        // layout: 0 = RGBA32 (R,G,B,A), 1 = ARGB32 (A,R,G,B), 2 = RGB24 (R,G,B)
        internal static void Encode(byte* src, int w, int h, int layout, bool dxt5, byte* dst) { EncodeRows(src, w, h, layout, dxt5, dst, 0, h / 4); }

        // 블록 줄 by0 ~ by1-1 만 압축 (dst 는 전체 결과의 시작. 여러 작업 스레드가 한 이미지를 줄 묶음으로 나눠 압축한다)
        internal static void EncodeRows(byte* src, int w, int h, int layout, bool dxt5, byte* dst, int by0, int by1)
        {
            int bpp = layout == 2 ? 3 : 4;
            int ro = layout == 1 ? 1 : 0, go = ro + 1, bo = ro + 2, ao = layout == 1 ? 0 : 3;
            byte* blk = stackalloc byte[64];   // 16픽셀 x RGBA
            int bw = w / 4;
            long rowStride = (long)w * bpp;
            dst += (long)by0 * bw * (dxt5 ? 16 : 8);
            for (int by = by0; by < by1; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    // 블록 16픽셀을 RGBA 로 모은다
                    for (int y = 0; y < 4; y++)
                    {
                        byte* s = src + (by * 4 + y) * rowStride + (long)bx * 4 * bpp;
                        byte* d = blk + y * 16;
                        for (int x = 0; x < 4; x++, s += bpp, d += 4)
                        {
                            d[0] = s[ro]; d[1] = s[go]; d[2] = s[bo]; d[3] = bpp == 4 ? s[ao] : (byte)255;
                        }
                    }
                    if (dxt5) { EncodeAlpha(blk, dst); dst += 8; }
                    EncodeColor(blk, dst); dst += 8;
                }
            }
        }

        internal static long BlocksSize(int w, int h, bool dxt5) { return (long)(w / 4) * (h / 4) * (dxt5 ? 16 : 8); }

        // 색 블록: 주축(PCA)으로 끝색을 고르고 최소제곱으로 두 번 다듬는다(stb_dxt 와 같은 흐름). 오차가 줄 때만 받아들인다.
        private static void EncodeColor(byte* p, byte* dst)
        {
            // 합과 곱의 합 (정수. 유니티 Mono 는 실수 계산이 느리다)
            int sr = 0, sg = 0, sb = 0, srr = 0, srg = 0, srb = 0, sgg = 0, sgb = 0, sbb = 0;
            int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
            for (int i = 0; i < 64; i += 4)
            {
                int r = p[i], g = p[i + 1], b = p[i + 2];
                sr += r; sg += g; sb += b;
                srr += r * r; srg += r * g; srb += r * b; sgg += g * g; sgb += g * b; sbb += b * b;
                if (r < minR) minR = r; if (r > maxR) maxR = r; if (g < minG) minG = g; if (g > maxG) maxG = g; if (b < minB) minB = b; if (b > maxB) maxB = b;
            }
            if (minR == maxR && minG == maxG && minB == maxB) { SolidColor(minR, minG, minB, dst); return; }
            // 거의 한 색이면(채널 범위 합이 작으면) 평균색 단색 최적만 쓰고 끝 (그라데이션 이미지 대부분의 블록)
            if (NearSolid > 0 && (maxR - minR) + (maxG - minG) + (maxB - minB) <= NearSolid)
            {
                int ar0 = (sr + 8) >> 4, ag0 = (sg + 8) >> 4, ab0 = (sb + 8) >> 4;
                int n0 = (O5a[ar0] << 11) | (O6a[ag0] << 5) | O5a[ab0], n1 = (O5b[ar0] << 11) | (O6b[ag0] << 5) | O5b[ab0];
                uint nidx; Match(p, n0, n1, out nidx);
                if (n0 < n1) { int t = n0; n0 = n1; n1 = t; nidx ^= 0x55555555u; } else if (n0 == n1) nidx = 0;
                dst[0] = (byte)n0; dst[1] = (byte)(n0 >> 8); dst[2] = (byte)n1; dst[3] = (byte)(n1 >> 8);
                dst[4] = (byte)nidx; dst[5] = (byte)(nidx >> 8); dst[6] = (byte)(nidx >> 16); dst[7] = (byte)(nidx >> 24);
                return;
            }
            // 공분산 x 256 (3x3 만 실수로)
            float crr = 16 * srr - sr * sr, crg = 16 * srg - sr * sg, crb = 16 * srb - sr * sb, cgg = 16 * sgg - sg * sg, cgb = 16 * sgb - sg * sb, cbb = 16 * sbb - sb * sb;
            // 거듭제곱법으로 주축
            float vr = maxR - minR, vg = maxG - minG, vb = maxB - minB;
            for (int it = 0; it < 4; it++)
            {
                float nr = vr * crr + vg * crg + vb * crb, ng = vr * crg + vg * cgg + vb * cgb, nb = vr * crb + vg * cgb + vb * cbb;
                float m = Math.Max(Math.Abs(nr), Math.Max(Math.Abs(ng), Math.Abs(nb)));
                if (m < 1e-6f) break;
                vr = nr / m; vg = ng / m; vb = nb / m;
            }
            // 축 위 양 끝 픽셀 (정수 축)
            int ar = (int)(vr * 256), ag = (int)(vg * 256), ab = (int)(vb * 256);
            int lo = int.MaxValue, hi = int.MinValue, li = 0, hiI = 0;
            for (int i = 0; i < 16; i++)
            {
                int d = p[i * 4] * ar + p[i * 4 + 1] * ag + p[i * 4 + 2] * ab;
                if (d < lo) { lo = d; li = i; }
                if (d > hi) { hi = d; hiI = i; }
            }
            int c0 = To565(p[hiI * 4], p[hiI * 4 + 1], p[hiI * 4 + 2]);
            int c1 = To565(p[li * 4], p[li * 4 + 1], p[li * 4 + 2]);
            uint idx; int err = Match(p, c0, c1, out idx);
            // 후보 2: 평균색 하나로 - 두 끝색의 2/3 지점이 평균색에 딱 맞는 쌍(단색 최적 표). 거의 한 색인 블록(그라데이션)에서 크게 낫다
            if (UseSolid && (maxR - minR) + (maxG - minG) + (maxB - minB) <= SolidMaxRange)   // 범위가 크면 평균색 하나로는 이기지 못한다
            {
                int ar2 = (sr + 8) >> 4, ag2 = (sg + 8) >> 4, ab2 = (sb + 8) >> 4;
                int s0 = (O5a[ar2] << 11) | (O6a[ag2] << 5) | O5a[ab2], s1 = (O5b[ar2] << 11) | (O6b[ag2] << 5) | O5b[ab2];
                uint sidx; int serr = Match(p, s0, s1, out sidx);
                if (serr < err) { c0 = s0; c1 = s1; idx = sidx; err = serr; }
            }
            // 후보 3: 채널별 상자를 범위의 1/16 만큼 당긴 두 끝 (빠른 방식) - 더 나으면 이것에서 출발
            if (UseBox)
            {
                int ir = (maxR - minR) >> 4, ig = (maxG - minG) >> 4, ib = (maxB - minB) >> 4;
                int b0 = To565(maxR - ir, maxG - ig, maxB - ib), b1 = To565(minR + ir, minG + ig, minB + ib);
                uint bidx; int berr = Match(p, b0, b1, out bidx);
                if (berr < err) { c0 = b0; c1 = b1; idx = bidx; err = berr; }
            }
            // 최소제곱으로 다듬기 (오차가 줄어드는 동안 최대 RefinePasses 번)
            for (int pass = 0; pass < RefinePasses; pass++)
            {
                int n0, n1;
                if (!Refine(p, idx, out n0, out n1)) break;
                uint nidx; int nerr = Match(p, n0, n1, out nidx);
                if (nerr >= err) break;
                c0 = n0; c1 = n1; idx = nidx; err = nerr;
            }
            // 4색 모드는 c0 > c1 이어야 한다
            if (c0 < c1)
            {
                int t = c0; c0 = c1; c1 = t;
                idx ^= 0x55555555u;   // 0<->1, 2<->3
            }
            else if (c0 == c1) idx = 0;
            dst[0] = (byte)c0; dst[1] = (byte)(c0 >> 8); dst[2] = (byte)c1; dst[3] = (byte)(c1 >> 8);
            dst[4] = (byte)idx; dst[5] = (byte)(idx >> 8); dst[6] = (byte)(idx >> 16); dst[7] = (byte)(idx >> 24);
        }

        private static int To565(int r, int g, int b)
        {
            return (((r * 31 + 127) / 255) << 11) | (((g * 63 + 127) / 255) << 5) | ((b * 31 + 127) / 255);
        }
        private static void Expand(int c, out int r, out int g, out int b)
        {
            int r5 = (c >> 11) & 31, g6 = (c >> 5) & 63, b5 = c & 31;
            r = (r5 << 3) | (r5 >> 2); g = (g6 << 2) | (g6 >> 4); b = (b5 << 3) | (b5 >> 2);
        }

        // 네 색 중 가장 가까운 것 번호와 전체 오차(제곱). 번호 0 = c0, 1 = c1, 2 = 2/3 c0, 3 = 1/3 c0
        // 네 색은 c1 -> c0 직선 위에 있으므로 그 직선에 투영해 가까운 칸을 바로 구한다(네 번 거리를 재는 것과 같은 답).
        private static int Match(byte* p, int c0, int c1, out uint idx)
        {
            int r0, g0, b0, r1, g1, b1;
            Expand(c0, out r0, out g0, out b0); Expand(c1, out r1, out g1, out b1);
            int dr = r0 - r1, dg = g0 - g1, db = b0 - b1;
            int len2 = dr * dr + dg * dg + db * db;
            int r2 = (2 * r0 + r1) / 3, g2 = (2 * g0 + g1) / 3, b2 = (2 * b0 + b1) / 3;
            int r3 = (r0 + 2 * r1) / 3, g3 = (g0 + 2 * g1) / 3, b3 = (b0 + 2 * b1) / 3;
            idx = 0; int total = 0;
            byte* q = p + 60;
            for (int i = 15; i >= 0; i--, q -= 4)
            {
                int r = q[0], g = q[1], b = q[2];
                int s = 3;
                if (len2 > 0)
                {
                    int dot = (r - r1) * dr + (g - g1) * dg + (b - b1) * db;   // 0 이면 c1, len2 이면 c0
                    s = (dot * 6 + len2) / (2 * len2);                         // 반올림한 3 * t
                }
                int er, eg, eb; uint best;
                if (s <= 0) { best = 1; er = r - r1; eg = g - g1; eb = b - b1; }
                else if (s == 1) { best = 3; er = r - r3; eg = g - g3; eb = b - b3; }
                else if (s == 2) { best = 2; er = r - r2; eg = g - g2; eb = b - b2; }
                else { best = 0; er = r - r0; eg = g - g0; eb = b - b0; }
                total += er * er + eg * eg + eb * eb;
                idx = (idx << 2) | best;
            }
            return total;
        }

        // 고른 번호를 두고 두 끝색을 최소제곱으로 다시 구한다
        // 정수 가중치(번호 0,1,2,3 -> c0 쪽 3,0,2,1 / 3)로: c0 = 3(ΣAp·ΣB² - ΣBp·ΣAB) / det, c1 = 3(ΣBp·ΣA² - ΣAp·ΣAB) / det
        private static bool Refine(byte* p, uint idx, out int c0, out int c1)
        {
            int aa = 0, bb = 0, ab = 0, axr = 0, axg = 0, axb = 0, bxr = 0, bxg = 0, bxb = 0;
            for (int i = 0; i < 16; i++)
            {
                int k = (int)((idx >> (2 * i)) & 3);
                int a = k == 0 ? 3 : k == 1 ? 0 : k == 2 ? 2 : 1, b = 3 - a;
                aa += a * a; bb += b * b; ab += a * b;
                int r = p[i * 4], g = p[i * 4 + 1], bl = p[i * 4 + 2];
                axr += a * r; axg += a * g; axb += a * bl;
                bxr += b * r; bxg += b * g; bxb += b * bl;
            }
            int det = aa * bb - ab * ab;
            c0 = c1 = 0;
            if (det == 0) return false;
            c0 = To565(Div(3 * (axr * bb - bxr * ab), det), Div(3 * (axg * bb - bxg * ab), det), Div(3 * (axb * bb - bxb * ab), det));
            c1 = To565(Div(3 * (bxr * aa - axr * ab), det), Div(3 * (bxg * aa - axg * ab), det), Div(3 * (bxb * aa - axb * ab), det));
            return true;
        }
        // 반올림 나눗셈 후 0~255 로
        private static int Div(int n, int d)
        {
            if (d < 0) { n = -n; d = -d; }
            int q = n >= 0 ? (n + d / 2) / d : -((-n + d / 2) / d);
            return q < 0 ? 0 : q > 255 ? 255 : q;
        }

        // 한 색 블록: 두 끝색의 2/3 지점((2 c0 + c1) / 3)이 그 색에 가장 가까운 쌍을 표에서 찾아 모두 그 지점(번호 2)으로.
        // 565 로 반올림한 한 색보다 훨씬 정확하다(빨강·파랑 5비트는 8단계 간격인데 2/3 지점은 그 사이를 채운다).
        private static void SolidColor(int r, int g, int b, byte* dst)
        {
            int c0 = (O5a[r] << 11) | (O6a[g] << 5) | O5a[b], c1 = (O5b[r] << 11) | (O6b[g] << 5) | O5b[b];
            uint idx = 0xAAAAAAAAu;   // 모두 번호 2
            if (c0 < c1) { int t = c0; c0 = c1; c1 = t; idx ^= 0x55555555u; }
            else if (c0 == c1) idx = 0;
            dst[0] = (byte)c0; dst[1] = (byte)(c0 >> 8); dst[2] = (byte)c1; dst[3] = (byte)(c1 >> 8);
            dst[4] = (byte)idx; dst[5] = (byte)(idx >> 8); dst[6] = (byte)(idx >> 16); dst[7] = (byte)(idx >> 24);
        }

        internal static bool UseSolid = true, UseBox = false;
        internal static int RefinePasses = 1;
        // 설정 근거(2026-09-26, 유니티 에디터에서 Arche 이미지로 비교): 단색 최적 + PCA + 다듬기 1번 + 거의 한 색(범위 합 12 이하) 지름길 ->
        // 원본과의 평균 오차가 모든 이미지에서 유니티 Compress(false) 이하(1.37/1.17/0.97/0.73 대 1.40/1.19/1.02/0.78), 한 코어 81ns/픽셀(유니티 32).
        internal static int NearSolid = 12;
        internal static int SolidMaxRange = 48;   // 같은 화질에 7% 빠름 (시험: 9999 / 96 / 48 모두 오차 같음)

        // 단색 최적 표: 값 v 마다 (2*확장(a) + 확장(b)) / 3 이 v 에 가장 가까운 a, b (같으면 a, b 가 가까운 쪽)
        private static readonly byte[] O5a = new byte[256], O5b = new byte[256], O6a = new byte[256], O6b = new byte[256];
        static DxtEncoder() { OptTable(O5a, O5b, 5); OptTable(O6a, O6b, 6); }
        private static void OptTable(byte[] ta, byte[] tb, int bits)
        {
            int n = 1 << bits;
            for (int v = 0; v < 256; v++)
            {
                int best = int.MaxValue;
                for (int a = 0; a < n; a++)
                    for (int b = 0; b < n; b++)
                    {
                        int xa = bits == 5 ? (a << 3) | (a >> 2) : (a << 2) | (a >> 4), xb = bits == 5 ? (b << 3) | (b >> 2) : (b << 2) | (b >> 4);
                        int e = Math.Abs((2 * xa + xb) / 3 - v) * 100 + Math.Abs(xa - xb) * 3;
                        if (e < best) { best = e; ta[v] = (byte)a; tb[v] = (byte)b; }
                    }
            }
        }

        // (이전 빠른 방식: 상자 + 1/16 당기기. 비교용으로 남겨 둠)
        private static void EncodeColorFast(byte* p, byte* dst)
        {
            int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
            for (int i = 0; i < 64; i += 4)
            {
                int r = p[i], g = p[i + 1], b = p[i + 2];
                if (r < minR) minR = r; if (r > maxR) maxR = r;
                if (g < minG) minG = g; if (g > maxG) maxG = g;
                if (b < minB) minB = b; if (b > maxB) maxB = b;
            }
            // 범위의 1/16 만큼 안쪽으로
            int ir = (maxR - minR) >> 4, ig = (maxG - minG) >> 4, ib = (maxB - minB) >> 4;
            minR = Math.Min(255, minR + ir); minG = Math.Min(255, minG + ig); minB = Math.Min(255, minB + ib);
            maxR = Math.Max(0, maxR - ir); maxG = Math.Max(0, maxG - ig); maxB = Math.Max(0, maxB - ib);

            int c0 = ((maxR >> 3) << 11) | ((maxG >> 2) << 5) | (maxB >> 3);
            int c1 = ((minR >> 3) << 11) | ((minG >> 2) << 5) | (minB >> 3);
            uint idx = 0;
            if (c0 != c1)
            {
                // 565 를 다시 8비트로 펼친 네 색
                int r0 = ((c0 >> 11) & 31) << 3 | ((c0 >> 11) & 31) >> 2, g0 = ((c0 >> 5) & 63) << 2 | ((c0 >> 5) & 63) >> 4, b0 = (c0 & 31) << 3 | (c0 & 31) >> 2;
                int r1 = ((c1 >> 11) & 31) << 3 | ((c1 >> 11) & 31) >> 2, g1 = ((c1 >> 5) & 63) << 2 | ((c1 >> 5) & 63) >> 4, b1 = (c1 & 31) << 3 | (c1 & 31) >> 2;
                int r2 = (2 * r0 + r1) / 3, g2 = (2 * g0 + g1) / 3, b2 = (2 * b0 + b1) / 3;
                int r3 = (r0 + 2 * r1) / 3, g3 = (g0 + 2 * g1) / 3, b3 = (b0 + 2 * b1) / 3;
                for (int i = 15; i >= 0; i--)
                {
                    int r = p[i * 4], g = p[i * 4 + 1], b = p[i * 4 + 2];
                    int d0 = Math.Abs(r - r0) + Math.Abs(g - g0) + Math.Abs(b - b0);
                    int d1 = Math.Abs(r - r1) + Math.Abs(g - g1) + Math.Abs(b - b1);
                    int d2 = Math.Abs(r - r2) + Math.Abs(g - g2) + Math.Abs(b - b2);
                    int d3 = Math.Abs(r - r3) + Math.Abs(g - g3) + Math.Abs(b - b3);
                    // 가장 가까운 것 (같으면 앞 번호). 번호: 0 = c0, 1 = c1, 2 = 2/3 c0, 3 = 1/3 c0
                    int best = 0, bd = d0;
                    if (d1 < bd) { bd = d1; best = 1; }
                    if (d2 < bd) { bd = d2; best = 2; }
                    if (d3 < bd) { best = 3; }
                    idx = (idx << 2) | (uint)best;
                }
            }
            dst[0] = (byte)c0; dst[1] = (byte)(c0 >> 8); dst[2] = (byte)c1; dst[3] = (byte)(c1 >> 8);
            dst[4] = (byte)idx; dst[5] = (byte)(idx >> 8); dst[6] = (byte)(idx >> 16); dst[7] = (byte)(idx >> 24);
        }

        // 알파 블록: 최소·최대 그대로와 1/32 당긴 것 둘 다 해 보고 오차가 작은 쪽
        private static void EncodeAlpha(byte* p, byte* dst)
        {
            int min = 255, max = 0;
            for (int i = 3; i < 64; i += 4) { int a = p[i]; if (a < min) min = a; if (a > max) max = a; }
            ulong bits; int e1 = AlphaTry(p, max, min, out bits);
            int inset = (max - min) >> 5;
            if (inset > 0)
            {
                ulong b2; int e2 = AlphaTry(p, max - inset, min + inset, out b2);
                if (e2 < e1) { bits = b2; max -= inset; min += inset; }
            }
            dst[0] = (byte)max; dst[1] = (byte)min;
            if (max == min) bits = 0;
            for (int k = 0; k < 6; k++) dst[2 + k] = (byte)(bits >> (8 * k));
        }
        // 8단계: 0 = max, 1 = min, 2..7 = (6max+min)/7 ... (max+6min)/7
        // 8단계는 max 에서 min 까지 고르게 놓여 있으므로 칸 번호를 계산으로 바로 구한다(가까운 칸 찾기와 같은 답)
        private static int AlphaTry(byte* p, int max, int min, out ulong bits)
        {
            bits = 0;
            int range = max - min, total = 0;
            if (range <= 0) { for (int i = 3; i < 64; i += 4) total += (p[i] - max) * (p[i] - max); return total; }
            for (int i = 15; i >= 0; i--)
            {
                int a = p[i * 4 + 3];
                int s = ((max - a) * 14 + range) / (2 * range);   // 반올림한 7 * (max - a) / range
                if (s < 0) s = 0; else if (s > 7) s = 7;
                int v = ((7 - s) * max + s * min) / 7;
                int best = s == 0 ? 0 : s == 7 ? 1 : s + 1;
                total += (a - v) * (a - v);
                bits = (bits << 3) | (uint)best;
            }
            return total;
        }

        // ── 개발자용: 풀어서 원본과의 평균 오차 재기 (우리 압축 / 유니티 압축 비교) ──
        internal static double MeanError(byte* orig, int w, int h, int layout, byte* dxt, bool dxt5)
        {
            int bpp = layout == 2 ? 3 : 4;
            int ro = layout == 1 ? 1 : 0, go = ro + 1, bo = ro + 2, ao = layout == 1 ? 0 : 3;
            long sum = 0, n = 0;
            int* cr = stackalloc int[4]; int* cg = stackalloc int[4]; int* cb = stackalloc int[4]; int* pa = stackalloc int[8];
            int bw = w / 4, bh = h / 4;
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    ulong abits = 0;
                    if (dxt5)
                    {
                        int a0 = dxt[0], a1 = dxt[1];
                        pa[0] = a0; pa[1] = a1;
                        if (a0 > a1) for (int k = 1; k <= 6; k++) pa[k + 1] = ((7 - k) * a0 + k * a1) / 7;
                        else { for (int k = 1; k <= 4; k++) pa[k + 1] = ((5 - k) * a0 + k * a1) / 5; pa[6] = 0; pa[7] = 255; }
                        for (int k = 0; k < 6; k++) abits |= (ulong)dxt[2 + k] << (8 * k);
                        dxt += 8;
                    }
                    int c0 = dxt[0] | dxt[1] << 8, c1 = dxt[2] | dxt[3] << 8;
                    uint ci = (uint)(dxt[4] | dxt[5] << 8 | dxt[6] << 16 | dxt[7] << 24);
                    cr[0] = ((c0 >> 11) & 31) * 255 / 31; cg[0] = ((c0 >> 5) & 63) * 255 / 63; cb[0] = (c0 & 31) * 255 / 31;
                    cr[1] = ((c1 >> 11) & 31) * 255 / 31; cg[1] = ((c1 >> 5) & 63) * 255 / 63; cb[1] = (c1 & 31) * 255 / 31;
                    if (c0 > c1 || dxt5) { cr[2] = (2 * cr[0] + cr[1]) / 3; cg[2] = (2 * cg[0] + cg[1]) / 3; cb[2] = (2 * cb[0] + cb[1]) / 3; cr[3] = (cr[0] + 2 * cr[1]) / 3; cg[3] = (cg[0] + 2 * cg[1]) / 3; cb[3] = (cb[0] + 2 * cb[1]) / 3; }
                    else { cr[2] = (cr[0] + cr[1]) / 2; cg[2] = (cg[0] + cg[1]) / 2; cb[2] = (cb[0] + cb[1]) / 2; cr[3] = cg[3] = cb[3] = 0; }
                    dxt += 8;
                    for (int y = 0; y < 4; y++)
                        for (int x = 0; x < 4; x++)
                        {
                            int i = y * 4 + x;
                            byte* s = orig + ((long)(by * 4 + y) * w + bx * 4 + x) * bpp;
                            int k = (int)((ci >> (2 * i)) & 3);
                            sum += Math.Abs(s[ro] - cr[k]) + Math.Abs(s[go] - cg[k]) + Math.Abs(s[bo] - cb[k]);
                            n += 3;
                            if (dxt5 && bpp == 4) { sum += Math.Abs(s[ao] - pa[(int)((abits >> (3 * i)) & 7)]); n++; }
                        }
                }
            return n > 0 ? (double)sum / n : 0;
        }
    }
}
