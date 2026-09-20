using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace StutterFix
{
    // 화면에 그려지는 스프라이트가 몇 개이고, 서로 이미지를 얼마나 공유하는지 센다.
    // 런타임 텍스처 아틀라스가 효과가 있을지 판단하려면 이 숫자가 먼저 필요하다.
    //   - 스프라이트 수에 비해 고유 텍스처 수가 적다  -> 묶어 그릴 여지가 크다
    //   - 스프라이트마다 텍스처가 제각각이고 크기도 크다 -> 아틀라스에 담기지 않아 효과가 없다
    public static class SceneScan
    {
        internal static string LastResult = "(아직 스캔 안 함)";

        internal static void Run()
        {
            try
            {
                var all = UnityEngine.Object.FindObjectsByType<SpriteRenderer>(FindObjectsSortMode.None);

                int active = 0, visible = 0;
                var textureCounts = new Dictionary<Texture, int>();
                long totalPixels = 0;
                var countedTextures = new HashSet<Texture>();
                var materials = new HashSet<Material>();

                foreach (var sr in all)
                {
                    if (!sr.enabled || !sr.gameObject.activeInHierarchy) continue;
                    active++;
                    if (sr.isVisible) visible++;

                    var mat = sr.sharedMaterial;
                    if (mat != null) materials.Add(mat);

                    var sprite = sr.sprite;
                    if (sprite == null) continue;
                    var tex = sprite.texture;
                    if (tex == null) continue;

                    int c;
                    textureCounts.TryGetValue(tex, out c);
                    textureCounts[tex] = c + 1;

                    if (countedTextures.Add(tex))
                    {
                        totalPixels += (long)tex.width * tex.height;
                    }
                }

                // 화면에 보이는 것만 따로 세면 컬링으로 얼마나 줄일 수 있는지 알 수 있다.
                int uniqueTex = textureCounts.Count;
                double sharing = uniqueTex > 0 ? (double)active / uniqueTex : 0;
                double mbEstimate = totalPixels * 4.0 / 1048576.0;

                var top = textureCounts.OrderByDescending(kv => kv.Value).Take(5)
                    .Select(kv => $"{(kv.Key.name.Length > 18 ? kv.Key.name.Substring(0, 18) : kv.Key.name)}({kv.Key.width}x{kv.Key.height}) x{kv.Value}");

                LastResult =
                    $"스프라이트 {active}개 (화면 안 {visible}개) | 고유 텍스처 {uniqueTex}개 | " +
                    $"평균 공유 {sharing:F1}개/텍스처 | 텍스처 총량 약 {mbEstimate:F0}MB | 머티리얼 {materials.Count}개";

                Main.Entry.Logger.Log("[scan] " + LastResult);
                Main.Entry.Logger.Log("[scan] 많이 쓰인 텍스처: " + string.Join(", ", top));
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("scan failed: " + ex.Message);
            }
        }
    }
}
