// Pass 3 of 3 - resets the stencil buffer after the outline has been drawn.
// ZTest Always so stencil is cleared even for parts of the mesh hidden
// behind walls; without this, stencil from object A would bleed into B.
Shader "LootOutline/Clear"
{
    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            ColorMask 0
            ZWrite    Off
            ZTest     Always

            Stencil
            {
                Ref  0
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
