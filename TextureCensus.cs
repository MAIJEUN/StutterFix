using System;
using System.Collections.Generic;
using UnityEngine;

namespace StutterFix
{
    // 게임이 VRAM을 무엇에 쓰는지 센다 (F6).
    //
    // D3D12로 돌리면 평균 프레임이 D3D11보다 압도적으로 높지만(190 vs 140),
    // VRAM이 8GB 한도에 걸려 넘친 590MB를 옮기는 순간 60~80ms씩 멈춘다.
    // 게임이 전용 VRAM을 5.5GB 쓰는데, 그중 무엇이 큰지 알아야 줄일 곳이 정해진다.
    //   - 압축 안 된 장식 이미지(RGBA32)라면 DXT로 압축해 4분의 1로 줄일 수 있다
    //   - 화면 크기 버퍼(RenderTexture)라면 필터 쪽을 봐야 한다
    //
    // 한 번 누를 때 모든 텍스처를 훑으므로 그 순간 잠깐 멈춘다. 곡 중에는 누르지 않는다.
    public static class TextureCensus
    {
        internal static string LastReport = "(F6을 누르면 VRAM 사용처를 셉니다)";

        private class Group { public long Bytes; public int Count; }

        internal static void Run()
        {
            try
            {
                var byFormat = new Dictionary<string, Group>();
                var big = new List<KeyValuePair<long, string>>();
                long total = 0, rtTotal = 0, texTotal = 0, uncompressed = 0;
                int count = 0;

                foreach (var t in Resources.FindObjectsOfTypeAll<Texture>())
                {
                    if (t == null) continue;
                    long bytes = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(t);
                    if (bytes <= 0) continue;
                    count++;
                    total += bytes;

                    string kind;
                    var t2 = t as Texture2D;
                    var rt = t as RenderTexture;
                    if (rt != null) { kind = "RenderTexture " + rt.format; rtTotal += bytes; }
                    else if (t2 != null)
                    {
                        kind = "Texture2D " + t2.format + (t2.mipmapCount > 1 ? " +mip" : "");
                        texTotal += bytes;
                        if (t2.format == TextureFormat.RGBA32 || t2.format == TextureFormat.ARGB32 || t2.format == TextureFormat.RGB24 || t2.format == TextureFormat.BGRA32)
                            uncompressed += bytes;
                    }
                    else kind = t.GetType().Name;

                    Group g;
                    if (!byFormat.TryGetValue(kind, out g)) { g = new Group(); byFormat[kind] = g; }
                    g.Bytes += bytes;
                    g.Count++;

                    if (bytes >= 8L * 1048576)
                        big.Add(new KeyValuePair<long, string>(bytes, string.Format("{0} ({1}x{2}, {3})", t.name, t.width, t.height, kind)));
                }

                var log = Main.Entry.Logger;
                LastReport = string.Format("텍스처 {0}개, 합계 {1:N0}MB | 이미지 {2:N0}MB (그중 압축 안 됨 {3:N0}MB) | 화면버퍼 {4:N0}MB",
                    count, total / 1048576.0, texTotal / 1048576.0, uncompressed / 1048576.0, rtTotal / 1048576.0);
                log.Log("[VRAM] " + LastReport);

                var groups = new List<KeyValuePair<string, Group>>(byFormat);
                groups.Sort((a, b) => b.Value.Bytes.CompareTo(a.Value.Bytes));
                foreach (var kv in groups)
                {
                    if (kv.Value.Bytes < 1048576) continue;
                    log.Log(string.Format("[VRAM]   {0,-36} {1,7:N0}MB  {2}개", kv.Key, kv.Value.Bytes / 1048576.0, kv.Value.Count));
                }

                big.Sort((a, b) => b.Key.CompareTo(a.Key));
                for (int i = 0; i < big.Count && i < 15; i++)
                    log.Log(string.Format("[VRAM]   큰 것 {0,5:N0}MB  {1}", big[i].Key / 1048576.0, big[i].Value));

                CountOthers(log);
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("[VRAM] 세기 실패: " + ex.Message);
            }
        }

        // 텍스처는 다 합쳐 100MB 안팎이었는데 게임은 VRAM을 5.3GB 쓴다. 이미지는 범인이 아니다.
        // 이 맵은 오브젝트가 23만 개라 도형 데이터(메시)나 D3D12 드라이버가 잡아 두는 메모리가 유력하다.
        private static void CountOthers(UnityModManagerNet.UnityModManager.ModEntry.ModLogger log)
        {
            try
            {
                long meshBytes = 0; int meshCount = 0, meshBig = 0;
                var meshNames = new Dictionary<string, Group>();
                foreach (var m in Resources.FindObjectsOfTypeAll<Mesh>())
                {
                    if (m == null) continue;
                    long b = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(m);
                    meshBytes += b; meshCount++;
                    if (b >= 1048576) meshBig++;
                    string key = string.IsNullOrEmpty(m.name) ? "(이름 없음)" : m.name;
                    Group g;
                    if (!meshNames.TryGetValue(key, out g)) { g = new Group(); meshNames[key] = g; }
                    g.Bytes += b; g.Count++;
                }

                long gfxDriver = UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver();
                log.Log(string.Format("[VRAM] 메시 {0:N0}개, 합계 {1:N0}MB (1MB 넘는 것 {2}개) | 그래픽 드라이버가 잡은 메모리 {3:N0}MB",
                    meshCount, meshBytes / 1048576.0, meshBig, gfxDriver / 1048576.0));

                var list = new List<KeyValuePair<string, Group>>(meshNames);
                list.Sort((a, b) => b.Value.Bytes.CompareTo(a.Value.Bytes));
                for (int i = 0; i < list.Count && i < 10; i++)
                    log.Log(string.Format("[VRAM]   메시 {0,-30} {1,7:N0}MB  {2}개", list[i].Key, list[i].Value.Bytes / 1048576.0, list[i].Value.Count));
            }
            catch (Exception ex)
            {
                log.Error("[VRAM] 메시 세기 실패: " + ex.Message);
            }
        }

    }
}
