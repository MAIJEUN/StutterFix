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
                ScanOthers();
                ScanComponents();
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("scan failed: " + ex.Message);
            }
        }

        // 스프라이트 말고 엔진이 직접 처리하는 것들(파티클, 애니메이터, UI, 메시)을 센다.
        // 스크립트 프로파일러에도, 스프라이트 컬링에도 잡히지 않는 비용이라 따로 확인해야 한다.
        internal static void ScanOthers()
        {
            try
            {
                var particles = UnityEngine.Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None);
                int psActive = 0, psPlaying = 0, liveParticles = 0, maxParticles = 0;
                foreach (var ps in particles)
                {
                    if (!ps.gameObject.activeInHierarchy) continue;
                    psActive++;
                    if (ps.isPlaying) psPlaying++;
                    int n = ps.particleCount;
                    liveParticles += n;
                    if (n > maxParticles) maxParticles = n;
                }

                var animators = UnityEngine.Object.FindObjectsByType<Animator>(FindObjectsSortMode.None);
                int animActive = 0;
                foreach (var a in animators) if (a.isActiveAndEnabled) animActive++;

                var meshes = UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
                int meshActive = 0, meshVisible = 0;
                foreach (var m in meshes)
                {
                    if (!m.enabled || !m.gameObject.activeInHierarchy) continue;
                    meshActive++;
                    if (m.isVisible) meshVisible++;
                }

                var all = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None);
                int goActive = 0;
                foreach (var t in all) if (t.gameObject.activeInHierarchy) goActive++;

                string line =
                    $"파티클시스템 {psActive}개(재생중 {psPlaying}, 입자 {liveParticles}개, 최대 한 곳 {maxParticles}개) | " +
                    $"애니메이터 {animActive}개 | " +
                    $"메시렌더러 {meshActive}개(화면 안 {meshVisible}) | 활성 오브젝트 {goActive}개";

                Main.Entry.Logger.Log("[scan2] " + line);
                LastResult += "\n    " + line;
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("scan2 failed: " + ex.Message);
            }
        }

        // Update()를 가진 컴포넌트가 씬에 종류별로 몇 개나 있는지 센다.
        // 엔진 단계 측정에서 ScriptRunBehaviourUpdate가 압도적으로 나왔기 때문에,
        // 수가 많은 컴포넌트를 찾아 프로파일러 대상으로 삼기 위한 것이다.
        internal static void ScanComponents()
        {
            try
            {
                var all = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
                var counts = new Dictionary<string, int>();
                var withUpdate = new Dictionary<string, int>();

                foreach (var mb in all)
                {
                    if (mb == null || !mb.isActiveAndEnabled) continue;
                    var t = mb.GetType();
                    string n = t.Name;
                    int c;
                    counts.TryGetValue(n, out c);
                    counts[n] = c + 1;

                    var m = t.GetMethod("Update", System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);   // 상속받은 Update까지 포함
                    if (m != null)
                    {
                        withUpdate.TryGetValue(n, out c);
                        withUpdate[n] = c + 1;
                    }
                }

                var top = counts.OrderByDescending(kv => kv.Value).Take(12)
                    .Select(kv => $"{kv.Key} x{kv.Value}");
                var topU = withUpdate.OrderByDescending(kv => kv.Value).Take(12)
                    .Select(kv => $"{kv.Key} x{kv.Value}");

                Main.Entry.Logger.Log($"[comp] 활성 컴포넌트 {all.Length}개 / 종류 {counts.Count}가지");
                Main.Entry.Logger.Log("[comp] 많은 것: " + string.Join(", ", top));
                Main.Entry.Logger.Log("[comp] Update 가진 것: " + string.Join(", ", topU));
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("comp scan failed: " + ex.Message);
            }
        }
    }
}
