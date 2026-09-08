// Pass 1 of 3 — writes the mesh silhouette into the stencil buffer.
// Rendered at a lower queue than OutlineDraw so all masks finish before
// any outline pixel is drawn (see LootOutlineController render-queue comments).
Shader "LootOutline/Stencil"
{
    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            ColorMask 0
            ZWrite    Off
            // ZTest Always: stamp the silhouette into stencil unconditionally,
            // regardless of what the scene depth buffer holds at AfterForwardAlpha.
            // With ZTest LEqual a mesh whose pixels failed the depth test would write
            // no stencil, and OutlineDraw's `NotEqual 200` would then match everywhere
            // and fill the whole silhouette. Depth-correct occlusion of the outline
            // itself still happens in OutlineDraw, which keeps its own depth test.
            ZTest     Always
            // Cull Off so meshes with either winding direction contribute to the
            // stencil; OutlineDraw is also Cull Off, so they must match.
            Cull      Off

            Stencil
            {
                Ref  200
                Comp Always
                Pass Replace
            }

            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; };
            struct v2f     { float4 pos    : SV_POSITION; };

            v2f    vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); return o; }
            fixed4 frag(v2f i)    : SV_Target { return 0; }
            ENDCG
        }
    }
    FallBack Off
}
