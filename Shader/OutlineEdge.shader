// Tier-2 pipeline, pass 2 of 2 — one fullscreen pass that turns the silhouette
// mask (OutlineMask) into outline rings and composites them over the camera
// colour. This is the whole point of Tier-2: outlining is now ONE fullscreen
// draw regardless of how many objects/submeshes are highlighted, instead of
// three draws per submesh per object every frame.
//
// For each screen pixel:
//   • If the mask is filled here (A > 0) it's INSIDE a silhouette → discard
//     (we draw the border ring, not a solid fill).
//   • Otherwise sample the mask in rings around the pixel. If any tap is filled,
//     this pixel is within _OutlineWidthPx of a silhouette edge → it's a ring
//     pixel. Take the nearest filled tap's colour (and its stored eye-depth).
//   • Ring occlusion (_EdgeOcclude): clip the ring pixel if scene geometry at
//     this location is nearer than the silhouette edge depth — keeps the ring
//     from drawing over walls in front of the object.
//
// Width is in screen pixels (distance-independent), matching the config.
Shader "LootOutline/Edge"
{
    Properties
    {
        _MainTex ("Mask", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "IgnoreProjector" = "True" }

        Pass
        {
            ZWrite Off
            ZTest  Always
            Cull   Off
            Blend  SrcAlpha OneMinusSrcAlpha   // composite the ring over the scene

            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f     { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            sampler2D _MainTex;
            sampler2D _FadeTex;
            float4    _MainTex_TexelSize;   // .xy = 1/width, 1/height
            sampler2D _CameraDepthTexture;

            float _OutlineWidthPx;      // ring thickness in screen pixels
            float _EdgeOcclude;         // 1 = occlude ring behind scene geometry
            // Ring-occlusion tolerance is PER TYPE, selected by the sign of the
            // mask's stored depth (negative = body). Items/containers get a tight
            // bias so their rings don't bleed through thin walls/doors; bodies get
            // a loose one so prone-corpse rings survive the floor they lie on.
            // Plain uniforms (not Properties) — driven from the controller.
            float _EdgeDepthBiasItem;   // eye-space (m), items + containers
            float _EdgeDepthBiasBody;   // eye-space (m), dead bodies
            float _OutlineAlpha;        // ring opacity

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = v.uv;
                return o;
            }

            // 8 evenly-spaced directions reused at each ring radius.
            static const float2 DIRS[8] =
            {
                float2( 1, 0), float2( 0.7071,  0.7071),
                float2( 0, 1), float2(-0.7071,  0.7071),
                float2(-1, 0), float2(-0.7071, -0.7071),
                float2( 0,-1), float2( 0.7071, -0.7071),
            };

            float4 frag(v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                if (c.a != 0.0) discard;  // interior of a silhouette — not the border
                                          // (depth is signed: negative = body)

                float2 texel = _MainTex_TexelSize.xy;

                float  bestDist  = 1e9;
                float3 bestColor = 0;
                float  bestDepth = 0;     // |stored depth| of nearest filled tap
                float  bestFade  = 0;
                bool   bestBody  = false; // nearest filled tap was a dead body
                bool   found     = false;

                // 3 concentric rings × 8 directions = 24 taps. Bounded regardless
                // of width, so thick outlines don't explode the tap count.
                [unroll] for (int r = 1; r <= 3; r++)
                {
                    float radius = _OutlineWidthPx * (r / 3.0);
                    [unroll] for (int a = 0; a < 8; a++)
                    {
                        float2 off = DIRS[a] * radius * texel;
                        float4 s   = tex2D(_MainTex, i.uv + off);
                        if (s.a != 0.0 && radius < bestDist)
                        {
                            bestDist  = radius;
                            bestColor = s.rgb;
                            bestDepth = abs(s.a);
                            bestBody  = s.a < 0.0;
                            bestFade  = tex2D(_FadeTex, i.uv + off).r;
                            found     = true;
                        }
                    }
                }

                if (!found) discard;

                if (_EdgeOcclude > 0.5)
                {
                    float bias = bestBody ? _EdgeDepthBiasBody : _EdgeDepthBiasItem;
                    float sd = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv));
                    if (sd < bestDepth - bias) discard;
                }

                return float4(bestColor, _OutlineAlpha * bestFade);
            }
            ENDCG
        }
    }
    FallBack Off
}
