// Pass 2 of 3 - draws the coloured outline border.
// Rendered after OutlineStencil so the combined silhouette of the whole
// object is already in the stencil buffer.
//
// Vertex expansion: instead of extruding along the face normal (which fails
// for camera-facing panels whose clip-space XY normal ≈ 0), each vertex is
// pushed outward in NDC space away from the object's world-space centre.
// This direction is always non-zero and always points toward the silhouette
// edge regardless of face orientation.
//
// OCCLUSION: done in the fragment shader against _CameraDepthTexture - a stable,
// fully-resolved scene depth EFT generates for its post-FX. Because that depth
// comes from RENDERED geometry it is collider-independent, so the colliderless
// walls EFT's maps are full of still occlude outlines correctly. _DepthOcclude
// selects the mode:
//   0 → no occlusion; always draw.
//   1 → PER-PIXEL: clip each ring pixel against scene depth at its own location.
//       Precise; right for items/containers standing in the world.
//   2 → PER-OBJECT (centre): sample scene depth ONCE at the object centre and
//       show/hide the whole silhouette as a unit. Right for dead bodies - a
//       prone corpse's edges sit on the floor, and per-pixel occlusion clips the
//       ring against that floor and fragments the outline. Centre-sampling shows
//       the complete body when its centre is visible and hides it entirely when
//       the centre is behind a wall. _OccludeRadius (the object's bound radius)
//       is the tolerance so the body's own half-thickness doesn't self-occlude.
Shader "LootOutline/Draw"
{
    Properties
    {
        _OutlineColor ("Outline Color", Color)           = (1, 1, 1, 1)
        _OutlineWidth ("Outline Width (clip units)", Float) = 0.003
        // World-space centre of the whole object - set per-draw via MaterialPropertyBlock
        // (static meshes) or CommandBuffer global (skinned bodies).
        _ObjectCenter ("Object Centre (world)", Vector)  = (0, 0, 0, 1)
        // Hardware depth test. Always for every material: real occlusion is per-pixel
        // / per-object against _CameraDepthTexture in frag, not the bound z-buffer.
        _ZTest ("ZTest", Float) = 8
        // Occlusion mode: 0=off, 1=per-pixel, 2=per-object centre (see header).
        _DepthOcclude ("Depth Occlude Mode", Float) = 1
        // Eye-space (metres) tolerance to avoid self-clipping at contact surfaces.
        _DepthBias ("Depth Bias (m)", Float) = 0.03
        // NOTE: mode 2's centre-test tolerance (the object's bound radius) is
        // passed in _ObjectCenter.w - NOT a separate property - because a
        // standalone property would take the material default over the per-body
        // CommandBuffer global and read 0, hiding every body.
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            ColorMask RGBA
            ZWrite    Off
            ZTest     [_ZTest]
            Blend     SrcAlpha OneMinusSrcAlpha
            Cull      Off

            Stencil
            {
                Ref  200
                Comp NotEqual   // only pixels OUTSIDE the stencil mask → clean border
                Pass Keep
            }

            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; };
            struct v2f
            {
                float4 pos         : SV_POSITION;
                float4 screenPos   : TEXCOORD0; // ring pixel screen pos (mode 1)
                float  eyeDepth    : TEXCOORD1; // eye depth of the un-extruded vertex (mode 1)
                float4 centerScreen: TEXCOORD2; // object-centre screen pos (mode 2)
                float  centerEye   : TEXCOORD3; // object-centre eye depth (mode 2)
            };

            float4 _OutlineColor;
            float  _OutlineWidth;
            float4 _ObjectCenter; // .xyz = world centre, .w = bound radius (mode 2)
            float  _DepthOcclude;
            float  _DepthBias;

            sampler2D _CameraDepthTexture;

            v2f vert(appdata v)
            {
                v2f o;

                // Eye-space depth of the ORIGINAL (un-extruded) vertex - the object-
                // edge depth used by the per-pixel mode.
                o.eyeDepth = -UnityObjectToViewPos(v.vertex).z;

                float4 clipPos    = UnityObjectToClipPos(v.vertex);
                float4 clipCenter = mul(UNITY_MATRIX_VP, float4(_ObjectCenter.xyz, 1.0));

                // Object-centre occlusion data (mode 2). clip.w == eye-space depth.
                o.centerEye    = max(clipCenter.w, 1e-4);
                o.centerScreen = ComputeScreenPos(clipCenter);

                // Guard: vertex/centre behind the camera → skip expansion, let the
                // hardware clipper handle it.
                if (clipPos.w < 0.001 || clipCenter.w < 0.001)
                {
                    o.pos       = clipPos;
                    o.screenPos = ComputeScreenPos(clipPos);
                    return o;
                }

                float2 vNDC = clipPos.xy    / clipPos.w;
                float2 cNDC = clipCenter.xy / clipCenter.w;

                float2 dir = vNDC - cNDC;
                float  len = length(dir);
                dir = (len > 1e-4) ? (dir / len) : float2(0.0, 1.0);

                // Expand in clip space; ×w keeps the pixel offset constant on screen.
                clipPos.xy += dir * _OutlineWidth * clipPos.w;

                o.pos       = clipPos;
                o.screenPos = ComputeScreenPos(clipPos);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                if (_DepthOcclude > 1.5)
                {
                    // PER-OBJECT: sample scene depth at the object centre and hide
                    // the whole silhouette when something is in front of the object
                    // by more than its bound radius (+bias). Empty depth reads as
                    // far → never hides (graceful "show" fallback).
                    float2 cuv = i.centerScreen.xy / i.centerScreen.w;
                    float  sd  = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, cuv));
                    clip(sd - i.centerEye + _ObjectCenter.w + _DepthBias);
                }
                else if (_DepthOcclude > 0.5)
                {
                    // PER-PIXEL: clip ring pixels behind scene geometry at their own
                    // screen location.
                    float2 uv = i.screenPos.xy / i.screenPos.w;
                    float  sd = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
                    clip(sd - i.eyeDepth + _DepthBias);
                }
                return _OutlineColor;
            }
            ENDCG
        }
    }
    FallBack Off
}
