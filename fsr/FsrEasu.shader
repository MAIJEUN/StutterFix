// AMD FidelityFX Super Resolution 1.0 - EASU (upscale). MIT license, see license.txt.
Shader "StutterFix/FsrEasu"
{
    Properties { _MainTex ("Texture", 2D) = "white" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0
            #include "UnityCG.cginc"
            #define A_GPU 1
            #define A_HLSL 1
            #include "ffx_a.hlsl"

            Texture2D _MainTex;
            SamplerState sampler_linear_clamp;
            float4 _Con0, _Con1, _Con2, _Con3;

            AF4 FsrEasuRF(AF2 p) { return _MainTex.GatherRed(sampler_linear_clamp, p); }
            AF4 FsrEasuGF(AF2 p) { return _MainTex.GatherGreen(sampler_linear_clamp, p); }
            AF4 FsrEasuBF(AF2 p) { return _MainTex.GatherBlue(sampler_linear_clamp, p); }
            #define FSR_EASU_F 1
            #include "ffx_fsr1.hlsl"

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata_img v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord; return o; }

            float4 frag(v2f i) : SV_Target
            {
                AU2 ip = AU2(i.pos.xy);
                AF3 c;
                FsrEasuF(c, ip, asuint(_Con0), asuint(_Con1), asuint(_Con2), asuint(_Con3));
                // 알파는 원래 그림 그대로 (쿼드가 알파를 쓸 수도 있어서)
                AF2 uv = (AF2(ip) * asfloat(asuint(_Con0.xy)) + _Con0.zw + 0.5) * _Con1.xy;
                float a = _MainTex.SampleLevel(sampler_linear_clamp, uv, 0).a;
                return float4(c, a);
            }
            ENDHLSL
        }
    }
}
