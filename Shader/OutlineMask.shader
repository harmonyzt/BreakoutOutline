// Tier-2 pipeline, pass 1 of 2 — render each object's silhouette into an
// offscreen MASK render target. Replaces the old per-object stencil/draw/clear
// trio: there is no vertex extrusion and no stencil here, just a flat fill of
// the object's outline colour plus the object's eye-depth. The fullscreen
// OutlineEdge pass then turns the mask into outline rings in a single draw,
// so cost no longer scales with (objects × submeshes × 3 passes).
//
// Mask RT layout (ARGBHalf): RGB = outline colour, A = eye-space depth (metres),
// SIGNED: positive for items/containers, NEGATIVE for dead bodies. The sign
// lets the OutlineEdge pass pick a per-type ring-occlusion tolerance (bodies
// need a loose bias so prone corpses don't fragment on the floor; items get a
// tight one so their rings don't bleed through thin walls/doors). |A| > 0
// doubles as the "filled" marker — the RT is cleared to 0 and any rendered
// pixel has |eye depth| >= 1e-4.
//
// OCCLUSION (LOS): per-pixel against _CameraDepthTexture, exactly like the old
// OutlineDraw — a silhouette pixel behind scene geometry is clipped, so it never
// reaches the mask and never gets outlined. Depth comes from rendered geometry
// (collider-independent), so EFT's colliderless walls occlude correctly.
Shader "LootOutline/Mask"
{
    Properties
    {
        _OutlineColor ("Outline Color", Color) = (1, 1, 1, 1)
        // 0 = always write (no occlusion); 1 = clip pixels behind scene depth.
        _DepthOcclude ("Depth Occlude", Float) = 1
        // Eye-space (metres) tolerance so a surface doesn't self-occlude at its
        // own contact edge. Bodies pass a larger value (flat-corpse floor clip).
        _DepthBias ("Depth Bias (m)", Float) = 0.03
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            ColorMask RGBA
            ZWrite    Off
            // Always: the mask is a depth-independent silhouette projection; real
            // occlusion is the per-pixel _CameraDepthTexture test in frag below.
            ZTest     Always
            Blend     Off       // overwrite — nearest-N draw order makes near win
            Cull      Off

            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; };
            struct v2f
            {
                float4 pos       : SV_POSITION;
                float4 screenPos : TEXCOORD0;
                float  eyeDepth  : TEXCOORD1;
            };

            float4 _OutlineColor;
            float  _DepthOcclude;
            float  _DepthBias;
            float  _OutlineFade;
            float  _MaskFadeOnly;
            // 1 = dead body → write NEGATIVE depth so the edge pass can apply the
            // loose body ring bias. Deliberately NOT in the Properties block: a
            // declared property's material default can shadow the per-object
            // CommandBuffer global that drives this; an undeclared uniform always
            // takes MPB/global values.
            float  _MaskBodyFlag;
            sampler2D _CameraDepthTexture;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos       = UnityObjectToClipPos(v.vertex);
                o.screenPos = ComputeScreenPos(o.pos);
                o.eyeDepth  = -UnityObjectToViewPos(v.vertex).z;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                if (_MaskFadeOnly > 0.5)
                    return float4(_OutlineFade, 0, 0, 0);

                if (_DepthOcclude > 0.5)
                {
                    float2 uv = i.screenPos.xy / i.screenPos.w;
                    float  sd = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
                    clip(sd - i.eyeDepth + _DepthBias);
                }
                // RGB = colour, A = signed eye depth (non-zero ⇒ filled;
                // negative ⇒ body — see header).
                float a = max(i.eyeDepth, 1e-4);
                if (_MaskBodyFlag > 0.5) a = -a;
                return float4(_OutlineColor.rgb, a);
            }
            ENDCG
        }
    }
    FallBack Off
}
