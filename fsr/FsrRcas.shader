// AMD FidelityFX Super Resolution 1.0 - RCAS (sharpen). MIT license, see license.txt.
Shader "StutterFix/FsrRcas"
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
            float4 _RcasCon;

            AF4 FsrRcasLoadF(ASU2 p) { return _MainTex.Load(int3(p, 0)); }
            void FsrRcasInputF(inout AF1 r, inout AF1 g, inout AF1 b) {}
            #define FSR_RCAS_F 1
            #define FSR_RCAS_PASSTHROUGH_ALPHA 1
            #include "ffx_fsr1.hlsl"

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata_img v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord; return o; }

            float4 frag(v2f i) : SV_Target
            {
                AF4 c;
                FsrRcasF(c.r, c.g, c.b, c.a, AU2(i.pos.xy), asuint(_RcasCon));
                return c;
            }
            ENDHLSL
        }
    }
}
