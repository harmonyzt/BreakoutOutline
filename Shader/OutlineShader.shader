// Screen-space dilation outline — stencil masking, fully self-contained passes.
//
// Pass 1  STENCIL_WRITE   Stamps mesh silhouette into stencil bit 6 (Ref 64).
//                         ZTest Always: EFT depth-offsets must not block the write.
//
// Passes 2-9  OUT_*       Mesh shifted N pixels in 8 screen-space directions.
//                         Stencil NotEqual 64 = only pixels OUTSIDE the original
//                         silhouette → clean uniform outline ring on every edge.
//                         ZTest LEqual = hidden behind walls.
//
// Pass 10  STENCIL_CLEAR  Resets bit 6 for the next draw.
Shader "LootOutline/Outline"
{
    Properties
    {
        _OutlineColor ("Outline Color",         Color) = (1, 1, 1, 1)
        _OutlineWidth ("Outline Width (pixels)", Float) = 3.0
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+100" "IgnoreProjector" = "True" }

        // ── Pass 1: stamp silhouette into stencil ─────────────────────────────────
        Pass
        {
            Name "STENCIL_WRITE"
            ColorMask 0
            ZWrite    Off
            ZTest     Always
            Stencil
            {
                Ref       64
                ReadMask  64
                WriteMask 64
                Comp      Always
                Pass      Replace
            }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata { float4 vertex : POSITION; };
            struct v2f    { float4 pos    : SV_POSITION; };
            v2f   vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); return o; }
            fixed4 frag(v2f i)   : SV_Target { return fixed4(0,0,0,0); }
            ENDCG
        }

        // ── Pass 2: right ─────────────────────────────────────────────────────────
        Pass
        {
            Name "OUT_R"
            ColorMask RGBA
            ZWrite    Off
            ZTest     LEqual
            Blend     SrcAlpha OneMinusSrcAlpha
            Stencil
            {
                Ref       64
                ReadMask  64
                WriteMask 0
                Comp      NotEqual
                Pass      Keep
            }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _OutlineColor; float _OutlineWidth;
            struct appdata { float4 vertex : POSITION; };
            struct v2f    { float4 pos    : SV_POSITION; };
            v2f vert(appdata v) {
                v2f o; float4 c = UnityObjectToClipPos(v.vertex);
                c.x += _OutlineWidth * (2.0 * c.w / _ScreenParams.x); o.pos = c; return o;
            }
            fixed4 frag(v2f i) : SV_Target { return _OutlineColor; }
            ENDCG
        }

        // ── Pass 3: left ──────────────────────────────────────────────────────────
        Pass
        {
            Name "OUT_L"
            ColorMask RGBA
            ZWrite    Off
            ZTest     LEqual
            Blend     SrcAlpha OneMinusSrcAlpha
            Stencil
            {
                Ref       64
                ReadMask  64
                WriteMask 0
                Comp      NotEqual
                Pass      Keep
            }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _OutlineColor; float _OutlineWidth;
            struct appdata { float4 vertex : POSITION; };
            struct v2f    { float4 pos    : SV_POSITION; };
            v2f vert(appdata v) {
                v2f o; float4 c = UnityObjectToClipPos(v.vertex);
                c.x -= _OutlineWidth * (2.0 * c.w / _ScreenParams.x); o.pos = c; return o;
            }
            fixed4 frag(v2f i) : SV_Target { return _OutlineColor; }
            ENDCG
        }

        // ── Pass 4: up ────────────────────────────────────────────────────────────
        Pass
        {
            Name "OUT_U"
            ColorMask RGBA
            ZWrite    Off
            ZTest     LEqual
            Blend     SrcAlpha OneMinusSrcAlpha
            Stencil
            {
                Ref       64
                ReadMask  64
                WriteMask 0
                Comp      NotEqual
                Pass      Keep
            }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _OutlineColor; float _OutlineWidth;
            struct appdata { float4 vertex : POSITION; };
            struct v2f    { float4 pos    : SV_POSITION; };
            v2f vert(appdata v) {
                v2f o; float4 c = UnityObjectToClipPos(v.vertex);
                c.y += _OutlineWidth * (2.0 * c.w / _ScreenParams.y); o.pos = c; return o;
            }
            fixed4 frag(v2f i) : SV_Target { return _OutlineColor; }
            ENDCG
        }

        // ── Pass 5: down ──────────────────────────────────────────────────────────
        Pass
        {
            Name "OUT_D"
            ColorMask RGBA
            ZWrite    Off
            ZTest     LEqual
            Blend     SrcAlpha OneMinusSrcAlpha
            Stencil
            {
                Ref       64
                ReadMask  64
                WriteMask 0
                Comp      NotEqual
                Pass      Keep
            }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _OutlineColor; float _OutlineWidth;
            struct appdata { float4 vertex : POSITION; };
            struct v2f    { float4 pos    : SV_POSITION; };
            v2f vert(appdata v) {
                v2f o; float4 c = UnityObjectToClipPos(v.vertex);
                c.y -= _OutlineWidth * (2.0 * c.w / _ScreenParams.y); o.pos = c; return o;
            }
            fixed4 frag(v2f i) : SV_Target { return _OutlineColor; }
            ENDCG
        }

        // ── Pass 6: up-right ──────────────────────────────────────────────────────
        Pass
        {
            Name "OUT_UR"
            ColorMask RGBA
            ZWrite    Off
            ZTest     LEqual
            Blend     SrcAlpha OneMinusSrcAlpha
            Stencil
            {
                Ref       64
                ReadMask  64
                WriteMask 0
                Comp      NotEqual
                Pass      Keep
            }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _OutlineColor; float _OutlineWidth;
            struct appdata { float4 vertex : POSITION; };
            struct v2f    { float4 pos    : SV_POSITION; };
            v2f vert(appdata v) {
                v2f o; float4 c = UnityObjectToClipPos(v.vertex);
                c.x += _OutlineWidth * (2.0 * c.w / _ScreenParams.x);
                c.y += _OutlineWidth * (2.0 * c.w / _ScreenParams.y); o.pos = c; return o;
            }
            fixed4 frag(v2f i) : SV_Target { return _OutlineColor; }
            ENDCG
        }

        // ── Pass 7: up-left ───────────────────────────────────────────────────────
        Pass
        {
            Name "OUT_UL"
            ColorMask RGBA
            ZWrite    Off
            ZTest     LEqual
            Blend     SrcAlpha OneMinusSrcAlpha
            Stencil
            {
                Ref       64
                ReadMask  64
                WriteMask 0
                Comp      NotEqual
                Pass      Keep
            }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _OutlineColor; float _OutlineWidth;
            struct appdata { float4 vertex : POSITION; };
            struct v2f    { float4 pos    : SV_POSITION; };
            v2f vert(appdata v) {
                v2f o; float4 c = UnityObjectToClipPos(v.vertex);
                c.x -= _OutlineWidth * (2.0 * c.w / _ScreenParams.x);
                c.y += _OutlineWidth * (2.0 * c.w / _ScreenParams.y); o.pos = c; return o;
            }
            fixed4 frag(v2f i) : SV_Target { return _OutlineColor; }
            ENDCG
        }

        // ── Pass 8: down-right ────────────────────────────────────────────────────
        Pass
        {
            Name "OUT_DR"
            ColorMask RGBA
            ZWrite    Off
            ZTest     LEqual
            Blend     SrcAlpha OneMinusSrcAlpha
            Stencil
            {
                Ref       64
                ReadMask  64
                WriteMask 0
                Comp      NotEqual
                Pass      Keep
            }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _OutlineColor; float _OutlineWidth;
            struct appdata { float4 vertex : POSITION; };
            struct v2f    { float4 pos    : SV_POSITION; };
            v2f vert(appdata v) {
                v2f o; float4 c = UnityObjectToClipPos(v.vertex);
                c.x += _OutlineWidth * (2.0 * c.w / _ScreenParams.x);
                c.y -= _OutlineWidth * (2.0 * c.w / _ScreenParams.y); o.pos = c; return o;
            }
            fixed4 frag(v2f i) : SV_Target { return _OutlineColor; }
            ENDCG
        }

        // ── Pass 9: down-left ─────────────────────────────────────────────────────
        Pass
        {
            Name "OUT_DL"
            ColorMask RGBA
            ZWrite    Off
            ZTest     LEqual
            Blend     SrcAlpha OneMinusSrcAlpha
            Stencil
            {
                Ref       64
                ReadMask  64
                WriteMask 0
                Comp      NotEqual
                Pass      Keep
            }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _OutlineColor; float _OutlineWidth;
            struct appdata { float4 vertex : POSITION; };
            struct v2f    { float4 pos    : SV_POSITION; };
            v2f vert(appdata v) {
                v2f o; float4 c = UnityObjectToClipPos(v.vertex);
                c.x -= _OutlineWidth * (2.0 * c.w / _ScreenParams.x);
                c.y -= _OutlineWidth * (2.0 * c.w / _ScreenParams.y); o.pos = c; return o;
            }
            fixed4 frag(v2f i) : SV_Target { return _OutlineColor; }
            ENDCG
        }

        // ── Pass 10: clear stencil ────────────────────────────────────────────────
        Pass
        {
            Name "STENCIL_CLEAR"
            ColorMask 0
            ZWrite    Off
            ZTest     Always
            Stencil
            {
                Ref       0
                ReadMask  64
                WriteMask 64
                Comp      Always
                Pass      Replace
            }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata { float4 vertex : POSITION; };
            struct v2f    { float4 pos    : SV_POSITION; };
            v2f   vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); return o; }
            fixed4 frag(v2f i)   : SV_Target { return fixed4(0,0,0,0); }
            ENDCG
        }
    }
    FallBack Off
}
