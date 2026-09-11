using UnityEngine;
using UnityEngine.Rendering;

namespace BreakoutOutlines.Drawer
{
    public partial class LootOutlineController
    {
        private void InitializeShaderPipeline()
        {
            // Mask+edge preferred; Plugin already dropped shaders that failed isSupported
            _useMaskEdge = Plugin.MaskShader != null && Plugin.EdgeShader != null;

            _useThreePass = !_useMaskEdge &&
                Plugin.StencilShader != null &&
                Plugin.DrawShader != null &&
                Plugin.ClearShader != null;

            if (_useMaskEdge)
            {
                InitializeMaskEdgePipeline();
            }
            else if (_useThreePass)
            {
                InitializeThreePassPipeline();
            }
            else
            {
                Debug.LogError("[LootOutline] No valid shader pipeline available!");
            }
        }

        private void InitializeMaskEdgePipeline()
        {
            _maskMat = new Material(Plugin.MaskShader) { hideFlags = HideFlags.HideAndDontSave };
            _edgeMat = new Material(Plugin.EdgeShader) { hideFlags = HideFlags.HideAndDontSave };
            _maskRtId = Shader.PropertyToID("_LootOutlineMask");
            _fadeRtId = Shader.PropertyToID("_LootOutlineFade");

            _cmd = new CommandBuffer { name = "LootOutline" };
            _mpb = new MaterialPropertyBlock();

            WarmupShaderPasses(_maskMat);
            WarmupShaderPasses(_edgeMat);
        }

        private void InitializeThreePassPipeline()
        {
            _itemStencilMat = new Material(Plugin.StencilShader) { hideFlags = HideFlags.HideAndDontSave, renderQueue = 3000 };
            _itemDrawMat = new Material(Plugin.DrawShader) { hideFlags = HideFlags.HideAndDontSave, renderQueue = 3050 };
            _itemClearMat = new Material(Plugin.ClearShader) { hideFlags = HideFlags.HideAndDontSave, renderQueue = 3100 };
            _contStencilMat = new Material(Plugin.StencilShader) { hideFlags = HideFlags.HideAndDontSave, renderQueue = 3000 };
            _contDrawMat = new Material(Plugin.DrawShader) { hideFlags = HideFlags.HideAndDontSave, renderQueue = 3050 };
            _contClearMat = new Material(Plugin.ClearShader) { hideFlags = HideFlags.HideAndDontSave, renderQueue = 3100 };
            _bodyDrawMat = new Material(Plugin.DrawShader) { hideFlags = HideFlags.HideAndDontSave, renderQueue = 3050 };

            // Stencil must be a pure depth-independent silhouette projection
            int zAlways = (int)CompareFunction.Always;
            foreach (var m in new[] { _itemStencilMat, _contStencilMat, _itemClearMat, _contClearMat })
            {
                m.SetInt("_ZTest", zAlways);
                m.SetInt("_ZWrite", 0);
            }

            // Occlusion is per-pixel vs _CameraDepthTexture in the shader
            _itemDrawMat.SetInt("_ZTest", zAlways);
            _contDrawMat.SetInt("_ZTest", zAlways);
            _bodyDrawMat.SetInt("_ZTest", zAlways);
            _itemDrawMat.SetFloat("_DepthOcclude", 1f);
            _contDrawMat.SetFloat("_DepthOcclude", 1f);
            _bodyDrawMat.SetFloat("_DepthOcclude", 1f);
            _bodyDrawMat.SetFloat("_DepthBias", Plugin.BodyDepthBias.Value);

            _cmd = new CommandBuffer { name = "LootOutline" };
            _mpb = new MaterialPropertyBlock();

            WarmupShaderPasses(_itemStencilMat);
            WarmupShaderPasses(_itemDrawMat);
            WarmupShaderPasses(_itemClearMat);
            WarmupShaderPasses(_bodyDrawMat);
        }

        private static void WarmupShaderPasses(Material mat)
        {
            if (mat == null || mat.shader == null) return;
            int passes = mat.shader.passCount;
            for (int p = 0; p < passes; p++)
            {
                try { mat.SetPass(p); }
                catch { }
            }
        }
    }
}
