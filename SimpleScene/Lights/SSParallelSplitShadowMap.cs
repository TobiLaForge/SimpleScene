﻿using System;
using OpenTK.Graphics.OpenGL;
using OpenTK;
using System.Collections.Generic;
using Util3d;

namespace SimpleScene
{
    public class SSParallelSplitShadowMap : SSShadowMapBase
    {
        // http://http.developer.nvidia.com/GPUGems3/gpugems3_ch10.html

        #region Constants
        public const int c_numberOfSplits = 4;
        private const float c_alpha = 0.992f; // logarithmic component ratio (GPU Gems 3 10.1.12)

        private static readonly Matrix4[] c_cropMatrices = {
            new Matrix4 (
                .5f, 0f, 0f, 0f,
                0f, .5f, 0f, 0f,
                0f, 0f, 1f, 0f,
                -.5f, -.5f, 0f, 1f),
            new Matrix4 (
                .5f, 0f, 0f, 0f,
                0f, .5f, 0f, 0f,
                0f, 0f, 1f, 0f,
                +.5f, -.5f, 0f, 1f),
            new Matrix4 (
                .5f, 0f, 0f, 0f,
                0f, .5f, 0f, 0f,
                0f, 0f, 1f, 0f,
                -.5f, +.5f, 0f, 1f),
            new Matrix4 (
                .5f, 0f, 0f, 0f,
                0f, .5f, 0f, 0f,
                0f, 0f, 1f, 0f,
                +.5f, +.5f, 0f, 1f),
        };
        #endregion

        #region Temp Use Variables
        private Matrix4[] m_shadowViewProjMatrices = new Matrix4[c_numberOfSplits];
        private Matrix4[] m_shadowViewProjBiasMatrices = new Matrix4[c_numberOfSplits];
        private Vector2[] m_poissonScaling = new Vector2[c_numberOfSplits];
        private float[] m_viewSplits = new float[c_numberOfSplits];

        private Matrix4[] m_frustumViewProjMatrices = new Matrix4[c_numberOfSplits];
        private SSAABB[] m_frustumLightBB = new SSAABB[c_numberOfSplits]; // light-aligned BB
        private SSAABB[] m_objsLightBB = new SSAABB[c_numberOfSplits];    // light-aligned BB
        private SSAABB[] m_resultLightBB = new SSAABB[c_numberOfSplits];  // light-aligned BB
        FrustumCuller[] m_splitFrustums = new FrustumCuller[c_numberOfSplits];
        private bool[] m_shrink = new bool[c_numberOfSplits];
        #endregion


        public SSParallelSplitShadowMap(TextureUnit unit) 
            : base(unit, 2048, 2048)        
        { }

        public override void PrepareForRender(SSRenderConfig renderConfig, 
                                              List<SSObject> objects,
                                              float fov, float aspect, 
                                              float cameraNearZ, float cameraFarZ) {
            base.PrepareForRenderBase(renderConfig, objects);

            ComputeProjections(
                objects,
                renderConfig.invCameraViewMat,
                renderConfig.projectionMatrix,
                fov, aspect, cameraNearZ, cameraFarZ);

            // update info for the regular draw pass later
            renderConfig.MainShader.Activate();
            renderConfig.MainShader.UniNumShadowMaps = c_numberOfSplits;
            if (renderConfig.usePoissonSampling) {
                renderConfig.MainShader.UpdatePoissonScaling(m_poissonScaling);
            }
            renderConfig.MainShader.UpdateShadowMapBiasVPs(m_shadowViewProjBiasMatrices);
            renderConfig.MainShader.UpdatePssmSplits(m_viewSplits);

            // setup for render shadowmap pass
            renderConfig.PssmShader.Activate();
            renderConfig.PssmShader.UpdateShadowMapVPs(m_shadowViewProjMatrices);
        }

        void ComputeProjections(
            List<SSObject> objects,
            Matrix4 cameraView,
            Matrix4 cameraProj,
            float fov, float aspect, float cameraNearZ, float cameraFarZ) 
        {
            if (m_light.Type != SSLight.LightType.Directional) {
                throw new NotSupportedException();
            }

            // light-aligned unit vectors
            Vector3 lightZ = m_light.Direction.Normalized();
            Vector3 lightX, lightY;
            OpenTKHelper.TwoPerpAxes(lightZ, out lightX, out lightY);
            // transform matrix from regular space into light aligned space
            Matrix4 lightTransform = new Matrix4 (
                lightX.X, lightX.Y, lightX.Z, 0f,
                lightY.X, lightY.Y, lightY.Z, 0f,
                lightZ.X, lightZ.Y, lightZ.Z, 0f,
                0f,       0f,       0f,       0f
            );

            // Step 0: camera projection matrix (nearZ and farZ modified) for each frustum split
            float prevFarZ = cameraNearZ;
            for (int i = 0; i < c_numberOfSplits; ++i) {
                // generate frustum splits using Practical Split Scheme (GPU Gems 3, 10.2.1)
                float iRatio = (float)(i+1) / (float)c_numberOfSplits;
                float cLog = cameraNearZ * (float)Math.Pow(cameraFarZ / cameraNearZ, iRatio);
                float cUni = cameraNearZ + (cameraFarZ - cameraNearZ) * iRatio;
                float nextFarZ = c_alpha * cLog + (1f - c_alpha) * cUni;
                float nextNearZ = prevFarZ;

                // exported to the shader
                m_viewSplits [i] = nextFarZ;

                // create a view proj matrix with the nearZ, farZ values for the current split
                m_frustumViewProjMatrices[i] = cameraView
                                             * Matrix4.CreatePerspectiveFieldOfView(fov, aspect, nextNearZ, nextFarZ);

                // create light-aligned AABBs of frustums
                m_frustumLightBB [i] = SSAABB.FromFrustum(ref lightTransform, ref m_frustumViewProjMatrices [i]);

                prevFarZ = nextFarZ;
            }

            #if true
            // Optional scene-dependent optimization
            for (int i = 0; i < c_numberOfSplits; ++i) {
                m_objsLightBB[i] = new SSAABB(float.PositiveInfinity, float.NegativeInfinity);
                m_splitFrustums[i] = new FrustumCuller(ref m_frustumViewProjMatrices[i]);
                m_shrink[i] = false;
            }
            foreach (var obj in objects) {
                // pass through all shadow casters and receivers
                if (obj.renderState.toBeDeleted  || obj.boundingSphere == null
                 || !obj.renderState.visible || !obj.renderState.receivesShadows) {
                    continue;
                } else {
                    for (int i = 0; i < c_numberOfSplits; ++i) {
                        if (m_splitFrustums[i].isSphereInsideFrustum(obj.Pos, obj.ScaledRadius)) {
                            // determine AABB in light coordinates of the objects so far
                            m_shrink[i] = true;                        
                            Vector3 lightAlignedPos = Vector3.Transform(obj.Pos, lightTransform);
                            Vector3 rad = new Vector3(obj.ScaledRadius);
                            Vector3 localMin = lightAlignedPos - rad;
                            Vector3 localMax = lightAlignedPos + rad;

                            m_objsLightBB[i].UpdateMin(localMin);
                            m_objsLightBB[i].UpdateMax(localMax);
                        }       
                    }
                }
            }
            #endif

            for (int i = 0; i < c_numberOfSplits; ++i) {
                if (m_shrink [i]) {
                    m_resultLightBB [i].Min.Xy = Vector2.ComponentMax(m_frustumLightBB[i].Min.Xy, 
                                                                        m_objsLightBB[i].Min.Xy);
                    m_resultLightBB [i].Min.Z = m_objsLightBB [i].Min.Z;
                    m_resultLightBB [i].Max = Vector3.ComponentMin(m_frustumLightBB [i].Max,
                                                                     m_objsLightBB [i].Max);
                } else {
                    m_resultLightBB [i] = m_frustumLightBB [i];
                }
            }

            for (int i = 0; i < c_numberOfSplits; ++i) {
                // Obtain view + projection + crop matrix, need it later
                Matrix4 shadowView, shadowProj;
                viewProjFromLightAlignedBB(ref m_resultLightBB [i], ref lightTransform, ref lightY,
                                           out shadowView, out shadowProj);
                m_shadowViewProjMatrices[i] = shadowView * shadowProj * c_cropMatrices[i];
                // obtain view + projection + clio + bias
                m_shadowViewProjBiasMatrices[i] = m_shadowViewProjMatrices[i] * c_biasMatrix;

                // There is, currently, no mathematically derived solution to how much Poisson spread scaling
                // you need for each split. Current improvisation combines 1) increasing spread for the near 
                // splits; reducing spread for the far splits and 2) reducing spread for splits with larger 
                // light-aligned areas; increasing spread for splits with smaller light-aligned areas
                m_poissonScaling [i] = m_resultLightBB [i].Diff().Xy / (100f * (float)Math.Pow(3.0, i - 1));
            }

            // Combine all splits' BB into one and extend it to include shadow casters closer to light
            SSAABB castersLightBB = new SSAABB (float.PositiveInfinity, float.NegativeInfinity);
            for (int i = 0; i < c_numberOfSplits; ++i) {
                castersLightBB.Combine(ref m_resultLightBB [i]);
            }

            // extend Z of the AABB to cover shadow-casters closer to the light
            foreach (var obj in objects) {
                if (obj.renderState.toBeDeleted || obj.boundingSphere == null 
                 || !obj.renderState.visible || !obj.renderState.castsShadow) {
                    continue;
                } else {
                    Vector3 lightAlignedPos = Vector3.Transform(obj.Pos, lightTransform);
                    Vector3 rad = new Vector3(obj.ScaledRadius);
                    Vector3 localMin = lightAlignedPos - rad;
                    Vector3 localMax = lightAlignedPos + rad;

                    if (localMin.Z < castersLightBB.Min.Z) {
                        if (OpenTKHelper.RectsOverlap(castersLightBB.Min.Xy,
                                                      castersLightBB.Max.Xy,
                                                      localMin.Xy,
                                                      localMax.Xy)) {
                            castersLightBB.Min.Z = localMin.Z;
                        }
                    }
                }
            }

            // Generate frustum culler from the BB extended towards light to include shadow casters
            Matrix4 frustumView, frustumProj;
            viewProjFromLightAlignedBB(ref castersLightBB, ref lightTransform, ref lightY,
                                       out frustumView, out frustumProj);
            Matrix4 frustumMatrix = frustumView * frustumProj;
            FrustumCuller = new FrustumCuller (ref frustumMatrix);
        }
    }
}

