using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace StutterFix
{
    // 실시간 모니터를 uGUI 로 그리는 뒤쪽.
    //
    // 예전 모니터는 IMGUI 였다. IMGUI 는 매 프레임 모든 요소를 처음부터 다시 그려서, 글자 하나에 0.015ms, 둥근 판 하나에도
    // 그리기 호출이 하나씩 들었다. 측정(Arche, 아이콘 + 옆 상세 패널): 그리기 한 번 0.45ms, 옆 패널을 펼치면 약 0.5ms 가
    // 더해졌고 렌더 스레드에도 0.6ms 가 붙었다(가벼운 구간 모니터 끔 341fps / 켬 278fps).
    //
    // 모양과 배치는 그대로 두려고, PerfOverlay 의 그리기 코드(어디에 무엇을 몇 픽셀로)는 손대지 않았다. 그 코드가 부르는
    // 기본 동작(둥근 판, 테두리, 글자, 막대)만 여기로 온다. 매 프레임 같은 순서로 부르면 같은 칸의 uGUI 요소를 다시 쓰고,
    // 값이 바뀐 것만 넣는다. uGUI 는 바뀐 요소만 다시 만들고 나머지는 묶어서 그린다.
    //
    // 좌표는 IMGUI 와 같다(왼쪽 위가 0,0, 아래로 +). 모니터 배율은 캔버스 배율(scaleFactor)로 준다. 루트를 키우면
    // 글자가 작은 크기로 만들어진 뒤 늘어나 흐려지기 때문이다.
    internal sealed class UiDraw
    {
        private GameObject go;
        private Canvas canvas;
        private RectTransform root;
        private Font font;
        private readonly List<Slot> slots = new List<Slot>();
        private int used;
        private readonly Dictionary<int, Sprite> fillSprites = new Dictionary<int, Sprite>();
        private readonly Dictionary<int, Sprite> lineSprites = new Dictionary<int, Sprite>();
        private const int SS = 4;   // 둥근 모서리 이미지 해상도 배수 (가장자리를 부드럽게)

        private const int KFill = 0, KLine = 1, KText = 2, KBars = 3;

        private sealed class Slot
        {
            public RectTransform rt;
            public int kind = -1;
            public Image img; public Text txt; public UiBars bars;
            public Rect rect = new Rect(float.NaN, 0, 0, 0);
            public Color col = new Color(-1, 0, 0, 0);
            public int rad = int.MinValue;
            public string s;
            public int size = -1; public FontStyle fs; public TextAnchor align; public bool wrap, clip, rich;
        }

        internal bool Ready { get { return go != null; } }

        internal void Create(Font f, int sortingOrder)
        {
            font = f;
            go = new GameObject("StutterFix.Monitor");
            Object.DontDestroyOnLoad(go);
            canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            canvas.pixelPerfect = false;
            var r = new GameObject("Root", typeof(RectTransform));
            r.transform.SetParent(go.transform, false);
            root = (RectTransform)r.transform;
            TopLeft(root);
            root.sizeDelta = Vector2.zero;
        }

        internal void Destroy()
        {
            if (go != null) Object.Destroy(go);
            go = null;
            slots.Clear();
            foreach (var s in fillSprites.Values) if (s != null) { Object.Destroy(s.texture); Object.Destroy(s); }
            foreach (var s in lineSprites.Values) if (s != null) { Object.Destroy(s.texture); Object.Destroy(s); }
            fillSprites.Clear(); lineSprites.Clear();
        }

        private static void TopLeft(RectTransform t)
        {
            t.anchorMin = t.anchorMax = new Vector2(0, 1);
            t.pivot = new Vector2(0, 1);
        }

        private static void Stretch(RectTransform t)
        {
            t.anchorMin = Vector2.zero; t.anchorMax = Vector2.one;
            t.pivot = new Vector2(0, 1);
            t.offsetMin = t.offsetMax = Vector2.zero;
        }

        // 한 프레임의 그리기 시작. offsetX 는 나타날 때 옆에서 미끄러져 들어오는 거리(모니터 단위).
        internal void Begin(float scale, float offsetX)
        {
            if (!go.activeSelf) go.SetActive(true);
            if (canvas.scaleFactor != scale) canvas.scaleFactor = scale;
            var p = new Vector2(offsetX, 0);
            if (root.anchoredPosition != p) root.anchoredPosition = p;
            used = 0;
        }

        // 이번 프레임에 안 쓴 칸은 끈다
        internal void End()
        {
            for (int i = used; i < slots.Count; i++)
                if (slots[i].rt.gameObject.activeSelf) slots[i].rt.gameObject.SetActive(false);
        }

        internal void Hide() { if (go != null && go.activeSelf) go.SetActive(false); }

        private Slot Next(int kind)
        {
            Slot s;
            if (used < slots.Count) s = slots[used];
            else
            {
                s = new Slot();
                var o = new GameObject("E", typeof(RectTransform));
                o.transform.SetParent(root, false);
                s.rt = (RectTransform)o.transform;
                TopLeft(s.rt);
                slots.Add(s);
            }
            used++;
            if (!s.rt.gameObject.activeSelf) s.rt.gameObject.SetActive(true);
            if (s.kind != kind) Switch(s, kind);
            return s;
        }

        // 한 칸은 판/테두리/글자/막대 중 하나다. 게임오브젝트 하나에 그래픽은 하나만 붙어서 종류마다 자식을 둔다.
        private void Switch(Slot s, int kind)
        {
            s.kind = kind;
            if (s.img != null) s.img.gameObject.SetActive(false);
            if (s.txt != null) s.txt.gameObject.SetActive(false);
            if (s.bars != null) s.bars.gameObject.SetActive(false);
            if (kind == KFill || kind == KLine)
            {
                if (s.img == null)
                {
                    var o = new GameObject("I", typeof(RectTransform));
                    o.transform.SetParent(s.rt, false);
                    Stretch((RectTransform)o.transform);
                    s.img = o.AddComponent<Image>();
                    s.img.raycastTarget = false;
                }
                s.img.gameObject.SetActive(true);
                s.rad = int.MinValue; s.col = new Color(-1, 0, 0, 0);
            }
            else if (kind == KText)
            {
                if (s.txt == null)
                {
                    var o = new GameObject("T", typeof(RectTransform));
                    o.transform.SetParent(s.rt, false);
                    Stretch((RectTransform)o.transform);
                    s.txt = o.AddComponent<Text>();
                    s.txt.raycastTarget = false;
                    s.txt.font = font;
                    s.txt.lineSpacing = 1f;
                }
                s.txt.gameObject.SetActive(true);
                s.size = -1; s.s = null; s.col = new Color(-1, 0, 0, 0);
            }
            else
            {
                if (s.bars == null)
                {
                    var o = new GameObject("B", typeof(RectTransform));
                    o.transform.SetParent(s.rt, false);
                    var t = (RectTransform)o.transform;
                    TopLeft(t); t.sizeDelta = Vector2.zero;
                    s.bars = o.AddComponent<UiBars>();
                    s.bars.raycastTarget = false;
                }
                s.bars.gameObject.SetActive(true);
            }
        }

        private static void Place(Slot s, Rect r)
        {
            if (s.rect == r) return;
            s.rect = r;
            s.rt.anchoredPosition = new Vector2(r.x, -r.y);
            s.rt.sizeDelta = new Vector2(r.width, r.height);
        }

        // 둥근 판(반지름 0 이면 네모)
        internal void Fill(Rect r, Color c, float radius)
        {
            var s = Next(KFill);
            Place(s, r);
            int key = Mathf.RoundToInt(radius * 2f);
            if (s.rad != key)
            {
                s.rad = key;
                if (key <= 0) { s.img.sprite = null; s.img.type = Image.Type.Simple; }
                else { s.img.sprite = RoundSprite(key, false); s.img.type = Image.Type.Sliced; s.img.pixelsPerUnitMultiplier = SS; s.img.fillCenter = true; }
            }
            if (s.col != c) { s.col = c; s.img.color = c; }
        }

        // 1 단위 두께의 둥근 테두리
        internal void Border(Rect r, Color c, float radius)
        {
            var s = Next(KLine);
            Place(s, r);
            int key = Mathf.Max(1, Mathf.RoundToInt(radius * 2f));
            if (s.rad != key)
            {
                s.rad = key;
                s.img.sprite = RoundSprite(key, true); s.img.type = Image.Type.Sliced; s.img.pixelsPerUnitMultiplier = SS; s.img.fillCenter = false;
            }
            if (s.col != c) { s.col = c; s.img.color = c; }
        }

        // 글자. IMGUI 스타일(크기, 굵기, 정렬, 줄바꿈, 자르기)을 그대로 옮긴다.
        internal void Label(Rect r, string text, GUIStyle st, Color c)
        {
            var s = Next(KText);
            Place(s, r);
            var t = s.txt;
            bool clip = st.clipping == TextClipping.Clip;
            if (s.size != st.fontSize || s.fs != st.fontStyle || s.align != st.alignment || s.wrap != st.wordWrap || s.clip != clip || s.rich != st.richText)
            {
                s.size = st.fontSize; s.fs = st.fontStyle; s.align = st.alignment; s.wrap = st.wordWrap; s.clip = clip; s.rich = st.richText;
                t.fontSize = st.fontSize;
                t.fontStyle = st.fontStyle;
                t.alignment = st.alignment;
                t.supportRichText = st.richText;
                t.horizontalOverflow = (st.wordWrap || clip) ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Overflow;
                t.verticalOverflow = clip ? VerticalWrapMode.Truncate : VerticalWrapMode.Overflow;
            }
            if (!ReferenceEquals(s.s, text) && s.s != text) { s.s = text; t.text = text ?? ""; }
            if (s.col != c) { s.col = c; t.color = c; }
        }

        // 막대 묶음: 이 칸의 그래픽 하나에 사각형을 모아 한 번에 그린다
        internal UiBars Bars()
        {
            var s = Next(KBars);
            Place(s, new Rect(0, 0, 0, 0));
            s.bars.Begin();
            return s.bars;
        }

        // 글자 폭 (오른쪽 정렬 값 옆에 보조 값을 붙일 때). 같은 글자는 다시 재지 않는다.
        private readonly TextGenerator gen = new TextGenerator();
        private readonly Dictionary<string, float> widths = new Dictionary<string, float>();
        private int widthsKey = -1;
        internal float Width(string text, GUIStyle st)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            int key = st.fontSize * 16 + (int)st.fontStyle;
            if (key != widthsKey || widths.Count > 256) { widths.Clear(); widthsKey = key; }
            float w;
            if (widths.TryGetValue(text, out w)) return w;
            var set = new TextGenerationSettings
            {
                font = font, fontSize = st.fontSize, fontStyle = st.fontStyle, scaleFactor = canvas != null ? canvas.scaleFactor : 1f,
                richText = false, color = Color.white, pivot = Vector2.zero, textAnchor = TextAnchor.UpperLeft,
                horizontalOverflow = HorizontalWrapMode.Overflow, verticalOverflow = VerticalWrapMode.Overflow,
                lineSpacing = 1f, generateOutOfBounds = true, generationExtents = Vector2.zero, updateBounds = false,
            };
            w = gen.GetPreferredWidth(text, set) / Mathf.Max(0.01f, set.scaleFactor);
            widths[text] = w;
            return w;
        }

        // 글자를 처음 쓸 때 글꼴 텍스처를 다시 만드느라 멈추지 않게, 쓰는 크기·굵기마다 미리 올려 둔다.
        // uGUI 는 (글자 크기 x 캔버스 배율) 픽셀로 글자를 만든다.
        internal void Warm(string chars, IEnumerable<GUIStyle> styles, float scale)
        {
            if (font == null) return;
            foreach (var st in styles)
            {
                if (st == null) continue;
                int px = Mathf.Max(1, Mathf.RoundToInt(st.fontSize * scale));
                font.RequestCharactersInTexture(chars, px, st.fontStyle);
            }
        }

        // 반지름 key/2 단위의 둥근 모양. SS 배 해상도로 가장자리 덮임 정도를 계산해 부드럽게 만든다.
        private Sprite RoundSprite(int key, bool outline)
        {
            var dict = outline ? lineSprites : fillSprites;
            Sprite sp;
            if (dict.TryGetValue(key, out sp) && sp != null) return sp;
            float rad = key / 2f * SS;                 // 텍스처 픽셀 단위 반지름
            int c = Mathf.CeilToInt(rad);               // 모서리 칸
            int n = c * 2 + 2;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
            var px = new Color32[n * n];
            float line = SS;                             // 테두리 두께 1 단위
            float half = n / 2f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    // 둥근 사각형(텍스처 전체, 모서리 반지름 rad)까지의 거리. 안쪽이 음수.
                    float qx = Mathf.Max(Mathf.Abs(x + 0.5f - half) - (half - rad), 0f);
                    float qy = Mathf.Max(Mathf.Abs(y + 0.5f - half) - (half - rad), 0f);
                    float qin = Mathf.Min(Mathf.Max(Mathf.Abs(x + 0.5f - half) - (half - rad), Mathf.Abs(y + 0.5f - half) - (half - rad)), 0f);
                    float d = Mathf.Sqrt(qx * qx + qy * qy) + qin - rad;
                    float a = Mathf.Clamp01(0.5f - d);                          // 바깥 가장자리 부드럽게
                    if (outline) a *= Mathf.Clamp01(d + line + 0.5f);           // 두께 1 단위 고리
                    px[y * n + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            float b = c;
            sp = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
            sp.hideFlags = HideFlags.HideAndDontSave;
            dict[key] = sp;
            return sp;
        }
    }

    // 막대 여러 개를 한 메시로
    internal sealed class UiBars : MaskableGraphic
    {
        private readonly List<Rect> rects = new List<Rect>(96), prevRects = new List<Rect>(96);
        private readonly List<Color32> cols = new List<Color32>(96), prevCols = new List<Color32>(96);

        internal void Begin() { rects.Clear(); cols.Clear(); }
        internal void Add(Rect r, Color c) { rects.Add(r); cols.Add(c); }

        // 지난번과 같으면 다시 만들지 않는다
        internal void Commit()
        {
            bool same = rects.Count == prevRects.Count;
            for (int i = 0; same && i < rects.Count; i++)
                if (rects[i] != prevRects[i] || !cols[i].Equals(prevCols[i])) same = false;
            if (same) return;
            prevRects.Clear(); prevRects.AddRange(rects);
            prevCols.Clear(); prevCols.AddRange(cols);
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var v = UIVertex.simpleVert;
            for (int i = 0; i < prevRects.Count; i++)
            {
                var r = prevRects[i]; v.color = prevCols[i];
                int b = vh.currentVertCount;
                v.position = new Vector3(r.xMin, -r.yMin); vh.AddVert(v);
                v.position = new Vector3(r.xMax, -r.yMin); vh.AddVert(v);
                v.position = new Vector3(r.xMax, -r.yMax); vh.AddVert(v);
                v.position = new Vector3(r.xMin, -r.yMax); vh.AddVert(v);
                vh.AddTriangle(b, b + 1, b + 2); vh.AddTriangle(b, b + 2, b + 3);
            }
        }
    }
}
