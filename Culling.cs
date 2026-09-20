using System;
using System.Collections.Generic;
using UnityEngine;

namespace StutterFix
{
    // 화면 밖 장식 스프라이트의 렌더러를 꺼서 엔진이 매 프레임 처리하는 양을 줄인다.
    //
    // 측정 근거: 고사양 맵에서 활성 스프라이트가 6,893개인데 화면에 보이는 것은 137~389개뿐이었다.
    // 나머지 6,500여 개도 매 프레임 경계 계산과 컬링 검사를 거친다.
    //
    // renderer.enabled = false 는 오브젝트 자체는 살려두고 그리기만 빼는 방식이라
    // 게임 로직이나 애니메이션에는 영향이 없다. 화면 안으로 들어오면 다시 켠다.
    public static class Culling
    {
        private static SpriteRenderer[] cache = new SpriteRenderer[0];
        private static readonly HashSet<SpriteRenderer> disabled = new HashSet<SpriteRenderer>();
        private static readonly HashSet<GameObject> deactivated = new HashSet<GameObject>();
        private static float sinceRefresh;
        private static float sinceCull;
        private static Camera cam;

        private static MeshRenderer[] meshCache = new MeshRenderer[0];
        private static readonly HashSet<MeshRenderer> disabledMeshes = new HashSet<MeshRenderer>();

        internal static bool Enabled;
        // true면 오브젝트를 통째로 비활성화한다. 렌더링뿐 아니라 그 오브젝트의 매 프레임 처리까지 사라진다.
        internal static bool DeactivateObjects;
        // 타일(메시 렌더러)까지 대상에 넣는다. 화면이 크게 망가지므로 진단용으로만 쓴다.
        internal static bool IncludeMeshes;
        internal static float Margin = 1.5f;   // 화면 크기의 몇 배까지 남겨둘지
        internal static string Status = "(꺼짐)";

        internal static void Tick(float dt)
        {
            if (!Enabled)
            {
                if (disabled.Count > 0) RestoreAll();
                return;
            }

            // 장식은 플레이 도중 계속 생기므로 주기적으로 목록을 다시 만든다.
            sinceRefresh += dt;
            if (sinceRefresh >= 3f || cache.Length == 0)
            {
                sinceRefresh = 0f;
                cache = UnityEngine.Object.FindObjectsByType<SpriteRenderer>(FindObjectsSortMode.None);
                if (IncludeMeshes) meshCache = UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
                cam = Camera.main;
            }

            // 매 프레임 전부 검사하면 그 자체가 비용이므로 조금 간격을 둔다.
            sinceCull += dt;
            if (sinceCull < 0.1f) return;
            sinceCull = 0f;

            if (cam == null) cam = Camera.main;
            if (cam == null) return;

            // 카메라가 보는 영역을 여유 있게 잡는다.
            float h = cam.orthographicSize * 2f * Margin;
            float w = h * cam.aspect;
            Vector3 c = cam.transform.position;
            var view = new Rect(c.x - w / 2f, c.y - h / 2f, w, h);

            int off = 0, on = 0;
            foreach (var sr in cache)
            {
                if (sr == null) continue;

                Vector3 p = sr.transform.position;
                bool inside = view.Contains(new Vector2(p.x, p.y));

                if (DeactivateObjects)
                {
                    var go = sr.gameObject;
                    if (!inside)
                    {
                        if (go.activeSelf)
                        {
                            go.SetActive(false);
                            deactivated.Add(go);
                            off++;
                        }
                    }
                    else if (deactivated.Contains(go))
                    {
                        go.SetActive(true);
                        deactivated.Remove(go);
                        on++;
                    }
                    continue;
                }

                if (!inside)
                {
                    if (sr.enabled)
                    {
                        sr.enabled = false;
                        disabled.Add(sr);
                        off++;
                    }
                }
                else if (disabled.Contains(sr))
                {
                    sr.enabled = true;
                    disabled.Remove(sr);
                    on++;
                }
            }

            if (IncludeMeshes)
            {
                foreach (var mr in meshCache)
                {
                    if (mr == null) continue;
                    Vector3 p = mr.transform.position;
                    bool inside = view.Contains(new Vector2(p.x, p.y));

                    if (!inside)
                    {
                        if (mr.enabled)
                        {
                            mr.enabled = false;
                            disabledMeshes.Add(mr);
                            off++;
                        }
                    }
                    else if (disabledMeshes.Contains(mr))
                    {
                        mr.enabled = true;
                        disabledMeshes.Remove(mr);
                        on++;
                    }
                }
            }

            int hidden = DeactivateObjects ? deactivated.Count : disabled.Count + disabledMeshes.Count;
            Status = $"대상 {cache.Length}개, 현재 꺼둠 {hidden}개 (이번에 끔 {off} / 켬 {on})" +
                     (DeactivateObjects ? " [오브젝트 비활성화 모드]" : " [렌더러만 끄기]");
        }

        internal static void RestoreAll()
        {
            foreach (var sr in disabled)
            {
                if (sr != null) sr.enabled = true;
            }
            disabled.Clear();

            foreach (var go in deactivated)
            {
                if (go != null) go.SetActive(true);
            }
            deactivated.Clear();

            foreach (var mr in disabledMeshes)
            {
                if (mr != null) mr.enabled = true;
            }
            disabledMeshes.Clear();

            Status = "(꺼짐 - 모두 복구함)";
        }
    }
}
